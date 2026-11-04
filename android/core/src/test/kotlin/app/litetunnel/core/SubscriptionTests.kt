package app.litetunnel.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test
import java.nio.file.Files
import java.time.Instant
import java.util.Base64
import java.util.UUID

/** Ported from Packages/LiteKit/Tests/LiteKitTests/SubscriptionTests.swift. */
class SubscriptionTests {
    private val lines = listOf(
        "vless://id-1@nl.example.com:443?security=reality&pbk=key&sni=www.microsoft.com&flow=xtls-rprx-vision#NL",
        "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
        "vless://id-2@fi.example.com:443?security=reality&pbk=key&type=xhttp#FI xhttp",
        "happ://routing/add/eyJOYW1lIjoiUlUifQ==",
        "# comment",
    )

    private fun parse(body: String, headers: Map<String, String> = emptyMap()) =
        SubscriptionParser.parse(body.toByteArray(), headers)

    private fun base64(s: String) = Base64.getEncoder().encodeToString(s.toByteArray())

    private inline fun <reified E : Throwable> assertThrows(block: () -> Unit): E {
        try {
            block()
        } catch (e: Throwable) {
            if (e is E) return e
            throw AssertionError("expected ${E::class.simpleName}, got $e", e)
        }
        throw AssertionError("expected ${E::class.simpleName}, nothing thrown")
    }

    @Test
    fun base64BodyWithHeaders() {
        val headers = mapOf(
            "Profile-Title" to "base64:" + base64("Мой VPN"),
            "Profile-Update-Interval" to "12",
            "Subscription-Userinfo" to "upload=100; download=900; total=10000; expire=1893456000",
            "Support-Url" to "https://t.me/support",
            "Announce" to "%D0%9F%D1%80%D0%B8%D0%B2%D0%B5%D1%82",
        )
        val result = parse(base64(lines.joinToString("\n")), headers)
        assertEquals(listOf("NL", "DE"), result.servers.map { it.name })
        assertEquals(1, result.skipped.size)
        assertTrue(result.skipped[0].reason.contains("XHTTP"))
        assertEquals(1, result.routingLines.size)
        assertEquals("Мой VPN", result.meta.title)
        assertEquals(12, result.meta.updateIntervalHours)
        assertEquals(1000L, result.meta.usage?.used)
        assertEquals(0.1, result.meta.usage?.fraction!!, 1e-12)
        assertEquals(Instant.ofEpochSecond(1893456000), result.meta.usage?.expire)
        assertEquals("https://t.me/support", result.meta.supportUrl)
        assertEquals("Привет", result.meta.announce)
    }

    @Test
    fun plainBodyAndUrlSafeBase64() {
        assertEquals(2, parse(lines.joinToString("\r\n")).servers.size)
        val urlSafe = base64(lines[1]).replace('+', '-').replace('/', '_').replace("=", "")
        assertEquals(ProxyProtocol.HYSTERIA2, parse(urlSafe).servers.first().proto)
    }

    @Test
    fun singBoxJsonBody() {
        val json = """
            {"outbounds":[
              {"type":"hysteria2","tag":"JP","server":"jp.example.com","server_port":443,"password":"x","detour":"other"},
              {"type":"direct","tag":"direct"},
              {"type":"selector","tag":"auto","outbounds":["JP"]}
            ]}
        """.trimIndent()
        val result = parse(json)
        assertEquals(1, result.servers.size)
        assertEquals("JP", result.servers[0].name)
        assertNull(result.servers[0].outbound["tag"])
        assertNull(result.servers[0].outbound["detour"])
    }

