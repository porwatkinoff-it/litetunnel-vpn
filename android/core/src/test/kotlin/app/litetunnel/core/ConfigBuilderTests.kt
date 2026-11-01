package app.litetunnel.core

import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Before
import org.junit.Test
import java.io.File
import java.nio.file.Files

/** Ported from Packages/LiteKit/Tests/LiteKitTests/ConfigBuilderTests.swift. */
class ConfigBuilderTests {
    private lateinit var ruleDir: File

    @Before
    fun setUp() {
        ruleDir = Files.createTempDirectory("rulesets").toFile()
    }

    @After
    fun tearDown() {
        ruleDir.deleteRecursively()
    }

    private fun server(link: String) = Server(null, ShareLink.parse(link))

    private fun writeRuleSet(tag: String) = File(ruleDir, "$tag.srs").writeBytes("SRS\u0001".toByteArray())

    private fun strings(vararg s: String) = Json.strings(s.toList())

    @Test
    fun globalModeIsMinimal() {
        val settings = AppSettings(routing = RoutingMode.GLOBAL)
        val config = ConfigBuilder.build(server("hy2://pw@h.example.com:443#H"), settings, ruleDir)

        assertEquals("warn", config["log"]?.get("level")?.string)
        assertNull("no clash API or cache file in the tunnel", config["experimental"])
        assertNull(config["route"]?.get("rule_set"))
        assertEquals("proxy", config["route"]?.get("final")?.string)
        assertEquals(true, config["route"]?.get("auto_detect_interface")?.bool)

        val outbounds = config["outbounds"]!!.array!!
        assertEquals(listOf("proxy", "direct"), outbounds.mapNotNull { it["tag"]?.string })
        assertFalse(outbounds.any { it["type"]?.string in setOf("urltest", "selector") })

        val tun = config["inbounds"]!![0]!!
        assertEquals("tun", tun["type"]?.string)
        assertEquals(strings("172.19.0.1/30"), tun["address"])
        assertEquals("mixed", tun["stack"]?.string)
        assertNull("the core picks the MTU", tun["mtu"])

        val dns = config["dns"]!!
        assertEquals("dns-remote", dns["final"]?.string)
        assertEquals("ipv4_only", dns["strategy"]?.string)
        assertEquals("proxy", dns["servers"]?.get(0)?.get("detour")?.string)
        assertEquals("https", dns["servers"]?.get(0)?.get("type")?.string)
        assertEquals(0, dns["rules"]?.array?.size)
    }

    @Test
    fun russiaDirectUsesOnlyDownloadedRuleSets() {
        writeRuleSet("geoip-ru")
        // The ads list is not downloaded: its rule must be left out.
        val settings = AppSettings(blockAds = true)
        val config = ConfigBuilder.build(server("vless://id@v.example.com:443?security=tls#V"), settings, ruleDir)

        val sets = config["route"]!!["rule_set"]!!.array!!
        assertEquals(1, sets.size)
        assertEquals("geoip-ru", sets[0]["tag"]?.string)
        assertEquals("local", sets[0]["type"]?.string)
        assertEquals(File(ruleDir, "geoip-ru.srs").absolutePath, sets[0]["path"]?.string)
        assertTrue("libbox does not chdir; paths must be absolute", sets[0]["path"]!!.string!!.startsWith("/"))

        val rules = config["route"]!!["rules"]!!.array!!
        assertFalse(rules.any { it["action"]?.string == "reject" })
        assertTrue(rules.any { it["rule_set"] == strings("geoip-ru") && it["outbound"]?.string == "direct" })
        assertTrue(rules.any { it["domain_suffix"] == strings("ru", "su", "xn--p1ai") })

        // DNS rules may only use domain rule-sets; the geoip one must stay out.
        val dnsRules = config["dns"]!!["rules"]!!.array!!
        assertFalse(dnsRules.any { it["rule_set"] != null })
        assertEquals("dns-direct", dnsRules.first()["server"]?.string)
    }

    @Test
    fun ruleOrderAndCustomDomains() {
        for (tag in listOf(RuleSets.ADS, RuleSets.GEOSITE_RU, RuleSets.GEOIP_RU)) writeRuleSet(tag)
        val settings = AppSettings(
            blockAds = true,
            blockDomains = listOf("tracker.example"),
            proxyDomains = listOf("youtube.com"),
            directDomains = listOf("bank.example"),
        )
        val config = ConfigBuilder.build(server("hy2://pw@h.example.com:443#H"), settings, ruleDir)
        val rules = config["route"]!!["rules"]!!.array!!
        fun index(match: (Json) -> Boolean) = rules.indexOfFirst(match)

        val order = listOf(
            index { it["action"]?.string == "sniff" },
            index { it["action"]?.string == "hijack-dns" },
            index { it["domain_suffix"] == strings("tracker.example") },
            index { it["rule_set"] == strings(RuleSets.ADS) },
            index { it["domain_suffix"] == strings("youtube.com") },
            index { it["domain_suffix"] == strings("bank.example") },
            index { it["rule_set"] == strings(RuleSets.GEOSITE_RU, RuleSets.GEOIP_RU) },
        )
        assertEquals(order.sorted(), order)
        assertFalse(order.contains(-1))
        assertEquals(3, config["route"]?.get("rule_set")?.array?.size)

        val dnsRules = config["dns"]!!["rules"]!!.array!!
        assertTrue(dnsRules.any { it["rule_set"] == strings(RuleSets.GEOSITE_RU) })
        assertTrue(dnsRules.any { it["domain_suffix"] == strings("bank.example") })
    }

