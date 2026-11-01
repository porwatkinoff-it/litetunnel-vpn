package app.litetunnel.ui

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.PersistableBundle
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyListScope
import androidx.compose.foundation.text.BasicTextField
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.DropdownMenu
import androidx.compose.material3.DropdownMenuItem
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.Switch
import androidx.compose.material3.SwitchDefaults
import androidx.compose.material3.Text
import androidx.compose.material3.TopAppBar
import androidx.compose.material3.TopAppBarDefaults
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.drawBehind
import androidx.compose.ui.draw.rotate
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.SolidColor
import androidx.compose.ui.layout.Layout
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardCapitalization
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.dp
import app.litetunnel.R

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun BackBar(title: String, onBack: () -> Unit, actions: @Composable () -> Unit = {}) {
    TopAppBar(
        title = { Text(title, style = LiteType.headline, maxLines = 1, overflow = TextOverflow.Ellipsis) },
        navigationIcon = {
            IconButton(onClick = onBack) {
                Icon(painterResource(R.drawable.ic_arrow_back), contentDescription = "Назад", tint = Lite.colors.accent)
            }
        },
        actions = { actions() },
        colors = barColors(),
    )
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun barColors() = TopAppBarDefaults.topAppBarColors(
    containerColor = Lite.colors.background,
    scrolledContainerColor = Lite.colors.background,
    titleContentColor = Lite.colors.text,
    actionIconContentColor = Lite.colors.accent,
)

/**
 * A grouped block like an iOS form section: header, a rounded surface with a
 * separator between rows, footer.
 */
fun LazyListScope.section(
    header: String? = null,
    footer: String? = null,
    key: Any? = null,
    content: @Composable () -> Unit,
) {
    item(key = key) {
        Section(header = header, footer = footer, content = content)
    }
}

@Composable
fun Section(
    header: String? = null,
    footer: String? = null,
    headerContent: (@Composable () -> Unit)? = null,
    content: @Composable () -> Unit,
) {
    Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
        when {
            headerContent != null -> Box(Modifier.padding(start = 16.dp, end = 4.dp, bottom = 8.dp)) { headerContent() }
            header != null -> Text(
                header,
                style = LiteType.footer,
                color = Lite.colors.secondary,
                modifier = Modifier.padding(start = 16.dp, end = 16.dp, bottom = 6.dp),
            )
        }
        SeparatedColumn(
            Modifier.fillMaxWidth().clip(RoundedCornerShape(10.dp)).background(Lite.colors.surface),
            content = content,
        )
        if (footer != null) {
            Text(
                footer,
                style = LiteType.footer,
                color = Lite.colors.secondary,
                modifier = Modifier.padding(start = 16.dp, top = 6.dp, end = 16.dp),
            )
        }
    }
}

/** Children one under another, a hairline between each two, inset from the start. */
@Composable
fun SeparatedColumn(modifier: Modifier = Modifier, inset: Dp = 16.dp, content: @Composable () -> Unit) {
    var lines by remember { mutableStateOf(IntArray(0)) }
    val color = Lite.colors.separator
    Layout(
        content = content,
        modifier = modifier.drawBehind {
            val start = inset.toPx()
            val y0 = 0.5f
            for (y in lines) {
                drawLine(color, Offset(start, y + y0), Offset(size.width, y + y0), strokeWidth = 1f)
            }
        },
    ) { measurables, constraints ->
        val placeables = measurables.map { it.measure(constraints.copy(minHeight = 0)) }
        val width = placeables.maxOfOrNull { it.width }?.coerceAtLeast(constraints.minWidth) ?: constraints.minWidth
        val height = placeables.sumOf { it.height }
        val tops = IntArray((placeables.size - 1).coerceAtLeast(0))
        layout(width, height) {
            var y = 0
            placeables.forEachIndexed { index, placeable ->
                if (index > 0) tops[index - 1] = y
                placeable.place(0, y)
                y += placeable.height
            }
        }.also { if (!tops.contentEquals(lines)) lines = tops }
    }
}