    @Test
    fun xrayJsonBody() {
        // As Remnawave serves it to Happ: one full Xray config per server.
        val json = """
            [
              {"remarks":"🇳🇱 NL","dns":{},"routing":{},"inbounds":[{"protocol":"socks","port":10808}],"outbounds":[
                {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"nl.example.com","port":443,
                  "users":[{"id":"id-1","encryption":"none","flow":"xtls-rprx-vision"}]}]},
                 "streamSettings":{"network":"tcp","security":"reality","realitySettings":{"serverName":"www.microsoft.com",
                  "fingerprint":"chrome","publicKey":"key","shortId":"ab","spiderX":""}}},
                {"tag":"direct","protocol":"freedom"},{"tag":"block","protocol":"blackhole"}]},
              {"remarks":"DE ws","outbounds":[
                {"tag":"proxy","protocol":"vless","settings":{"address":"de.example.com","port":8443,"id":"id-2","encryption":"none"},
                 "streamSettings":{"network":"ws","security":"tls","tlsSettings":{"serverName":"cdn.example.com","alpn":["h2","http/1.1"],
                  "allowInsecure":true},"wsSettings":{"path":"/ws?ed=2048","headers":{"Host":"cdn.example.com"}}}}]},
              {"remarks":"FI","outbounds":[
                {"tag":"proxy","protocol":"trojan","settings":{"servers":[{"address":"fi.example.com","port":443,"password":"pw"}]},
                 "streamSettings":{"network":"grpc","security":"tls","grpcSettings":{"serviceName":"svc"}}},
                {"tag":"proxy-2","protocol":"shadowsocks","settings":{"servers":[{"address":"1.2.3.4","port":8388,
                  "method":"2022-BLAKE3-AES-128-GCM","password":"AAAAAAAAAAAAAAAAAAAAAA=="}]}}]},
              {"remarks":"JP hy2","outbounds":[
                {"tag":"proxy","protocol":"hysteria","settings":{"version":2,"address":"jp.example.com","port":443},
                 "streamSettings":{"network":"hysteria","security":"tls","tlsSettings":{"serverName":"jp.example.com","alpn":["h3"],
                  "fingerprint":"chrome"},"hysteriaSettings":{"version":2,"auth":"secret"}}}]},
              {"remarks":"US xhttp","outbounds":[
                {"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"us.example.com","port":443,"users":[{"id":"id-3"}]}]},
                 "streamSettings":{"network":"xhttp","security":"reality","realitySettings":{"publicKey":"key"}}}]}
            ]
        """.trimIndent()
        val result = parse(json)
        assertEquals(listOf("🇳🇱 NL", "DE ws", "FI · proxy", "FI · proxy-2", "JP hy2"), result.servers.map { it.name })
        assertEquals(
            listOf(ProxyProtocol.VLESS, ProxyProtocol.VLESS, ProxyProtocol.TROJAN, ProxyProtocol.SHADOWSOCKS, ProxyProtocol.HYSTERIA2),
            result.servers.map { it.proto },
        )
        assertEquals(listOf("US xhttp"), result.skipped.map { it.line })
        assertTrue(result.skipped[0].reason.contains("XHTTP"))

        val nl = result.servers[0].outbound
        assertEquals("id-1", nl["uuid"]?.string)
        assertEquals("xtls-rprx-vision", nl["flow"]?.string)
        assertEquals("www.microsoft.com", nl["tls"]?.get("server_name")?.string)
        assertEquals("key", nl["tls"]?.get("reality")?.get("public_key")?.string)
        assertEquals("ab", nl["tls"]?.get("reality")?.get("short_id")?.string)
        assertNull(nl["transport"])

        val de = result.servers[1].outbound
        assertEquals(8443, de["server_port"]?.int)
        assertEquals(true, de["tls"]?.get("insecure")?.bool)
        assertEquals(Json.strings(listOf("h2", "http/1.1")), de["tls"]?.get("alpn"))
        assertEquals("/ws", de["transport"]?.get("path")?.string)
        assertEquals(2048, de["transport"]?.get("max_early_data")?.int)
        assertEquals("cdn.example.com", de["transport"]?.get("headers")?.get("Host")?.string)

        assertEquals("svc", result.servers[2].outbound["transport"]?.get("service_name")?.string)
        assertEquals("fi.example.com", result.servers[2].outbound["tls"]?.get("server_name")?.string)
        assertEquals("2022-blake3-aes-128-gcm", result.servers[3].outbound["method"]?.string)

        val jp = result.servers[4].outbound
        assertEquals("secret", jp["password"]?.string)
        assertEquals(Json.strings(listOf("h3")), jp["tls"]?.get("alpn"))
        assertNull(jp["tls"]?.get("utls"))
    }

