package app.litetunnel.core

import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test
import java.util.Base64

/** Ported from Packages/LiteKit/Tests/LiteKitTests/ShareLinkTests.swift. */
class ShareLinkTests {
    private fun base64(s: String) = Base64.getEncoder().encodeToString(s.toByteArray())

    private fun parseError(link: String): ShareLinkError = try {
        ShareLink.parse(link)
        throw AssertionError("parsed: $link")
    } catch (e: ShareLinkError) {
        e
    }

    @Test
    fun vlessRealityVision() {
        val link = "vless://b831381d-6324-4d53-ad4f-8cda48b30811@vpn.example.com:443?encryption=none&flow=xtls-rprx-vision" +
            "&security=reality&sni=www.microsoft.com&fp=firefox&pbk=Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw" +
            "&sid=6ba85179e30d4fc2&type=tcp#%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B"
        val s = ShareLink.parse(link)
        assertEquals(ProxyProtocol.VLESS, s.proto)
        assertEquals("Нидерланды", s.name)
        assertEquals("vpn.example.com", s.host)
        assertEquals(443, s.port)
        val o = s.outbound
        assertEquals("b831381d-6324-4d53-ad4f-8cda48b30811", o["uuid"]?.string)
        assertEquals("xtls-rprx-vision", o["flow"]?.string)
        assertEquals("www.microsoft.com", o["tls"]?.get("server_name")?.string)
        assertEquals("firefox", o["tls"]?.get("utls")?.get("fingerprint")?.string)
        assertEquals("Z84J2IelR9ch3k8VtlVhhs5ycBUlXA7wHBWcBrjqnAw", o["tls"]?.get("reality")?.get("public_key")?.string)
        assertEquals("6ba85179e30d4fc2", o["tls"]?.get("reality")?.get("short_id")?.string)
        assertNull(o["transport"])
        assertNull(o["tag"])
        assertEquals("Reality · Vision", Server(null, s).detail)
    }

    @Test
    fun realityDefaultsToChromeFingerprint() {
        val s = ShareLink.parse("vless://id@1.2.3.4:443?security=reality&pbk=key&sni=a.com")
        assertEquals("chrome", s.outbound["tls"]?.get("utls")?.get("fingerprint")?.string)
        assertEquals("", s.outbound["tls"]?.get("reality")?.get("short_id")?.string)
    }

    @Test
    fun realityWithoutKeyIsRejected() {
        parseError("vless://id@1.2.3.4:443?security=reality&sni=a.com")
    }

    @Test
    fun vlessWebSocketEarlyData() {
        val s = ShareLink.parse("vless://id@1.2.3.4:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com#ws")
        val t = s.outbound["transport"]
        assertEquals("ws", t?.get("type")?.string)
        assertEquals("/ws", t?.get("path")?.string)
        assertEquals(2048, t?.get("max_early_data")?.int)
        assertEquals("cdn.example.com", t?.get("headers")?.get("Host")?.string)
        // No sni: falls back to the Host header, not the IP.
        assertEquals("cdn.example.com", s.outbound["tls"]?.get("server_name")?.string)
    }

    @Test
    fun vlessGrpc() {
        val s = ShareLink.parse("vless://id@g.example.com:443?security=tls&type=grpc&serviceName=api#g")
        assertEquals("grpc", s.outbound["transport"]?.get("type")?.string)
        assertEquals("api", s.outbound["transport"]?.get("service_name")?.string)
        assertEquals("g.example.com", s.outbound["tls"]?.get("server_name")?.string)
    }

    // Through Xray

    private fun Json?.at(vararg path: Any): Json? = path.fold(this) { node, key ->
        when (key) {
            is Int -> node?.get(key)
            else -> node?.get(key.toString())
        }
    }

