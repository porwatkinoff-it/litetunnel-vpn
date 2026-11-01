package app.litetunnel

import android.app.Application
import app.litetunnel.core.Store

class LiteTunnelApp : Application() {
    /** One for the UI and the tunnel: both run in this process. */
    val store by lazy { Store(filesDir).also { it.prepareDirectories() } }
}
