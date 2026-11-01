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

    [Fact]
    public void XhttpIsReportedUnsupported()
    {
        var e = Assert.Throws<ShareLinkException>(() => ShareLink.Parse("vless://id@1.2.3.4:443?security=reality&pbk=k&type=xhttp"));
        Assert.Equal(ShareLinkErrorKind.Unsupported, e.Kind);
        Assert.Equal("XHTTP (нет в ядре sing-box)", e.Detail);
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
