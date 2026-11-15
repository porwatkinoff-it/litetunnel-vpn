package app.litetunnel.core

/**
 * Servers only Xray-core reaches: XHTTP, mKCP, VLESS Encryption, TCP with an
 * HTTP header, finalmask. They run as LiteTunnel's own sing-box outbound,
 * `{"type": "xray", "outbound": <Xray outbound>}` (scripts/xray), so Xray
 * talks to the server and sing-box does everything else. Every other server
 * keeps sing-box's own outbound, the one the battery figures were measured on.
 */
object XrayOutbound {
    const val TYPE = "xray"

    /**
     * The Xray protocols linked into the core, and what the list calls them.
     * Hysteria is version 2 only, as in Xray itself.
     */
    private val protocols = mapOf(
        "vless" to ProxyProtocol.VLESS, "vmess" to ProxyProtocol.VMESS, "trojan" to ProxyProtocol.TROJAN,
        "shadowsocks" to ProxyProtocol.SHADOWSOCKS, "socks" to ProxyProtocol.SOCKS, "http" to ProxyProtocol.HTTP,
        "hysteria" to ProxyProtocol.HYSTERIA2,
    )

    private val empty: Json = JObject(emptyMap())

    /** The sing-box outbound for an Xray one. The tag is the config's own and would make the same server look like two. */
    fun wrap(xray: Json): Json = obj("type" to TYPE, "outbound" to xray.with("tag", null))

    /** The Xray outbound inside, or null for any other outbound. */
    fun inner(outbound: Json): Json? = if (outbound["type"]?.string == TYPE) outbound["outbound"] else null

    data class Endpoint(val proto: ProxyProtocol, val host: String, val port: Int)

    /** The server an Xray outbound connects to: in vnext or servers, or flat in settings as newer Xray allows. */
    fun endpoint(xray: Json): Endpoint? {
        val proto = protocols[(xray["protocol"]?.string ?: "").lowercase()] ?: return null
        val settings = xray["settings"] ?: empty
        val endpoint = settings["vnext"]?.get(0) ?: settings["servers"]?.get(0) ?: settings
        val host = ShareLink.nonEmpty(endpoint["address"]?.string) ?: return null
        val port = endpoint["port"]?.int?.takeIf(ShareLink::validPort) ?: return null
        return Endpoint(proto, host, port)
    }

    /** Protocol, host and port of a sing-box outbound, an Xray one included. */
    fun endpointOf(outbound: Json): Endpoint? {
        inner(outbound)?.let { return endpoint(it) }
        val proto = ProxyProtocol.of(outbound["type"]?.string) ?: return null
        val host = outbound["server"]?.string ?: return null
        val port = outbound["server_port"]?.int ?: return null
        return Endpoint(proto, host, port)
    }

    /** An outbound from an Xray config, kept as it is. */
    fun server(xray: Json, name: String?): ParsedServer {
        val proto = (xray["protocol"]?.string ?: "").lowercase()
        if (proto !in protocols) throw ShareLinkError.Unsupported("Xray ${proto.ifEmpty { "без protocol" }}")
        if (proto == "hysteria" && (xray["settings"]?.get("version")?.int ?: 2) != 2) {
            throw ShareLinkError.Unsupported("Hysteria 1")
        }
        val e = endpoint(xray) ?: throw ShareLinkError.Invalid("нет адреса или порта")
        checkSecurity(xray, e.host)
        return ParsedServer(name ?: "${e.host}:${e.port}", e.proto, e.host, e.port, wrap(xray), null)
    }

    /**
     * Whether an Xray config's outbound has to stay Xray even where sing-box
     * has the protocol: finalmask changes every packet, and nothing in
     * sing-box does the same.
     */
    fun needsXray(xray: Json): Boolean {
        val stream = xray["streamSettings"] ?: empty
        return stream["finalmask"] != null || stream["finalMask"] != null
    }

    // Share links

    /**
     * An Xray outbound from a share link that sing-box cannot run. `user` is
     * the protocol's own part: id and encryption for VLESS, a password for Trojan.
     */
    fun fromLink(
        proto: String, host: String, port: Int, user: Json,
        network: String, security: String, q: Map<String, String>,
    ): Json {
        var server = obj("address" to host, "port" to port)
        val settings = when (proto) {
            "vless", "vmess" -> {
                server = server.with("users", JArray(listOf(user)))
                obj("vnext" to JArray(listOf(server)))
            }
            "trojan" -> {
                for ((key, value) in user.obj ?: emptyMap()) server = server.with(key, value)
                obj("servers" to JArray(listOf(server)))
            }
            else -> throw ShareLinkError.Unsupported(proto)
        }
        val xray = obj("protocol" to proto, "settings" to settings, "streamSettings" to stream(network, security, q, host))
        checkSecurity(xray, host)
        return xray
    }

