using System.Text;
using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

public class ShareLinkTests
{
    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    [Fact]
    public void VlessRealityVision()
    {
        var link = "vless://11111111-2222-3333-4444-555555555555@vpn.example.com:443?encryption=none&flow=xtls-rprx-vision"
            + "&security=reality&sni=www.microsoft.com&fp=firefox&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"
            + "&sid=0123456789abcdef&type=tcp#%D0%9D%D0%B8%D0%B4%D0%B5%D1%80%D0%BB%D0%B0%D0%BD%D0%B4%D1%8B";
        var s = ShareLink.Parse(link);
        Assert.Equal(ProxyProtocol.Vless, s.Proto);
        Assert.Equal("Нидерланды", s.Name);
        Assert.Equal("vpn.example.com", s.Host);
        Assert.Equal(443, s.Port);
        var o = s.Outbound;
        Assert.Equal("11111111-2222-3333-4444-555555555555", o["uuid"]?.AsString);
        Assert.Equal("xtls-rprx-vision", o["flow"]?.AsString);
        Assert.Equal("www.microsoft.com", o["tls"]?["server_name"]?.AsString);
        Assert.Equal("firefox", o["tls"]?["utls"]?["fingerprint"]?.AsString);
        Assert.Equal("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", o["tls"]?["reality"]?["public_key"]?.AsString);
        Assert.Equal("0123456789abcdef", o["tls"]?["reality"]?["short_id"]?.AsString);
        Assert.Null(o["transport"]);
        Assert.Null(o["tag"]);
        Assert.Equal("Reality · Vision", new Server(null, s).Detail);
    }

    [Fact]
    public void RealityDefaultsToChromeFingerprint()
    {
        var s = ShareLink.Parse("vless://id@1.2.3.4:443?security=reality&pbk=key&sni=a.com");
        Assert.Equal("chrome", s.Outbound["tls"]?["utls"]?["fingerprint"]?.AsString);
        Assert.Equal("", s.Outbound["tls"]?["reality"]?["short_id"]?.AsString);
    }

    [Fact]
    public void RealityWithoutKeyIsRejected() =>
        Assert.Throws<ShareLinkException>(() => ShareLink.Parse("vless://id@1.2.3.4:443?security=reality&sni=a.com"));

    [Fact]
    public void VlessWebSocketEarlyData()
    {
        var s = ShareLink.Parse("vless://id@1.2.3.4:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com#ws");
        var t = s.Outbound["transport"];
        Assert.Equal("ws", t?["type"]?.AsString);
        Assert.Equal("/ws", t?["path"]?.AsString);
        Assert.Equal(2048, t?["max_early_data"]?.AsInt);
        Assert.Equal("cdn.example.com", t?["headers"]?["Host"]?.AsString);
        // No sni: falls back to the Host header, not the IP.
        Assert.Equal("cdn.example.com", s.Outbound["tls"]?["server_name"]?.AsString);
    }

    [Fact]
    public void VlessGrpc()
    {
        var s = ShareLink.Parse("vless://id@g.example.com:443?security=tls&type=grpc&serviceName=api#g");
        Assert.Equal("grpc", s.Outbound["transport"]?["type"]?.AsString);
        Assert.Equal("api", s.Outbound["transport"]?["service_name"]?.AsString);
        Assert.Equal("g.example.com", s.Outbound["tls"]?["server_name"]?.AsString);
    }

    // Through Xray

    private static Json? At(Json? node, params object[] path)
    {
        foreach (var key in path) node = key is int i ? node?[i] : node?[(string)key];
        return node;
    }

    [Fact]
    public void XhttpRunsThroughXray()
    {
        var s = ShareLink.Parse("vless://id@x.example.com:443?security=reality&pbk=k&sid=ab&type=xhttp&path=%2Fx#X");
        Assert.Equal(ProxyProtocol.Vless, s.Proto);
        Assert.Equal("x.example.com", s.Host);
        Assert.Equal(443, s.Port);
        Assert.Equal("xray", s.Outbound["type"]?.AsString);
        var x = s.Outbound["outbound"];
        Assert.Equal("vless", At(x, "protocol")?.AsString);
        Assert.Equal("x.example.com", At(x, "settings", "vnext", 0, "address")?.AsString);
        Assert.Equal("id", At(x, "settings", "vnext", 0, "users", 0, "id")?.AsString);
        Assert.Equal("none", At(x, "settings", "vnext", 0, "users", 0, "encryption")?.AsString);
        Assert.Equal("xhttp", At(x, "streamSettings", "network")?.AsString);
        Assert.Equal("/x", At(x, "streamSettings", "xhttpSettings", "path")?.AsString);
        Assert.Equal("auto", At(x, "streamSettings", "xhttpSettings", "mode")?.AsString);
        Assert.Equal("k", At(x, "streamSettings", "realitySettings", "publicKey")?.AsString);
        Assert.Equal("ab", At(x, "streamSettings", "realitySettings", "shortId")?.AsString);
        Assert.Equal("chrome", At(x, "streamSettings", "realitySettings", "fingerprint")?.AsString);
        Assert.Equal("x.example.com", At(x, "streamSettings", "realitySettings", "serverName")?.AsString);
        Assert.Equal("VLESS · Reality · XHTTP · Xray", new Server(null, s).Subtitle);
    }

