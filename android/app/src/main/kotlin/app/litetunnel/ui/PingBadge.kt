package app.litetunnel.ui

import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.CornerRadius
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.semantics.clearAndSetSemantics
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.unit.dp
import app.litetunnel.core.PingDisplay
import app.litetunnel.core.PingResult

/** A measured ping in the style the settings pick. Screen readers read the text in every style. */
@Composable
fun PingBadge(ping: PingResult, display: PingDisplay, modifier: Modifier = Modifier) {
    val label = ping.label
    Row(
        modifier.clearAndSetSemantics { contentDescription = label },
        horizontalArrangement = Arrangement.spacedBy(4.dp),
        verticalAlignment = Alignment.CenterVertically,
    ) {
        when (display) {
            PingDisplay.TIME -> PingText(ping)
            PingDisplay.BARS -> PingBars(ping)
            PingDisplay.BOTH -> {
                PingText(ping)
                PingBars(ping)
            }
            PingDisplay.DOTS -> Box(
                Modifier.size(8.dp).background(
                    if (ping == PingResult.Failed) Lite.colors.danger else Lite.colors.connected,
                    CircleShape,
                ),
            )
        }
    }
}

@Composable
private fun PingText(ping: PingResult) {
    Text(ping.label, style = LiteType.caption.tabular(), color = ping.color)
}

/** Four bars rising left to right; [PingResult.level] of them in the ping's colour. */
@Composable
private fun PingBars(ping: PingResult) {
    val on = ping.color
    val off = Lite.colors.separator
    val level = ping.level
    Canvas(Modifier.size(width = 16.dp, height = 12.dp)) {
        val gap = 1.5.dp.toPx()
        val bar = (size.width - gap * 3) / 4
        val radius = CornerRadius(1.dp.toPx())
        for (i in 0 until 4) {
            val height = size.height * (i + 1) / 4
            drawRoundRect(
                color = if (i < level) on else off,
                topLeft = Offset(i * (bar + gap), size.height - height),
                size = Size(bar, height),
                cornerRadius = radius,
            )
        }
    }
}