    @Test
    fun xhttpRunsThroughXray() {
        val s = ShareLink.parse("vless://id@x.example.com:443?security=reality&pbk=k&sid=ab&type=xhttp&path=%2Fx#X")
        assertEquals(ProxyProtocol.VLESS, s.proto)
        assertEquals("x.example.com", s.host)
        assertEquals(443, s.port)
        assertEquals("xray", s.outbound["type"]?.string)
        val x = s.outbound["outbound"]
        assertEquals("vless", x.at("protocol")?.string)
        assertEquals("x.example.com", x.at("settings", "vnext", 0, "address")?.string)
        assertEquals("id", x.at("settings", "vnext", 0, "users", 0, "id")?.string)
        assertEquals("none", x.at("settings", "vnext", 0, "users", 0, "encryption")?.string)
        assertEquals("xhttp", x.at("streamSettings", "network")?.string)
        assertEquals("/x", x.at("streamSettings", "xhttpSettings", "path")?.string)
        assertEquals("auto", x.at("streamSettings", "xhttpSettings", "mode")?.string)
        assertEquals("k", x.at("streamSettings", "realitySettings", "publicKey")?.string)
        assertEquals("ab", x.at("streamSettings", "realitySettings", "shortId")?.string)
        assertEquals("chrome", x.at("streamSettings", "realitySettings", "fingerprint")?.string)
        assertEquals("x.example.com", x.at("streamSettings", "realitySettings", "serverName")?.string)
        assertEquals("VLESS · Reality · XHTTP · Xray", Server(null, s).subtitle)
    }

    @Test
    fun xhttpExtraAndMode() {
        val extra = java.net.URLEncoder.encode("""{"xPaddingBytes":"100-1000","noGRPCHeader":true,"path":"/ignored"}""", "UTF-8")
        val link = "vless://id@1.2.3.4:443?security=tls&sni=a.com&type=xhttp&mode=packet-up&host=cdn.com&path=%2Fp&extra=$extra"
        val x = ShareLink.parse(link).outbound.at("outbound", "streamSettings")
        assertEquals("100-1000", x.at("xhttpSettings", "xPaddingBytes")?.string)
        assertEquals(true, x.at("xhttpSettings", "noGRPCHeader")?.bool)
        // The link's own keys win over extra.
        assertEquals("/p", x.at("xhttpSettings", "path")?.string)
        assertEquals("cdn.com", x.at("xhttpSettings", "host")?.string)
        assertEquals("packet-up", x.at("xhttpSettings", "mode")?.string)
        assertEquals("a.com", x.at("tlsSettings", "serverName")?.string)
    }

    @Test
    fun vlessEncryptionRunsThroughXray() {
        val s = ShareLink.parse("vless://id@e.example.com:443?encryption=mlkem768x25519plus.native.0rtt.key&flow=xtls-rprx-vision&type=tcp#E")
        val x = s.outbound["outbound"]
        val user = x.at("settings", "vnext", 0, "users", 0)
        assertEquals("mlkem768x25519plus.native.0rtt.key", user.at("encryption")?.string)
        assertEquals("xtls-rprx-vision", user.at("flow")?.string)
        assertEquals("raw", x.at("streamSettings", "network")?.string)
        assertNull(x.at("streamSettings", "security"))
        assertEquals("Vision · Encryption · Xray", Server(null, s).detail)
    }

    @Test
    fun mkcpLink() {
        val s = ShareLink.parse("vless://id@k.example.com:8443?security=tls&type=kcp&headerType=wechat-video&seed=s33d#K")
        val k = s.outbound.at("outbound", "streamSettings")
        assertEquals("kcp", k.at("network")?.string)
        assertEquals("wechat-video", k.at("kcpSettings", "header", "type")?.string)
        assertEquals("s33d", k.at("kcpSettings", "seed")?.string)
        assertEquals("TLS · mKCP · Xray", Server(null, s).detail)
    }

    @Test
    fun plainVlessAndTrojanToTheInternetAreSkipped() {
        // Xray refuses them: the id or the password would go out in the clear.
        assertEquals(ShareLinkError.Unsupported("VLESS без TLS и шифрования"), parseError("vless://id@k.example.com:8443?type=kcp&seed=s#K"))
        assertEquals(ShareLinkError.Unsupported("Trojan без TLS"), parseError("trojan://p@8.8.8.8:80?security=none&type=xhttp"))
        // Inside the local network Xray allows them.
        for (host in listOf("192.168.1.2", "10.0.0.5", "router.lan", "[fd00::1]")) {
            assertEquals(host, "xray", ShareLink.parse("vless://id@$host:8443?type=kcp#L").outbound["type"]?.string)
        }
        // Xray runs Reality over RAW, XHTTP and gRPC only.
        assertEquals(ShareLinkError.Unsupported("Reality с транспортом kcp"), parseError("vless://id@k.example.com:443?security=reality&pbk=k&type=kcp#R"))
        // sing-box's own plain VLESS is not Xray's to refuse.
        assertEquals("vless", ShareLink.parse("vless://id@k.example.com:443?type=ws").outbound["type"]?.string)
    }