    [Fact]
    public void XhttpExtraAndMode()
    {
        var extra = Uri.EscapeDataString("""{"xPaddingBytes":"100-1000","noGRPCHeader":true,"path":"/ignored"}""");
        var link = $"vless://id@1.2.3.4:443?security=tls&sni=a.com&type=xhttp&mode=packet-up&host=cdn.com&path=%2Fp&extra={extra}";
        var x = At(ShareLink.Parse(link).Outbound, "outbound", "streamSettings");
        Assert.Equal("100-1000", At(x, "xhttpSettings", "xPaddingBytes")?.AsString);
        Assert.Equal(true, At(x, "xhttpSettings", "noGRPCHeader")?.AsBool);
        // The link's own keys win over extra.
        Assert.Equal("/p", At(x, "xhttpSettings", "path")?.AsString);
        Assert.Equal("cdn.com", At(x, "xhttpSettings", "host")?.AsString);
        Assert.Equal("packet-up", At(x, "xhttpSettings", "mode")?.AsString);
        Assert.Equal("a.com", At(x, "tlsSettings", "serverName")?.AsString);
    }

    [Fact]
    public void VlessEncryptionRunsThroughXray()
    {
        var s = ShareLink.Parse("vless://id@e.example.com:443?encryption=mlkem768x25519plus.native.0rtt.key&flow=xtls-rprx-vision&type=tcp#E");
        var x = s.Outbound["outbound"];
        var user = At(x, "settings", "vnext", 0, "users", 0);
        Assert.Equal("mlkem768x25519plus.native.0rtt.key", At(user, "encryption")?.AsString);
        Assert.Equal("xtls-rprx-vision", At(user, "flow")?.AsString);
        Assert.Equal("raw", At(x, "streamSettings", "network")?.AsString);
        Assert.Null(At(x, "streamSettings", "security"));
        Assert.Equal("Vision · Encryption · Xray", new Server(null, s).Detail);
    }

    [Fact]
    public void MkcpLink()
    {
        var s = ShareLink.Parse("vless://id@k.example.com:8443?security=tls&type=kcp&headerType=wechat-video&seed=s33d#K");
        var k = At(s.Outbound, "outbound", "streamSettings");
        Assert.Equal("kcp", At(k, "network")?.AsString);
        Assert.Equal("wechat-video", At(k, "kcpSettings", "header", "type")?.AsString);
        Assert.Equal("s33d", At(k, "kcpSettings", "seed")?.AsString);
        Assert.Equal("TLS · mKCP · Xray", new Server(null, s).Detail);
    }

    [Fact]
    public void PlainVlessAndTrojanToTheInternetAreSkipped()
    {
        // Xray refuses them: the id or the password would go out in the clear.
        var vless = Assert.Throws<ShareLinkException>(() => ShareLink.Parse("vless://id@k.example.com:8443?type=kcp&seed=s#K"));
        Assert.Equal(ShareLinkErrorKind.Unsupported, vless.Kind);
        Assert.Equal("VLESS без TLS и шифрования", vless.Detail);
        var trojan = Assert.Throws<ShareLinkException>(() => ShareLink.Parse("trojan://p@8.8.8.8:80?security=none&type=xhttp"));
        Assert.Equal("Trojan без TLS", trojan.Detail);
        // Inside the local network Xray allows them.
        foreach (var host in new[] { "192.168.1.2", "10.0.0.5", "router.lan", "[fd00::1]" })
            Assert.Equal("xray", ShareLink.Parse($"vless://id@{host}:8443?type=kcp#L").Outbound["type"]?.AsString);
        // Xray runs Reality over RAW, XHTTP and gRPC only.
        var reality = Assert.Throws<ShareLinkException>(() => ShareLink.Parse("vless://id@k.example.com:443?security=reality&pbk=k&type=kcp#R"));
        Assert.Equal("Reality с транспортом kcp", reality.Detail);
        // sing-box's own plain VLESS is not Xray's to refuse.
        Assert.Equal("vless", ShareLink.Parse("vless://id@k.example.com:443?type=ws").Outbound["type"]?.AsString);
    }

