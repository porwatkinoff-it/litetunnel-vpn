package app.litetunnel.ui

import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.defaultMinSize
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Scaffold
import androidx.compose.material3.SwipeToDismissBox
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.rememberSwipeToDismissBoxState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.semantics.stateDescription
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import app.litetunnel.AppModel
import app.litetunnel.R
import app.litetunnel.Screen
import app.litetunnel.core.PingDisplay
import app.litetunnel.core.Server
import app.litetunnel.core.Subscription
import app.litetunnel.core.Usage
import app.litetunnel.vpn.CoreStatus
import app.litetunnel.vpn.Traffic
import app.litetunnel.vpn.TunnelState
import app.litetunnel.vpn.TunnelStatus
import app.litetunnel.vpn.WatchCore
import kotlinx.coroutines.delay

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun HomeScreen(model: AppModel, onToggle: () -> Unit) {
    val tunnel by model.tunnel.collectAsState()
    val state = model.state
    WatchCore(tunnel.status == TunnelStatus.CONNECTED)

    Scaffold(
        containerColor = Lite.colors.background,
        topBar = {
            TopAppBar(
                title = { Text("LiteTunnel", style = LiteType.title) },
                actions = {
                    ToolbarIcon(R.drawable.ic_add, "Добавить") { model.push(Screen.Add) }
                    ToolbarIcon(
                        if (model.pinging) R.drawable.ic_bolt_fill else R.drawable.ic_bolt,
                        "Пинг всех",
                        enabled = !model.pinging && state.servers.isNotEmpty(),
                        onClick = model::pingAll,
                    )
                    ToolbarIcon(R.drawable.ic_settings, "Настройки") { model.push(Screen.Settings) }
                },
                colors = barColors(),
            )
        },
    ) { padding ->
        LazyColumn(
            Modifier.fillMaxSize(),
            contentPadding = PaddingValues(top = padding.calculateTopPadding(), bottom = padding.calculateBottomPadding() + 24.dp),
        ) {
            item(key = "connect") {
                Section { ConnectCard(tunnel, state.selectedServer, onToggle) }
            }

            if (state.servers.isEmpty() && state.subscriptions.isEmpty()) {
                item(key = "empty") {
                    Section { EmptyServers { model.push(Screen.Add) } }
                }
            }

            for (sub in state.subscriptions) {
                item(key = sub.id) {
                    val servers = state.servers(sub.id)
                    val collapsed = sub.id.toString() in model.collapsed
                    Section(headerContent = { SubscriptionHeader(model, sub, servers.size, collapsed) }) {
                        if (!collapsed) {
                            sub.announce?.takeIf { it.isNotBlank() }?.let { Announce(it) }
                            for (server in state.sorted(servers)) ServerRow(model, server)
                        }
                    }
                }
            }

            val manual = state.servers(null)
            if (manual.isNotEmpty()) {
                item(key = "manual") {
                    Section(header = "Мои серверы") {
                        for (server in state.sorted(manual)) {
                            // Keyed, so a swiped-away row does not leave its state to the next one.
                            androidx.compose.runtime.key(server.id) { SwipeToDelete({ model.removeServer(server.id) }) { ServerRow(model, server) } }
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun ToolbarIcon(icon: Int, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    IconButton(onClick = onClick, enabled = enabled) {
        Icon(
            painterResource(icon),
            contentDescription = label,
            tint = if (enabled || icon == R.drawable.ic_bolt_fill) Lite.colors.accent else Lite.colors.secondary,
        )
    }
}

// connect card

@Composable
private fun ConnectCard(tunnel: TunnelState, server: Server?, onToggle: () -> Unit) {
    val tint = when (tunnel.status) {
        TunnelStatus.CONNECTED -> Lite.colors.connected
        TunnelStatus.CONNECTING, TunnelStatus.STOPPING -> Lite.colors.busy
        TunnelStatus.STOPPED -> Lite.colors.secondary
    }
    val status = when (tunnel.status) {
        TunnelStatus.CONNECTED -> "Подключено"
        TunnelStatus.CONNECTING -> "Подключение…"
        TunnelStatus.STOPPING -> "Отключение…"
        TunnelStatus.STOPPED -> "Отключено"
    }
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(14.dp),
    ) {
        Box(
            Modifier
                .size(112.dp)
                .clip(CircleShape)
                .background(tint.copy(alpha = if (tunnel.isActive) 0.18f else 0.08f))
                .border(3.dp, tint, CircleShape)
                .clickable(enabled = tunnel.status != TunnelStatus.STOPPING, role = Role.Button, onClick = onToggle)
                .semantics { contentDescription = if (tunnel.isActive) "Отключить VPN" else "Подключить VPN" },
            contentAlignment = Alignment.Center,
        ) {
            Icon(painterResource(R.drawable.ic_power), contentDescription = null, tint = tint, modifier = Modifier.size(38.dp))
        }

        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
            Text(status, style = LiteType.title)
            val since = tunnel.since
            if (tunnel.status == TunnelStatus.CONNECTED && since != null) Elapsed(since)
        }

        Column(horizontalAlignment = Alignment.CenterHorizontally, verticalArrangement = Arrangement.spacedBy(2.dp)) {
            if (server != null) {
                Text(server.name, style = LiteType.callout, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(server.proto.title, style = LiteType.caption, color = Lite.colors.secondary)
            } else {
                Text("Сервер не выбран", style = LiteType.callout, color = Lite.colors.secondary)
            }
        }

        if (tunnel.status == TunnelStatus.CONNECTED) {
            val traffic by CoreStatus.traffic.collectAsState()
            traffic?.let { TrafficView(it) }
        }
    }
}

/** Ticks once a second, only while the app is on screen. */
@Composable
private fun Elapsed(since: Long) {
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    val lifecycle = LocalLifecycleOwner.current.lifecycle
    LaunchedEffect(since, lifecycle) {
        lifecycle.repeatOnLifecycle(Lifecycle.State.STARTED) {
            while (true) {
                now = System.currentTimeMillis()
                delay(1_000 - (now - since).mod(1_000L))
            }
        }
    }
    Text(Format.duration((now - since) / 1_000), style = LiteType.callout.tabular(), color = Lite.colors.secondary)
}

@Composable
private fun TrafficView(traffic: Traffic) {
    Row(horizontalArrangement = Arrangement.spacedBy(20.dp)) {
        TrafficColumn(R.drawable.ic_arrow_up, "Отдано", traffic.uplink, traffic.uplinkTotal)
        TrafficColumn(R.drawable.ic_arrow_down, "Получено", traffic.downlink, traffic.downlinkTotal)
    }
}

@Composable
private fun TrafficColumn(icon: Int, label: String, speed: Long, total: Long) {
    val style = LiteType.caption.tabular()
    Column(
        Modifier.defaultMinSize(minWidth = 110.dp).semantics(mergeDescendants = true) {
            contentDescription = "$label: ${Format.speed(speed)}, всего ${Format.size(total)}"
        },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(2.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Icon(painterResource(icon), contentDescription = null, tint = Lite.colors.text, modifier = Modifier.size(14.dp))
            Spacer(Modifier.width(2.dp))
            Text(Format.speed(speed), style = style)
        }
        Text(Format.size(total), style = style, color = Lite.colors.secondary)
    }
}

// lists

@Composable
private fun EmptyServers(onAdd: () -> Unit) {
    Column(
        Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 20.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Icon(painterResource(R.drawable.ic_dns), contentDescription = null, tint = Lite.colors.secondary, modifier = Modifier.size(40.dp))
        Text("Серверов пока нет", style = LiteType.headline)
        Text(
            "Добавьте ссылку на подписку или сервер: vless://, hy2://, trojan://, ss:// и другие.",
            style = LiteType.callout,
            color = Lite.colors.secondary,
            textAlign = TextAlign.Center,
        )
        PrimaryButton("Добавить", onClick = onAdd)
    }
}

@Composable
private fun SubscriptionHeader(model: AppModel, sub: Subscription, count: Int, collapsed: Boolean) {
    val refreshing = sub.id in model.refreshing
    val uri = LocalUriHandler.current
    var menu by remember { mutableStateOf(false) }
    var confirm by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxWidth().padding(top = 4.dp), verticalArrangement = Arrangement.spacedBy(4.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            Row(
                Modifier
                    .weight(1f)
                    .heightIn(min = 40.dp)
                    .clickable(onClickLabel = if (collapsed) "Показать серверы" else "Скрыть серверы") { model.toggleCollapsed(sub.id) }
                    .semantics { stateDescription = if (collapsed) "свёрнута" else "развёрнута" },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(
                    sub.name,
                    style = LiteType.headline,
                    color = Lite.colors.text,
                    maxLines = 1,
                    overflow = TextOverflow.Ellipsis,
                    modifier = Modifier.weight(1f, fill = false),
                )
                Spacer(Modifier.width(4.dp))
                Chevron(Modifier.rotate(if (collapsed) -90f else 0f))
            }
            HeaderButton(
                R.drawable.ic_bolt,
                "Пинг серверов",
                busy = model.pingingSubscription == sub.id,
                enabled = !model.pinging && count > 0,
            ) { model.pingSubscription(sub.id) }
            HeaderButton(R.drawable.ic_refresh, "Обновить подписку", busy = refreshing, enabled = !refreshing) { model.refresh(sub.id) }
            Box {
                HeaderButton(R.drawable.ic_more, "Ещё") { menu = true }
                DropdownMenu(expanded = menu, onDismissRequest = { menu = false }) {
                    DropdownMenuItem(text = { Text("Настроить") }, onClick = {
                        menu = false
                        model.push(Screen.SubscriptionDetail(sub.id))
                    })
                    sub.supportUrl?.let { url ->
                        DropdownMenuItem(text = { Text("Поддержка") }, onClick = {
                            menu = false
                            runCatching { uri.openUri(url) }
                        })
                    }
                    sub.webPageUrl?.let { url ->
                        DropdownMenuItem(text = { Text("Сайт провайдера") }, onClick = {
                            menu = false
                            runCatching { uri.openUri(url) }
                        })
                    }
                    HorizontalDivider(color = Lite.colors.separator)
                    DropdownMenuItem(text = { Text("Удалить подписку", color = Lite.colors.danger) }, onClick = {
                        menu = false
                        confirm = true
                    })
                }
            }
        }

        sub.usage?.let { Box(Modifier.padding(end = 12.dp)) { UsageLine(it) } }
        val error = sub.lastError
        val updated = sub.lastUpdated
        if (error != null) {
            Text(error, style = LiteType.caption, color = Lite.colors.danger, maxLines = 2, overflow = TextOverflow.Ellipsis)
        } else if (updated != null) {
            val tail = if (collapsed) " · ${Format.servers(count)}" else ""
            Text("Обновлена ${Format.relative(updated)}$tail", style = LiteType.caption, color = Lite.colors.secondary)
        }
    }

    if (confirm) {
        ConfirmDelete("Удалить подписку «${sub.name}» и её серверы?", onDismiss = { confirm = false }) {
            model.removeSubscription(sub.id)
        }
    }
}

/** A 24 icon in a 40 touch area; a small spinner while [busy]. */
@Composable
private fun HeaderButton(icon: Int, label: String, busy: Boolean = false, enabled: Boolean = true, onClick: () -> Unit) {
    IconButton(onClick = onClick, enabled = enabled && !busy, modifier = Modifier.size(40.dp)) {
        if (busy) {
            Spinner(16.dp)
        } else {
            Icon(
                painterResource(icon),
                contentDescription = label,
                tint = if (enabled) Lite.colors.accent else Lite.colors.secondary,
                modifier = Modifier.size(24.dp),
            )
        }
    }
}

@Composable
fun ConfirmDelete(text: String, onDismiss: () -> Unit, onConfirm: () -> Unit) {
    AlertDialog(
        onDismissRequest = onDismiss,
        text = { Text(text, style = LiteType.body) },
        confirmButton = {
            TextButton(onClick = {
                onDismiss()
                onConfirm()
            }) { Text("Удалить", color = Lite.colors.danger) }
        },
        dismissButton = { TextButton(onClick = onDismiss) { Text("Отмена", color = Lite.colors.accent) } },
        containerColor = Lite.colors.surface,
    )
}

@Composable
private fun Announce(text: String) {
    Row(Modifier.row(), verticalAlignment = Alignment.CenterVertically) {
        Icon(painterResource(R.drawable.ic_campaign), contentDescription = null, tint = Lite.colors.secondary, modifier = Modifier.size(18.dp))
        Spacer(Modifier.width(8.dp))
        Text(text, style = LiteType.footer, color = Lite.colors.secondary)
    }
}

/** Traffic used and left, and when the subscription ends. */
@Composable
fun UsageLine(usage: Usage) {
    val now = remember { java.time.Instant.now() }
    Column(verticalArrangement = Arrangement.spacedBy(3.dp)) {
        usage.fraction?.let { fraction ->
            LinearProgressIndicator(
                progress = { fraction.toFloat() },
                color = if (fraction > 0.9) Lite.colors.danger else Lite.colors.accent,
                trackColor = Lite.colors.separator,
                modifier = Modifier.fillMaxWidth().height(4.dp).clip(RoundedCornerShape(2.dp)),
                gapSize = 0.dp,
                drawStopIndicator = {},
            )
        }
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            val traffic = if (usage.total > 0) {
                "${Format.size(usage.used)} из ${Format.size(usage.total)}"
            } else {
                "${Format.size(usage.used)} · без лимита"
            }
            Text(traffic, style = LiteType.caption, color = Lite.colors.secondary, modifier = Modifier.weight(1f))
            usage.expire?.let { expire ->
                Text(
                    Format.expiry(expire, now),
                    style = LiteType.caption,
                    color = if (expire.isAfter(now)) Lite.colors.secondary else Lite.colors.danger,
                )
            }
        }
    }
}

@OptIn(ExperimentalFoundationApi::class)
@Composable
private fun ServerRow(model: AppModel, server: Server) {
    val context = LocalContext.current
    val selected = server.id == model.state.selectedServerId
    var menu by remember { mutableStateOf(false) }
    val openDetails = { model.push(Screen.ServerDetail(server.id)) }

    Box {
        Row(
            Modifier
                .background(Lite.colors.surface)
                .combinedClickable(
                    onClickLabel = "Выбрать",
                    onLongClickLabel = "Действия",
                    onLongClick = { menu = true },
                    onClick = { model.select(server.id) },
                )
                .semantics { if (selected) stateDescription = "выбран" }
                .fillMaxWidth()
                .heightIn(min = 52.dp)
                .padding(start = 16.dp, end = 4.dp, top = 6.dp, bottom = 6.dp),
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Icon(
                painterResource(if (selected) R.drawable.ic_check_circle else R.drawable.ic_unchecked),
                contentDescription = null,
                tint = if (selected) Lite.colors.accent else Lite.colors.secondary,
                modifier = Modifier.size(24.dp),
            )
            Spacer(Modifier.width(12.dp))
            Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                Text(server.name, style = LiteType.body, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(describe(server), style = LiteType.caption, color = Lite.colors.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            }
            Spacer(Modifier.width(8.dp))
            server.lastPing?.let { PingBadge(it, model.settings.pingDisplay) }
            IconButton(onClick = openDetails, modifier = Modifier.semantics { onClick(label = "Подробнее о сервере", action = null) }) {
                Icon(painterResource(R.drawable.ic_info), contentDescription = "Подробнее о сервере", tint = Lite.colors.accent)
            }
        }
        DropdownMenu(expanded = menu, onDismissRequest = { menu = false }, modifier = Modifier.align(Alignment.TopEnd)) {
            DropdownMenuItem(text = { Text("Подробнее") }, onClick = {
                menu = false
                openDetails()
            })
            DropdownMenuItem(text = { Text("Проверить пинг") }, enabled = !model.pinging, onClick = {
                menu = false
                model.ping(listOf(server))
            })
            server.shareLink?.let { link ->
                DropdownMenuItem(text = { Text("Копировать ссылку") }, onClick = {
                    menu = false
                    copySensitive(context, server.name, link)
                    model.show("Ссылка скопирована")
                })
            }
            if (server.subscriptionId == null) {
                DropdownMenuItem(text = { Text("Удалить", color = Lite.colors.danger) }, onClick = {
                    menu = false
                    model.removeServer(server.id)
                })
            }
        }
    }
}

/** Swipe from the end to delete, like a list row on iOS. */
@Composable
private fun SwipeToDelete(onDelete: () -> Unit, content: @Composable () -> Unit) {
    val state = rememberSwipeToDismissBoxState()
    SwipeToDismissBox(
        state = state,
        enableDismissFromStartToEnd = false,
        onDismiss = { onDelete() },
        backgroundContent = {
            Box(
                Modifier.fillMaxSize().background(Lite.colors.danger).padding(horizontal = 20.dp),
                contentAlignment = Alignment.CenterEnd,
            ) {
                Icon(painterResource(R.drawable.ic_delete), contentDescription = "Удалить", tint = Color.White)
            }
        },
    ) { content() }
}

/** "Протокол · транспорт". */
fun describe(server: Server): String = listOf(server.proto.title, server.detail).filter { it.isNotEmpty() }.joinToString(" · ")
