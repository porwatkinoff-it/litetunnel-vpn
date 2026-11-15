package app.litetunnel.vpn

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.os.Handler
import android.os.Looper
import android.os.PowerManager
import android.os.SystemClock
import androidx.core.content.ContextCompat
import app.litetunnel.core.ConfigBuilder
import app.litetunnel.core.GroupWatch
import io.nekohasekai.libbox.CommandClient
import io.nekohasekai.libbox.CommandClientHandler
import io.nekohasekai.libbox.CommandClientOptions
import io.nekohasekai.libbox.ConnectionEvents
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.LogIterator
import io.nekohasekai.libbox.OutboundGroupItemIterator
import io.nekohasekai.libbox.OutboundGroupIterator
import io.nekohasekai.libbox.StatusMessage
import io.nekohasekai.libbox.StringIterator

/**
 * Keeps an auto-select group on a working server with the app closed, by the
 * rules of [GroupWatch]: a full test as soon as the picked server has no
 * working result, and with the screen on a check of the pick alone every two
 * minutes. The core's group reports come only when a result changes; with
 * the screen off nothing here ticks.
 */
class GroupGuard(private val context: Context) {
    private val main = Handler(Looper.getMainLooper())

    // Main thread only.
    private val watch = GroupWatch()
    private var isGroup = false
    private var generation = 0

    /** The member the core picked, as its tag ("proxy-3"). */
    private var picked: String? = null
    private var client: CommandClient? = null
    private var screenReceiver: BroadcastReceiver? = null
    private var screenOn = true

    private val probeTick = object : Runnable {
        override fun run() {
            // A delayed post may run a little early.
            if (watch.probeDue(now, GroupWatch.PROBE_INTERVAL - 15)) probe()
            main.postDelayed(this, PROBE_MS)
        }
    }

    private val now: Double get() = SystemClock.elapsedRealtime() / 1000.0

    /**
     * The core started or reloaded. A group is tested again a little later:
     * the core's own first test can run before the network is up, fail for
     * every member and leave the first one picked until the next test.
     */
    fun changed(group: Boolean) = main.post {
        isGroup = group
        generation++
        watch.reset()
        picked = null
        if (!group) {
            stopWatching()
            return@post
        }
        startWatching()
        val current = generation
        for (delay in longArrayOf(3_000, 20_000)) {
            main.postDelayed({ if (generation == current) testGroup() }, delay)
        }
    }

    /** The app came to the front: test the group unless it was checked in the last minute. */
    fun appOpened() = main.post {
        if (isGroup && watch.probeDue(now, GroupWatch.WAKE_GAP)) testGroup()
    }

    fun close() = changed(false)

    private fun testGroup() {
        if (!isGroup) return
        watch.tested(now)
        urlTest(ConfigBuilder.PROXY_TAG)
    }

    /** One request through the picked server. A failure drops its result, the core reports that, and [reported] tests the group. */
    private fun probe() {
        val tag = picked ?: return
        if (isGroup) urlTest(tag)
    }

    private fun urlTest(tag: String) {
        Thread({ runCatching { Libbox.newStandaloneCommandClient().urlTest(tag) } }, "group-test").start()
    }

    private fun reported(tag: String?, delay: Int) {
        if (!isGroup) return
        picked = tag
        if (tag == null) return
        if (watch.reported(answered = delay > 0, now = now)) {
            urlTest(ConfigBuilder.PROXY_TAG)
            scheduleRetry()
        }
    }

    /** Tests again later while the pick still has no answer and the screen is on; with it off, the screen turning on checks it. */
    private fun scheduleRetry() {
        val current = generation
        main.postDelayed({
            if (generation == current && screenOn && watch.retryDue(now)) {
                urlTest(ConfigBuilder.PROXY_TAG)
                scheduleRetry()
            }
        }, (watch.retryDelay * 1000).toLong())
    }

    private fun screen(on: Boolean) {
        screenOn = on
        main.removeCallbacks(probeTick)
        if (!on || !isGroup) return
        val current = generation
        // The network may need a moment after the screen comes on.
        main.postDelayed({
            if (generation == current && screenOn && watch.probeDue(now, GroupWatch.WAKE_GAP)) probe()
        }, 3_000)
        main.postDelayed(probeTick, PROBE_MS)
    }

    private fun startWatching() {
        if (screenReceiver == null) {
            val receiver = object : BroadcastReceiver() {
                override fun onReceive(context: Context, intent: Intent) {
                    screen(intent.action == Intent.ACTION_SCREEN_ON)
                }
            }
            val filter = IntentFilter().apply {
                addAction(Intent.ACTION_SCREEN_ON)
                addAction(Intent.ACTION_SCREEN_OFF)
            }
            ContextCompat.registerReceiver(context, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
            screenReceiver = receiver
        }
        screenOn = context.getSystemService(PowerManager::class.java).isInteractive
        main.removeCallbacks(probeTick)
        if (screenOn) main.postDelayed(probeTick, PROBE_MS)
        connect()
    }

    private fun stopWatching() {
        screenReceiver?.let { runCatching { context.unregisterReceiver(it) } }
        screenReceiver = null
        main.removeCallbacks(probeTick)
        val running = client ?: return
        client = null
        Thread({ runCatching { running.disconnect() } }, "group-close").start()
    }

    private fun connect() {
        if (client != null || !isGroup) return
        val options = CommandClientOptions().apply { addCommand(Libbox.CommandGroup) }
        val listener = Listener()
        val next = CommandClient(listener, options)
        listener.owner = next
        client = next
        Thread({
            try {
                next.connect()
            } catch (e: Exception) {
                main.post { dropped(next) }
            }
        }, "group-watch").start()
    }

    /** The socket closed under us: reconnect while a group runs. */
    private fun dropped(which: CommandClient) {
        if (client !== which) return
        client = null
        main.postDelayed({ connect() }, 5_000)
    }

    private inner class Listener : CommandClientHandler {
        @Volatile
        var owner: CommandClient? = null

        override fun connected() = Unit

        override fun disconnected(message: String?) {
            val which = owner ?: return
            main.post { dropped(which) }
        }

        override fun writeGroups(groups: OutboundGroupIterator) {
            var tag: String? = null
            var delay = 0
            while (groups.hasNext()) {
                val group = groups.next()
                if (group.tag != ConfigBuilder.PROXY_TAG) continue
                tag = group.selected.ifEmpty { null }
                val items = group.items ?: continue
                while (items.hasNext()) {
                    val item = items.next()
                    if (item.tag == group.selected) delay = item.urlTestDelay
                }
            }
            main.post { reported(tag, delay) }
        }

        override fun clearLogs() = Unit
        override fun writeLogs(iterator: LogIterator) = Unit
        override fun writeStatus(message: StatusMessage) = Unit
        override fun initializeClashMode(modes: StringIterator, current: String) = Unit
        override fun setDefaultLogLevel(level: Int) = Unit
        override fun updateClashMode(mode: String) = Unit
        override fun writeConnectionEvents(events: ConnectionEvents) = Unit
        override fun writeOutbounds(outbounds: OutboundGroupItemIterator) = Unit
    }

    private companion object {
        const val PROBE_MS = (GroupWatch.PROBE_INTERVAL * 1000).toLong()
    }
}