    [Fact]
    public void VmessHttpHeaderAndKcp()
    {
        const string http = """{"v":"2","ps":"vh","add":"v.example.com","port":"80","id":"uuid-1","aid":"0","scy":"aes-128-gcm","net":"tcp","type":"http","host":"a.com,b.com","path":"/x"}""";
        var s = ShareLink.Parse("vmess://" + B64(http));
        Assert.Equal("vh", s.Name);
        Assert.Equal(ProxyProtocol.Vmess, s.Proto);
        var x = s.Outbound["outbound"];
        Assert.Equal("vmess", At(x, "protocol")?.AsString);
        Assert.Equal("aes-128-gcm", At(x, "settings", "vnext", 0, "users", 0, "security")?.AsString);
        Assert.Null(At(x, "settings", "vnext", 0, "users", 0, "alterId"));
        var request = At(x, "streamSettings", "rawSettings", "header", "request");
        Assert.Equal(Json.Strings(["/x"]), At(request, "path"));
        Assert.Equal(Json.Strings(["a.com", "b.com"]), At(request, "headers", "Host"));
        Assert.Equal("HTTP · Xray", new Server(null, s).Detail);

        // Old vmess links carry the mKCP seed in path.
        const string kcp = """{"v":"2","ps":"vk","add":"1.2.3.4","port":443,"id":"uuid-1","net":"kcp","type":"none","path":"seed1"}""";
        var k = At(ShareLink.Parse("vmess://" + B64(kcp)).Outbound, "outbound", "streamSettings");
        Assert.Equal("seed1", At(k, "kcpSettings", "seed")?.AsString);
        Assert.Null(At(k, "kcpSettings", "header"));
    }

    [Fact]
    public void TrojanXhttp()
    {
        var x = ShareLink.Parse("trojan://secret@t.example.com:443?type=xhttp&path=%2Ft#T").Outbound["outbound"];
        Assert.Equal("trojan", At(x, "protocol")?.AsString);
        Assert.Equal("t.example.com", At(x, "settings", "servers", 0, "address")?.AsString);
        Assert.Equal("secret", At(x, "settings", "servers", 0, "password")?.AsString);
        Assert.Equal("tls", At(x, "streamSettings", "security")?.AsString);
        Assert.Equal("t.example.com", At(x, "streamSettings", "tlsSettings", "serverName")?.AsString);
    }

    [Fact]
    public void XrayTlsNeverAllowsInsecure()
    {
        var s = ShareLink.Parse("vless://id@1.2.3.4:443?security=tls&insecure=1&allowInsecure=1&pcs=abcd&vcn=real.com&type=xhttp");
        var t = At(s.Outbound, "outbound", "streamSettings", "tlsSettings");
        Assert.Null(At(t, "allowInsecure"));
        Assert.Equal("abcd", At(t, "pinnedPeerCertSha256")?.AsString);
        Assert.Equal("real.com", At(t, "verifyPeerCertByName")?.AsString);
        // No sni and an IP address: no server name at all.
        Assert.Null(At(t, "serverName"));
    }

    [Fact]
    public void TransportsSingBoxHasStayNative()
    {
        // ws and grpc keep sing-box's own outbound, which costs less.
        Assert.Equal("vless", ShareLink.Parse("vless://id@1.2.3.4:443?security=tls&type=ws").Outbound["type"]?.AsString);
        Assert.Equal("trojan", ShareLink.Parse("trojan://p@1.2.3.4:443?type=grpc").Outbound["type"]?.AsString);
    }

    [Fact]
    public void Hysteria2PortHoppingAndObfs()
    {
        var link = "hysteria2://p%40ss@hy.example.com:443?mport=20000-30000&mportHopInt=30&obfs=salamander"
            + "&obfs-password=secret&sni=real.example.com&insecure=1#HY";
        var s = ShareLink.Parse(link);
        Assert.Equal(ProxyProtocol.Hysteria2, s.Proto);
        Assert.Equal(443, s.Port);
        var o = s.Outbound;
        Assert.Equal("p@ss", o["password"]?.AsString);
        Assert.Equal(Json.Strings(["443:443", "20000:30000"]), o["server_ports"]);
        Assert.Equal("30s", o["hop_interval"]?.AsString);
        Assert.Equal("salamander", o["obfs"]?["type"]?.AsString);
        Assert.Equal("secret", o["obfs"]?["password"]?.AsString);
        Assert.Equal("real.example.com", o["tls"]?["server_name"]?.AsString);
        Assert.True(o["tls"]?["insecure"]?.AsBool);
    }

    [Fact]
    public void Hy2PortRangeInAuthority()
    {
        var s = ShareLink.Parse("hy2://pw@5.6.7.8:20000-30000,40000/?sni=a.com#r");
        // The single port in the list is the main one; it leads server_ports.
        Assert.Equal(40000, s.Port);
        Assert.Equal(Json.Strings(["40000:40000", "20000:30000"]), s.Outbound["server_ports"]);
        Assert.Equal("a.com", s.Outbound["tls"]?["server_name"]?.AsString);
    }

