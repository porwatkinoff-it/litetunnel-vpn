package app.litetunnel

import android.annotation.SuppressLint
import android.app.Application
import android.content.Context
import android.os.Build
import android.provider.Settings
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.litetunnel.core.AppSettings
import app.litetunnel.core.AppState
import app.litetunnel.core.DeviceInfo
import app.litetunnel.core.ImportItem
import app.litetunnel.core.Importer
import app.litetunnel.core.Ping
import app.litetunnel.core.PingMode
import app.litetunnel.core.PingResult
import app.litetunnel.core.RuleSetUpdater
import app.litetunnel.core.RuleSets
import app.litetunnel.core.Server
import app.litetunnel.core.Subscription
import app.litetunnel.core.SubscriptionClient
import app.litetunnel.core.SubscriptionError
import app.litetunnel.vpn.ProxyProbe
import app.litetunnel.vpn.Tunnel
import app.litetunnel.vpn.VpnController
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import java.net.ConnectException
import java.net.NoRouteToHostException
import java.net.SocketTimeoutException
import java.net.URI
import java.net.UnknownHostException
import java.time.Instant
import java.util.UUID
import javax.net.ssl.SSLException

/** Where the app is; Back pops the last one. */
sealed interface Screen {
    data object Home : Screen
    data object Add : Screen
    data object Settings : Screen
    data object DomainRules : Screen
    data object Logs : Screen
    data class ServerDetail(val id: UUID) : Screen
    data class SubscriptionDetail(val id: UUID) : Screen
}

/**
 * The app's state: servers, subscriptions and settings, saved where the
 * tunnel reads its config.
 *
 * Nothing here runs on a timer. Subscriptions update when the app opens and
 * their interval has passed, rule-sets once a week, and pings only when asked.
 */
class AppModel(app: Application) : AndroidViewModel(app) {
    private val store = (app as LiteTunnelApp).store
    private val prefs = app.getSharedPreferences("ui", Context.MODE_PRIVATE)

    /** Saves in order, one at a time. */
    @OptIn(kotlinx.coroutines.ExperimentalCoroutinesApi::class)
    private val disk = Dispatchers.IO.limitedParallelism(1)

    private val loaded = store.loadState()
    var state by mutableStateOf(loaded.syncAutoGroups())
        private set
    var refreshing by mutableStateOf(emptySet<UUID>())
        private set
    var pinging by mutableStateOf(false)
        private set

    /** The subscription whose header ping button started the current ping. */
    var pingingSubscription by mutableStateOf<UUID?>(null)
        private set
    var updatingRuleSets by mutableStateOf(false)
        private set
    var ruleSetsReady by mutableStateOf(true)
        private set
    var collapsed by mutableStateOf(prefs.getStringSet(COLLAPSED, null).orEmpty().toSet())
        private set
    var screens by mutableStateOf(listOf<Screen>(Screen.Home))
        private set

    data class Message(val text: String, val isError: Boolean)

    private val _messages = MutableSharedFlow<Message>(extraBufferCapacity = 8)
    val messages: SharedFlow<Message> = _messages

    val tunnel = Tunnel.state

    private val client by lazy { SubscriptionClient(deviceInfo().headers) }

    init {
        if (state !== loaded) viewModelScope.launch { persist(state) }
        viewModelScope.launch { Tunnel.errors.collect { show(it, error = true) } }
        viewModelScope.launch { checkRuleSets() }
    }

    val settings: AppSettings get() = state.settings

    fun show(text: String, error: Boolean = false) {
        _messages.tryEmit(Message(text, error))
    }

    // navigation

    fun push(screen: Screen) {
        screens = screens + screen
    }

    fun pop() {
        if (screens.size > 1) screens = screens.dropLast(1)
    }

    // saving

    /**
     * Applies a change, saves it and, when the config changed while the
     * tunnel is up, reloads the core in place.
     */
    private fun commit(change: (AppState) -> AppState) {
        val next = change(state)
        if (next == state) return
        state = next
        viewModelScope.launch {
            if (persist(next)) applyConfig(next)
        }
    }

    private suspend fun persist(snapshot: AppState): Boolean = withContext(disk) {
        try {
            store.save(snapshot)
            true
        } catch (e: Exception) {
            show("Не удалось сохранить: ${e.message}", error = true)
            false
        }
    }

    private suspend fun applyConfig(snapshot: AppState) = withContext(disk) {
        val before = store.readConfig()
        try {
            store.writeConfig(snapshot)
        } catch (e: Exception) {
            show("Не удалось записать конфигурацию: ${e.message}", error = true)
            return@withContext
        }
        val after = store.readConfig()
        if (before == after || !Tunnel.state.value.isActive) return@withContext
        // No server left to run: stop; otherwise the core swaps configs in place.
        if (after == null) VpnController.stop() else VpnController.reload()
    }

