package app.litetunnel.core

import java.io.ByteArrayOutputStream

sealed class ShareLinkError(message: String) : Exception(message) {
    data class UnsupportedScheme(val scheme: String) : ShareLinkError("Протокол $scheme:// не поддерживается")
    data class Unsupported(val what: String) : ShareLinkError("Не поддерживается: $what")
    data class Invalid(val why: String) : ShareLinkError("Ссылка повреждена: $why")
}

/**
 * Turns proxy share links (vless://, hysteria2://, trojan://, ss://, vmess://,
 * tuic://, socks://) into sing-box outbounds.
 */
object ShareLink {
    private val parsers: Map<String, (String) -> ParsedServer> = mapOf(
        "vless" to ::parseVless,
        "trojan" to ::parseTrojan,
        "hysteria2" to ::parseHysteria2,
        "hy2" to ::parseHysteria2,
        "ss" to ::parseShadowsocks,
        "vmess" to ::parseVmess,
        "tuic" to ::parseTuic,
        "socks" to ::parseSocks,
        "socks5" to ::parseSocks,
    )

    /** Schemes we recognise as proxy links, including ones we can only report as unsupported. */
    private val knownSchemes: Set<String> =
        parsers.keys + setOf("wireguard", "wg", "hysteria", "ssr", "anytls", "naive+https", "juicity")

    fun scheme(line: String): String? {
        val end = line.indexOf("://")
        if (end < 0) return null
        val scheme = line.substring(0, end).lowercase()
        return if (scheme in knownSchemes) scheme else null
    }

    fun parse(raw: String): ParsedServer {
        val line = raw.trim()
        val scheme = scheme(line) ?: throw ShareLinkError.UnsupportedScheme(line.substringBefore("://"))
        val parser = parsers[scheme] ?: throw ShareLinkError.UnsupportedScheme(scheme)
        return parser(line)
    }

    // VLESS

    private fun parseVless(line: String): ParsedServer {
        val uri = LinkUri(line)
        val uuid = uri.user?.takeIf { it.isNotEmpty() } ?: throw ShareLinkError.Invalid("нет UUID")
        val port = uri.singlePort()
        val encryption = uri.query["encryption"]
        if (!encryption.isNullOrEmpty() && encryption != "none") throw ShareLinkError.Unsupported("VLESS Encryption")
        var outbound = obj(
            "type" to "vless",
            "server" to uri.host,
            "server_port" to port,
            "uuid" to uuid,
            "packet_encoding" to "xudp",
        )
        if (uri.query["flow"]?.startsWith("xtls-rprx-vision") == true) {
            outbound = outbound.with("flow", JString("xtls-rprx-vision"))
        }
        outbound = outbound.with("tls", tls(uri.query["security"] ?: "none", uri.query, uri.host))
        outbound = outbound.with("transport", transport(uri.query["type"] ?: "tcp", uri.query))
        return ParsedServer(uri.name, ProxyProtocol.VLESS, uri.host, port, outbound, line)
    }

    // Trojan

    private fun parseTrojan(line: String): ParsedServer {
        val uri = LinkUri(line)
        val password = uri.userinfo?.takeIf { it.isNotEmpty() } ?: throw ShareLinkError.Invalid("нет пароля")
        val port = uri.singlePort()
        var outbound = obj(
            "type" to "trojan",
            "server" to uri.host,
            "server_port" to port,
            "password" to password,
        )
        outbound = outbound.with("tls", tls(uri.query["security"] ?: "tls", uri.query, uri.host))
        outbound = outbound.with("transport", transport(uri.query["type"] ?: "tcp", uri.query))
        return ParsedServer(uri.name, ProxyProtocol.TROJAN, uri.host, port, outbound, line)
    }

    // Hysteria2

