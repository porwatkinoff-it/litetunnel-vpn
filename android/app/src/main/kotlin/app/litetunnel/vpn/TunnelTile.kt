package app.litetunnel.vpn

import android.annotation.SuppressLint
import android.app.PendingIntent
import android.content.Intent
import android.net.VpnService
import android.os.Build
import android.service.quicksettings.Tile
import android.service.quicksettings.TileService
import app.litetunnel.LiteTunnelApp
import app.litetunnel.MainActivity
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.cancel
import kotlinx.coroutines.flow.launchIn
import kotlinx.coroutines.flow.onEach

/**
 * The Quick Settings switch: connects to the server picked in the app and
 * disconnects, without opening it. It listens to the tunnel only while the
 * panel is open, so it costs nothing the rest of the time. A long press opens
 * the app (the system's own shortcut to the tile's app).
 */
class TunnelTile : TileService() {
    private var scope: CoroutineScope? = null

    override fun onStartListening() {
        super.onStartListening()
        val listening = CoroutineScope(Dispatchers.Main + Job())
        scope = listening
        Tunnel.state.onEach(::show).launchIn(listening)
    }

    override fun onStopListening() {
        scope?.cancel()
        scope = null
        super.onStopListening()
    }

    override fun onClick() {
        super.onClick()
        val state = Tunnel.state.value
        when {
            state.isActive -> VpnController.stop()
            state.status == TunnelStatus.STOPPING -> Unit
            // The system asks for the VPN permission once, in the app.
            VpnService.prepare(this) != null -> openApp()
            (application as LiteTunnelApp).store.readConfig() == null -> openApp()
            else -> VpnController.start(this)
        }
    }

    private fun show(state: TunnelState) {
        val tile = qsTile ?: return
        tile.state = when (state.status) {
            TunnelStatus.CONNECTED, TunnelStatus.CONNECTING -> Tile.STATE_ACTIVE
            else -> Tile.STATE_INACTIVE
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            tile.subtitle = when (state.status) {
                TunnelStatus.CONNECTED -> state.serverName ?: "Подключено"
                TunnelStatus.CONNECTING -> "Подключение…"
                TunnelStatus.STOPPING -> "Отключение…"
                TunnelStatus.STOPPED -> "Отключено"
            }
        }
        tile.updateTile()
    }

    @SuppressLint("StartActivityAndCollapseDeprecated")
    private fun openApp() {
        val intent = Intent(this, MainActivity::class.java).addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.UPSIDE_DOWN_CAKE) {
            startActivityAndCollapse(PendingIntent.getActivity(this, 0, intent, PendingIntent.FLAG_IMMUTABLE))
        } else {
            @Suppress("DEPRECATION")
            startActivityAndCollapse(intent)
        }
    }
}
