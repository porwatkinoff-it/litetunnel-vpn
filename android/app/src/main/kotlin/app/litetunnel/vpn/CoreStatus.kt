package app.litetunnel.vpn

import android.os.Handler
import android.os.Looper
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.getValue
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.compose.currentStateAsState
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
import java.util.concurrent.Executors
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/** Speeds in bytes per second, totals in bytes. */
data class Traffic(val uplink: Long, val downlink: Long, val uplinkTotal: Long, val downlinkTotal: Long)

/** One line of the core's log; level as in sing-box, 0 panic … 6 trace. */
data class LogLine(val level: Int, val text: String) {
    val isError: Boolean get() = level <= 2
    val isWarning: Boolean get() = level == 3
}

/**
 * Traffic and log lines of the running core, read from its command socket only
 * while a screen that shows them is on screen. With the app in the background
 * there is no client, and the core sends nothing.
 */
object CoreStatus {
    private val _traffic = MutableStateFlow<Traffic?>(null)
    val traffic: StateFlow<Traffic?> = _traffic.asStateFlow()

    private val _logs = MutableStateFlow<List<LogLine>>(emptyList())

    /** The last [MAX_LINES] lines; kept after the VPN stops, until it starts again. */
    val logs: StateFlow<List<LogLine>> = _logs.asStateFlow()

    private val main = Handler(Looper.getMainLooper())
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "core-status") }

    // Main thread only.
    private var users = 0
    private var client: CommandClient? = null
    private val close = Runnable { disconnect() }
    private val retry = Runnable { connect() }

    /** A screen showing traffic or the log appeared. Main thread. */
    fun acquire() {
        users++
        main.removeCallbacks(close)
        connect()
    }

    /** Closes a moment after the last screen goes, so turning the screen does not reconnect. */
    fun release() {
        users = maxOf(0, users - 1)
        if (users == 0) {
            main.removeCallbacks(retry)
            main.postDelayed(close, 5_000)
        }
    }

    private fun connect() {
        if (client != null || users == 0 || LiteVpnService.current == null) return
        val options = CommandClientOptions().apply {
            addCommand(Libbox.CommandStatus)
            addCommand(Libbox.CommandLog)
            setStatusInterval(1_000_000_000L)
        }
        val listener = Listener()
        val next = CommandClient(listener, options)
        listener.owner = next
        client = next
        worker.execute {
            try {
                next.connect()
            } catch (e: Exception) {
                main.post { dropped(next) }
            }
        }
    }

    private fun disconnect() {
        val running = client ?: return
        client = null
        _traffic.value = null
        worker.execute { runCatching { running.disconnect() } }
    }

    /** The socket closed under us, e.g. while the core reloads: try again while someone watches. */
    private fun dropped(which: CommandClient) {
        if (client !== which) return
        client = null
        _traffic.value = null
        if (users > 0) main.postDelayed(retry, 2_000)
    }

    private class Listener : CommandClientHandler {
        @Volatile
        var owner: CommandClient? = null

        // The core sends every line; the level set in the app comes first, then lines above it are dropped.
        @Volatile
        private var maxLevel = 6

        override fun connected() = Unit

        override fun disconnected(message: String?) {
            val which = owner ?: return
            main.post { dropped(which) }
        }

        override fun clearLogs() {
            _logs.value = emptyList()
        }

        override fun writeLogs(iterator: LogIterator) {
            val lines = ArrayList<LogLine>()
            while (iterator.hasNext()) {
                val entry = iterator.next()
                if (entry.level <= maxLevel) lines.add(LogLine(entry.level, ANSI.replace(entry.message, "")))
            }
            if (lines.isNotEmpty()) _logs.update { (it + lines).takeLast(MAX_LINES) }
        }

        override fun writeStatus(message: StatusMessage) {
            _traffic.value = if (message.trafficAvailable) {
                Traffic(message.uplink, message.downlink, message.uplinkTotal, message.downlinkTotal)
            } else {
                null
            }
        }

        override fun initializeClashMode(modes: StringIterator, current: String) = Unit

        override fun setDefaultLogLevel(level: Int) {
            maxLevel = level
        }

        override fun updateClashMode(mode: String) = Unit

        override fun writeConnectionEvents(events: ConnectionEvents) = Unit

        override fun writeGroups(groups: OutboundGroupIterator) = Unit

        override fun writeOutbounds(outbounds: OutboundGroupItemIterator) = Unit
    }

    private const val MAX_LINES = 300
    private val ANSI = Regex("\u001B\\[[0-9;]*m")
}

/** Reads [CoreStatus] while [active] and this screen is resumed. */
@Composable
fun WatchCore(active: Boolean) {
    val lifecycle by LocalLifecycleOwner.current.lifecycle.currentStateAsState()
    val watching = active && lifecycle.isAtLeast(Lifecycle.State.RESUMED)
    DisposableEffect(watching) {
        if (watching) CoreStatus.acquire()
        onDispose { if (watching) CoreStatus.release() }
    }
}
