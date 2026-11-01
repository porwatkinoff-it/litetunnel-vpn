package app.litetunnel.ui

import android.content.ClipboardManager
import android.content.Context
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.unit.dp
import app.litetunnel.AppModel
import kotlinx.coroutines.launch

@Composable
fun AddScreen(model: AppModel) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var text by rememberSaveable { mutableStateOf("") }
    var busy by rememberSaveable { mutableStateOf(false) }

    fun add(input: String) {
        if (busy || input.isBlank()) return
        busy = true
        scope.launch {
            val ok = model.import(input)
            busy = false
            if (ok) model.pop()
        }
    }

    Scaffold(containerColor = Lite.colors.background, topBar = { BackBar("Добавить", model::pop) }) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding)) {
            section(
                header = "Из буфера обмена",
                footer = "Ссылка на подписку (https://…) или на серверы: vless://, hy2://, trojan://, ss://, vmess://, tuic://. Можно несколько строк сразу.",
            ) {
                ActionRow("Вставить", enabled = !busy) {
                    // Read only on a tap: Android shows a "pasted from" notice for every read.
                    val clipboard = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
                    val clip = clipboard.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.coerceToText(context)?.toString()
                    if (clip.isNullOrBlank()) model.show("Буфер обмена пуст", error = true) else add(clip)
                }
            }

            section(header = "Вставить вручную") {
                PlainField(
                    value = text,
                    onValueChange = { text = it },
                    placeholder = "vless://… или https://…",
                    style = LiteType.callout.copy(fontFamily = FontFamily.Monospace),
                    enabled = !busy,
                    singleLine = false,
                    minLines = 3,
                    maxLines = 8,
                    modifier = Modifier.fillMaxWidth().padding(RowPadding),
                )
                ActionRow("Добавить", enabled = !busy && text.isNotBlank()) { add(text) }
            }

            if (busy) {
                item(key = "busy") {
                    Row(
                        Modifier.fillMaxWidth().padding(horizontal = 32.dp, vertical = 12.dp),
                        horizontalArrangement = Arrangement.spacedBy(10.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Spinner()
                        Text("Загружаю…", color = Lite.colors.secondary)
                    }
                }
            }
        }
    }
}
