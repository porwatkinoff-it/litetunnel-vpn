package app.litetunnel.vpn

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.content.pm.ServiceInfo
import android.net.VpnService
import android.os.Build
import android.os.PowerManager
import androidx.core.content.ContextCompat
import app.litetunnel.LiteTunnelApp
import app.litetunnel.MainActivity
import app.litetunnel.R
import app.litetunnel.core.Store
import io.nekohasekai.libbox.CommandServer
import io.nekohasekai.libbox.CommandServerHandler
import io.nekohasekai.libbox.OverrideOptions
import io.nekohasekai.libbox.SystemProxyStatus
import java.util.concurrent.Executors
import java.util.concurrent.RejectedExecutionException

/**
 * Runs sing-box over a TUN. Start, reload and stop happen on one thread, in
 * order. Nothing here wakes up on a timer: the core's own timers are paused
 * in Doze when "Умный сон" is on.
 */
class LiteVpnService : VpnService() {
    private val worker = Executors.newSingleThreadExecutor { Thread(it, "tunnel") }
    private val store: Store get() = (application as LiteTunnelApp).store
    private lateinit var platform: AndroidPlatform
    private var server: CommandServer? = null
    private var idleReceiver: BroadcastReceiver? = null

    override fun onCreate() {
        super.onCreate()
        platform = AndroidPlatform(this)
        current = this
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        if (intent?.action == ACTION_STOP) {
            stop()
            return START_NOT_STICKY
        }
        // Within five seconds of startForegroundService, before any slow work.
        // The system's "Always-on VPN" starts the service this way too.
        showNotification(null)
        post(::start)
        return START_NOT_STICKY
    }

    override fun onRevoke() {
        // Another VPN took over, or the user turned this one off in Settings.
        stop()
    }

    override fun onDestroy() {
        if (current === this) current = null
        post(::release)
        worker.shutdown()
        super.onDestroy()
    }

    fun stop() {
        Tunnel.stopping()
        post(::shutdown)
    }

    fun reload() = post {
        val running = server ?: return@post
        val config = store.readConfig()
        if (config == null) {
            shutdown()
            return@post
        }
        try {
            running.startOrReloadService(config, OverrideOptions())
            val name = store.loadState().selectedServer?.name
            Tunnel.connected(name)
            showNotification(name)
        } catch (e: Exception) {
            Tunnel.fail(e.message ?: "Не удалось применить настройки")
            shutdown()
        }
    }

    fun wake() = post { server?.wake() }

    /** After onDestroy the worker is gone; a late call has nothing left to do. */
    private fun post(task: () -> Unit) {
        try {
            worker.execute(task)
        } catch (e: RejectedExecutionException) {
            Unit
        }
    }

    private fun start() {
        if (server != null) {
            reload()
            return
        }
        Tunnel.connecting()
        try {
            if (prepare(this) != null) throw Exception("Нет разрешения на VPN")
            val config = store.readConfig() ?: throw Exception("Сервер не выбран")
            Core.setup(this)
            val running = CommandServer(handler, platform)
            server = running
            running.start()
            running.startOrReloadService(config, OverrideOptions())
            watchIdle()
            val name = store.loadState().selectedServer?.name
            Tunnel.connected(name)
            showNotification(name)
        } catch (e: Exception) {
            Tunnel.fail(e.message ?: "Не удалось подключиться")
            shutdown()
        }
    }

    private fun shutdown() {
        release()
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun release() {
        idleReceiver?.let { runCatching { unregisterReceiver(it) } }
        idleReceiver = null
        server?.let { running ->
            runCatching { running.closeService() }
            runCatching { running.close() }
        }
        server = null
        platform.close()
        Tunnel.stopped()
    }

    /** Doze: the core closes idle connections and stops its timers until the device wakes. */
    private fun watchIdle() {
        val settings = store.loadState().settings
        if (!settings.smartSleep) return
        val power = getSystemService(PowerManager::class.java)
        val receiver = object : BroadcastReceiver() {
            override fun onReceive(context: Context, intent: Intent) {
                val idle = power.isDeviceIdleMode
                post { if (idle) server?.pause() else server?.wake() }
            }
        }
        val filter = IntentFilter(PowerManager.ACTION_DEVICE_IDLE_MODE_CHANGED)
        ContextCompat.registerReceiver(this, receiver, filter, ContextCompat.RECEIVER_NOT_EXPORTED)
        idleReceiver = receiver
    }

    private fun showNotification(serverName: String?) {
        val manager = getSystemService(NotificationManager::class.java)
        if (manager.getNotificationChannel(CHANNEL) == null) {
            manager.createNotificationChannel(
                NotificationChannel(CHANNEL, "VPN", NotificationManager.IMPORTANCE_LOW).apply {
                    setShowBadge(false)
                },
            )
        }
        val open = PendingIntent.getActivity(
            this, 0, Intent(this, MainActivity::class.java), PendingIntent.FLAG_IMMUTABLE,
        )
        val stop = PendingIntent.getService(
            this, 1, Intent(this, LiteVpnService::class.java).setAction(ACTION_STOP), PendingIntent.FLAG_IMMUTABLE,
        )
        val since = Tunnel.state.value.since
        val notification = Notification.Builder(this, CHANNEL)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentTitle(if (serverName != null) "Подключено: $serverName" else "Подключение…")
            .setContentIntent(open)
            .setOngoing(true)
            .setShowWhen(since != null)
            .setWhen(since ?: System.currentTimeMillis())
            .setCategory(Notification.CATEGORY_SERVICE)
            .addAction(Notification.Action.Builder(null, "Отключить", stop).build())
            .build()
        if (Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFICATION_ID, notification, ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFICATION_ID, notification)
        }
    }

    private val handler = object : CommandServerHandler {
        override fun serviceStop() = stop()

        override fun serviceReload() {
            reload()
        }

        override fun getSystemProxyStatus() = SystemProxyStatus()

        override fun setSystemProxyEnabled(enabled: Boolean) = Unit

        override fun writeDebugMessage(message: String) = Unit

        override fun connectSSHAgent(): Int = throw Exception("Не поддерживается")

        override fun triggerNativeCrash() = throw Exception("Не поддерживается")
    }

    companion object {
        const val ACTION_STOP = "app.litetunnel.STOP"
        private const val CHANNEL = "vpn"
        private const val NOTIFICATION_ID = 1

        @Volatile
        var current: LiteVpnService? = null
            private set
    }
}