    @Test
    fun vmessHttpHeaderAndKcp() {
        val b64 = { s: String -> java.util.Base64.getEncoder().encodeToString(s.toByteArray()) }
        val http = """{"v":"2","ps":"vh","add":"v.example.com","port":"80","id":"uuid-1","aid":"0","scy":"aes-128-gcm","net":"tcp","type":"http","host":"a.com,b.com","path":"/x"}"""
        val s = ShareLink.parse("vmess://" + b64(http))
        assertEquals("vh", s.name)
        assertEquals(ProxyProtocol.VMESS, s.proto)
        val x = s.outbound["outbound"]
        assertEquals("vmess", x.at("protocol")?.string)
        assertEquals("aes-128-gcm", x.at("settings", "vnext", 0, "users", 0, "security")?.string)
        assertNull(x.at("settings", "vnext", 0, "users", 0, "alterId"))
        val request = x.at("streamSettings", "rawSettings", "header", "request")
        assertEquals(Json.strings(listOf("/x")), request.at("path"))
        assertEquals(Json.strings(listOf("a.com", "b.com")), request.at("headers", "Host"))
        assertEquals("HTTP · Xray", Server(null, s).detail)

        // Old vmess links carry the mKCP seed in path.
        val kcp = """{"v":"2","ps":"vk","add":"1.2.3.4","port":443,"id":"uuid-1","net":"kcp","type":"none","path":"seed1"}"""
        val k = ShareLink.parse("vmess://" + b64(kcp)).outbound.at("outbound", "streamSettings")
        assertEquals("seed1", k.at("kcpSettings", "seed")?.string)
        assertNull(k.at("kcpSettings", "header"))
    }

    @Test
    fun trojanXhttp() {
        val x = ShareLink.parse("trojan://secret@t.example.com:443?type=xhttp&path=%2Ft#T").outbound["outbound"]
        assertEquals("trojan", x.at("protocol")?.string)
        assertEquals("t.example.com", x.at("settings", "servers", 0, "address")?.string)
        assertEquals("secret", x.at("settings", "servers", 0, "password")?.string)
        assertEquals("tls", x.at("streamSettings", "security")?.string)
        assertEquals("t.example.com", x.at("streamSettings", "tlsSettings", "serverName")?.string)
    }

    @Test
    fun xrayTlsNeverAllowsInsecure() {
        val s = ShareLink.parse("vless://id@1.2.3.4:443?security=tls&insecure=1&allowInsecure=1&pcs=abcd&vcn=real.com&type=xhttp")
        val t = s.outbound.at("outbound", "streamSettings", "tlsSettings")
        assertNull(t.at("allowInsecure"))
        assertEquals("abcd", t.at("pinnedPeerCertSha256")?.string)
        assertEquals("real.com", t.at("verifyPeerCertByName")?.string)
        // No sni and an IP address: no server name at all.
        assertNull(t.at("serverName"))
    }

    @Test
    fun transportsSingBoxHasStayNative() {
        // ws and grpc keep sing-box's own outbound, which costs less.
        assertEquals("vless", ShareLink.parse("vless://id@1.2.3.4:443?security=tls&type=ws").outbound["type"]?.string)
        assertEquals("trojan", ShareLink.parse("trojan://p@1.2.3.4:443?type=grpc").outbound["type"]?.string)
    }

    @Test
    fun hysteria2PortHoppingAndObfs() {
        val link = "hysteria2://p%40ss@hy.example.com:443?mport=20000-30000&mportHopInt=30&obfs=salamander" +
            "&obfs-password=secret&sni=real.example.com&insecure=1#HY"
        val s = ShareLink.parse(link)
        assertEquals(ProxyProtocol.HYSTERIA2, s.proto)
        assertEquals(443, s.port)
        val o = s.outbound
        assertEquals("p@ss", o["password"]?.string)
        assertEquals(Json.strings(listOf("443:443", "20000:30000")), o["server_ports"])
        assertEquals("30s", o["hop_interval"]?.string)
        assertEquals("salamander", o["obfs"]?.get("type")?.string)
        assertEquals("secret", o["obfs"]?.get("password")?.string)
        assertEquals("real.example.com", o["tls"]?.get("server_name")?.string)
        assertEquals(true, o["tls"]?.get("insecure")?.bool)
    }

