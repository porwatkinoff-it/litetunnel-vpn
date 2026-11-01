package app.litetunnel.core

import java.io.File

/**
 * Builds the sing-box config the tunnel runs.
 *
 * Power rules this file enforces: no selector groups, a urltest group only
 * for a subscription's "Авто-выбор" entry and then tested every 10 minutes,
 * no remote rule-sets (they re-download on a timer), no clash API, log level
 * from settings (warn by default).
 */
object ConfigBuilder {
    const val PROXY_TAG = "proxy"
    const val DIRECT_TAG = "direct"
    private const val REMOTE_DNS_TAG = "dns-remote"
    private const val DIRECT_DNS_TAG = "dns-direct"
    private const val BOOTSTRAP_DNS_TAG = "dns-bootstrap"
    private val russianSuffixes = listOf("ru", "su", "xn--p1ai")

    /**
     * @param ruleSetDirectory folder with downloaded `<tag>.srs` files.
     *   Rule-sets that are missing there are left out of the config.
     */
    fun build(server: Server, settings: AppSettings, ruleSetDirectory: File?): Json {
        val available = ruleSetDirectory?.let(RuleSets::available) ?: emptySet()

        // A group runs its members behind a urltest outbound with the proxy tag.
        var proxies = ServerGroup.outbounds(server.outbound, PROXY_TAG)
        if (settings.tlsFragment) proxies = fragmentTls(proxies)

        val usedSets = ArrayList<String>()
        fun use(tags: List<String>): List<String> {
            val present = tags.filter { it in available }
            for (tag in present) if (tag !in usedSets) usedSets.add(tag)
            return present
        }

        // DNS: remote resolver through the proxy; the direct one only for
        // domains that go direct anyway and for the proxy server's own name.
        val dnsRules = ArrayList<Json>()
        if (settings.routing == RoutingMode.RUSSIA_DIRECT) {
            dnsRules.add(obj("domain_suffix" to russianSuffixes, "server" to DIRECT_DNS_TAG))
            val sets = use(listOf(RuleSets.GEOSITE_RU))
            if (sets.isNotEmpty()) dnsRules.add(obj("rule_set" to sets, "server" to DIRECT_DNS_TAG))
        }
        if (settings.directDomains.isNotEmpty()) {
            dnsRules.add(obj("domain_suffix" to settings.directDomains, "server" to DIRECT_DNS_TAG))
        }
        val dnsServers = arrayListOf(
            dnsServer(settings.remoteDns, REMOTE_DNS_TAG, PROXY_TAG, DIRECT_DNS_TAG),
            dnsServer(settings.directDns, DIRECT_DNS_TAG, null, BOOTSTRAP_DNS_TAG),
        )
        if (!ShareLink.isIpAddress(settings.directDns.address)) {
            dnsServers.add(dnsServer(DnsServer.YANDEX, BOOTSTRAP_DNS_TAG, null, null))
        }
        val dns = obj(
            "servers" to dnsServers,
            "rules" to dnsRules,
            "final" to REMOTE_DNS_TAG,
            "strategy" to if (settings.ipv6) "prefer_ipv4" else "ipv4_only",
        )

        val addresses = arrayListOf("172.19.0.1/30")
        if (settings.ipv6) addresses.add("fdfe:dcba:9876::1/126")
        var tun = obj(
            "type" to "tun",
            "tag" to "tun-in",
            "address" to addresses,
            "auto_route" to true,
            "strict_route" to true,
            "stack" to settings.tunStack.raw,
        )
        if (settings.mtu > 0) tun = tun.with("mtu", JInt(settings.mtu.toLong()))

        val rules = ArrayList<Json>()
        if (settings.sniffing) rules.add(obj("action" to "sniff"))
        rules.add(obj("protocol" to "dns", "action" to "hijack-dns"))
        rules.add(obj("port" to 53, "action" to "hijack-dns"))
        rules.add(obj("ip_is_private" to true, "outbound" to DIRECT_TAG))
        if (settings.blockDomains.isNotEmpty()) {
            rules.add(obj("domain_suffix" to settings.blockDomains, "action" to "reject"))
        }
        if (settings.blockAds) {
            val sets = use(listOf(RuleSets.ADS))
            if (sets.isNotEmpty()) rules.add(obj("rule_set" to sets, "action" to "reject"))
        }
        if (settings.proxyDomains.isNotEmpty()) {
            rules.add(obj("domain_suffix" to settings.proxyDomains, "outbound" to PROXY_TAG))
        }
        if (settings.directDomains.isNotEmpty()) {
            rules.add(obj("domain_suffix" to settings.directDomains, "outbound" to DIRECT_TAG))
        }
        if (settings.routing == RoutingMode.RUSSIA_DIRECT) {
            rules.add(obj("domain_suffix" to russianSuffixes, "outbound" to DIRECT_TAG))
            val sets = use(listOf(RuleSets.GEOSITE_RU, RuleSets.GEOIP_RU))
            if (sets.isNotEmpty()) rules.add(obj("rule_set" to sets, "outbound" to DIRECT_TAG))
        }

        var route = obj(
            "rules" to rules,
            "final" to PROXY_TAG,
            "auto_detect_interface" to true,
            "default_domain_resolver" to DIRECT_DNS_TAG,
        )
        if (ruleSetDirectory != null && usedSets.isNotEmpty()) {
            route = route.with("rule_set", JArray(usedSets.map { tag ->
                obj(
                    "type" to "local",
                    "tag" to tag,
                    "format" to "binary",
                    "path" to File(ruleSetDirectory, "$tag.srs").absolutePath,
                )
            }))
        }

        return obj(
            "log" to obj("level" to settings.logLevel.raw, "timestamp" to true),
            "dns" to dns,
            "inbounds" to listOf(tun),
            "outbounds" to proxies + obj("type" to "direct", "tag" to DIRECT_TAG),
            "route" to route,
        )
    }

