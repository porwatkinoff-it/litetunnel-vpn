package app.litetunnel.core

import java.io.IOException
import java.net.HttpURLConnection
import java.net.URI
import java.net.URL
import java.time.Instant

data class SubscriptionMeta(
    val title: String? = null,
    val updateIntervalHours: Int? = null,
    val usage: Usage? = null,
    val supportUrl: String? = null,
    val webPageUrl: String? = null,
    val announce: String? = null,
)

data class SkippedEntry(val line: String, val reason: String)

class SubscriptionResult(
    val servers: MutableList<ParsedServer> = ArrayList(),
    val skipped: MutableList<SkippedEntry> = ArrayList(),
    var meta: SubscriptionMeta = SubscriptionMeta(),
    /** `happ://routing/...` lines found in the body; read later by the routing importer. */
    val routingLines: MutableList<String> = ArrayList(),
)

sealed class SubscriptionError(message: String) : Exception(message) {
    data class Http(val code: Int) : SubscriptionError("Сервер подписки ответил $code")
    data object Empty : SubscriptionError("Подписка пустая")
    data class NothingSupported(val reason: String) : SubscriptionError("В подписке нет поддерживаемых серверов ($reason)")
    data object DeviceLimit : SubscriptionError("Достигнут лимит устройств подписки: отвяжите старое устройство у провайдера")
    data object DeviceIdRefused : SubscriptionError("Провайдер не принял ID устройства и не выдал серверы")
}

object SubscriptionParser {
    /** Splits like Foundation's CharacterSet.newlines. */
    private val newlines = Regex("[\n\u000B\u000C\r\u0085  ]")

    fun parse(body: ByteArray, headers: Map<String, String>): SubscriptionResult {
        // Device-limit panels answer 200 with a placeholder server and say why
        // in these headers (docs.rw/docs/features/hwid-device-limit).
        fun flag(name: String) = headers.any { it.key.lowercase() == name && it.value.lowercase() == "true" }
        if (flag("x-hwid-max-devices-reached")) throw SubscriptionError.DeviceLimit
        if (flag("x-hwid-not-supported")) throw SubscriptionError.DeviceIdRefused
        val result = parseBody(body)
        result.meta = parseHeaders(headers)
        if (result.servers.isEmpty()) {
            result.skipped.firstOrNull()?.let { throw SubscriptionError.NothingSupported(it.reason) }
            throw SubscriptionError.Empty
        }
        return result
    }

    fun parseBody(body: ByteArray): SubscriptionResult {
        val result = SubscriptionResult()
        var text = Base64Text.utf8OrNull(body)?.removePrefix("﻿")?.trim() ?: return result
        if (text.isEmpty()) return result

        if (text.startsWith("{") || text.startsWith("[")) {
            parseJson(text, result)
            return result
        }
        if ("://" !in text) {
            Base64Text.decodeString(text)?.takeIf { "://" in it }?.let { text = it }
        }
        for (raw in text.split(newlines)) {
            val line = raw.trim()
            if (line.isEmpty() || line.startsWith("#") || line.startsWith("//")) continue
            if (line.lowercase().startsWith("happ://routing/")) {
                result.routingLines.add(line)
                continue
            }
            if (ShareLink.scheme(line) == null) {
                // Some panels put a base64 block per line.
                val decoded = Base64Text.decodeString(line)
                if (decoded != null && "://" in decoded) {
                    val inner = parseBody(decoded.toByteArray())
                    result.servers += inner.servers
                    result.skipped += inner.skipped
                    result.routingLines += inner.routingLines
                }
                continue
            }
            try {
                result.servers.add(ShareLink.parse(line))
            } catch (e: ShareLinkError) {
                result.skipped.add(SkippedEntry(line, e.message ?: ""))
            }
        }
        return result
    }

    /**
     * sing-box JSON: proxy outbounds taken as they are. Xray JSON (an array of
     * configs, one per server, or one config): converted by XrayConfig.
     */
    private fun parseJson(text: String, result: SubscriptionResult) {
        val unknown = SkippedEntry(text.take(80), "Неизвестный формат JSON")
        val json = try {
            Json.parse(text)
        } catch (e: JsonException) {
            result.skipped.add(unknown)
            return
        }
        val configs = json.array ?: listOf(json)
        if (configs.none { it["outbounds"]?.array != null }) result.skipped.add(unknown)
        for (config in configs) {
            val outbounds = config["outbounds"]?.array ?: emptyList()
            if (outbounds.any { it["protocol"] != null }) {
                XrayConfig.parse(config, result)
            } else {
                parseSingBox(outbounds, result)
            }
        }
    }

