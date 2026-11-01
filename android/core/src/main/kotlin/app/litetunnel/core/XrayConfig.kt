package app.litetunnel.core

/**
 * Xray JSON, as panels serve it to Happ and v2RayTun: an array of full Xray
 * configs, one per server, or a single config. Each proxy outbound becomes a
 * sing-box one through the same TLS and transport code as share links. A
 * config with a balancer is one entry: a ServerGroup of the balanced outbounds.
 */
internal object XrayConfig {
    /** Routing targets inside the config, not servers. */
    private val ignored = setOf("freedom", "blackhole", "dns", "loopback")

    fun parse(config: Json, result: SubscriptionResult) {
        val remarks = ShareLink.nonEmpty(config["remarks"]?.string)
        val proxies = (config["outbounds"]?.array ?: emptyList()).filter {
            (it["protocol"]?.string ?: "freedom").lowercase() !in ignored
        }
        val balanced = balancedOutbounds(config, proxies)
        if (balanced.size > 1) {
            parseGroup(balanced, remarks ?: "Авто-выбор", result)
            return
        }
        for (outbound in proxies) {
            val tag = ShareLink.nonEmpty(outbound["tag"]?.string)
            var name = remarks ?: tag
            if (proxies.size > 1 && remarks != null && tag != null) name = "$remarks · $tag"
            try {
                result.servers.add(server(outbound, name))
            } catch (e: ShareLinkError) {
                result.skipped.add(SkippedEntry(name ?: "Xray", e.message ?: ""))
            }
        }
    }

    /**
     * Proxy outbounds a routing balancer picks from: its `selector` lists tag
     * prefixes, and as in Xray an empty one matches every tag. Empty when the
     * config has no balancer.
     */
    private fun balancedOutbounds(config: Json, proxies: List<Json>): List<Json> {
        val prefixes = (config["routing"]?.get("balancers")?.array ?: emptyList())
            .flatMap { it["selector"]?.array ?: emptyList() }
            .mapNotNull { it.string }
        if (prefixes.isEmpty()) return emptyList()
        return proxies.filter { outbound ->
            val tag = outbound["tag"]?.string ?: ""
            prefixes.any { tag.startsWith(it) }
        }
    }

    /**
     * Members the core cannot run are left out and listed as skipped; the
     * group stays as long as one of them is left.
     */
    private fun parseGroup(outbounds: List<Json>, name: String, result: SubscriptionResult) {
        val members = ArrayList<ParsedServer>()
        for (outbound in outbounds) {
            val line = "$name · ${outbound["tag"]?.string ?: "?"}"
            try {
                members.add(server(outbound, line))
            } catch (e: ShareLinkError) {
                result.skipped.add(SkippedEntry(line, e.message ?: ""))
            }
        }
        if (members.size == 1) {
            result.servers.add(members[0].copy(name = name))
        } else if (members.isNotEmpty()) {
            result.servers.add(ServerGroup.make(members, name))
        }
    }

    private val empty: Json = JObject(emptyMap())

