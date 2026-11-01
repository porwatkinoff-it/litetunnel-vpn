package app.litetunnel.vpn

import android.content.Context
import android.os.Build
import app.litetunnel.BuildConfig
import app.litetunnel.LiteTunnelApp
import io.nekohasekai.libbox.Libbox
import io.nekohasekai.libbox.SetupOptions

/** The core library, set up once per process for the tunnel and the ping probes. */
internal object Core {
    private var ready = false

    fun setup(context: Context) {
        synchronized(this) {
            if (ready) return
            val app = context.applicationContext as LiteTunnelApp
            val store = app.store
            store.prepareDirectories()
            Libbox.setup(SetupOptions().apply {
                basePath = app.filesDir.path
                workingPath = store.workDirectory.path
                tempPath = store.tempDirectory.path
                fixAndroidStack = Build.VERSION.SDK_INT >= 28
                // 0: a unix socket in basePath, never a TCP port.
                commandServerListenPort = 0
                logMaxLines = 300
                crashReportSource = "Android"
                appVersion = BuildConfig.VERSION_CODE.toString()
                appMarketingVersion = BuildConfig.VERSION_NAME
            })
            ready = true
        }
    }
}