    private fun parseHysteria2(line: String): ParsedServer {
        val uri = LinkUri(line)
        val q = uri.query
        var single: Int? = null
        val ranges = ArrayList<String>()
        val portParts = uri.portSpec.split(',').filter { it.isNotEmpty() } +
            (q["mport"] ?: "").split(',').filter { it.isNotEmpty() }
        for (part in portParts) {
            val bounds = part.split('-', limit = 2).filter { it.isNotEmpty() }.map { it.trim().toIntOrNull() }
            val lo = bounds.getOrNull(0)
            val hi = bounds.getOrNull(1)
            if (bounds.size == 2 && lo != null && hi != null && validPort(lo) && validPort(hi) && lo <= hi) {
                ranges.add("$lo:$hi")
            } else if (bounds.size == 1 && lo != null && validPort(lo)) {
                if (single == null) single = lo else ranges.add("$lo:$lo")
            } else {
                throw ShareLinkError.Invalid("порт $part")
            }
        }
        if (single == null && ranges.isEmpty()) single = 443
        val port = single ?: ranges[0].substringBefore(':').toInt()

        var outbound = obj(
            "type" to "hysteria2",
            "server" to uri.host,
            "server_port" to port,
            "password" to (uri.userinfo ?: ""),
        )
        if (ranges.isNotEmpty()) {
            // server_port is ignored once server_ports is set, so the main port goes in the list too.
            single?.let { ranges.add(0, "$it:$it") }
            outbound = outbound.with("server_ports", Json.strings(ranges))
            val interval = q["mporthopint"] ?: q["hopinterval"] ?: q["hop_interval"]
            if (!interval.isNullOrEmpty()) {
                outbound = outbound.with("hop_interval", JString(if (interval.toIntOrNull() != null) "${interval}s" else interval))
            }
        }
        val obfs = q["obfs"]
        if (!obfs.isNullOrEmpty() && obfs != "none") {
            if (obfs != "salamander") throw ShareLinkError.Unsupported("obfs $obfs")
            outbound = outbound.with("obfs", obj("type" to "salamander", "password" to (q["obfs-password"] ?: "")))
        }
        q["upmbps"]?.toIntOrNull()?.let { outbound = outbound.with("up_mbps", JInt(it.toLong())) }
        q["downmbps"]?.toIntOrNull()?.let { outbound = outbound.with("down_mbps", JInt(it.toLong())) }

        var tls = obj("enabled" to true)
        (nonEmpty(q["sni"]) ?: uri.host.takeUnless(::isIpAddress))?.let { tls = tls.with("server_name", JString(it)) }
        if (isTrue(q["insecure"]) || isTrue(q["allowinsecure"])) tls = tls.with("insecure", JBool(true))
        nonEmpty(q["alpn"])?.let { tls = tls.with("alpn", Json.strings(splitList(it))) }
        outbound = outbound.with("tls", tls)
        return ParsedServer(uri.name, ProxyProtocol.HYSTERIA2, uri.host, port, outbound, line)
    }

    // TUIC

    private fun parseTuic(line: String): ParsedServer {
        val uri = LinkUri(line)
        val uuid = uri.user?.takeIf { it.isNotEmpty() } ?: throw ShareLinkError.Invalid("нет UUID")
        val port = uri.singlePort()
        val q = uri.query
        var outbound = obj(
            "type" to "tuic",
            "server" to uri.host,
            "server_port" to port,
            "uuid" to uuid,
            "password" to (uri.password ?: ""),
        )
        nonEmpty(q["congestion_control"])?.let { outbound = outbound.with("congestion_control", JString(it)) }
        nonEmpty(q["udp_relay_mode"])?.let { outbound = outbound.with("udp_relay_mode", JString(it)) }
        var tls = obj("enabled" to true)
        (nonEmpty(q["sni"]) ?: uri.host.takeUnless(::isIpAddress))?.let { tls = tls.with("server_name", JString(it)) }
        if (isTrue(q["allow_insecure"]) || isTrue(q["insecure"]) || isTrue(q["allowinsecure"])) {
            tls = tls.with("insecure", JBool(true))
        }
        tls = tls.with("alpn", Json.strings(splitList(nonEmpty(q["alpn"]) ?: "h3")))
        outbound = outbound.with("tls", tls)
        return ParsedServer(uri.name, ProxyProtocol.TUIC, uri.host, port, outbound, line)
    }

    // SOCKS

    private fun parseSocks(line: String): ParsedServer {
        val uri = LinkUri(line)
        val port = uri.singlePort()
        var outbound = obj(
            "type" to "socks",
            "server" to uri.host,
            "server_port" to port,
            "version" to "5",
        )
        val userinfo = uri.userinfo
        if (!userinfo.isNullOrEmpty()) {
            // Either user:pass, or base64(user:pass) as v2rayN writes it.
            val plain = if (':' in userinfo) userinfo else (Base64Text.decodeString(userinfo) ?: userinfo)
            val parts = plain.split(':', limit = 2).filter { it.isNotEmpty() }
            parts.getOrNull(0)?.let { outbound = outbound.with("username", JString(it)) }
            parts.getOrNull(1)?.let { outbound = outbound.with("password", JString(it)) }
        }
        return ParsedServer(uri.name, ProxyProtocol.SOCKS, uri.host, port, outbound, line)
    }

