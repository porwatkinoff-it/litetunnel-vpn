package app.litetunnel.ui

import androidx.compose.foundation.BorderStroke
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.unit.dp
import app.litetunnel.AppModel
import app.litetunnel.core.SubscriptionClient
import java.net.URI
import java.util.UUID

@Composable
fun SubscriptionScreen(model: AppModel, id: UUID) {
    val sub = model.state.subscriptions.firstOrNull { it.id == id }
    if (sub == null) {
        Scaffold(containerColor = Lite.colors.background, topBar = { BackBar("Подписка", model::pop) }) { padding ->
            Box(Modifier.fillMaxSize().padding(padding), contentAlignment = Alignment.Center) {
                Text("Подписка удалена", color = Lite.colors.secondary)
            }
        }
        return
    }
    val uri = LocalUriHandler.current
    var name by rememberSaveable { mutableStateOf(sub.name) }
    var url by rememberSaveable { mutableStateOf(sub.url) }
    var interval by rememberSaveable { mutableIntStateOf(sub.updateIntervalHours) }
    var userAgent by rememberSaveable { mutableStateOf(sub.userAgent.orEmpty()) }
    var confirm by remember { mutableStateOf(false) }
    val urlValid = validUrl(url)
    val busy = sub.id in model.refreshing

    fun save() = model.editSubscription(sub.id, name.trim().ifEmpty { sub.name }, url.trim(), interval, userAgent.trim().ifEmpty { null })

    Scaffold(
        containerColor = Lite.colors.background,
        topBar = {
            BackBar(sub.name, model::pop) {
                TextButton(
                    enabled = urlValid,
                    onClick = {
                        save()
                        model.pop()
                    },
                ) { Text("Готово", color = if (urlValid) Lite.colors.accent else Lite.colors.secondary) }
            }
        },
    ) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            section(header = "Подписка") {
                PlainField(name, { name = it }, Modifier.padding(RowPadding), placeholder = "Название")
                PlainField(
                    url,
                    { url = it },
                    Modifier.padding(RowPadding),
                    placeholder = "https://…",
                    style = LiteType.callout.copy(fontFamily = FontFamily.Monospace),
                    keyboardType = KeyboardType.Uri,
                    singleLine = false,
                    maxLines = 4,
                )
                if (!urlValid) {
                    Text("Нужен адрес http:// или https://", style = LiteType.caption, color = Lite.colors.danger, modifier = Modifier.padding(RowPadding))
                }
            }

            section(
                header = "Обновление",
                // No background refresh on Android: it would cost battery for nothing.
                footer = "Обновляется при открытии приложения, если прошло больше указанного времени. " +
                    "Некоторые провайдеры отдают разные списки разным клиентам: тогда поможет другой User-Agent.",
            ) {
                Row(Modifier.row(), verticalAlignment = Alignment.CenterVertically) {
                    Text("Обновлять каждые", Modifier.weight(1f))
                    Text("$interval ч", style = LiteType.body.tabular(), color = Lite.colors.secondary)
                    Spacer(Modifier.width(12.dp))
                    Stepper(
                        onMinus = { interval = maxOf(1, interval - 1) },
                        onPlus = { interval = minOf(168, interval + 1) },
                        minusEnabled = interval > 1,
                        plusEnabled = interval < 168,
                    )
                }
                PlainField(
                    userAgent,
                    { userAgent = it },
                    Modifier.padding(RowPadding),
                    placeholder = SubscriptionClient.DEFAULT_USER_AGENT,
                    keyboardType = KeyboardType.Uri,
                )
            }

            section(header = "Состояние") {
                ValueRow("Серверов", model.state.serverCount(sub.id).toString())
                if (sub.skippedCount > 0) ValueRow("Пропущено строк", sub.skippedCount.toString())
                sub.lastUpdated?.let { ValueRow("Обновлена", Format.relative(it)) }
                sub.lastError?.let { Text(it, color = Lite.colors.danger, modifier = Modifier.padding(RowPadding)) }
                sub.usage?.let { Box(Modifier.padding(RowPadding)) { UsageLine(it) } }
                sub.supportUrl?.let { link -> ActionRow("Поддержка провайдера") { runCatching { uri.openUri(link) } } }
                sub.webPageUrl?.let { link -> ActionRow("Личный кабинет") { runCatching { uri.openUri(link) } } }
                ActionRow("Обновить сейчас", enabled = urlValid && !busy, trailing = { if (busy) Spinner() }) {
                    save()
                    model.refresh(sub.id)
                }
            }

            section {
                ActionRow("Удалить подписку", color = Lite.colors.danger) { confirm = true }
            }
        }
    }

    if (confirm) {
        ConfirmDelete("Удалить подписку и её серверы?", onDismiss = { confirm = false }) {
            model.pop()
            model.removeSubscription(sub.id)
        }
    }
}

/** − and +, side by side, like a SwiftUI Stepper. */
@Composable
private fun Stepper(onMinus: () -> Unit, onPlus: () -> Unit, minusEnabled: Boolean, plusEnabled: Boolean) {
    Row {
        StepButton("−", "Меньше", minusEnabled, RoundedCornerShape(topStart = 8.dp, bottomStart = 8.dp), onMinus)
        StepButton("+", "Больше", plusEnabled, RoundedCornerShape(topEnd = 8.dp, bottomEnd = 8.dp), onPlus)
    }
}

@Composable
private fun StepButton(text: String, label: String, enabled: Boolean, shape: RoundedCornerShape, onClick: () -> Unit) {
    OutlinedButton(
        onClick = onClick,
        enabled = enabled,
        shape = shape,
        border = BorderStroke(1.dp, Lite.colors.separator),
        contentPadding = PaddingValues(0.dp),
        colors = ButtonDefaults.outlinedButtonColors(contentColor = Lite.colors.accent, disabledContentColor = Lite.colors.secondary),
        modifier = Modifier.size(width = 48.dp, height = 36.dp),
    ) {
        Text(text, style = LiteType.headline, modifier = Modifier.clearAndSetSemantics { contentDescription = label })
    }
}

private fun validUrl(text: String): Boolean {
    val uri = runCatching { URI(text.trim()) }.getOrNull() ?: return false
    val scheme = uri.scheme?.lowercase()
    return (scheme == "http" || scheme == "https") && !uri.host.isNullOrEmpty()
}