    [Fact]
    public void Hy2DefaultPortAndIpHasNoSni()
    {
        var s = ShareLink.Parse("hy2://pw@5.6.7.8?obfs=none");
        Assert.Equal(443, s.Port);
        Assert.Null(s.Outbound["server_ports"]);
        Assert.Null(s.Outbound["obfs"]);
        Assert.Null(s.Outbound["tls"]?["server_name"]);
    }

    [Fact]
    public void ShadowsocksSip002()
    {
        var user = B64("aes-256-gcm:pa:ss").Replace("=", "");
        var s = ShareLink.Parse($"ss://{user}@1.2.3.4:8388#ss1");
        Assert.Equal(ProxyProtocol.Shadowsocks, s.Proto);
        Assert.Equal("aes-256-gcm", s.Outbound["method"]?.AsString);
        Assert.Equal("pa:ss", s.Outbound["password"]?.AsString);
        Assert.Equal(8388, s.Port);
        Assert.Equal("ss1", s.Name);
    }

    [Fact]
    public void Shadowsocks2022PercentEncoded()
    {
        var s = ShareLink.Parse("ss://2022-blake3-aes-128-gcm:abc%2Bdef%3D@h.example.com:1080/?plugin=obfs-local%3Bobfs%3Dhttp#x");
        Assert.Equal("2022-blake3-aes-128-gcm", s.Outbound["method"]?.AsString);
        Assert.Equal("abc+def=", s.Outbound["password"]?.AsString);
        Assert.Equal("obfs-local", s.Outbound["plugin"]?.AsString);
        Assert.Equal("obfs=http", s.Outbound["plugin_opts"]?.AsString);
    }

    [Fact]
    public void ShadowsocksLegacy()
    {
        var s = ShareLink.Parse($"ss://{B64("chacha20-ietf-poly1305:pw@legacy.example.com:443")}#legacy");
        Assert.Equal("legacy.example.com", s.Host);
        Assert.Equal(443, s.Port);
        Assert.Equal("chacha20-ietf-poly1305", s.Outbound["method"]?.AsString);
    }

    [Fact]
    public void VmessWebSocketTls()
    {
        var json = """{"v":"2","ps":"vm","add":"v.example.com","port":443,"id":"uuid-1","aid":"0","net":"ws","path":"/p","host":"h.example.com","tls":"tls","sni":""}""";
        var s = ShareLink.Parse("vmess://" + B64(json));
        Assert.Equal(ProxyProtocol.Vmess, s.Proto);
        Assert.Equal("vm", s.Name);
        Assert.Equal(443, s.Port);
        Assert.Equal("auto", s.Outbound["security"]?.AsString);
        Assert.Equal("/p", s.Outbound["transport"]?["path"]?.AsString);
        Assert.Equal("h.example.com", s.Outbound["tls"]?["server_name"]?.AsString);
    }

    [Fact]
    public void TrojanWebSocket()
    {
        var s = ShareLink.Parse("trojan://secret@t.example.com:443?type=ws&path=%2Ftr#t");
        Assert.Equal("secret", s.Outbound["password"]?.AsString);
        Assert.True(s.Outbound["tls"]?["enabled"]?.AsBool);
        Assert.Equal("t.example.com", s.Outbound["tls"]?["server_name"]?.AsString);
        Assert.Equal("/tr", s.Outbound["transport"]?["path"]?.AsString);
    }

    [Fact]
    public void TuicAndSocks()
    {
        var t = ShareLink.Parse("tuic://uuid:pw@t.example.com:443?congestion_control=bbr#t");
        Assert.Equal("pw", t.Outbound["password"]?.AsString);
        Assert.Equal(Json.Strings(["h3"]), t.Outbound["tls"]?["alpn"]);
        var s = ShareLink.Parse($"socks://{B64("user:pw")}@1.2.3.4:1080#s");
        Assert.Equal("user", s.Outbound["username"]?.AsString);
        Assert.Equal("pw", s.Outbound["password"]?.AsString);
    }

    [Fact]
    public void Ipv6Host()
    {
        var s = ShareLink.Parse("trojan://pw@[2001:db8::1]:443?sni=a.com#v6");
        Assert.Equal("2001:db8::1", s.Host);
        Assert.Equal(443, s.Port);
    }

    [Fact]
    public void UnknownSchemes()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLink.Parse("wireguard://abc@1.2.3.4:51820"));
        Assert.Equal(ShareLinkErrorKind.UnsupportedScheme, e.Kind);
        Assert.Equal("wireguard", e.Detail);
        Assert.Null(ShareLink.SchemeOf("https://example.com"));
    }
}