    const val LOCAL_PROXY_TAG = "mixed-in"

    /** The same config with a local SOCKS and HTTP proxy in place of the tun. */
    fun withLocalProxy(config: Json, port: Int): Json = config.with(
        "inbounds",
        arr(obj("type" to "mixed", "tag" to LOCAL_PROXY_TAG, "listen" to "127.0.0.1", "listen_port" to port)),
    )

    /**
     * A core for the proxy ping modes: a local proxy on 127.0.0.1 for each
     * server, routed to that server and nowhere else. Groups are left out;
     * pass their members. `ports` goes with `servers` by position.
     */
    fun probe(servers: List<Server>, ports: List<Int>, settings: AppSettings): Json {
        val inbounds = ArrayList<Json>()
        var proxies = ArrayList<Json>()
        val rules = ArrayList<Json>()
        servers.zip(ports).forEachIndexed { index, (server, port) ->
            if (server.members.isNotEmpty()) return@forEachIndexed
            val inTag = "in-$index"
            val outTag = "probe-$index"
            inbounds.add(obj("type" to "mixed", "tag" to inTag, "listen" to "127.0.0.1", "listen_port" to port))
            proxies.add(server.outbound.with("tag", JString(outTag)))
            rules.add(obj("inbound" to listOf(inTag), "outbound" to outTag))
        }
        if (settings.tlsFragment) proxies = ArrayList(fragmentTls(proxies))

        val dnsServers = arrayListOf(dnsServer(settings.directDns, DIRECT_DNS_TAG, null, BOOTSTRAP_DNS_TAG))
        if (!ShareLink.isIpAddress(settings.directDns.address)) {
            dnsServers.add(dnsServer(DnsServer.YANDEX, BOOTSTRAP_DNS_TAG, null, null))
        }
        return obj(
            "log" to obj("level" to "error"),
            "dns" to obj(
                "servers" to dnsServers,
                "final" to DIRECT_DNS_TAG,
                "strategy" to if (settings.ipv6) "prefer_ipv4" else "ipv4_only",
            ),
            "inbounds" to inbounds,
            "outbounds" to proxies + obj("type" to "direct", "tag" to DIRECT_TAG),
            "route" to obj(
                "rules" to rules,
                "final" to DIRECT_TAG,
                "auto_detect_interface" to true,
                "default_domain_resolver" to DIRECT_DNS_TAG,
            ),
            // The tunnel holds a lock on the default cache file; a second
            // core waits on it and fails to start.
            "experimental" to obj("cache_file" to obj("enabled" to true, "path" to "probe.db")),
        )
    }

    /**
     * Splits the TLS ClientHello of outbounds that run TLS over TCP. Reality
     * and QUIC protocols cannot take it.
     */
    private fun fragmentTls(outbounds: List<Json>): List<Json> = outbounds.map { proxy ->
        val tls = proxy["tls"]
        val type = proxy["type"]?.string
        if (tls?.get("enabled")?.bool == true && tls["reality"] == null &&
            type != ProxyProtocol.HYSTERIA2.raw && type != ProxyProtocol.TUIC.raw
        ) {
            proxy.with("tls", tls.with("fragment", JBool(true)))
        } else {
            proxy
        }
    }

    private fun dnsServer(server: DnsServer, tag: String, detour: String?, resolver: String?): Json {
        var json = obj("type" to server.kind.raw, "tag" to tag, "server" to server.address)
        if (detour != null) json = json.with("detour", JString(detour))
        if (resolver != null && !ShareLink.isIpAddress(server.address)) {
            // A DNS server given by name needs another server to find its address.
            json = json.with("domain_resolver", JString(resolver))
        }
        return json
    }
}

object RuleSets {
    const val GEOIP_RU = "geoip-ru"
    const val GEOSITE_RU = "geosite-category-ru"
    const val ADS = "geosite-category-ads-all"

    /** Primary source first, then a CDN mirror. */
    fun urls(tag: String): List<String> {
        val repo = if (tag.startsWith("geoip-")) "sing-geoip" else "sing-geosite"
        return listOf(
            "https://raw.githubusercontent.com/SagerNet/$repo/rule-set/$tag.srs",
            "https://cdn.jsdelivr.net/gh/SagerNet/$repo@rule-set/$tag.srs",
        )
    }

    fun needed(settings: AppSettings): List<String> {
        val tags = ArrayList<String>()
        if (settings.routing == RoutingMode.RUSSIA_DIRECT) tags += listOf(GEOSITE_RU, GEOIP_RU)
        if (settings.blockAds) tags.add(ADS)
        return tags
    }

    fun available(directory: File): Set<String> =
        (directory.list() ?: emptyArray()).filter { it.endsWith(".srs") }.map { it.dropLast(4) }.toSet()

    /** Binary rule-sets start with "SRS". */
    fun looksValid(data: ByteArray): Boolean =
        data.size > 3 && data[0] == 'S'.code.toByte() && data[1] == 'R'.code.toByte() && data[2] == 'S'.code.toByte()
}