val RowPadding = PaddingValues(horizontal = 16.dp, vertical = 12.dp)

private val RowMinHeight = 48.dp

/** The base of every row: full width, at least 48 high, 16 in from the sides. */
fun Modifier.row(enabled: Boolean = true, onClick: (() -> Unit)? = null): Modifier {
    val clickable = if (onClick != null) clickable(enabled = enabled, onClick = onClick) else this
    return clickable.fillMaxWidth().heightIn(min = RowMinHeight).padding(horizontal = 16.dp, vertical = 10.dp)
}

@Composable
fun ValueRow(label: String, value: String, valueColor: Color = Color.Unspecified) {
    Row(Modifier.row(), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        Text(
            value,
            color = if (valueColor == Color.Unspecified) Lite.colors.secondary else valueColor,
            maxLines = 2,
            overflow = TextOverflow.Ellipsis,
        )
    }
}

@Composable
fun SwitchRow(label: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(Modifier.row { onChange(!checked) }, verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        Spacer(Modifier.width(12.dp))
        Switch(
            checked = checked,
            onCheckedChange = onChange,
            colors = SwitchDefaults.colors(
                checkedTrackColor = Lite.colors.accent,
                checkedThumbColor = Color.White,
                checkedBorderColor = Color.Transparent,
                uncheckedTrackColor = Lite.colors.background,
                uncheckedThumbColor = Lite.colors.secondary,
                uncheckedBorderColor = Lite.colors.secondary,
            ),
        )
    }
}

@Composable
fun ActionRow(label: String, enabled: Boolean = true, color: Color = Color.Unspecified, trailing: @Composable () -> Unit = {}, onClick: () -> Unit) {
    Row(Modifier.row(enabled, onClick), verticalAlignment = Alignment.CenterVertically) {
        val tint = when {
            !enabled -> Lite.colors.secondary
            color != Color.Unspecified -> color
            else -> Lite.colors.accent
        }
        Text(label, Modifier.weight(1f), color = tint)
        trailing()
    }
}

/** A row that opens another screen: label, an optional value, a chevron. */
@Composable
fun NavRow(label: String, value: String? = null, onClick: () -> Unit) {
    Row(Modifier.row(onClick = onClick), verticalAlignment = Alignment.CenterVertically) {
        Text(label, Modifier.weight(1f))
        if (value != null) {
            Text(value, color = Lite.colors.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.width(6.dp))
        }
        Chevron(Modifier.rotate(-90f))
    }
}

@Composable
fun Chevron(modifier: Modifier = Modifier, tint: Color = Lite.colors.secondary) {
    Icon(painterResource(R.drawable.ic_expand_more), contentDescription = null, tint = tint, modifier = modifier.size(20.dp))
}

