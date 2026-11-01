package app.litetunnel

import android.Manifest
import android.app.Activity
import android.content.Intent
import android.content.pm.PackageManager
import android.net.VpnService
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.BackHandler
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.material3.Snackbar
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.Surface
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.core.content.ContextCompat
import androidx.lifecycle.lifecycleScope
import app.litetunnel.ui.AddScreen
import app.litetunnel.ui.DomainRulesScreen
import app.litetunnel.ui.HomeScreen
import app.litetunnel.ui.Lite
import app.litetunnel.ui.LiteTheme
import app.litetunnel.ui.LogsScreen
import app.litetunnel.ui.ServerDetailScreen
import app.litetunnel.ui.SettingsScreen
import app.litetunnel.ui.SubscriptionScreen
import kotlinx.coroutines.launch

class MainActivity : ComponentActivity() {
    private val model by viewModels<AppModel>()

    private val vpnPermission = registerForActivityResult(ActivityResultContracts.StartActivityForResult()) { result ->
        if (result.resultCode == Activity.RESULT_OK) model.connect() else model.show("Без разрешения VPN не включится", error = true)
    }

    // The answer does not matter: the tunnel runs either way, only its notification hides.
    private val notificationPermission = registerForActivityResult(ActivityResultContracts.RequestPermission()) {}

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { LiteTheme { App(model, ::toggle) } }
        if (savedInstanceState == null) handle(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handle(intent)
    }

    override fun onStart() {
        super.onStart()
        model.becameActive()
    }

    private fun handle(intent: Intent?) {
        when (intent?.action) {
            Intent.ACTION_VIEW -> intent.dataString?.let(model::open)
            Intent.ACTION_SEND -> intent.getStringExtra(Intent.EXTRA_TEXT)?.let { text ->
                lifecycleScope.launch { model.import(text) }
            }
        }
    }

    private fun toggle() {
        if (model.tunnel.value.isActive) {
            model.disconnect()
            return
        }
        if (model.state.selectedServer == null) {
            model.show("Сначала выберите сервер", error = true)
            return
        }
        askForNotifications()
        val consent = VpnService.prepare(this)
        if (consent != null) vpnPermission.launch(consent) else model.connect()
    }

    private fun askForNotifications() {
        if (Build.VERSION.SDK_INT < 33) return
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED) return
        val prefs = getSharedPreferences("ui", MODE_PRIVATE)
        if (prefs.getBoolean(ASKED_NOTIFICATIONS, false)) return
        prefs.edit().putBoolean(ASKED_NOTIFICATIONS, true).apply()
        notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
    }

    private companion object {
        const val ASKED_NOTIFICATIONS = "askedNotifications"
    }
}

@Composable
private fun App(model: AppModel, onToggle: () -> Unit) {
    val snackbar = remember { SnackbarHostState() }
    LaunchedEffect(Unit) {
        model.messages.collect { message ->
            snackbar.currentSnackbarData?.dismiss()
            snackbar.showSnackbar(message.text, withDismissAction = message.isError)
        }
    }
    BackHandler(enabled = model.screens.size > 1) { model.pop() }

    Surface(Modifier.fillMaxSize(), color = Lite.colors.background) {
        Box(Modifier.fillMaxSize()) {
            when (val screen = model.screens.last()) {
                Screen.Home -> HomeScreen(model, onToggle)
                Screen.Add -> AddScreen(model)
                Screen.Settings -> SettingsScreen(model)
                Screen.DomainRules -> DomainRulesScreen(model)
                Screen.Logs -> LogsScreen(model)
                is Screen.ServerDetail -> ServerDetailScreen(model, screen.id)
                is Screen.SubscriptionDetail -> SubscriptionScreen(model, screen.id)
            }
            SnackbarHost(snackbar, Modifier.align(Alignment.BottomCenter).navigationBarsPadding().imePadding()) { data ->
                Snackbar(data)
            }
        }
    }
}