    @Test
    fun xrayJsonSingleConfig() {
        val json = """
            {"outbounds":[{"tag":"proxy","protocol":"vmess","settings":{"vnext":[{"address":"vm.example.com","port":443,
              "users":[{"id":"id","alterId":0,"security":"auto"}]}]},"streamSettings":{"network":"tcp"}},
              {"protocol":"freedom","tag":"direct"}]}
        """.trimIndent()
        val result = parse(json)
        assertEquals(listOf("proxy"), result.servers.map { it.name })
        assertEquals("id", result.servers[0].outbound["uuid"]?.string)
        assertNull(result.servers[0].outbound["tls"])
        val error = assertThrows<SubscriptionError> { parse("""{"foo":1}""") }
        assertEquals(SubscriptionError.NothingSupported("Неизвестный формат JSON"), error)
    }

    @Test
    fun xrayJsonBalancer() {
        val reality = """"security":"reality","realitySettings":{"serverName":"sni.example.com","publicKey":"pbk","shortId":"ab","fingerprint":"chrome"}"""
        val json = """
            [{"remarks":"Авто-выбор","outbounds":[
              {"tag":"proxy-1","protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
               "streamSettings":{"network":"grpc","grpcSettings":{"serviceName":"g"},$reality}},
              {"tag":"proxy-2","protocol":"vless","settings":{"vnext":[{"address":"b.example.com","port":443,"users":[{"id":"id","flow":"xtls-rprx-vision","encryption":"none"}]}]},
               "streamSettings":{"network":"tcp",$reality}},
              {"tag":"proxy-3","protocol":"vless","settings":{"vnext":[{"address":"c.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
               "streamSettings":{"network":"xhttp",$reality}},
              {"protocol":"freedom","tag":"direct"}],
              "routing":{"balancers":[{"tag":"auto","selector":["proxy"]}]}}]
        """.trimIndent()
        val result = parse(json)
        assertEquals(listOf("Авто-выбор"), result.servers.map { it.name })
        assertEquals(listOf("Авто-выбор · proxy-3"), result.skipped.map { it.line })

        val server = Server(null, result.servers[0])
        assertEquals(listOf("a.example.com", "b.example.com"), server.members.map { it.host })
        assertEquals("Самый быстрый из 2", server.detail)
        assertEquals("Самый быстрый из 2", server.subtitle)

        val config = ConfigBuilder.build(server, AppSettings(), null)
        val outbounds = config["outbounds"]?.array.orEmpty()
        assertEquals(listOf("proxy", "proxy-1", "proxy-2", "direct"), outbounds.mapNotNull { it["tag"]?.string })
        assertEquals("urltest", outbounds[0]["type"]?.string)
        assertEquals(Json.strings(listOf("proxy-1", "proxy-2")), outbounds[0]["outbounds"])
        assertEquals("xtls-rprx-vision", outbounds[2]["flow"]?.string)
    }

    @Test
    fun emptyAndUnsupportedBodies() {
        assertEquals(SubscriptionError.Empty, assertThrows<SubscriptionError> { parse("") })
        assertThrows<SubscriptionError.NothingSupported> { parse("wireguard://a@b:1") }
    }

    @Test
    fun xrayJsonBehindCommentsOrBase64() {
        val config = """[{"remarks":"A","outbounds":[{"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"00000000-0000-0000-0000-000000000000","encryption":"none"}]}]}}]}]"""
        val commented = "#profile-title: X\r\n#announce: Y\r\n\r\n$config"
        val encoder = Base64.getEncoder()
        for (body in listOf(
            commented,
            "\uFEFF$commented",
            encoder.encodeToString(config.toByteArray()),
            encoder.encodeToString(commented.toByteArray()),
        )) {
            assertEquals(listOf("A"), parse(body).servers.map { it.name })
        }
    }