/** A row that opens a menu of choices, like a SwiftUI Picker. */
@Composable
fun <T> ChoiceRow(label: String, options: List<T>, selected: T, title: (T) -> String, onSelect: (T) -> Unit) {
    var open by remember { mutableStateOf(false) }
    Box {
        Row(
            Modifier.row { open = true },
            verticalAlignment = Alignment.CenterVertically,
            horizontalArrangement = Arrangement.SpaceBetween,
        ) {
            Text(label, Modifier.weight(1f))
            Spacer(Modifier.width(12.dp))
            Text(title(selected), color = Lite.colors.secondary, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Spacer(Modifier.width(2.dp))
            Chevron()
        }
        DropdownMenu(expanded = open, onDismissRequest = { open = false }, modifier = Modifier.align(Alignment.TopEnd)) {
            for (option in options) {
                DropdownMenuItem(
                    text = { Text(title(option), color = if (option == selected) Lite.colors.accent else Lite.colors.text) },
                    onClick = {
                        open = false
                        onSelect(option)
                    },
                )
            }
        }
    }
}

@Composable
fun Spinner(size: Dp = 18.dp) {
    CircularProgressIndicator(Modifier.size(size), strokeWidth = 2.dp, color = Lite.colors.secondary)
}

/** The only filled button of a screen. */
@Composable
fun PrimaryButton(text: String, modifier: Modifier = Modifier, enabled: Boolean = true, onClick: () -> Unit) {
    Button(
        onClick = onClick,
        enabled = enabled,
        shape = RoundedCornerShape(10.dp),
        colors = ButtonDefaults.buttonColors(containerColor = Lite.colors.accent, contentColor = Color.White),
        modifier = modifier.heightIn(min = 48.dp),
    ) {
        Text(text, style = LiteType.body.copy(fontWeight = FontWeight.SemiBold))
    }
}

/** A borderless field for a form row: the text, a grey placeholder while empty, an accent cursor. */
@Composable
fun PlainField(
    value: String,
    onValueChange: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String = "",
    style: TextStyle = LiteType.body,
    keyboardType: KeyboardType = KeyboardType.Text,
    enabled: Boolean = true,
    singleLine: Boolean = true,
    minLines: Int = 1,
    maxLines: Int = if (singleLine) 1 else Int.MAX_VALUE,
    onDone: (() -> Unit)? = null,
) {
    val end = style.textAlign == TextAlign.End
    BasicTextField(
        value = value,
        onValueChange = onValueChange,
        enabled = enabled,
        singleLine = singleLine,
        minLines = minLines,
        maxLines = maxLines,
        textStyle = style.copy(color = if (enabled) Lite.colors.text else Lite.colors.secondary),
        cursorBrush = SolidColor(Lite.colors.accent),
        keyboardOptions = KeyboardOptions(
            capitalization = KeyboardCapitalization.None,
            autoCorrectEnabled = keyboardType == KeyboardType.Text,
            keyboardType = keyboardType,
            imeAction = if (singleLine) ImeAction.Done else ImeAction.Default,
        ),
        keyboardActions = KeyboardActions(onDone = onDone?.let { done -> { done() } }),
        modifier = modifier,
        decorationBox = { inner ->
            Box(Modifier.fillMaxWidth(), contentAlignment = if (end) Alignment.CenterEnd else Alignment.CenterStart) {
                if (value.isEmpty()) {
                    Text(placeholder, style = style, color = Lite.colors.secondary, maxLines = maxLines, overflow = TextOverflow.Ellipsis)
                }
                inner()
            }
        },
    )
}

/**
 * A field that saves when editing ends (Done or focus leaves), not on every key:
 * each save of a setting rewrites the core config. [onCommit] gets the trimmed
 * text; the field then shows [value] again, whatever the caller made of it.
 */
@Composable
fun RowField(
    value: String,
    onCommit: (String) -> Unit,
    modifier: Modifier = Modifier,
    placeholder: String = "",
    style: TextStyle = LiteType.body,
    keyboardType: KeyboardType = KeyboardType.Text,
    filter: (String) -> String = { it },
) {
    var text by remember(value) { mutableStateOf(value) }
    var focused by remember { mutableStateOf(false) }
    val focus = LocalFocusManager.current
    PlainField(
        value = text,
        onValueChange = { text = filter(it) },
        placeholder = placeholder,
        style = style,
        keyboardType = keyboardType,
        onDone = { focus.clearFocus() },
        modifier = modifier.onFocusChanged {
            if (focused && !it.isFocused) {
                onCommit(text.trim())
                text = value
            }
            focused = it.isFocused
        },
    )
}

/** Keeps the text out of the keyboard's clipboard preview on Android 13+. */
fun copySensitive(context: Context, label: String, text: String) {
    val clip = ClipData.newPlainText(label, text)
    clip.description.extras = PersistableBundle().apply { putBoolean("android.content.extra.IS_SENSITIVE", true) }
    (context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager).setPrimaryClip(clip)
}
