package app.litetunnel.vpn

import android.content.Context
import android.content.Intent
import androidx.core.content.ContextCompat

/** Start, stop and nudge the tunnel. The caller has done VpnService.prepare. */
object VpnController {
    fun start(context: Context) {
        ContextCompat.startForegroundService(context, Intent(context, LiteVpnService::class.java))
    }

    fun stop() {
        LiteVpnService.current?.stop()
    }

    /** The config on disk changed: the core picks it up without dropping the TUN. */
    fun reload() {
        LiteVpnService.current?.reload()
    }

    /** The app came to the front: the core resumes at once instead of on its next timer. */
    fun wake() {
        LiteVpnService.current?.wake()
    }
}
