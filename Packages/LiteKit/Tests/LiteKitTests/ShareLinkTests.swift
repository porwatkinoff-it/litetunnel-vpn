import XCTest
@testable import LiteKit

final class ShareLinkTests: XCTestCase {
    func testVLESSRealityVision() throws {
        let link = "vless://11111111-2222-3333-4444-555555555555@vpn.example.com:443?encryption=none&flow=xtls-rprx-vision"
            + "&security=reality&sni=www.microsoft.com&fp=firefox&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "&sid=0123456789abcdef&type=tcp#%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B"
        let s = try ShareLink.parse(link)
        XCTAssertEqual(s.proto, .vless)
        XCTAssertEqual(s.name, "Нидерланды")
        XCTAssertEqual(s.host, "vpn.example.com")
        XCTAssertEqual(s.port, 443)
        let o = s.outbound
        XCTAssertEqual(o["uuid"]?.stringValue, "11111111-2222-3333-4444-555555555555")
        XCTAssertEqual(o["flow"]?.stringValue, "xtls-rprx-vision")
        XCTAssertEqual(o["tls"]?["server_name"]?.stringValue, "www.microsoft.com")
        XCTAssertEqual(o["tls"]?["utls"]?["fingerprint"]?.stringValue, "firefox")
        XCTAssertEqual(o["tls"]?["reality"]?["public_key"]?.stringValue, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")
        XCTAssertEqual(o["tls"]?["reality"]?["short_id"]?.stringValue, "0123456789abcdef")
        XCTAssertNil(o["transport"])
        XCTAssertNil(o["tag"])
        XCTAssertEqual(Server(subscriptionID: nil, parsed: s).detail, "Reality · Vision")
    }

    func testRealityDefaultsToChromeFingerprint() throws {
        let s = try ShareLink.parse("vless://id@1.2.3.4:443?security=reality&pbk=key&sni=a.com")
        XCTAssertEqual(s.outbound["tls"]?["utls"]?["fingerprint"]?.stringValue, "chrome")
        XCTAssertEqual(s.outbound["tls"]?["reality"]?["short_id"]?.stringValue, "")
    }

    func testRealityWithoutKeyIsRejected() {
        XCTAssertThrowsError(try ShareLink.parse("vless://id@1.2.3.4:443?security=reality&sni=a.com"))
    }

    func testVLESSWebSocketEarlyData() throws {
        let s = try ShareLink.parse("vless://id@1.2.3.4:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com#ws")
        let t = s.outbound["transport"]
        XCTAssertEqual(t?["type"]?.stringValue, "ws")
        XCTAssertEqual(t?["path"]?.stringValue, "/ws")
        XCTAssertEqual(t?["max_early_data"]?.intValue, 2048)
        XCTAssertEqual(t?["headers"]?["Host"]?.stringValue, "cdn.example.com")
        // No sni: falls back to the Host header, not the IP.
        XCTAssertEqual(s.outbound["tls"]?["server_name"]?.stringValue, "cdn.example.com")
    }

    func testVLESSGRPC() throws {
        let s = try ShareLink.parse("vless://id@g.example.com:443?security=tls&type=grpc&serviceName=api#g")
        XCTAssertEqual(s.outbound["transport"]?["type"]?.stringValue, "grpc")
        XCTAssertEqual(s.outbound["transport"]?["service_name"]?.stringValue, "api")
        XCTAssertEqual(s.outbound["tls"]?["server_name"]?.stringValue, "g.example.com")
    }

    // MARK: - Through Xray

    func testXHTTPRunsThroughXray() throws {
        let s = try ShareLink.parse("vless://id@x.example.com:443?security=reality&pbk=k&sid=ab&type=xhttp&path=%2Fx#X")
        XCTAssertEqual(s.proto, .vless)
        XCTAssertEqual(s.host, "x.example.com")
        XCTAssertEqual(s.port, 443)
        XCTAssertEqual(s.outbound["type"], "xray")
        let x = try XCTUnwrap(s.outbound["outbound"])
        XCTAssertEqual(x["protocol"], "vless")
        XCTAssertEqual(x["settings"]?["vnext"]?[0]?["address"], "x.example.com")
        XCTAssertEqual(x["settings"]?["vnext"]?[0]?["users"]?[0]?["id"], "id")
        XCTAssertEqual(x["settings"]?["vnext"]?[0]?["users"]?[0]?["encryption"], "none")
        XCTAssertEqual(x["streamSettings"]?["network"], "xhttp")
        XCTAssertEqual(x["streamSettings"]?["xhttpSettings"]?["path"], "/x")
        XCTAssertEqual(x["streamSettings"]?["xhttpSettings"]?["mode"], "auto")
        XCTAssertEqual(x["streamSettings"]?["realitySettings"]?["publicKey"], "k")
        XCTAssertEqual(x["streamSettings"]?["realitySettings"]?["shortId"], "ab")
        XCTAssertEqual(x["streamSettings"]?["realitySettings"]?["fingerprint"], "chrome")
        XCTAssertEqual(x["streamSettings"]?["realitySettings"]?["serverName"], "x.example.com")
        XCTAssertEqual(Server(subscriptionID: nil, parsed: s).subtitle, "VLESS · Reality · XHTTP · Xray")
    }

    func testXHTTPExtraAndMode() throws {
        let extra = #"{"xPaddingBytes":"100-1000","noGRPCHeader":true,"path":"/ignored"}"#
        let link = "vless://id@1.2.3.4:443?security=tls&sni=a.com&type=xhttp&mode=packet-up&host=cdn.com&path=%2Fp&extra="
            + extra.addingPercentEncoding(withAllowedCharacters: .alphanumerics)!
        let x = try XCTUnwrap(try ShareLink.parse(link).outbound["outbound"]?["streamSettings"])
        XCTAssertEqual(x["xhttpSettings"]?["xPaddingBytes"], "100-1000")
        XCTAssertEqual(x["xhttpSettings"]?["noGRPCHeader"], true)
        // The link's own keys win over extra.
        XCTAssertEqual(x["xhttpSettings"]?["path"], "/p")
        XCTAssertEqual(x["xhttpSettings"]?["host"], "cdn.com")
        XCTAssertEqual(x["xhttpSettings"]?["mode"], "packet-up")
        XCTAssertEqual(x["tlsSettings"]?["serverName"], "a.com")
    }

    func testVLESSEncryptionRunsThroughXray() throws {
        let s = try ShareLink.parse("vless://id@e.example.com:443?encryption=mlkem768x25519plus.native.0rtt.key&flow=xtls-rprx-vision&type=tcp#E")
        let x = try XCTUnwrap(s.outbound["outbound"])
        let user = x["settings"]?["vnext"]?[0]?["users"]?[0]
        XCTAssertEqual(user?["encryption"], "mlkem768x25519plus.native.0rtt.key")
        XCTAssertEqual(user?["flow"], "xtls-rprx-vision")
        XCTAssertEqual(x["streamSettings"]?["network"], "raw")
        XCTAssertNil(x["streamSettings"]?["security"])
        XCTAssertEqual(Server(subscriptionID: nil, parsed: s).detail, "Vision · Encryption · Xray")
    }

    func testMKCPLink() throws {
        let s = try ShareLink.parse("vless://id@k.example.com:8443?security=tls&type=kcp&headerType=wechat-video&seed=s33d#K")
        let k = s.outbound["outbound"]?["streamSettings"]
        XCTAssertEqual(k?["network"], "kcp")
        XCTAssertEqual(k?["kcpSettings"]?["header"]?["type"], "wechat-video")
        XCTAssertEqual(k?["kcpSettings"]?["seed"], "s33d")
        XCTAssertEqual(Server(subscriptionID: nil, parsed: s).detail, "TLS · mKCP · Xray")
    }

    func testPlainVLESSAndTrojanToTheInternetAreSkipped() throws {
        // Xray refuses them: the id or the password would go out in the clear.
        XCTAssertThrowsError(try ShareLink.parse("vless://id@k.example.com:8443?type=kcp&seed=s#K")) {
            XCTAssertEqual($0 as? ShareLinkError, .unsupported("VLESS без TLS и шифрования"))
        }
        XCTAssertThrowsError(try ShareLink.parse("trojan://p@8.8.8.8:80?security=none&type=xhttp")) {
            XCTAssertEqual($0 as? ShareLinkError, .unsupported("Trojan без TLS"))
        }
        // Inside the local network Xray allows them.
        for host in ["192.168.1.2", "10.0.0.5", "router.lan", "[fd00::1]"] {
            let s = try ShareLink.parse("vless://id@\(host):8443?type=kcp#L")
            XCTAssertEqual(s.outbound["type"], "xray", host)
        }
        // Xray runs Reality over RAW, XHTTP and gRPC only.
        XCTAssertThrowsError(try ShareLink.parse("vless://id@k.example.com:443?security=reality&pbk=k&type=kcp#R")) {
            XCTAssertEqual($0 as? ShareLinkError, .unsupported("Reality с транспортом kcp"))
        }
        // sing-box's own plain VLESS is not Xray's to refuse.
        XCTAssertEqual(try ShareLink.parse("vless://id@k.example.com:443?type=ws").outbound["type"], "vless")
    }

    func testVMessHTTPHeaderAndKCP() throws {
        let http = #"{"v":"2","ps":"vh","add":"v.example.com","port":"80","id":"uuid-1","aid":"0","scy":"aes-128-gcm","net":"tcp","type":"http","host":"a.com,b.com","path":"/x"}"#
        let s = try ShareLink.parse("vmess://" + Data(http.utf8).base64EncodedString())
        XCTAssertEqual(s.name, "vh")
        XCTAssertEqual(s.proto, .vmess)
        let x = try XCTUnwrap(s.outbound["outbound"])
        XCTAssertEqual(x["protocol"], "vmess")
        XCTAssertEqual(x["settings"]?["vnext"]?[0]?["users"]?[0]?["security"], "aes-128-gcm")
        XCTAssertNil(x["settings"]?["vnext"]?[0]?["users"]?[0]?["alterId"])
        let request = x["streamSettings"]?["rawSettings"]?["header"]?["request"]
        XCTAssertEqual(request?["path"], .strings(["/x"]))
        XCTAssertEqual(request?["headers"]?["Host"], .strings(["a.com", "b.com"]))
        XCTAssertEqual(Server(subscriptionID: nil, parsed: s).detail, "HTTP · Xray")

        // Old vmess links carry the mKCP seed in path.
        let kcp = #"{"v":"2","ps":"vk","add":"1.2.3.4","port":443,"id":"uuid-1","net":"kcp","type":"none","path":"seed1"}"#
        let k = try ShareLink.parse("vmess://" + Data(kcp.utf8).base64EncodedString()).outbound["outbound"]?["streamSettings"]
        XCTAssertEqual(k?["kcpSettings"]?["seed"], "seed1")
        XCTAssertNil(k?["kcpSettings"]?["header"])
    }

    func testTrojanXHTTP() throws {
        let s = try ShareLink.parse("trojan://secret@t.example.com:443?type=xhttp&path=%2Ft#T")
        let x = try XCTUnwrap(s.outbound["outbound"])
        XCTAssertEqual(x["protocol"], "trojan")
        XCTAssertEqual(x["settings"]?["servers"]?[0]?["address"], "t.example.com")
        XCTAssertEqual(x["settings"]?["servers"]?[0]?["password"], "secret")
        XCTAssertEqual(x["streamSettings"]?["security"], "tls")
        XCTAssertEqual(x["streamSettings"]?["tlsSettings"]?["serverName"], "t.example.com")
    }

    func testXrayTLSNeverAllowsInsecure() throws {
        let s = try ShareLink.parse("vless://id@1.2.3.4:443?security=tls&insecure=1&allowInsecure=1&pcs=abcd&vcn=real.com&type=xhttp")
        let t = s.outbound["outbound"]?["streamSettings"]?["tlsSettings"]
        XCTAssertNil(t?["allowInsecure"])
        XCTAssertEqual(t?["pinnedPeerCertSha256"], "abcd")
        XCTAssertEqual(t?["verifyPeerCertByName"], "real.com")
        // No sni and an IP address: no server name at all.
        XCTAssertNil(t?["serverName"])
    }

    func testTransportsSingBoxHasStayNative() throws {
        // ws and grpc keep sing-box's own outbound, which costs less.
        XCTAssertEqual(try ShareLink.parse("vless://id@1.2.3.4:443?security=tls&type=ws").outbound["type"], "vless")
        XCTAssertEqual(try ShareLink.parse("trojan://p@1.2.3.4:443?type=grpc").outbound["type"], "trojan")
    }

    func testHysteria2PortHoppingAndObfs() throws {
        let link = "hysteria2://p%40ss@hy.example.com:443?mport=20000-30000&mportHopInt=30&obfs=salamander"
            + "&obfs-password=secret&sni=real.example.com&insecure=1#HY"
        let s = try ShareLink.parse(link)
        XCTAssertEqual(s.proto, .hysteria2)
        XCTAssertEqual(s.port, 443)
        let o = s.outbound
        XCTAssertEqual(o["password"]?.stringValue, "p@ss")
        XCTAssertEqual(o["server_ports"], ["443:443", "20000:30000"])
        XCTAssertEqual(o["hop_interval"]?.stringValue, "30s")
        XCTAssertEqual(o["obfs"]?["type"]?.stringValue, "salamander")
        XCTAssertEqual(o["obfs"]?["password"]?.stringValue, "secret")
        XCTAssertEqual(o["tls"]?["server_name"]?.stringValue, "real.example.com")
        XCTAssertEqual(o["tls"]?["insecure"]?.boolValue, true)
    }

    func testHy2PortRangeInAuthority() throws {
        let s = try ShareLink.parse("hy2://pw@5.6.7.8:20000-30000,40000/?sni=a.com#r")
        // The single port in the list is the main one; it leads server_ports.
        XCTAssertEqual(s.port, 40000)
        XCTAssertEqual(s.outbound["server_ports"], ["40000:40000", "20000:30000"])
        XCTAssertEqual(s.outbound["tls"]?["server_name"]?.stringValue, "a.com")
    }

    func testHy2DefaultPortAndIPHasNoSNI() throws {
        let s = try ShareLink.parse("hy2://pw@5.6.7.8?obfs=none")
        XCTAssertEqual(s.port, 443)
        XCTAssertNil(s.outbound["server_ports"])
        XCTAssertNil(s.outbound["obfs"])
        XCTAssertNil(s.outbound["tls"]?["server_name"])
    }

    func testShadowsocksSIP002() throws {
        let user = Data("aes-256-gcm:pa:ss".utf8).base64EncodedString()
            .replacingOccurrences(of: "=", with: "")
        let s = try ShareLink.parse("ss://\(user)@1.2.3.4:8388#ss1")
        XCTAssertEqual(s.proto, .shadowsocks)
        XCTAssertEqual(s.outbound["method"]?.stringValue, "aes-256-gcm")
        XCTAssertEqual(s.outbound["password"]?.stringValue, "pa:ss")
        XCTAssertEqual(s.port, 8388)
        XCTAssertEqual(s.name, "ss1")
    }

    func testShadowsocks2022PercentEncoded() throws {
        let s = try ShareLink.parse("ss://2022-blake3-aes-128-gcm:abc%2Bdef%3D@h.example.com:1080/?plugin=obfs-local%3Bobfs%3Dhttp#x")
        XCTAssertEqual(s.outbound["method"]?.stringValue, "2022-blake3-aes-128-gcm")
        XCTAssertEqual(s.outbound["password"]?.stringValue, "abc+def=")
        XCTAssertEqual(s.outbound["plugin"]?.stringValue, "obfs-local")
        XCTAssertEqual(s.outbound["plugin_opts"]?.stringValue, "obfs=http")
    }

    func testShadowsocksLegacy() throws {
        let body = Data("chacha20-ietf-poly1305:pw@legacy.example.com:443".utf8).base64EncodedString()
        let s = try ShareLink.parse("ss://\(body)#legacy")
        XCTAssertEqual(s.host, "legacy.example.com")
        XCTAssertEqual(s.port, 443)
        XCTAssertEqual(s.outbound["method"]?.stringValue, "chacha20-ietf-poly1305")
    }

    func testVMessWebSocketTLS() throws {
        let json = #"{"v":"2","ps":"vm","add":"v.example.com","port":443,"id":"uuid-1","aid":"0","net":"ws","path":"/p","host":"h.example.com","tls":"tls","sni":""}"#
        let s = try ShareLink.parse("vmess://" + Data(json.utf8).base64EncodedString())
        XCTAssertEqual(s.proto, .vmess)
        XCTAssertEqual(s.name, "vm")
        XCTAssertEqual(s.port, 443)
        XCTAssertEqual(s.outbound["security"]?.stringValue, "auto")
        XCTAssertEqual(s.outbound["transport"]?["path"]?.stringValue, "/p")
        XCTAssertEqual(s.outbound["tls"]?["server_name"]?.stringValue, "h.example.com")
    }

    func testTrojanWebSocket() throws {
        let s = try ShareLink.parse("trojan://secret@t.example.com:443?type=ws&path=%2Ftr#t")
        XCTAssertEqual(s.outbound["password"]?.stringValue, "secret")
        XCTAssertEqual(s.outbound["tls"]?["enabled"]?.boolValue, true)
        XCTAssertEqual(s.outbound["tls"]?["server_name"]?.stringValue, "t.example.com")
        XCTAssertEqual(s.outbound["transport"]?["path"]?.stringValue, "/tr")
    }

    func testTUICAndSOCKS() throws {
        let t = try ShareLink.parse("tuic://uuid:pw@t.example.com:443?congestion_control=bbr#t")
        XCTAssertEqual(t.outbound["password"]?.stringValue, "pw")
        XCTAssertEqual(t.outbound["tls"]?["alpn"], ["h3"])
        let s = try ShareLink.parse("socks://" + Data("user:pw".utf8).base64EncodedString() + "@1.2.3.4:1080#s")
        XCTAssertEqual(s.outbound["username"]?.stringValue, "user")
        XCTAssertEqual(s.outbound["password"]?.stringValue, "pw")
    }

    func testIPv6Host() throws {
        let s = try ShareLink.parse("trojan://pw@[2001:db8::1]:443?sni=a.com#v6")
        XCTAssertEqual(s.host, "2001:db8::1")
        XCTAssertEqual(s.port, 443)
    }

    func testUnknownSchemes() {
        XCTAssertThrowsError(try ShareLink.parse("wireguard://abc@1.2.3.4:51820")) { error in
            XCTAssertEqual(error as? ShareLinkError, .unsupportedScheme("wireguard"))
        }
        XCTAssertNil(ShareLink.scheme(of: "https://example.com"))
    }
}