    // Shadowsocks

    private fun parseShadowsocks(line: String): ParsedServer {
        var body = line.substring("ss://".length)
        var name: String? = null
        body.indexOf('#').takeIf { it >= 0 }?.let { hash ->
            name = decodeName(body.substring(hash + 1))
            body = body.substring(0, hash)
        }
        var query: Map<String, String> = emptyMap()
        body.indexOf('?').takeIf { it >= 0 }?.let { q ->
            query = LinkUri.parseQuery(body.substring(q + 1))
            body = body.substring(0, q)
        }
        body = body.trimEnd('/')

        val methodPassword: String
        val hostPort: String
        val at = body.lastIndexOf('@')
        if (at >= 0) {
            // SIP002: userinfo is base64url(method:password), or percent-encoded for 2022 ciphers.
            val rawUser = body.substring(0, at)
            hostPort = body.substring(at + 1)
            val decoded = percentDecode(rawUser) ?: rawUser
            methodPassword = if (':' in decoded) {
                decoded
            } else {
                Base64Text.decodeString(rawUser) ?: throw ShareLinkError.Invalid("userinfo")
            }
        } else {
            // Legacy: base64(method:password@host:port)
            val decoded = Base64Text.decodeString(body)
            val legacyAt = decoded?.lastIndexOf('@') ?: -1
            if (decoded == null || legacyAt < 0) throw ShareLinkError.Invalid("base64")
            methodPassword = decoded.substring(0, legacyAt)
            hostPort = decoded.substring(legacyAt + 1)
        }
        val mp = methodPassword.split(':', limit = 2).filter { it.isNotEmpty() }
        if (mp.size != 2) throw ShareLinkError.Invalid("метод и пароль")
        val (host, portSpec) = LinkUri.splitHostPort(hostPort)
        val port = portSpec.toIntOrNull()?.takeIf(::validPort) ?: throw ShareLinkError.Invalid("порт")

        var outbound = obj(
            "type" to "shadowsocks",
            "server" to host,
            "server_port" to port,
            "method" to mp[0].lowercase(),
            "password" to mp[1],
        )
        nonEmpty(query["plugin"])?.let { plugin ->
            val parts = plugin.split(';', limit = 2).filter { it.isNotEmpty() }
            val pluginName = parts.getOrNull(0) ?: ""
            outbound = when (pluginName) {
                "obfs-local", "simple-obfs" -> outbound.with("plugin", JString("obfs-local"))
                "v2ray-plugin" -> outbound.with("plugin", JString("v2ray-plugin"))
                else -> throw ShareLinkError.Unsupported("плагин $pluginName")
            }
            parts.getOrNull(1)?.let { outbound = outbound.with("plugin_opts", JString(it)) }
        }
        return ParsedServer(name ?: "$host:$port", ProxyProtocol.SHADOWSOCKS, host, port, outbound, line)
    }

    // VMess

    private fun parseVmess(line: String): ParsedServer {
        var body = line.substring("vmess://".length)
        val cut = body.indexOfFirst { it == '?' || it == '#' }
        if (cut >= 0) body = body.substring(0, cut)
        val json = Base64Text.decodeString(body)
            ?.let { runCatching { Json.parse(it) }.getOrNull() }
            ?.takeIf { it.obj != null }
            ?: throw ShareLinkError.Invalid("vmess base64")

        fun field(key: String): String? = when (val v = json[key]) {
            is JString -> v.value.ifEmpty { null }
            is JInt -> v.value.toString()
            is JDouble -> v.value.toLong().toString()
            else -> null
        }

        val host = field("add") ?: throw ShareLinkError.Invalid("нет адреса")
        val port = field("port")?.toIntOrNull()?.takeIf(::validPort) ?: throw ShareLinkError.Invalid("порт")
        val uuid = field("id") ?: throw ShareLinkError.Invalid("нет UUID")

        var outbound = obj(
            "type" to "vmess",
            "server" to host,
            "server_port" to port,
            "uuid" to uuid,
            "security" to (field("scy") ?: "auto"),
            "alter_id" to (field("aid")?.toIntOrNull() ?: 0),
            "packet_encoding" to "xudp",
        )
        val query = HashMap<String, String>()
        field("host")?.let { query["host"] = it }
        field("path")?.let { query["path"] = it; query["servicename"] = it }
        field("type")?.let { query["headertype"] = it }
        field("sni")?.let { query["sni"] = it }
        field("alpn")?.let { query["alpn"] = it }
        field("fp")?.let { query["fp"] = it }
        (field("allowInsecure") ?: field("insecure"))?.let { query["allowinsecure"] = it }
        outbound = outbound.with("tls", tls(field("tls") ?: "none", query, host))
        outbound = outbound.with("transport", transport(field("net") ?: "tcp", query))
        return ParsedServer(field("ps") ?: "$host:$port", ProxyProtocol.VMESS, host, port, outbound, line)
    }