    @Test
    fun noServersSaysWhatCameInstead() {
        val cases = listOf(
            " \n".toByteArray() to SubscriptionError.Empty,
            byteArrayOf(-1, -2, 0) to SubscriptionError.NoServers("пришли двоичные данные"),
            "<!doctype html><html></html>".toByteArray() to SubscriptionError.NoServers("пришла веб-страница"),
            """[{"outbounds":[{"tag":"direct","protocol":"freedom"}]}]""".toByteArray() to SubscriptionError.NoServers("пришли настройки без прокси"),
            "someapp://crypt/AAAA\nsomeapp://crypt/BBBB".toByteArray() to SubscriptionError.NoServers("пришли ссылки someapp://"),
            "Client not supported".toByteArray() to SubscriptionError.NoServers("сервер ответил «Client not supported»"),
            "AAAA\nBBBB".toByteArray() to SubscriptionError.NoServers("пришёл текст, 9 байт"),
        )
        for ((body, expected) in cases) {
            assertEquals(expected, assertThrows<SubscriptionError> { SubscriptionParser.parse(body, emptyMap()) })
        }
    }

    @Test
    fun applyKeepsIdsSelectionAndPosition() {
        var state = AppState().addManual(listOf(ShareLink.parse("trojan://pw@m.example.com:443#Manual"))).first
        val sub = Subscription(name = "sub", url = "https://s.example.com/x")
        state = state.copy(subscriptions = state.subscriptions + sub)

        state = state.apply(parse(lines.joinToString("\n")), sub.id)
        assertEquals(listOf("Manual", "Авто-выбор", "NL", "DE"), state.servers.map { it.name })
        assertEquals(1, state.subscriptions[0].skippedCount)
        assertTrue(state.subscriptions[0].skippedReasons.single().contains("XHTTP"))
        assertFalse("the skipped line holds keys", state.subscriptions[0].skippedReasons.single().contains("://"))
        val de = state.servers[3]
        state = state.copy(selectedServerId = de.id).updateServer(de.id) { it.copy(lastPing = PingResult.Ms(42)) }

        // Provider drops NL, keeps DE, adds US.
        val second = parse(
            listOf(
                "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
                "vless://id-3@us.example.com:443?security=tls#US",
            ).joinToString("\n"),
            mapOf("profile-title" to "Renamed"),
        )
        state = state.apply(second, sub.id)
        assertEquals(listOf("Manual", "Авто-выбор", "DE", "US"), state.servers.map { it.name })
        assertEquals(de.id, state.servers[2].id)
        assertEquals(PingResult.Ms(42), state.servers[2].lastPing)
        assertEquals(de.id, state.selectedServerId)
        assertEquals("Renamed", state.subscriptions[0].name)

        // Provider drops the selected server: selection moves to its first server.
        state = state.apply(parse("vless://id-3@us.example.com:443?security=tls#US"), sub.id)
        assertEquals("US", state.selectedServer?.name)

        state = state.removeSubscription(sub.id)
        assertEquals(listOf("Manual"), state.servers.map { it.name })
        assertEquals("Manual", state.selectedServer?.name)
    }

    // auto row

    private fun parsed(vararg names: String) = names.map { ShareLink.parse("trojan://pw@$it.example.com:443#$it") }

    private fun result(servers: List<ParsedServer>) = SubscriptionResult(servers.toMutableList())

    private fun subscribed(servers: List<ParsedServer>, state: AppState = AppState()): Pair<AppState, UUID> {
        val sub = Subscription(name = "sub", url = "https://s.example.com/x")
        return state.copy(subscriptions = state.subscriptions + sub).apply(result(servers), sub.id) to sub.id
    }

    @Test
    fun everySubscriptionGetsAnAutoRow() {
        var (state, id) = subscribed(parsed("a", "b"))
        assertEquals(listOf("Авто-выбор", "a", "b"), state.servers.map { it.name })

        // A new subscription starts on it, and it lists the servers by name.
        val auto = state.servers[0]
        assertTrue(auto.isAuto)
        assertEquals(auto.id, state.selectedServerId)
        assertNull(auto.shareLink)
        assertEquals("Самый быстрый из 2", auto.subtitle)
        assertEquals(listOf("a", "b"), auto.members.map { it.name })
        assertEquals(2, state.serverCount(id))

        // A refresh keeps the row and adds the new server to it.
        state = state.updateServer(auto.id) { it.copy(lastPing = PingResult.Ms(30)) }
        state = state.apply(result(parsed("a", "b", "c")), id)
        assertEquals(auto.id, state.servers[0].id)
        assertEquals(PingResult.Ms(30), state.servers[0].lastPing)
        assertEquals(listOf("a", "b", "c"), state.servers[0].members.map { it.name })
        assertEquals(auto.id, state.selectedServerId)

        // With one server left there is nothing to choose from.
        state = state.apply(result(parsed("c")), id)
        assertEquals(listOf("c"), state.servers.map { it.name })
        assertEquals("c", state.selectedServer?.name)
    }

