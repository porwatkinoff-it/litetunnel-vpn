package app.litetunnel.ui

import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.itemsIndexed
import androidx.compose.foundation.lazy.rememberLazyListState
import androidx.compose.foundation.text.selection.SelectionContainer
import androidx.compose.material3.Icon
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.litetunnel.AppModel
import app.litetunnel.R
import app.litetunnel.vpn.CoreStatus
import app.litetunnel.vpn.LogLine
import app.litetunnel.vpn.TunnelStatus
import app.litetunnel.vpn.WatchCore

/** The core's log, read from it only while this screen is open and the VPN is on. */
@Composable
fun LogsScreen(model: AppModel) {
    val context = LocalContext.current
    val tunnel by model.tunnel.collectAsState()
    val connected = tunnel.status == TunnelStatus.CONNECTED
    WatchCore(connected)
    val lines by CoreStatus.logs.collectAsState()

    Scaffold(
        containerColor = Lite.colors.background,
        topBar = {
            BackBar("Журнал", model::pop) {
                TextButton(
                    enabled = lines.isNotEmpty(),
                    onClick = {
                        copySensitive(context, "Журнал", lines.joinToString("\n") { it.text })
                        model.show("Журнал скопирован")
                    },
                ) { Text("Копировать", color = if (lines.isNotEmpty()) Lite.colors.accent else Lite.colors.secondary) }
            }
        },
    ) { padding ->
        if (lines.isEmpty()) {
            Column(
                Modifier.fillMaxSize().padding(padding).padding(32.dp),
                horizontalAlignment = Alignment.CenterHorizontally,
                verticalArrangement = Arrangement.spacedBy(10.dp, Alignment.CenterVertically),
            ) {
                Icon(painterResource(R.drawable.ic_notes), contentDescription = null, tint = Lite.colors.secondary, modifier = Modifier.size(40.dp))
                Text(
                    if (connected) "Записей пока нет" else "Журнал виден, пока VPN включён",
                    style = LiteType.callout,
                    color = Lite.colors.secondary,
                    textAlign = TextAlign.Center,
                )
            }
            return@Scaffold
        }

        val list = rememberLazyListState()
        LaunchedEffect(lines.size) { list.scrollToItem(lines.lastIndex) }
        SelectionContainer(Modifier.padding(padding)) {
            LazyColumn(
                Modifier.fillMaxSize(),
                state = list,
                contentPadding = PaddingValues(horizontal = 16.dp, vertical = 8.dp),
                verticalArrangement = Arrangement.spacedBy(2.dp),
            ) {
                itemsIndexed(lines) { _, line -> LogText(line) }
            }
        }
    }
}

@Composable
private fun LogText(line: LogLine) {
    Text(
        line.text,
        style = LiteType.caption.copy(fontFamily = FontFamily.Monospace),
        color = when {
            line.isError -> Lite.colors.danger
            line.isWarning -> Lite.colors.busy
            else -> Lite.colors.text
        },
    )
}
