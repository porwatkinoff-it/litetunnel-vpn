package app.litetunnel.ui

import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.litetunnel.AppModel
import app.litetunnel.BuildConfig
import app.litetunnel.R
import app.litetunnel.Screen
import app.litetunnel.core.AppSettings
import app.litetunnel.core.DnsServer
import app.litetunnel.core.LogLevel
import app.litetunnel.core.Ping
import app.litetunnel.core.PingDisplay
import app.litetunnel.core.PingMode
import app.litetunnel.core.RoutingMode
import app.litetunnel.core.RuleSets
import app.litetunnel.core.SortMode
import app.litetunnel.core.TunStack
import java.net.URI

private val Mono = LiteType.callout.copy(fontFamily = FontFamily.Monospace)

@Composable
fun SettingsScreen(model: AppModel) {
    val context = LocalContext.current
    val uri = LocalUriHandler.current
    val s = model.settings
    fun set(change: (AppSettings) -> AppSettings) = model.setSettings(change(model.settings))

    Scaffold(containerColor = Lite.colors.background, topBar = { BackBar("Настройки", model::pop) }) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            section(
                header = "Маршрутизация",
                footer = "«Россия напрямую»: российские сайты и адреса идут мимо VPN. Базы доменов и адресов скачиваются раз в неделю.",
            ) {
                ChoiceRow("Режим", RoutingMode.entries, s.routing, { it.title }) { mode -> set { it.copy(routing = mode) } }
                SwitchRow("Блокировать рекламу", s.blockAds) { on -> set { it.copy(blockAds = on) } }
                NavRow("Свои правила для доменов") { model.push(Screen.DomainRules) }
                if (RuleSets.needed(s).isNotEmpty()) {
                    ActionRow(
                        "Обновить базы",
                        enabled = !model.updatingRuleSets,
                        trailing = {
                            when {
                                model.updatingRuleSets -> Spinner()
                                !model.ruleSetsReady -> Text("не скачаны", color = Lite.colors.busy)
                            }
                        },
                    ) { model.updateRuleSets(force = true) }
                }
            }

            section(
                header = "DNS",
                footer = "UDP — адрес вроде 77.88.8.8; DoT и DoH — адрес или имя сервера, например 1.1.1.1 или dns.google.",
            ) {
                DnsRow("Через VPN", s.remoteDns) { dns -> set { it.copy(remoteDns = dns) } }
                DnsRow("Напрямую", s.directDns) { dns -> set { it.copy(directDns = dns) } }
            }

            section(
                header = "Подключение",
                footer = "Kill switch на Android включается в системе: «Постоянная VPN» и «Блокировать соединения без VPN». " +
                    "Тогда трафик не идёт мимо туннеля, даже пока он переподключается. " +
                    "Фрагментация TLS помогает, когда провайдер режет соединения по имени сайта.",
            ) {
                NavRow("Kill switch (настройки VPN)") {
                    runCatching { context.startActivity(Intent(Settings.ACTION_VPN_SETTINGS)) }
                }
                SwitchRow("Локальная сеть мимо VPN", s.excludeLocalNetworks) { on -> set { it.copy(excludeLocalNetworks = on) } }
                SwitchRow("IPv6", s.ipv6) { on -> set { it.copy(ipv6 = on) } }
                SwitchRow("Фрагментация TLS", s.tlsFragment) { on -> set { it.copy(tlsFragment = on) } }
            }

            section(
                header = "Энергосбережение",
                footer = "Пока устройство спит, ядро закрывает простаивающие соединения и останавливает таймеры. Просыпается, как только включается экран.",
            ) {
                SwitchRow("Умный сон", s.smartSleep) { on -> set { it.copy(smartSleep = on) } }
            }

            section(header = "Список серверов") {
                ChoiceRow("Сортировка", SortMode.entries, s.sortMode, { it.title }) { mode -> set { it.copy(sortMode = mode) } }
            }

            section(header = "Пинг", footer = s.pingMode.footer) {
                ChoiceRow("Способ", PingMode.entries, s.pingMode, { it.title }) { mode -> set { it.copy(pingMode = mode) } }
                PingUrlRow(s.pingUrl) { url -> set { it.copy(pingUrl = url) } }
                ChoiceRow("Вид", PingDisplay.entries, s.pingDisplay, { it.title }) { display -> set { it.copy(pingDisplay = display) } }
                SwitchRow("Пинг при открытии", s.pingOnOpen) { on -> set { it.copy(pingOnOpen = on) } }
                SwitchRow("Пинг после обновления подписки", s.pingOnUpdate) { on -> set { it.copy(pingOnUpdate = on) } }
            }

            section(header = "Дополнительно") {
                SwitchRow("Определять сайт (sniffing)", s.sniffing) { on -> set { it.copy(sniffing = on) } }
                ChoiceRow("Сетевой стек", TunStack.entries, s.tunStack, { it.title }) { stack -> set { it.copy(tunStack = stack) } }
                MtuRow(s.mtu) { mtu -> set { it.copy(mtu = mtu) } }
                ChoiceRow("Журнал", LogLevel.entries, s.logLevel, { it.raw }) { level -> set { it.copy(logLevel = level) } }
                NavRow("Открыть журнал") { model.push(Screen.Logs) }
            }

            section(header = "О приложении") {
                ValueRow("Версия", BuildConfig.VERSION_NAME)
                ActionRow("Ядро sing-box (GPLv3)") { runCatching { uri.openUri("https://github.com/SagerNet/sing-box") } }
            }
        }
    }
}

