package app.litetunnel.ui

import androidx.compose.runtime.Composable
import androidx.compose.runtime.ReadOnlyComposable
import androidx.compose.ui.graphics.Color
import app.litetunnel.core.DnsServer
import app.litetunnel.core.PingDisplay
import app.litetunnel.core.PingMode
import app.litetunnel.core.PingResult
import app.litetunnel.core.RoutingMode
import app.litetunnel.core.SortMode
import app.litetunnel.core.TunStack
import java.time.Duration
import java.time.Instant
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import java.util.Locale

object Format {
    private val ru: Locale = Locale.forLanguageTag("ru")
    private val dateFormat = DateTimeFormatter.ofPattern("d MMM yyyy", ru)
    private val timeFormat = DateTimeFormatter.ofPattern("HH:mm", ru)

    /** Binary units, like the iOS app: "1,5 ГБ". */
    fun size(value: Long): String {
        val units = listOf("КБ", "МБ", "ГБ", "ТБ")
        var v = value / 1024.0
        var unit = 0
        while (v >= 1024 && unit < units.lastIndex) {
            v /= 1024
            unit++
        }
        val number = if (v < 10) String.format(ru, "%.1f", v).removeSuffix(",0") else String.format(ru, "%.0f", v)
        return "$number ${units[unit]}"
    }

    fun date(instant: Instant): String = dateFormat.format(instant.atZone(ZoneId.systemDefault()))

    fun time(millis: Long): String = timeFormat.format(Instant.ofEpochMilli(millis).atZone(ZoneId.systemDefault()))

    /** "1,5 МБ/с". */
    fun speed(bytesPerSecond: Long): String = size(bytesPerSecond) + "/с"

    /** "05:07", or "1:05:07" past an hour. */
    fun duration(seconds: Long): String {
        val s = maxOf(0, seconds)
        val h = s / 3600
        val m = s % 3600 / 60
        val sec = s % 60
        return if (h > 0) String.format(ru, "%d:%02d:%02d", h, m, sec) else String.format(ru, "%02d:%02d", m, sec)
    }

    fun relative(instant: Instant, now: Instant = Instant.now()): String {
        val minutes = Duration.between(instant, now).toMinutes()
        return when {
            minutes < 1 -> "только что"
            minutes < 60 -> "$minutes мин. назад"
            minutes < 24 * 60 -> "${minutes / 60} ч. назад"
            minutes < 48 * 60 -> "вчера"
            minutes < 7 * 24 * 60 -> "${minutes / (24 * 60)} дн. назад"
            else -> date(instant)
        }
    }

    /** "до 12 нояб. 2026 · 12 дн.", "истекает сегодня" or "истекла …". */
    fun expiry(date: Instant, now: Instant = Instant.now()): String {
        if (!date.isAfter(now)) return "истекла ${date(date)}"
        val days = Duration.between(now, date).toDays()
        return if (days < 1) "истекает сегодня" else "до ${date(date)} · $days дн."
    }

    /** "1 сервер", "3 сервера", "5 серверов". */
    fun servers(count: Int): String {
        val tens = count % 100
        val ones = count % 10
        val word = when {
            tens in 11..14 -> "серверов"
            ones == 1 -> "сервер"
            ones in 2..4 -> "сервера"
            else -> "серверов"
        }
        return "$count $word"
    }
}

val PingResult.label: String
    get() = when (this) {
        is PingResult.Ms -> "$value мс"
        PingResult.Failed -> "нет связи"
    }

val PingResult.color: Color
    @Composable @ReadOnlyComposable get() = when (this) {
        is PingResult.Ms -> if (value < 150) Lite.colors.connected else if (value < 400) Lite.colors.busy else Lite.colors.danger
        PingResult.Failed -> Lite.colors.danger
    }

/** Signal bars, 0 to 4. */
val PingResult.level: Int
    get() = when (this) {
        is PingResult.Ms -> if (value < 100) 4 else if (value < 200) 3 else if (value < 400) 2 else 1
        PingResult.Failed -> 0
    }

val PingMode.title: String
    get() = when (this) {
        PingMode.REAL -> "Реальный"
        PingMode.PROXY_GET -> "Через сервер, GET"
        PingMode.PROXY_HEAD -> "Через сервер, HEAD"
        PingMode.TCP -> "TCP"
        PingMode.ICMP -> "ICMP"
    }

private const val PING_CHECK = "Каждый сервер проверяется запросом к адресу проверки и загрузкой 64 КБ через него. " +
    "Время показано только у серверов, которые прошли проверку, у остальных — «нет связи»."

val PingMode.footer: String
    get() = PING_CHECK + " " + when (this) {
        PingMode.REAL -> "Время запроса делится на 3,3, чтобы оно читалось как обычный пинг."
        PingMode.PROXY_GET, PingMode.PROXY_HEAD -> "Показано полное время запроса: оно в несколько раз больше обычного пинга."
        PingMode.TCP -> "Показано время соединения с сервером, а где его не измерить — время запроса, делённое на 3,3."
        PingMode.ICMP -> "Показан обычный ping. Если сервер на него не отвечает, показано время запроса, делённое на 3,3."
    }

val PingDisplay.title: String
    get() = when (this) {
        PingDisplay.TIME -> "Цифры"
        PingDisplay.BARS -> "Полоски"
        PingDisplay.BOTH -> "Цифры и полоски"
        PingDisplay.DOTS -> "Точки"
    }

val RoutingMode.title: String
    get() = when (this) {
        RoutingMode.GLOBAL -> "Всё через VPN"
        RoutingMode.RUSSIA_DIRECT -> "Россия напрямую"
    }

val DnsServer.Kind.title: String
    get() = when (this) {
        DnsServer.Kind.UDP -> "UDP"
        DnsServer.Kind.TLS -> "DoT"
        DnsServer.Kind.HTTPS -> "DoH"
    }

val SortMode.title: String
    get() = when (this) {
        SortMode.NONE -> "Как в подписке"
        SortMode.PING -> "По пингу"
        SortMode.NAME -> "По имени"
    }

val TunStack.title: String
    get() = when (this) {
        TunStack.SYSTEM -> "system"
        TunStack.GVISOR -> "gVisor"
        TunStack.MIXED -> "mixed"
    }