    // connection

    /** Call once the VPN permission is granted. */
    fun connect() {
        val snapshot = state
        if (snapshot.selectedServer == null) {
            show("Сначала выберите сервер", error = true)
            return
        }
        viewModelScope.launch {
            withContext(disk) { store.writeConfig(snapshot) }
            VpnController.start(getApplication())
        }
    }

    fun disconnect() = VpnController.stop()

    fun select(id: UUID) {
        if (state.selectedServerId == id) return
        commit { it.copy(selectedServerId = id) }
    }

    // settings

    fun setSettings(new: AppSettings) {
        val old = state.settings
        if (old == new) return
        commit { it.copy(settings = new) }
        if (RuleSets.needed(old) != RuleSets.needed(new)) {
            viewModelScope.launch {
                updateRuleSetsNow(force = false)
                checkRuleSets()
            }
        }
    }

    fun toggleCollapsed(id: UUID) {
        val key = id.toString()
        collapsed = if (key in collapsed) collapsed - key else collapsed + key
        prefs.edit().putStringSet(COLLAPSED, collapsed).apply()
    }

    // import

    /** Pasted text or a typed link. True when something was added. */
    suspend fun import(text: String): Boolean = handle(Importer.classify(text))

    /** A `litetunnel://` link. */
    fun open(link: String) {
        viewModelScope.launch { handle(Importer.deepLink(link)) }
    }

    private suspend fun handle(item: ImportItem): Boolean = when (item) {
        is ImportItem.Subscription -> {
            val existing = state.subscriptions.firstOrNull { it.url == item.url }
            if (existing != null) {
                show("Эта подписка уже добавлена, обновляю")
                if (refreshNow(existing.id)) pingAfterUpdate(listOf(existing.id))
            } else {
                val host = runCatching { URI(item.url).host }.getOrNull()
                val sub = Subscription(name = item.name ?: host ?: "Подписка", url = item.url)
                commit { it.copy(subscriptions = it.subscriptions + sub) }
                if (refreshNow(sub.id)) pingAfterUpdate(listOf(sub.id))
            }
            true
        }
        is ImportItem.Servers -> {
            var added = emptyList<Server>()
            commit { current ->
                val (next, list) = current.addManual(item.servers)
                added = list
                next
            }
            var text = if (added.isEmpty()) "Эти серверы уже есть" else "Добавлено серверов: ${added.size}"
            if (item.skipped.isNotEmpty()) {
                text += ", пропущено: ${item.skipped.size} (${item.skipped.map { it.reason }.distinct().joinToString("; ")})"
            }
            show(text, error = added.isEmpty())
            added.isNotEmpty()
        }
        is ImportItem.Nothing -> {
            show(item.reason, error = true)
            false
        }
    }

    // servers and subscriptions

    fun removeServer(id: UUID) = commit { it.removeServer(id) }

    fun removeSubscription(id: UUID) = commit { it.removeSubscription(id) }

    /** Takes only what the settings screen edits, keeping what a refresh may have written meanwhile. */
    fun editSubscription(id: UUID, name: String, url: String, intervalHours: Int, userAgent: String?) =
        commit { current ->
            current.updateSubscription(id) {
                it.copy(
                    name = name.ifBlank { it.name },
                    url = url,
                    updateIntervalHours = intervalHours,
                    userAgent = userAgent,
                )
            }
        }

    fun refresh(id: UUID) {
        viewModelScope.launch { if (refreshNow(id)) pingAfterUpdate(listOf(id)) }
    }

    /** True when the subscription was fetched and its servers replaced. */
    private suspend fun refreshNow(id: UUID): Boolean {
        val sub = state.subscriptions.firstOrNull { it.id == id } ?: return false
        if (id in refreshing) return false
        refreshing = refreshing + id
        return try {
            val result = withContext(Dispatchers.IO) { client.fetch(sub) }
            commit { it.apply(result, id, Instant.now()) }
            true
        } catch (e: CancellationException) {
            throw e
        } catch (e: Exception) {
            val message = describe(e)
            commit { current -> current.updateSubscription(id) { it.copy(lastError = message) } }
            show("${sub.name}: $message", error = true)
            false
        } finally {
            refreshing = refreshing - id
        }
    }

    /**
     * Subscriptions whose interval has passed. Called when the app opens;
     * `ping` is off when the open pings every server anyway.
     */
    private suspend fun refreshDue(ping: Boolean) {
        val now = Instant.now()
        val updated = state.subscriptions.filter { it.isDue(now) }.map { it.id }.filter { refreshNow(it) }
        if (ping) pingAfterUpdate(updated)
    }