    @Test
    fun autoRowRunsAsAPlainGroup() {
        val (state) = subscribed(parsed("a", "b"))
        val outbounds = ConfigBuilder.build(state.servers[0], AppSettings(), null)["outbounds"]?.array.orEmpty()
        assertEquals(listOf("proxy", "proxy-1", "proxy-2", "direct"), outbounds.mapNotNull { it["tag"]?.string })
        assertEquals("urltest", outbounds[0]["type"]?.string)
        assertEquals("10m", outbounds[0]["interval"]?.string)
        assertNull(outbounds[0]["auto"])
    }

    @Test
    fun providerGroupCoveringEverythingIsEnough() {
        val singles = parsed("a", "b")
        val (state, id) = subscribed(listOf(ServerGroup.make(singles, "Группа")) + singles)
        assertEquals(listOf("Группа", "a", "b"), state.servers.map { it.name })
        assertEquals(2, state.serverCount(id))

        // The same with nothing but the group in the subscription.
        val (only, onlyId) = subscribed(listOf(ServerGroup.make(singles, "Группа")))
        assertEquals(listOf("Группа"), only.servers.map { it.name })
        assertEquals(2, only.serverCount(onlyId))
    }

    @Test
    fun providerGroupOverSomeServersGetsAnAutoRowOverAll() {
        val (state, id) = subscribed(listOf(ServerGroup.make(parsed("a", "b"), "Группа")) + parsed("a", "c"))
        assertEquals(listOf("Авто-выбор", "Группа", "a", "c"), state.servers.map { it.name })
        assertEquals(listOf("a", "c", "b.example.com:443"), state.servers[0].members.map { it.name })
        assertEquals(3, state.serverCount(id))
    }

    @Test
    fun syncAddsAutoRowToOlderStateOnce() {
        val sub = Subscription(name = "sub", url = "https://s.example.com/x")
        val rows = parsed("a", "b").map { Server(sub.id, it) }
        val older = AppState(subscriptions = listOf(sub), servers = rows, selectedServerId = rows[1].id)

        val state = older.syncAutoGroups()
        assertEquals(listOf("Авто-выбор", "a", "b"), state.servers.map { it.name })
        assertEquals("b", state.selectedServer?.name)

        val again = state.syncAutoGroups()
        assertEquals(state, again)
        assertEquals(state.servers[0].id, again.servers[0].id)
    }

    @Test
    fun sortKeepsGroupsOnTop() {
        val (start) = subscribed(parsed("b", "a"))
        val pings = listOf(PingResult.Ms(10), PingResult.Ms(90), PingResult.Ms(10))
        val state = start.copy(servers = start.servers.mapIndexed { i, s -> s.copy(lastPing = pings[i]) })
        val byName = state.copy(settings = state.settings.copy(sortMode = SortMode.NAME))
        assertEquals(listOf("Авто-выбор", "a", "b"), byName.sorted(byName.servers).map { it.name })
        val byPing = state.copy(settings = state.settings.copy(sortMode = SortMode.PING))
        assertEquals(listOf("Авто-выбор", "a", "b"), byPing.sorted(byPing.servers).map { it.name })
    }

    @Test
    fun groupIsPingedThroughItsRows() {
        var (state, id) = subscribed(parsed("a", "b"))
        val (auto, a, b) = state.servers

        // Each server once, whatever is asked for.
        assertEquals(listOf(a.id, b.id), state.pingTargets(listOf(auto)).map { it.id })
        assertEquals(listOf(a.id, b.id), state.pingTargets(state.servers(id)).map { it.id })
        assertEquals(listOf(b.id), state.pingTargets(listOf(b)).map { it.id })

        // The group shows its fastest as results come in.
        state = state.settingPing(PingResult.Ms(80), a.id)
        assertEquals(PingResult.Ms(80), state.servers[0].lastPing)
        state = state.settingPing(PingResult.Ms(20), b.id)
        assertEquals(PingResult.Ms(20), state.servers[0].lastPing)
        state = state.settingPing(PingResult.Failed, b.id)
        assertEquals(PingResult.Ms(80), state.servers[0].lastPing)
        state = state.settingPing(PingResult.Failed, a.id)
        assertEquals(PingResult.Failed, state.servers[0].lastPing)
    }

