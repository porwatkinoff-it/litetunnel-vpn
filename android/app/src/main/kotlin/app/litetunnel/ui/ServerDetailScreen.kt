package app.litetunnel.ui

import android.content.Intent
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.rememberScrollState
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.litetunnel.AppModel
import app.litetunnel.core.Server
import java.util.UUID

@Composable
fun ServerDetailScreen(model: AppModel, id: UUID) {
    val context = LocalContext.current
    val server = model.state.servers.firstOrNull { it.id == id }
    var showJson by remember { mutableStateOf(false) }

    Scaffold(containerColor = Lite.colors.background, topBar = { BackBar(server?.name ?: "Сервер", model::pop) }) { padding ->
        if (server == null) {
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                Text("Сервер удалён", color = Lite.colors.secondary)
            }
            return@Scaffold
        }
        val subscription = server.subscriptionId?.let { sid -> model.state.subscriptions.firstOrNull { it.id == sid } }
        val members = server.members

        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            section {
                if (members.isEmpty()) {
                    ValueRow("Протокол", server.proto.title)
                    ValueRow("Адрес", "${server.host}:${server.port}")
                    if (server.detail.isNotEmpty()) ValueRow("Транспорт", server.detail)
                }
                val ping = server.lastPing
                if (server.id in model.pingingIds) {
                    Row(Modifier.row(), verticalAlignment = Alignment.CenterVertically) {
                        Text("Пинг", Modifier.weight(1f))
                        PingSpinner()
                    }
                } else {
                    ValueRow("Пинг", ping?.label ?: "—", ping?.color ?: Lite.colors.secondary)
                }
                subscription?.let { ValueRow("Подписка", it.name) }
            }

            if (members.isNotEmpty()) {
                section(
                    header = "Серверы: ${members.size}",
                    footer = "Ядро само выбирает самый быстрый и переходит на другой, если этот перестал отвечать. Проверка раз в 10 минут.",
                ) {
                    for (member in members) {
                        Column(Modifier.fillMaxWidth().padding(RowPadding), verticalArrangement = Arrangement.spacedBy(2.dp)) {
                            Text(member.name, style = LiteType.body, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(member.subtitle, style = LiteType.caption, color = Lite.colors.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        }
                    }
                }
            }

            section {
                val selected = model.state.selectedServerId == server.id
                ActionRow(if (selected) "Выбран" else "Выбрать этот сервер", enabled = !selected) { model.select(server.id) }
                ActionRow("Проверить пинг", enabled = !model.pinging, trailing = { if (model.pinging) Spinner() }) {
                    model.ping(listOf(server))
                }
                server.shareLink?.let { link ->
                    ActionRow("Копировать ссылку") {
                        copySensitive(context, server.name, link)
                        model.show("Ссылка скопирована")
                    }
                    ActionRow("Поделиться") {
                        val send = Intent(Intent.ACTION_SEND).setType("text/plain").putExtra(Intent.EXTRA_TEXT, link)
                        context.startActivity(Intent.createChooser(send, server.name))
                    }
                }
            }

            section(footer = "Здесь есть ключи доступа к серверу. Не публикуйте их.") {
                ActionRow(if (showJson) "Скрыть настройки для ядра" else "Настройки для ядра (JSON)") { showJson = !showJson }
                if (showJson) JsonBlock(server)
            }

            if (server.subscriptionId == null) {
                section {
                    ActionRow("Удалить сервер", color = Lite.colors.danger) {
                        model.pop()
                        model.removeServer(server.id)
                    }
                }
            }
        }
    }
}

@Composable
private fun JsonBlock(server: Server) {
    val json = remember(server.outbound) { server.outbound.jsonString(pretty = true) }
    Text(
        json,
        style = LiteType.caption,
        fontFamily = FontFamily.Monospace,
        modifier = Modifier.horizontalScroll(rememberScrollState()).padding(RowPadding),
    )
}