    // Shared pieces

    private val utlsFingerprints = setOf("chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized")

    internal fun tls(securityRaw: String, q: Map<String, String>, host: String): Json? {
        val security = securityRaw.lowercase()
        when (security) {
            "", "none" -> return null
            "tls", "xtls", "reality" -> {}
            else -> throw ShareLinkError.Unsupported("security=$security")
        }
        var tls = obj("enabled" to true)
        val sni = nonEmpty(q["sni"]) ?: nonEmpty(q["peer"]) ?: nonEmpty(q["host"])?.let { splitList(it).firstOrNull() }
            ?: host.takeUnless(::isIpAddress)
        sni?.let { tls = tls.with("server_name", JString(it)) }
        if (isTrue(q["allowinsecure"]) || isTrue(q["insecure"])) tls = tls.with("insecure", JBool(true))
        nonEmpty(q["alpn"])?.let { tls = tls.with("alpn", Json.strings(splitList(it))) }
        val fingerprint = nonEmpty(q["fp"])?.lowercase()
        if (fingerprint != null || security == "reality") {
            var fp = fingerprint ?: "chrome"
            if (fp == "randomizednoalpn") fp = "randomized"
            if (fp !in utlsFingerprints) fp = "chrome"
            tls = tls.with("utls", obj("enabled" to true, "fingerprint" to fp))
        }
        if (security == "reality") {
            val pbk = nonEmpty(q["pbk"]) ?: throw ShareLinkError.Invalid("Reality без pbk")
            tls = tls.with("reality", obj("enabled" to true, "public_key" to pbk, "short_id" to (q["sid"] ?: "")))
        }
        if (q["fragment"] != null) tls = tls.with("fragment", JBool(true))
        return tls
    }

    internal fun transport(type: String, q: Map<String, String>): Json? {
        val host = nonEmpty(q["host"])
        when (type.lowercase()) {
            "", "tcp", "raw" -> {
                if (q["headertype"]?.lowercase() == "http") throw ShareLinkError.Unsupported("TCP с HTTP-маскировкой")
                return null
            }
            "ws", "websocket" -> {
                var t = obj("type" to "ws")
                var path = nonEmpty(q["path"]) ?: "/"
                val ed = path.indexOf("?ed=")
                if (ed >= 0) {
                    path.substring(ed + 4).takeWhile { it.isDigit() }.toIntOrNull()?.let { n ->
                        t = t.with("max_early_data", JInt(n.toLong()))
                        t = t.with("early_data_header_name", JString("Sec-WebSocket-Protocol"))
                    }
                    path = path.substring(0, ed)
                }
                t = t.with("path", JString(path))
                if (host != null) t = t.with("headers", obj("Host" to host))
                return t
            }
            "grpc", "gun" -> return obj("type" to "grpc", "service_name" to (nonEmpty(q["servicename"]) ?: ""))
            "httpupgrade" -> {
                var t = obj("type" to "httpupgrade", "path" to (nonEmpty(q["path"]) ?: "/"))
                if (host != null) t = t.with("host", JString(host))
                return t
            }
            "http", "h2" -> {
                var t = obj("type" to "http", "path" to (nonEmpty(q["path"]) ?: "/"))
                if (host != null) t = t.with("host", Json.strings(splitList(host)))
                return t
            }
            "xhttp", "splithttp" -> throw ShareLinkError.Unsupported("XHTTP (нет в ядре sing-box)")
            "kcp", "mkcp" -> throw ShareLinkError.Unsupported("mKCP")
            else -> throw ShareLinkError.Unsupported("транспорт $type")
        }
    }

    internal fun validPort(port: Int): Boolean = port in 1..65535

    internal fun isTrue(value: String?): Boolean {
        val v = value?.lowercase() ?: return false
        return v == "1" || v == "true"
    }

    internal fun nonEmpty(value: String?): String? = value?.takeIf { it.isNotEmpty() }

    internal fun splitList(value: String): List<String> =
        value.split(',').map { it.trim() }.filter { it.isNotEmpty() }

    internal fun decodeName(raw: String): String? = (percentDecode(raw) ?: raw).trim().ifEmpty { null }