    @Test
    fun groupWithoutItsRowsIsPingedItself() {
        val group = ServerGroup.make(parsed("a", "b"), "Группа")
        var (state) = subscribed(listOf(group))
        val row = state.servers[0]
        assertEquals(listOf(row.id), state.pingTargets(listOf(row)).map { it.id })
        state = state.settingPing(PingResult.Ms(50), row.id)
        assertEquals(PingResult.Ms(50), state.servers[0].lastPing)

        // Servers added by hand are never treated as a group's rows.
        val manual = AppState().addManual(listOf(group) + parsed("a", "b")).first
        assertEquals(listOf(manual.servers[0].id), manual.pingTargets(listOf(manual.servers[0])).map { it.id })
    }

    @Test
    fun sortByPingIsStable() {
        var state = AppState().addManual(listOf("a", "b", "c", "d").map { ShareLink.parse("trojan://pw@$it.example.com:443#$it") }).first
        val pings = listOf(PingResult.Failed, PingResult.Ms(80), null, PingResult.Ms(20))
        state = state.copy(
            servers = state.servers.mapIndexed { i, s -> s.copy(lastPing = pings[i]) },
            settings = state.settings.copy(sortMode = SortMode.PING),
        )
        assertEquals(listOf("d", "b", "a", "c"), state.sorted(state.servers).map { it.name })
    }