    fun server(outbound: Json, name: String?): ParsedServer {
        val proto = (outbound["protocol"]?.string ?: "").lowercase()
        val settings = outbound["settings"] ?: empty
        // Nested in vnext/servers, or flat in settings as newer Xray allows.
        val endpoint = settings["vnext"]?.get(0) ?: settings["servers"]?.get(0) ?: settings
        val user = endpoint["users"]?.get(0) ?: endpoint
        val host = ShareLink.nonEmpty(endpoint["address"]?.string) ?: throw ShareLinkError.Invalid("нет адреса")
        val port = endpoint["port"]?.int?.takeIf(ShareLink::validPort) ?: throw ShareLinkError.Invalid("порт")
        val stream = outbound["streamSettings"] ?: empty
        val security = stream["security"]?.string ?: "none"
        val network = stream["network"]?.string ?: "tcp"
        val q = query(stream, security, network)
        val label = name ?: "$host:$port"

        var out = obj("server" to host, "server_port" to port)
        val kind: ProxyProtocol
        when (proto) {
            "vless" -> {
                val id = ShareLink.nonEmpty(user["id"]?.string) ?: throw ShareLinkError.Invalid("нет UUID")
                val encryption = ShareLink.nonEmpty(user["encryption"]?.string)
                if (encryption != null && encryption != "none") throw ShareLinkError.Unsupported("VLESS Encryption")
                kind = ProxyProtocol.VLESS
                out = out.with("type", JString("vless"))
                    .with("uuid", JString(id))
                    .with("packet_encoding", JString("xudp"))
                if (user["flow"]?.string?.startsWith("xtls-rprx-vision") == true) {
                    out = out.with("flow", JString("xtls-rprx-vision"))
                }
            }
            "vmess" -> {
                val id = ShareLink.nonEmpty(user["id"]?.string) ?: throw ShareLinkError.Invalid("нет UUID")
                kind = ProxyProtocol.VMESS
                out = out.with("type", JString("vmess"))
                    .with("uuid", JString(id))
                    .with("security", JString(ShareLink.nonEmpty(user["security"]?.string) ?: "auto"))
                    .with("alter_id", JInt((user["alterId"]?.int ?: 0).toLong()))
                    .with("packet_encoding", JString("xudp"))
            }
            "trojan" -> {
                val password = ShareLink.nonEmpty(user["password"]?.string) ?: throw ShareLinkError.Invalid("нет пароля")
                kind = ProxyProtocol.TROJAN
                out = out.with("type", JString("trojan")).with("password", JString(password))
            }
            "shadowsocks" -> {
                val method = ShareLink.nonEmpty(user["method"]?.string) ?: throw ShareLinkError.Invalid("нет метода")
                out = out.with("type", JString("shadowsocks"))
                    .with("method", JString(method.lowercase()))
                    .with("password", JString(user["password"]?.string ?: ""))
                return ParsedServer(label, ProxyProtocol.SHADOWSOCKS, host, port, out, null)
            }
            "socks" -> {
                out = out.with("type", JString("socks")).with("version", JString("5"))
                ShareLink.nonEmpty(user["user"]?.string)?.let { out = out.with("username", JString(it)) }
                ShareLink.nonEmpty(user["pass"]?.string)?.let { out = out.with("password", JString(it)) }
                return ParsedServer(label, ProxyProtocol.SOCKS, host, port, out, null)
            }
            "hysteria" -> {
                if ((settings["version"]?.int ?: 2) != 2) throw ShareLinkError.Unsupported("Hysteria 1")
                // Salamander and the like live in finalmask, whose shape is not documented yet.
                if (stream["finalmask"] != null || stream["finalMask"] != null) {
                    throw ShareLinkError.Unsupported("Hysteria с finalmask")
                }
                out = out.with("type", JString("hysteria2"))
                    .with("password", JString(stream["hysteriaSettings"]?.get("auth")?.string ?: ""))
                // QUIC: no uTLS fingerprint and no Reality.
                out = out.with("tls", ShareLink.tls("tls", q, host)?.with("utls", null))
                return ParsedServer(label, ProxyProtocol.HYSTERIA2, host, port, out, null)
            }
            else -> throw ShareLinkError.Unsupported("Xray ${proto.ifEmpty { "без protocol" }}")
        }
        out = out.with("tls", ShareLink.tls(security, q, host))
        out = out.with("transport", ShareLink.transport(network, q))
        return ParsedServer(label, kind, host, port, out, null)
    }

    /** streamSettings as the share-link query keys ShareLink.tls and .transport read. */
    private fun query(stream: Json, security: String, network: String): Map<String, String> {
        val q = HashMap<String, String>()
        fun put(key: String, value: String?) {
            if (value != null) q[key] = value
        }
        val tls = (if (security.lowercase() == "reality") stream["realitySettings"] else stream["tlsSettings"]) ?: empty
        put("sni", tls["serverName"]?.string)
        put("fp", tls["fingerprint"]?.string)
        put("alpn", list(tls["alpn"]))
        if (tls["allowInsecure"]?.bool == true) q["allowinsecure"] = "1"
        put("pbk", tls["publicKey"]?.string ?: tls["password"]?.string)
        put("sid", tls["shortId"]?.string)

        val transport = when (network.lowercase()) {
            "tcp", "raw" -> stream["rawSettings"] ?: stream["tcpSettings"]
            "ws", "websocket" -> stream["wsSettings"]
            "grpc", "gun" -> stream["grpcSettings"]
            "httpupgrade" -> stream["httpupgradeSettings"]
            "h2", "http" -> stream["httpSettings"]
            else -> null
        } ?: return q
        put("path", transport["path"]?.string)
        put("host", list(transport["host"]) ?: transport["headers"]?.get("Host")?.string)
        put("servicename", transport["serviceName"]?.string)
        put("headertype", transport["header"]?.get("type")?.string)
        return q
    }

    /** A string, or an array of strings joined by commas as share links write them. */
    private fun list(value: Json?): String? {
        value?.string?.let { return it }
        return value?.array?.mapNotNull { it.string }?.joinToString(",")
    }
}
