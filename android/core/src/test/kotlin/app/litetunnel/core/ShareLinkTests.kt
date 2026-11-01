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

    @Test
    fun xhttpIsReportedUnsupported() {
        assertEquals(
            ShareLinkError.Unsupported("XHTTP (нет в ядре sing-box)"),
            parseError("vless://id@1.2.3.4:443?security=reality&pbk=k&type=xhttp"),
        )
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