    @Test
    fun stateRoundTripsThroughStore() {
        val dir = Files.createTempDirectory("litetunnel").toFile()
        try {
            val store = Store(dir)
            assertTrue(store.loadState().servers.isEmpty())

            var state = AppState().addManual(listOf(ShareLink.parse("hy2://pw@h.example.com:443?obfs=salamander&obfs-password=x#H"))).first
            state = state.copy(settings = state.settings.copy(blockAds = true))
            store.save(state)
            val loaded = store.loadState()
            assertEquals(state.servers, loaded.servers)
            assertEquals(state.selectedServerId, loaded.selectedServerId)
            assertTrue(loaded.settings.blockAds)
            assertTrue(store.writeConfig(loaded))
            assertTrue(store.readConfig()!!.contains("\"salamander\""))
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun stateRoundTripKeepsSubscriptionsAndSettings() {
        val sub = Subscription(
            name = "Дом", url = "https://s.example.com/x", updateIntervalHours = 6,
            lastUpdated = Instant.ofEpochSecond(1_700_000_000), usage = Usage(1, 2, 3, Instant.ofEpochSecond(1_800_000_000)),
            supportUrl = "https://t.me/x", announce = "Привет", skippedCount = 2,
            skippedReasons = listOf("XHTTP (нет в ядре sing-box)"),
        )
        val state = AppState(
            subscriptions = listOf(sub),
            settings = AppSettings(routing = RoutingMode.GLOBAL, directDomains = listOf("bank.example"), mtu = 1400,
                remoteDns = DnsServer(DnsServer.Kind.TLS, "dns.google"), sortMode = SortMode.NAME,
                pingMode = PingMode.PROXY_HEAD, pingUrl = "https://cp.example.com/", pingDisplay = PingDisplay.DOTS,
                pingOnUpdate = true),
        )
        assertEquals(state, StateCodec.decode(Json.parse(StateCodec.encode(state).jsonString())))
        // Missing keys take their defaults.
        assertEquals(AppState(), StateCodec.decode(Json.parse("{}")))
    }

    @Test
    fun deviceLimitHeaders() {
        val placeholder = "vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1#App not supported"
        assertEquals(
            SubscriptionError.DeviceIdRefused,
            assertThrows<SubscriptionError> { parse(placeholder, mapOf("X-Hwid-Active" to "true", "X-Hwid-Not-Supported" to "true")) },
        )
        assertEquals(
            SubscriptionError.DeviceLimit,
            assertThrows<SubscriptionError> { parse(placeholder, mapOf("x-hwid-max-devices-reached" to "true")) },
        )
        parse(placeholder, mapOf("x-hwid-active" to "true", "x-hwid-not-supported" to "false"))
    }

    @Test
    fun deviceInfo() {
        val hwid = DeviceInfo.hwid("9774d56d682e549c")
        assertTrue(Regex("^[A-Za-z0-9=-]{10,64}$").matches(hwid))
        assertEquals(hwid, DeviceInfo.hwid("9774d56d682e549c"))
        assertFalse(hwid == DeviceInfo.hwid("other"))
        val info = DeviceInfo(hwid, "Android", "15", "Pixel «8» Про")
        assertEquals(hwid, info.headers["x-hwid"])
        assertEquals("Pixel _8_ ___", info.headers["x-device-model"])
        assertEquals("Android", info.headers["x-device-os"])
    }

    @Test
    fun importer() {
        assertEquals(ImportItem.Subscription("https://sub.example.com/abc", null), Importer.classify(" https://sub.example.com/abc \n"))
        assertEquals(
            ImportItem.Subscription("https://sub.example.com/abc", null),
            Importer.classify("litetunnel://add/https%3A%2F%2Fsub.example.com%2Fabc"),
        )
        assertEquals(
            ImportItem.Subscription("https://s.example.com/1", "Home"),
            Importer.classify("litetunnel://import?url=https%3A%2F%2Fs.example.com%2F1&name=Home"),
        )
        val servers = Importer.classify("hy2://pw@h.example.com:443#H\nvless://id@v.example.com:443#V")
        if (servers !is ImportItem.Servers) fail("$servers") else {
            assertEquals(listOf(ProxyProtocol.HYSTERIA2, ProxyProtocol.VLESS), servers.servers.map { it.proto })
        }
        assertTrue(Importer.classify("hello") is ImportItem.Nothing)
    }

    @Test
    fun jsonFormatUrl() {
        val cases = listOf(
            "https://s.example.com/abc/" to "https://s.example.com/abc/json",
            "https://s.example.com/abc" to "https://s.example.com/abc/json",
            "https://s.example.com:8443/a%20b/?k=1" to "https://s.example.com:8443/a%20b/json?k=1",
        )
        for ((plain, json) in cases) assertEquals(json, SubscriptionClient.jsonFormatUrl(plain))
        for (address in listOf("https://s.example.com/abc/json", "https://s.example.com/abc/json/", "https://s.example.com", "https://s.example.com/")) {
            assertNull(address, SubscriptionClient.jsonFormatUrl(address))
        }
    }

    @Test
    fun blankAnswerAsksForJson() {
        val asked = ArrayList<String>()
        val answer = SubscriptionClient.withJsonFallback("https://s.example.com/abc/") { url ->
            asked += url
            if (asked.size == 1) throw SubscriptionError.Empty
            7
        }
        assertEquals(7, answer)
        assertEquals(listOf("https://s.example.com/abc/", "https://s.example.com/abc/json"), asked)
    }

    @Test
    fun jsonFallbackKeepsTheErrorWorthTelling() {
        fun outcome(address: String, vararg answers: SubscriptionError): Pair<SubscriptionError?, Int> {
            var count = 0
            return try {
                SubscriptionClient.withJsonFallback<Int>(address) { throw answers[count++] }
                null to count
            } catch (e: SubscriptionError) {
                e to count
            }
        }
        val plain = "https://s.example.com/abc/"
        assertEquals(SubscriptionError.Empty to 2, outcome(plain, SubscriptionError.Empty, SubscriptionError.Http(404)))
        assertEquals(SubscriptionError.DeviceLimit to 2, outcome(plain, SubscriptionError.Empty, SubscriptionError.DeviceLimit))
        assertEquals("only a blank answer asks again", SubscriptionError.Http(500) to 1, outcome(plain, SubscriptionError.Http(500)))
        assertEquals("the address already asks for JSON", SubscriptionError.Empty to 1, outcome(plain + "json", SubscriptionError.Empty))
    }
}