    @Test
    fun hy2PortRangeInAuthority() {
        val s = ShareLink.parse("hy2://pw@5.6.7.8:20000-30000,40000/?sni=a.com#r")
        // The single port in the list is the main one; it leads server_ports.
        assertEquals(40000, s.port)
        assertEquals(Json.strings(listOf("40000:40000", "20000:30000")), s.outbound["server_ports"])
        assertEquals("a.com", s.outbound["tls"]?.get("server_name")?.string)
    }

    @Test
    fun hy2DefaultPortAndIpHasNoSni() {
        val s = ShareLink.parse("hy2://pw@5.6.7.8?obfs=none")
        assertEquals(443, s.port)
        assertNull(s.outbound["server_ports"])
        assertNull(s.outbound["obfs"])
        assertNull(s.outbound["tls"]?.get("server_name"))
    }

    @Test
    fun shadowsocksSip002() {
        val user = base64("aes-256-gcm:pa:ss").replace("=", "")
        val s = ShareLink.parse("ss://$user@1.2.3.4:8388#ss1")
        assertEquals(ProxyProtocol.SHADOWSOCKS, s.proto)
        assertEquals("aes-256-gcm", s.outbound["method"]?.string)
        assertEquals("pa:ss", s.outbound["password"]?.string)
        assertEquals(8388, s.port)
        assertEquals("ss1", s.name)
    }

    @Test
    fun shadowsocks2022PercentEncoded() {
        val s = ShareLink.parse("ss://2022-blake3-aes-128-gcm:abc%2Bdef%3D@h.example.com:1080/?plugin=obfs-local%3Bobfs%3Dhttp#x")
        assertEquals("2022-blake3-aes-128-gcm", s.outbound["method"]?.string)
        assertEquals("abc+def=", s.outbound["password"]?.string)
        assertEquals("obfs-local", s.outbound["plugin"]?.string)
        assertEquals("obfs=http", s.outbound["plugin_opts"]?.string)
    }

    @Test
    fun shadowsocksLegacy() {
        val s = ShareLink.parse("ss://${base64("chacha20-ietf-poly1305:pw@legacy.example.com:443")}#legacy")
        assertEquals("legacy.example.com", s.host)
        assertEquals(443, s.port)
        assertEquals("chacha20-ietf-poly1305", s.outbound["method"]?.string)
    }

    @Test
    fun vmessWebSocketTls() {
        val json = """{"v":"2","ps":"vm","add":"v.example.com","port":443,"id":"uuid-1","aid":"0","net":"ws","path":"/p","host":"h.example.com","tls":"tls","sni":""}"""
        val s = ShareLink.parse("vmess://" + base64(json))
        assertEquals(ProxyProtocol.VMESS, s.proto)
        assertEquals("vm", s.name)
        assertEquals(443, s.port)
        assertEquals("auto", s.outbound["security"]?.string)
        assertEquals("/p", s.outbound["transport"]?.get("path")?.string)
        assertEquals("h.example.com", s.outbound["tls"]?.get("server_name")?.string)
    }

    @Test
    fun trojanWebSocket() {
        val s = ShareLink.parse("trojan://secret@t.example.com:443?type=ws&path=%2Ftr#t")
        assertEquals("secret", s.outbound["password"]?.string)
        assertEquals(true, s.outbound["tls"]?.get("enabled")?.bool)
        assertEquals("t.example.com", s.outbound["tls"]?.get("server_name")?.string)
        assertEquals("/tr", s.outbound["transport"]?.get("path")?.string)
    }

    @Test
    fun tuicAndSocks() {
        val t = ShareLink.parse("tuic://uuid:pw@t.example.com:443?congestion_control=bbr#t")
        assertEquals("pw", t.outbound["password"]?.string)
        assertEquals(Json.strings(listOf("h3")), t.outbound["tls"]?.get("alpn"))
        val s = ShareLink.parse("socks://" + base64("user:pw") + "@1.2.3.4:1080#s")
        assertEquals("user", s.outbound["username"]?.string)
        assertEquals("pw", s.outbound["password"]?.string)
    }

    @Test
    fun ipv6Host() {
        val s = ShareLink.parse("trojan://pw@[2001:db8::1]:443?sni=a.com#v6")
        assertEquals("2001:db8::1", s.host)
        assertEquals(443, s.port)
    }

    @Test
    fun unknownSchemes() {
        assertEquals(ShareLinkError.UnsupportedScheme("wireguard"), parseError("wireguard://abc@1.2.3.4:51820"))
        assertNull(ShareLink.scheme("https://example.com"))
        assertTrue(ShareLink.scheme("VLESS://x@y:1") != null)
    }
}