    @Test
    fun tlsFragmentSkipsRealityAndQuic() {
        val settings = AppSettings(tlsFragment = true)
        val tls = ConfigBuilder.build(server("trojan://pw@t.example.com:443#T"), settings, null)
        assertEquals(true, tls["outbounds"]?.get(0)?.get("tls")?.get("fragment")?.bool)
        val reality = ConfigBuilder.build(server("vless://id@v.example.com:443?security=reality&pbk=k#R"), settings, null)
        assertNull(reality["outbounds"]?.get(0)?.get("tls")?.get("fragment"))
        val hy2 = ConfigBuilder.build(server("hy2://pw@h.example.com:443#H"), settings, null)
        assertNull(hy2["outbounds"]?.get(0)?.get("tls")?.get("fragment"))
    }

    @Test
    fun dnsByNameGetsResolvers() {
        val settings = AppSettings(
            remoteDns = DnsServer(DnsServer.Kind.HTTPS, "dns.google"),
            directDns = DnsServer(DnsServer.Kind.TLS, "common.dot.dns.yandex.net"),
            ipv6 = true,
        )
        val config = ConfigBuilder.build(server("hy2://pw@h.example.com:443#H"), settings, null)
        val servers = config["dns"]!!["servers"]!!.array!!
        assertEquals(listOf("dns-remote", "dns-direct", "dns-bootstrap"), servers.map { it["tag"]?.string })
        assertEquals("dns-direct", servers[0]["domain_resolver"]?.string)
        assertEquals("dns-bootstrap", servers[1]["domain_resolver"]?.string)
        assertNull(servers[2]["domain_resolver"])
        assertEquals("prefer_ipv4", config["dns"]?.get("strategy")?.string)
        assertEquals(2, config["inbounds"]?.get(0)?.get("address")?.array?.size)
    }

    @Test
    fun outputIsValidJson() {
        val config = ConfigBuilder.build(server("vless://id@v.example.com:443?security=reality&pbk=k&sni=a.com#V"), AppSettings(), ruleDir)
        assertEquals(config, Json.parse(config.jsonString(pretty = true)))
        assertEquals(config, Json.parse(config.jsonString()))
    }

    @Test
    fun localProxyReplacesOnlyTheTun() {
        val config = ConfigBuilder.build(server("hy2://pw@h.example.com:443#H"), AppSettings(), ruleDir)
        val local = ConfigBuilder.withLocalProxy(config, 51080)

        val inbounds = local["inbounds"]!!.array!!
        assertEquals(1, inbounds.size)
        assertEquals("mixed", inbounds[0]["type"]?.string)
        assertEquals("never reachable from the network", "127.0.0.1", inbounds[0]["listen"]?.string)
        assertEquals(51080, inbounds[0]["listen_port"]?.int)
        for (key in listOf("log", "dns", "outbounds", "route")) assertEquals(key, config[key], local[key])
    }

    @Test
    fun probeRoutesEachPortToItsServer() {
        val servers = listOf(
            server("vless://11111111-1111-1111-1111-111111111111@a.example.com:443?security=tls&sni=a.example.com#A"),
            server("hy2://pw@h.example.com:443#H"),
        )
        val config = ConfigBuilder.probe(servers, listOf(20001, 20002), AppSettings(tlsFragment = true))

        assertEquals("error", config["log"]?.get("level")?.string)
        assertEquals("the tunnel locks the default cache file", "probe.db",
            config["experimental"]?.get("cache_file")?.get("path")?.string)
        assertNull(config["route"]?.get("rule_set"))
        assertEquals(true, config["route"]?.get("auto_detect_interface")?.bool)

        val inbounds = config["inbounds"]!!.array!!
        assertEquals(listOf(20001, 20002), inbounds.map { it["listen_port"]?.int })
        assertEquals(listOf("mixed", "mixed"), inbounds.map { it["type"]?.string })
        assertEquals(listOf("127.0.0.1", "127.0.0.1"), inbounds.map { it["listen"]?.string })

        assertEquals(
            listOf(
                obj("inbound" to listOf("in-0"), "outbound" to "probe-0"),
                obj("inbound" to listOf("in-1"), "outbound" to "probe-1"),
            ),
            config["route"]?.get("rules")?.array,
        )
        val outbounds = config["outbounds"]!!.array!!
        assertEquals(listOf("probe-0", "probe-1", "direct"), outbounds.map { it["tag"]?.string })
        assertEquals("a.example.com", outbounds[0]["server"]?.string)
        assertEquals(true, outbounds[0]["tls"]?.get("fragment")?.bool)
        assertNull("QUIC cannot fragment", outbounds[1]["tls"]?.get("fragment"))

        // Only the direct resolver: nothing goes through a remote one.
        assertEquals(listOf("dns-direct"), config["dns"]!!["servers"]!!.array!!.map { it["tag"]?.string })
        assertEquals(config, Json.parse(config.jsonString()))
    }

    @Test
    fun probeSkipsGroups() {
        val members = listOf("a", "b").map { ShareLink.parse("hy2://pw@$it.example.com:443#$it") }
        val group = Server(null, ServerGroup.make(members, "Авто"))
        val config = ConfigBuilder.probe(listOf(group) + group.members, listOf(1, 2, 3), AppSettings())
        assertEquals(listOf(2, 3), config["inbounds"]?.array?.map { it["listen_port"]?.int })
    }
}
