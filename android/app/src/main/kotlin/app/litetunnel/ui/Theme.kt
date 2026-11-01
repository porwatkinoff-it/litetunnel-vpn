package app.litetunnel.ui

import androidx.compose.foundation.isSystemInDarkTheme
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Shapes
import androidx.compose.material3.Typography
import androidx.compose.material3.darkColorScheme
import androidx.compose.material3.lightColorScheme
import androidx.compose.runtime.Composable
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.Immutable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.runtime.staticCompositionLocalOf
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp

/** The colours of design/tokens.json, the same on every platform. */
@Immutable
data class LiteColors(
    val accent: Color,
    val connected: Color,
    val busy: Color,
    val danger: Color,
    val background: Color,
    val surface: Color,
    val text: Color,
    val secondary: Color,
    val separator: Color,
)

private val Light = LiteColors(
    accent = Color(0xFF1EB082),
    connected = Color(0xFF34C759),
    busy = Color(0xFFFF9500),
    danger = Color(0xFFFF3B30),
    background = Color(0xFFF2F2F7),
    surface = Color(0xFFFFFFFF),
    text = Color(0xFF000000),
    secondary = Color(0xFF6C6C70),
    separator = Color(0xFFC6C6C8),
)

private val Dark = LiteColors(
    accent = Color(0xFF33CC99),
    connected = Color(0xFF30D158),
    busy = Color(0xFFFF9F0A),
    danger = Color(0xFFFF453A),
    background = Color(0xFF000000),
    surface = Color(0xFF1C1C1E),
    text = Color(0xFFFFFFFF),
    secondary = Color(0xFF8E8E93),
    separator = Color(0xFF38383A),
)

/** The type scale of design/tokens.json, Android sizes. */
object LiteType {
    val title = TextStyle(fontSize = 20.sp, lineHeight = 25.sp, fontWeight = FontWeight.SemiBold)
    val headline = TextStyle(fontSize = 17.sp, lineHeight = 22.sp, fontWeight = FontWeight.SemiBold)
    val body = TextStyle(fontSize = 16.sp, lineHeight = 21.sp)
    val callout = TextStyle(fontSize = 15.sp, lineHeight = 20.sp)
    val caption = TextStyle(fontSize = 12.sp, lineHeight = 16.sp)
    val footer = TextStyle(fontSize = 12.sp, lineHeight = 16.sp)
}

/** Digits of equal width, for numbers that change in place. */
fun TextStyle.tabular(): TextStyle = copy(fontFeatureSettings = "tnum")

private val LocalLiteColors = staticCompositionLocalOf { Light }

object Lite {
    val colors: LiteColors
        @Composable @ReadOnlyComposable get() = LocalLiteColors.current
}

@Composable
fun LiteTheme(content: @Composable () -> Unit) {
    val c = if (isSystemInDarkTheme()) Dark else Light
    val scheme = (if (c === Dark) darkColorScheme() else lightColorScheme()).copy(
        primary = c.accent,
        onPrimary = Color.White,
        primaryContainer = c.accent,
        onPrimaryContainer = Color.White,
        secondary = c.accent,
        onSecondary = Color.White,
        tertiary = c.accent,
        background = c.background,
        onBackground = c.text,
        surface = c.surface,
        onSurface = c.text,
        surfaceVariant = c.background,
        onSurfaceVariant = c.secondary,
        surfaceContainerLowest = c.surface,
        surfaceContainerLow = c.surface,
        surfaceContainer = c.surface,
        surfaceContainerHigh = c.surface,
        surfaceContainerHighest = c.separator,
        surfaceTint = Color.Transparent,
        outline = c.separator,
        outlineVariant = c.separator,
        error = c.danger,
        onError = Color.White,
    )
    val typography = Typography(
        titleLarge = LiteType.title,
        titleMedium = LiteType.headline,
        bodyLarge = LiteType.body,
        bodyMedium = LiteType.callout,
        bodySmall = LiteType.caption,
        labelLarge = LiteType.callout.copy(fontWeight = FontWeight.Medium),
    )
    val shapes = Shapes(
        small = RoundedCornerShape(10.dp),
        medium = RoundedCornerShape(10.dp),
        large = RoundedCornerShape(12.dp),
    )
    CompositionLocalProvider(LocalLiteColors provides c) {
        MaterialTheme(colorScheme = scheme, typography = typography, shapes = shapes, content = content)
    }
}