/** A server's protocol, and its address under it, as one row. */
@Composable
private fun DnsRow(label: String, value: DnsServer, onChange: (DnsServer) -> Unit) {
    Column {
        ChoiceRow(label, DnsServer.Kind.entries, value.kind, { it.title }) { kind -> onChange(value.copy(kind = kind)) }
        RowField(
            value = value.address,
            onCommit = { address -> if (address.isNotEmpty() && address != value.address) onChange(value.copy(address = address)) },
            placeholder = "Адрес",
            style = Mono,
            keyboardType = KeyboardType.Uri,
            modifier = Modifier.fillMaxWidth().padding(start = 16.dp, end = 16.dp, bottom = 12.dp),
        )
    }
}

@Composable
private fun MtuRow(value: Int, onChange: (Int) -> Unit) {
    Row(Modifier.row(), verticalAlignment = Alignment.CenterVertically) {
        Text("MTU", Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        RowField(
            value = if (value == 0) "" else value.toString(),
            onCommit = { text ->
                // 0 lets the core pick; anything else must fit a real link.
                val parsed = text.toIntOrNull() ?: 0
                val mtu = if (parsed == 0) 0 else parsed.coerceIn(1280, 9000)
                if (mtu != value) onChange(mtu)
            },
            placeholder = "авто",
            style = LiteType.body.tabular().copy(textAlign = TextAlign.End),
            keyboardType = KeyboardType.Number,
            filter = { it.filter(Char::isDigit).take(4) },
            modifier = Modifier.width(96.dp),
        )
    }
}

/** The address the proxy ping modes request; an empty field goes back to the default. */
@Composable
private fun PingUrlRow(value: String, onChange: (String) -> Unit) {
    Column(Modifier.row(), verticalArrangement = Arrangement.spacedBy(6.dp)) {
        Text("Адрес проверки")
        RowField(
            value = value,
            onCommit = { text ->
                val saved = if (text.isEmpty()) AppSettings.DEFAULT_PING_URL else Ping.testUrl(text).toString()
                if (saved != value) onChange(saved)
            },
            placeholder = AppSettings.DEFAULT_PING_URL,
            style = Mono,
            keyboardType = KeyboardType.Uri,
            modifier = Modifier.fillMaxWidth(),
        )
    }
}

@Composable
fun DomainRulesScreen(model: AppModel) {
    val s = model.settings
    Scaffold(containerColor = Lite.colors.background, topBar = { BackBar("Свои правила", model::pop) }) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            domainSection("Через VPN", "Например, youtube.com", s.proxyDomains) { list ->
                model.setSettings(model.settings.copy(proxyDomains = list))
            }
            domainSection("Напрямую", "Например, bank.ru", s.directDomains) { list ->
                model.setSettings(model.settings.copy(directDomains = list))
            }
            domainSection(
                "Блокировать",
                "Например, ads.example.com",
                s.blockDomains,
                footer = "Правило действует на домен и все его поддомены. Блокировка важнее остальных, «через VPN» важнее «напрямую».",
            ) { list -> model.setSettings(model.settings.copy(blockDomains = list)) }
        }
    }
}

private fun LazyListScope.domainSection(
    header: String,
    hint: String,
    domains: List<String>,
    footer: String? = null,
    onChange: (List<String>) -> Unit,
) = section(header = header, footer = footer) {
    for (domain in domains) {
        Row(Modifier.fillMaxWidth().padding(start = 16.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(domain, Modifier.weight(1f), style = Mono)
            IconButton(onClick = { onChange(domains - domain) }) {
                Icon(painterResource(R.drawable.ic_delete), contentDescription = "Удалить $domain", tint = Lite.colors.danger)
            }
        }
    }
    var text by rememberSaveable(header) { mutableStateOf("") }
    val domain = normalizeDomain(text)
    fun add() {
        val new = domain ?: return
        if (new !in domains) onChange(domains + new)
        text = ""
    }
    Row(Modifier.fillMaxWidth().padding(start = 16.dp, end = 4.dp), verticalAlignment = Alignment.CenterVertically) {
        PlainField(
            value = text,
            onValueChange = { text = it },
            placeholder = hint,
            keyboardType = KeyboardType.Uri,
            onDone = ::add,
            modifier = Modifier.weight(1f),
        )
        TextButton(onClick = ::add, enabled = domain != null) {
            Text("Добавить", color = if (domain != null) Lite.colors.accent else Lite.colors.secondary)
        }
    }
}

/** "https://www.YouTube.com/watch" → "youtube.com"; null for anything that is not a domain. */
fun normalizeDomain(input: String): String? {
    var s = input.trim().lowercase()
    if (s.contains("://")) s = runCatching { URI(s).host }.getOrNull() ?: return null
    s = s.removePrefix("www.").removePrefix(".")
    return s.takeIf { it.contains('.') && !it.contains(' ') && !it.contains('/') }
}