    /**
     * Xray refuses VLESS without TLS or VLESS Encryption, and Trojan without
     * TLS, to a server outside the local network: the id or the password would
     * go out in the clear. It also runs Reality over RAW, XHTTP and gRPC only.
     * Such a server is skipped with that reason, not added to fail on connect.
     */
    fun checkSecurity(xray: Json, host: String) {
        val security = (xray["streamSettings"]?.get("security")?.string ?: "").lowercase()
        val network = (xray["streamSettings"]?.get("network")?.string ?: "").lowercase()
        if (security == "reality" && network !in realityNetworks) {
            throw ShareLinkError.Unsupported("Reality с транспортом $network")
        }
        if ((security.isNotEmpty() && security != "none") || isLocal(host)) return
        when ((xray["protocol"]?.string ?: "").lowercase()) {
            "vless" -> {
                val settings = xray["settings"] ?: empty
                val user = settings["vnext"]?.get(0)?.get("users")?.get(0) ?: settings
                val encryption = user["encryption"]?.string ?: ""
                if (encryption.isEmpty() || encryption == "none") throw ShareLinkError.Unsupported("VLESS без TLS и шифрования")
            }
            "trojan" -> throw ShareLinkError.Unsupported("Trojan без TLS")
        }
    }

    /** A private, loopback or link-local address, or a name Xray takes as local. */
    fun isLocal(raw: String): Boolean {
        val host = raw.lowercase().removeSuffix(".")
        if (':' in host) {
            return host == "::1" || host.startsWith("fc") || host.startsWith("fd") ||
                listOf("fe8", "fe9", "fea", "feb").any(host::startsWith)
        }
        if (ShareLink.isIpAddress(host)) {
            val b = host.split('.').map(String::toInt)
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] in 16..31) ||
                (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254)
        }
        if ('.' !in host) return true
        return localSuffixes.any { host == it || host.endsWith(".$it") }
    }

    private val realityNetworks = setOf("", "raw", "tcp", "xhttp", "splithttp", "grpc")

    private val localSuffixes = listOf("lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal")

    /** streamSettings from the share-link keys Xray's own links use. */
    fun stream(network: String, securityRaw: String, q: Map<String, String>, address: String): Json {
        val host = ShareLink.nonEmpty(q["host"])
        val path = ShareLink.nonEmpty(q["path"])
        var s = empty
        when (network.lowercase()) {
            "", "tcp", "raw" -> {
                s = s.with("network", JString("raw"))
                if (q["headertype"]?.lowercase() == "http") {
                    var request = obj("path" to Json.strings(ShareLink.splitList(path ?: "/")))
                    if (host != null) request = request.with("headers", obj("Host" to Json.strings(ShareLink.splitList(host))))
                    s = s.with("rawSettings", obj("header" to obj("type" to "http", "request" to request)))
                }
            }
            "xhttp", "splithttp" -> {
                // extra is the rest of xhttpSettings, as JSON.
                var x = q["extra"]?.let { runCatching { Json.parse(it) }.getOrNull() }?.takeIf { it.obj != null } ?: empty
                x = x.with("path", JString(path ?: "/"))
                if (host != null) x = x.with("host", JString(host))
                x = x.with("mode", JString(ShareLink.nonEmpty(q["mode"]) ?: "auto"))
                s = s.with("network", JString("xhttp")).with("xhttpSettings", x)
            }
            "kcp", "mkcp" -> {
                var k = empty
                ShareLink.nonEmpty(q["headertype"])?.takeIf { it != "none" }?.let { k = k.with("header", obj("type" to it)) }
                ShareLink.nonEmpty(q["seed"])?.let { k = k.with("seed", JString(it)) }
                s = s.with("network", JString("kcp")).with("kcpSettings", k)
            }
            "ws", "websocket" -> {
                var w = obj("path" to (path ?: "/"))
                if (host != null) w = w.with("host", JString(host))
                s = s.with("network", JString("ws")).with("wsSettings", w)
            }
            "grpc", "gun" -> {
                var g = obj("serviceName" to (ShareLink.nonEmpty(q["servicename"]) ?: ""))
                if (q["mode"]?.lowercase() == "multi") g = g.with("multiMode", JBool(true))
                ShareLink.nonEmpty(q["authority"])?.let { g = g.with("authority", JString(it)) }
                s = s.with("network", JString("grpc")).with("grpcSettings", g)
            }
            "httpupgrade" -> {
                var h = obj("path" to (path ?: "/"))
                if (host != null) h = h.with("host", JString(host))
                s = s.with("network", JString("httpupgrade")).with("httpupgradeSettings", h)
            }
            else -> throw ShareLinkError.Unsupported("транспорт $network")
        }

        val security = securityRaw.lowercase()
        val sni = ShareLink.nonEmpty(q["sni"]) ?: ShareLink.nonEmpty(q["peer"])
            ?: host?.let { ShareLink.splitList(it).firstOrNull() } ?: address.takeUnless(ShareLink::isIpAddress)
        val fingerprint = ShareLink.nonEmpty(q["fp"])?.lowercase()
        when (security) {
            "", "none" -> {}
            "tls", "xtls" -> {
                // allowInsecure is gone from Xray 26; the core drops it, and a pinned
                // certificate (pcs) or a name to check it by (vcn) stands in.
                var t = empty
                sni?.let { t = t.with("serverName", JString(it)) }
                fingerprint?.let { t = t.with("fingerprint", JString(it)) }
                ShareLink.nonEmpty(q["alpn"])?.let { t = t.with("alpn", Json.strings(ShareLink.splitList(it))) }
                ShareLink.nonEmpty(q["pcs"])?.let { t = t.with("pinnedPeerCertSha256", JString(it)) }
                ShareLink.nonEmpty(q["vcn"])?.let { t = t.with("verifyPeerCertByName", JString(it)) }
                ShareLink.nonEmpty(q["ech"])?.let { t = t.with("echConfigList", JString(it)) }
                s = s.with("security", JString("tls")).with("tlsSettings", t)
            }
            "reality" -> {
                val pbk = ShareLink.nonEmpty(q["pbk"]) ?: throw ShareLinkError.Invalid("Reality без pbk")
                var r = obj("publicKey" to pbk, "fingerprint" to (fingerprint ?: "chrome"))
                sni?.let { r = r.with("serverName", JString(it)) }
                ShareLink.nonEmpty(q["sid"])?.let { r = r.with("shortId", JString(it)) }
                ShareLink.nonEmpty(q["spx"])?.let { r = r.with("spiderX", JString(it)) }
                ShareLink.nonEmpty(q["pqv"])?.let { r = r.with("mldsa65Verify", JString(it)) }
                s = s.with("security", JString("reality")).with("realitySettings", r)
            }
            else -> throw ShareLinkError.Unsupported("security=$security")
        }
        return s
    }

    // The list

    /**
     * What a ping times for an Xray server. Hysteria and XHTTP over HTTP/3
     * answer a QUIC handshake, unless finalmask hides it; mKCP answers nothing
     * without the seed; everything else runs over TCP.
     */
    fun handshake(xray: Json, host: String): Handshake? {
        val stream = xray["streamSettings"] ?: empty
        val tls = stream["tlsSettings"]
        val alpn = tls?.get("alpn")?.array?.mapNotNull { it.string }.orEmpty()
        val quic = Handshake.Quic(tls?.get("serverName")?.string ?: host, alpn.ifEmpty { listOf("h3") })
        return when ((stream["network"]?.string ?: "raw").lowercase()) {
            "kcp", "mkcp" -> null
            "hysteria" -> if (needsXray(xray)) null else quic
            "xhttp", "splithttp" -> when {
                stream["security"]?.string != "tls" || alpn != listOf("h3") -> Handshake.Tcp
                needsXray(xray) -> null
                else -> quic
            }
            else -> Handshake.Tcp
        }
    }

    /** Security, transport and what needs Xray, e.g. "Reality · XHTTP · Xray". */
    fun detail(xray: Json): String {
        val stream = xray["streamSettings"] ?: empty
        val user = xray["settings"]?.get("vnext")?.get(0)?.get("users")?.get(0) ?: xray["settings"] ?: empty
        val parts = ArrayList<String>()
        when (stream["security"]?.string?.lowercase()) {
            "tls" -> parts.add("TLS")
            "reality" -> parts.add("Reality")
        }
        when (val network = (stream["network"]?.string ?: "raw").lowercase()) {
            "xhttp", "splithttp" -> parts.add("XHTTP")
            "kcp", "mkcp" -> parts.add("mKCP")
            "raw", "tcp" -> {
                val header = stream["rawSettings"]?.get("header") ?: stream["tcpSettings"]?.get("header")
                if (header?.get("type")?.string == "http") parts.add("HTTP")
            }
            "hysteria" -> {}
            else -> parts.add(network.uppercase())
        }
        if (user["flow"]?.string?.startsWith("xtls-rprx-vision") == true) parts.add("Vision")
        val encryption = user["encryption"]?.string
        if (!encryption.isNullOrEmpty() && encryption != "none") parts.add("Encryption")
        if (needsXray(xray)) parts.add("finalmask")
        parts.add("Xray")
        return parts.joinToString(" · ")
    }
}