    private fun parseSingBox(outbounds: List<Json>, result: SubscriptionResult) {
        for (outbound in outbounds) {
            val proto = ProxyProtocol.of(outbound["type"]?.string) ?: continue
            val host = outbound["server"]?.string ?: continue
            val port = outbound["server_port"]?.int
                ?: outbound["server_ports"]?.array?.firstOrNull()?.string?.substringBefore(':')?.toIntOrNull()
                ?: continue
            val stripped = outbound.with("tag", null).with("detour", null)
            val name = outbound["tag"]?.string ?: "$host:$port"
            result.servers.add(ParsedServer(name, proto, host, port, stripped, null))
        }
    }

    fun parseHeaders(headers: Map<String, String>): SubscriptionMeta {
        val lower = headers.mapKeys { it.key.lowercase() }
        return SubscriptionMeta(
            title = lower["profile-title"]?.let(::decodeHeaderText),
            updateIntervalHours = lower["profile-update-interval"]?.trim()?.toIntOrNull()?.let { maxOf(1, it) },
            usage = lower["subscription-userinfo"]?.let(::parseUserInfo),
            supportUrl = lower["support-url"]?.let(::urlOrNull),
            webPageUrl = lower["profile-web-page-url"]?.let(::urlOrNull),
            announce = lower["announce"]?.let(::decodeHeaderText)?.ifEmpty { null },
        )
    }

    /** `upload=1; download=2; total=3; expire=1700000000` */
    internal fun parseUserInfo(value: String): Usage? {
        val fields = HashMap<String, Long>()
        for (part in value.split(';')) {
            val kv = part.split('=', limit = 2).filter { it.isNotEmpty() }
            if (kv.size != 2) continue
            val key = kv[0].trim().lowercase()
            val number = kv[1].trim()
            val parsed = number.toLongOrNull() ?: number.toDoubleOrNull()?.takeIf { it.isFinite() }?.toLong()
            if (parsed != null) fields[key] = parsed else fields.remove(key)
        }
        if (fields.isEmpty()) return null
        val expire = fields["expire"]?.takeIf { it > 0 }?.let(Instant::ofEpochSecond)
        return Usage(fields["upload"] ?: 0, fields["download"] ?: 0, fields["total"] ?: 0, expire)
    }

    /** Header text is plain, percent-encoded, or `base64:...`. */
    internal fun decodeHeaderText(value: String): String {
        val trimmed = value.trim()
        if (trimmed.lowercase().startsWith("base64:")) {
            return Base64Text.decodeString(trimmed.substring(7))?.trim() ?: trimmed
        }
        return percentDecode(trimmed) ?: trimmed
    }

    private fun urlOrNull(value: String): String? {
        val s = value.trim()
        if (s.isEmpty()) return null
        return try {
            URI(s)
            s
        } catch (e: Exception) {
            null
        }
    }
}

/**
 * Fetches a subscription. Blocking: call it off the main thread. One request
 * per refresh, no retries and no cache, so a refresh costs one radio wake-up.
 */
class SubscriptionClient(
    /** x-hwid and the other device headers, sent with every request. */
    private val deviceHeaders: Map<String, String>,
    private val timeoutMs: Int = 15_000,
) {
    fun fetch(subscription: Subscription): SubscriptionResult {
        val connection = try {
            URL(subscription.url).openConnection() as HttpURLConnection
        } catch (e: Exception) {
            throw IOException("Неверный адрес подписки", e)
        }
        try {
            connection.connectTimeout = timeoutMs
            connection.readTimeout = timeoutMs
            connection.useCaches = false
            connection.instanceFollowRedirects = true
            connection.setRequestProperty("User-Agent", subscription.userAgent ?: DEFAULT_USER_AGENT)
            connection.setRequestProperty("Accept", "*/*")
            for ((name, value) in deviceHeaders) connection.setRequestProperty(name, value)
            val code = connection.responseCode
            if (code !in 200..299) throw SubscriptionError.Http(code)
            val headers = HashMap<String, String>()
            for ((key, values) in connection.headerFields) {
                if (key != null && values.isNotEmpty()) headers[key] = values.joinToString(", ")
            }
            val body = connection.inputStream.use { it.readBytes() }
            return SubscriptionParser.parse(body, headers)
        } finally {
            connection.disconnect()
        }
    }

    companion object {
        const val DEFAULT_USER_AGENT = "LiteTunnel/0.1"
    }
}