    /** Launched, not awaited: an import returns once the servers are in. */
    private fun pingAfterUpdate(ids: List<UUID>) {
        if (!state.settings.pingOnUpdate || ids.isEmpty()) return
        ping(ids.flatMap { state.servers(it) })
    }

    private fun describe(e: Exception): String = when (e) {
        is SubscriptionError -> e.message ?: "Ошибка подписки"
        is UnknownHostException, is ConnectException, is NoRouteToHostException -> "Нет связи с сервером подписки"
        is SocketTimeoutException -> "Сервер подписки не ответил вовремя"
        is SSLException -> "Не удалось установить защищённое соединение с сервером подписки"
        else -> e.message ?: e.javaClass.simpleName
    }

    // ping

    fun ping(servers: List<Server>) {
        viewModelScope.launch { pingNow(servers) }
    }

    fun pingAll() = ping(state.servers)

    fun pingSubscription(id: UUID) {
        if (pinging) return
        pingingSubscription = id
        viewModelScope.launch {
            try {
                pingNow(state.servers(id))
            } finally {
                pingingSubscription = null
            }
        }
    }

    private suspend fun pingNow(rows: List<Server>) {
        val servers = state.pingTargets(rows)
        if (pinging || servers.isEmpty()) return
        pinging = true
        try {
            val settings = state.settings
            // Posted to the main thread in order, ahead of the code after measureAll.
            val report: (UUID, PingResult?) -> Unit = { id, result -> viewModelScope.launch(Dispatchers.Main) { setPing(id, result) } }
            val failure = withContext(Dispatchers.IO) {
                when (settings.pingMode) {
                    PingMode.TCP -> Ping.measureAll(servers, Tunnel.pinger(), onResult = report)
                    PingMode.ICMP -> Ping.measureAll(servers, Tunnel.icmpPinger(), onResult = report)
                    PingMode.REAL, PingMode.PROXY_GET, PingMode.PROXY_HEAD -> try {
                        ProxyProbe.measureAll(getApplication(), servers, settings, report)
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: Exception) {
                        Ping.measureAll(servers, Tunnel.pinger(), onResult = report)
                        return@withContext e.message?.lineSequence()?.firstOrNull()?.take(160) ?: e.javaClass.simpleName
                    }
                }
                null
            }
            failure?.let { show("Пинг через сервер не запустился: ${it.trimEnd('.')}. Показан TCP.", error = true) }
            // The config does not hold pings: save the state only.
            persist(state)
        } finally {
            pinging = false
        }
    }

    private fun setPing(id: UUID, result: PingResult?) {
        state = state.settingPing(result, id)
    }

    // rule-sets

    fun updateRuleSets(force: Boolean) {
        viewModelScope.launch { updateRuleSetsNow(force) }
    }

    private suspend fun updateRuleSetsNow(force: Boolean) {
        val tags = RuleSets.needed(state.settings)
        val directory = store.ruleSetDirectory
        if (tags.isEmpty() || updatingRuleSets) return
        if (!force && !withContext(Dispatchers.IO) { RuleSetUpdater.isDue(tags, directory) }) return
        updatingRuleSets = true
        try {
            val failed = withContext(Dispatchers.IO) { RuleSetUpdater.update(tags, directory) }
            applyConfig(state)
            checkRuleSets()
            if (failed.isNotEmpty()) {
                show("Не удалось скачать базы: ${failed.joinToString(", ")}", error = true)
            } else if (force) {
                show("Базы маршрутизации обновлены")
            }
        } finally {
            updatingRuleSets = false
        }
    }

    private suspend fun checkRuleSets() {
        val needed = RuleSets.needed(state.settings)
        ruleSetsReady = withContext(Dispatchers.IO) { RuleSets.available(store.ruleSetDirectory).containsAll(needed) }
    }

    // lifecycle

    private var activating = false

    /** The app came to the screen. */
    fun becameActive() {
        if (activating) return
        activating = true
        viewModelScope.launch {
            try {
                if (Tunnel.state.value.isActive) VpnController.wake()
                refreshDue(ping = !state.settings.pingOnOpen)
                updateRuleSetsNow(force = false)
                if (state.settings.pingOnOpen) pingNow(state.servers)
            } finally {
                activating = false
            }
        }
    }

    /**
     * What subscription requests say about this device. ANDROID_ID is per app
     * and survives a reinstall, so the provider sees the same device.
     */
    @SuppressLint("HardwareIds")
    private fun deviceInfo(): DeviceInfo {
        val app: Application = getApplication()
        val seed = Settings.Secure.getString(app.contentResolver, Settings.Secure.ANDROID_ID) ?: app.packageName
        return DeviceInfo(DeviceInfo.hwid(seed), "Android", Build.VERSION.RELEASE, Build.MODEL)
    }

    private companion object {
        const val COLLAPSED = "collapsed"
    }
}