    fun isIpAddress(host: String): Boolean {
        if (':' in host) return true
        val parts = host.split('.')
        return parts.size == 4 && parts.all { it.toUByteOrNull() != null }
    }
}

/**
 * `scheme://userinfo@host:port/path?query#name`, parsed by hand: share links
 * carry port lists and raw passwords that java.net.URI rejects.
 */
internal class LinkUri(line: String) {
    val scheme: String
    val userinfo: String?
    val host: String
    val portSpec: String
    val query: Map<String, String>
    val fragment: String?

    init {
        val schemeEnd = line.indexOf("://")
        if (schemeEnd < 0) throw ShareLinkError.Invalid("нет схемы")
        scheme = line.substring(0, schemeEnd).lowercase()
        var rest = line.substring(schemeEnd + 3)
        val hash = rest.indexOf('#')
        fragment = if (hash >= 0) ShareLink.decodeName(rest.substring(hash + 1)) else null
        if (hash >= 0) rest = rest.substring(0, hash)
        val q = rest.indexOf('?')
        query = if (q >= 0) parseQuery(rest.substring(q + 1)) else emptyMap()
        if (q >= 0) rest = rest.substring(0, q)

        var authority = rest
        val at = authority.lastIndexOf('@')
        val hostStart = if (at >= 0) at + 1 else 0
        val slash = authority.indexOf('/', hostStart)
        if (slash >= 0) authority = authority.substring(0, slash)
        if (at >= 0) {
            val raw = authority.substring(0, at)
            userinfo = percentDecode(raw) ?: raw
            authority = authority.substring(at + 1)
        } else {
            userinfo = null
        }
        val (h, p) = splitHostPort(authority)
        if (h.isEmpty()) throw ShareLinkError.Invalid("нет адреса")
        host = h
        portSpec = p
    }

    val name: String get() = fragment ?: "$host:$portSpec"

    /** The part of userinfo before the first `:`. */
    val user: String? get() = userinfo?.substringBefore(':')

    val password: String?
        get() {
            val info = userinfo ?: return null
            val colon = info.indexOf(':')
            return if (colon >= 0) info.substring(colon + 1) else null
        }

    fun singlePort(): Int = portSpec.toIntOrNull()?.takeIf(ShareLink::validPort)
        ?: throw ShareLinkError.Invalid(if (portSpec.isEmpty()) "нет порта" else "порт $portSpec")

    companion object {
        fun splitHostPort(s: String): Pair<String, String> {
            if (s.startsWith("[")) {
                val close = s.indexOf(']')
                if (close < 0) throw ShareLinkError.Invalid("IPv6-адрес")
                val after = s.substring(close + 1)
                return s.substring(1, close) to (if (after.startsWith(":")) after.substring(1) else "")
            }
            val colon = s.indexOf(':')
            if (colon >= 0) return s.substring(0, colon) to s.substring(colon + 1)
            return s to ""
        }

        /**
         * Keys are lowercased; values are percent-decoded. `+` stays `+`:
         * passwords and keys in these links use it literally.
         */
        fun parseQuery(s: String): Map<String, String> {
            val result = LinkedHashMap<String, String>()
            for (pair in s.split('&')) {
                if (pair.isEmpty()) continue
                val eq = pair.indexOf('=')
                val rawKey = if (eq >= 0) pair.substring(0, eq) else pair
                val rawValue = if (eq >= 0) pair.substring(eq + 1) else ""
                val key = (percentDecode(rawKey) ?: rawKey).lowercase()
                if (key !in result) result[key] = percentDecode(rawValue) ?: rawValue
            }
            return result
        }
    }
}

/**
 * Like Foundation's removingPercentEncoding: null when a `%` is not followed
 * by two hex digits or the bytes are not UTF-8. `+` is not a space.
 */
fun percentDecode(s: String): String? {
    if ('%' !in s) return s
    val out = ByteArrayOutputStream(s.length)
    var i = 0
    while (i < s.length) {
        val c = s[i]
        if (c == '%') {
            val hi = s.getOrNull(i + 1)?.digitToIntOrNull(16) ?: return null
            val lo = s.getOrNull(i + 2)?.digitToIntOrNull(16) ?: return null
            out.write(hi * 16 + lo)
            i += 3
        } else {
            val end = if (c.isHighSurrogate() && i + 1 < s.length) i + 2 else i + 1
            out.write(s.substring(i, end).toByteArray(Charsets.UTF_8))
            i = end
        }
    }
    return Base64Text.utf8OrNull(out.toByteArray())
}
