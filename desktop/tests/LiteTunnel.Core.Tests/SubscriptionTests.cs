using System.Text;
using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

public class SubscriptionTests
{
    private static readonly string[] Lines =
    [
        "vless://id-1@nl.example.com:443?security=reality&pbk=key&sni=www.microsoft.com&flow=xtls-rprx-vision#NL",
        "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
        "vless://id-2@fi.example.com:443?security=reality&pbk=key&type=xhttp#FI xhttp",
        "happ://routing/add/eyJOYW1lIjoiUlUifQ==",
        "# comment",
    ];

    private static readonly Dictionary<string, string> NoHeaders = [];

    private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);

    private static SubscriptionResult Parse(string body, Dictionary<string, string>? headers = null) =>
        SubscriptionParser.Parse(Utf8(body), headers ?? NoHeaders);

    [Fact]
    public void Base64BodyWithHeaders()
    {
        var body = Utf8(Convert.ToBase64String(Utf8(string.Join("\n", Lines))));
        var headers = new Dictionary<string, string>
        {
            ["Profile-Title"] = "base64:" + Convert.ToBase64String(Utf8("Мой VPN")),
            ["Profile-Update-Interval"] = "12",
            ["Subscription-Userinfo"] = "upload=100; download=900; total=10000; expire=1893456000",
            ["Support-Url"] = "https://t.me/support",
            ["Announce"] = "%D0%9F%D1%80%D0%B8%D0%B2%D0%B5%D1%82",
        };
        var result = SubscriptionParser.Parse(body, headers);
        Assert.Equal(["NL", "DE"], result.Servers.Select(s => s.Name));
        Assert.Single(result.Skipped);
        Assert.Contains("XHTTP", result.Skipped[0].Reason);
        Assert.Single(result.RoutingLines);
        Assert.Equal("Мой VPN", result.Meta.Title);
        Assert.Equal(12, result.Meta.UpdateIntervalHours);
        Assert.Equal(1000, result.Meta.Usage?.Used);
        Assert.Equal(0.1, result.Meta.Usage?.Fraction);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1893456000), result.Meta.Usage?.Expire);
        Assert.Equal("https://t.me/support", result.Meta.SupportUrl);
        Assert.Equal("Привет", result.Meta.Announce);
    }

    [Fact]
    public void PlainBodyAndUrlSafeBase64()
    {
        Assert.Equal(2, Parse(string.Join("\r\n", Lines)).Servers.Count);
        var urlSafe = Convert.ToBase64String(Utf8(Lines[1])).Replace('+', '-').Replace('/', '_').Replace("=", "");
        Assert.Equal(ProxyProtocol.Hysteria2, Parse(urlSafe).Servers.First().Proto);
    }

    [Fact]
    public void SingBoxJsonBody()
    {
        var json = """
            {"outbounds":[
              {"type":"hysteria2","tag":"JP","server":"jp.example.com","server_port":443,"password":"x","detour":"other"},
              {"type":"direct","tag":"direct"},
              {"type":"selector","tag":"auto","outbounds":["JP"]}
            ]}
            """;
        var result = Parse(json);
        Assert.Single(result.Servers);
        Assert.Equal("JP", result.Servers[0].Name);
        Assert.Null(result.Servers[0].Outbound["tag"]);
        Assert.Null(result.Servers[0].Outbound["detour"]);
    }

    [Fact]
    public void XrayJsonBody()
    {
        // As Remnawave serves it to Happ: one full Xray config per server.
        var json = """
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
            """;
        var result = Parse(json);
        Assert.Equal(["🇳🇱 NL", "DE ws", "FI · proxy", "FI · proxy-2", "JP hy2"], result.Servers.Select(s => s.Name));
        Assert.Equal(
            [ProxyProtocol.Vless, ProxyProtocol.Vless, ProxyProtocol.Trojan, ProxyProtocol.Shadowsocks, ProxyProtocol.Hysteria2],
            result.Servers.Select(s => s.Proto));
        Assert.Equal(["US xhttp"], result.Skipped.Select(s => s.Line));
        Assert.Contains("XHTTP", result.Skipped[0].Reason);

        var nl = result.Servers[0].Outbound;
        Assert.Equal("id-1", nl["uuid"]?.AsString);
        Assert.Equal("xtls-rprx-vision", nl["flow"]?.AsString);
        Assert.Equal("www.microsoft.com", nl["tls"]?["server_name"]?.AsString);
        Assert.Equal("key", nl["tls"]?["reality"]?["public_key"]?.AsString);
        Assert.Equal("ab", nl["tls"]?["reality"]?["short_id"]?.AsString);
        Assert.Null(nl["transport"]);

        var de = result.Servers[1].Outbound;
        Assert.Equal(8443, de["server_port"]?.AsInt);
        Assert.True(de["tls"]?["insecure"]?.AsBool);
        Assert.Equal(Json.Strings(["h2", "http/1.1"]), de["tls"]?["alpn"]);
        Assert.Equal("/ws", de["transport"]?["path"]?.AsString);
        Assert.Equal(2048, de["transport"]?["max_early_data"]?.AsInt);
        Assert.Equal("cdn.example.com", de["transport"]?["headers"]?["Host"]?.AsString);

        Assert.Equal("svc", result.Servers[2].Outbound["transport"]?["service_name"]?.AsString);
        Assert.Equal("fi.example.com", result.Servers[2].Outbound["tls"]?["server_name"]?.AsString);
        Assert.Equal("2022-blake3-aes-128-gcm", result.Servers[3].Outbound["method"]?.AsString);

        var jp = result.Servers[4].Outbound;
        Assert.Equal("secret", jp["password"]?.AsString);
        Assert.Equal(Json.Strings(["h3"]), jp["tls"]?["alpn"]);
        Assert.Null(jp["tls"]?["utls"]);
    }

    [Fact]
    public void XrayJsonSingleConfig()
    {
        var json = """
            {"outbounds":[{"tag":"proxy","protocol":"vmess","settings":{"vnext":[{"address":"vm.example.com","port":443,
              "users":[{"id":"id","alterId":0,"security":"auto"}]}]},"streamSettings":{"network":"tcp"}},
              {"protocol":"freedom","tag":"direct"}]}
            """;
        var result = Parse(json);
        Assert.Equal(["proxy"], result.Servers.Select(s => s.Name));
        Assert.Equal("id", result.Servers[0].Outbound["uuid"]?.AsString);
        Assert.Null(result.Servers[0].Outbound["tls"]);
        var e = Assert.Throws<SubscriptionException>(() => Parse("""{"foo":1}"""));
        Assert.Equal(SubscriptionErrorKind.NothingSupported, e.Kind);
        Assert.Equal("Неизвестный формат JSON", e.Reason);
    }

    [Fact]
    public void XrayJsonBalancer()
    {
        var reality = "\"security\":\"reality\",\"realitySettings\":{\"serverName\":\"sni.example.com\",\"publicKey\":\"pbk\",\"shortId\":\"ab\",\"fingerprint\":\"chrome\"}";
        var json = $$$"""
            [{"remarks":"Авто-выбор","outbounds":[
              {"tag":"proxy-1","protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
               "streamSettings":{"network":"grpc","grpcSettings":{"serviceName":"g"},{{{reality}}}}},
              {"tag":"proxy-2","protocol":"vless","settings":{"vnext":[{"address":"b.example.com","port":443,"users":[{"id":"id","flow":"xtls-rprx-vision","encryption":"none"}]}]},
               "streamSettings":{"network":"tcp",{{{reality}}}}},
              {"tag":"proxy-3","protocol":"vless","settings":{"vnext":[{"address":"c.example.com","port":443,"users":[{"id":"id","encryption":"none"}]}]},
               "streamSettings":{"network":"xhttp",{{{reality}}}}},
              {"protocol":"freedom","tag":"direct"}],
              "routing":{"balancers":[{"tag":"auto","selector":["proxy"]}]}}]
            """;
        var result = Parse(json);
        Assert.Equal(["Авто-выбор"], result.Servers.Select(s => s.Name));
        Assert.Equal(["Авто-выбор · proxy-3"], result.Skipped.Select(s => s.Line));

        var server = new Server(null, result.Servers[0]);
        Assert.Equal(["a.example.com", "b.example.com"], server.Members.Select(m => m.Host));
        Assert.Equal("Самый быстрый из 2", server.Detail);

        var config = ConfigBuilder.Build(server, new AppSettings(), null);
        var outbounds = config["outbounds"]?.AsArray ?? [];
        Assert.Equal(["proxy", "proxy-1", "proxy-2", "direct"], outbounds.Select(o => o["tag"]?.AsString).OfType<string>());
        Assert.Equal("urltest", outbounds[0]["type"]?.AsString);
        Assert.Equal(Json.Strings(["proxy-1", "proxy-2"]), outbounds[0]["outbounds"]);
        Assert.Equal("xtls-rprx-vision", outbounds[2]["flow"]?.AsString);
    }

    [Fact]
    public void EmptyAndUnsupportedBodies()
    {
        Assert.Equal(SubscriptionErrorKind.Empty, Assert.Throws<SubscriptionException>(() => SubscriptionParser.Parse([], NoHeaders)).Kind);
        Assert.Equal(SubscriptionErrorKind.NothingSupported, Assert.Throws<SubscriptionException>(() => Parse("wireguard://a@b:1")).Kind);
    }

    [Fact]
    public void XrayJsonBehindCommentsOrBase64()
    {
        const string config = """[{"remarks":"A","outbounds":[{"tag":"proxy","protocol":"vless","settings":{"vnext":[{"address":"a.example.com","port":443,"users":[{"id":"00000000-0000-0000-0000-000000000000","encryption":"none"}]}]}}]}]""";
        var commented = "#profile-title: X\r\n#announce: Y\r\n\r\n" + config;
        foreach (var body in new[]
        {
            commented,
            "\uFEFF" + commented,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(config)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(commented)),
        })
        {
            Assert.Equal(["A"], Parse(body).Servers.Select(s => s.Name));
        }
    }

    [Theory]
    [InlineData("<!doctype html><html></html>", "пришла веб-страница")]
    [InlineData("""[{"outbounds":[{"tag":"direct","protocol":"freedom"}]}]""", "пришли настройки без прокси")]
    [InlineData("someapp://crypt/AAAA\nsomeapp://crypt/BBBB", "пришли ссылки someapp://")]
    [InlineData("Client not supported", "сервер ответил «Client not supported»")]
    [InlineData("AAAA\nBBBB", "пришёл текст, 9 байт")]
    public void NoServersSaysWhatCameInstead(string body, string what)
    {
        var error = Assert.Throws<SubscriptionException>(() => Parse(body));
        Assert.Equal(SubscriptionErrorKind.NoServers, error.Kind);
        Assert.Equal(what, error.Reason);
    }

    [Fact]
    public void NoServersTellsBlankAndBinaryApart()
    {
        Assert.Equal(SubscriptionErrorKind.Empty, Assert.Throws<SubscriptionException>(() => Parse(" \n")).Kind);
        var binary = Assert.Throws<SubscriptionException>(() => SubscriptionParser.Parse([0xFF, 0xFE, 0x00], NoHeaders));
        Assert.Equal("пришли двоичные данные", binary.Reason);
    }

    [Fact]
    public void ApplyKeepsIdsSelectionAndPosition()
    {
        var state = new AppState().AddManual([ShareLink.Parse("trojan://pw@m.example.com:443#Manual")]).State;
        var sub = new Subscription { Name = "sub", Url = "https://s.example.com/x" };
        state = state with { Subscriptions = [.. state.Subscriptions, sub] };

        state = state.Apply(Parse(string.Join("\n", Lines)), sub.Id);
        Assert.Equal(["Manual", "Авто-выбор", "NL", "DE"], state.Servers.Select(s => s.Name));
        var de = state.Servers[3];
        state = state with { SelectedServerId = de.Id };
        state = state.UpdateServer(de.Id, s => s with { LastPing = PingResult.Ms(42) });

        // Provider drops NL, keeps DE, adds US.
        var second = Parse(string.Join("\n",
            "hysteria2://pw@de.example.com:443?sni=de.example.com#DE",
            "vless://id-3@us.example.com:443?security=tls#US"), new() { ["profile-title"] = "Renamed" });
        state = state.Apply(second, sub.Id);
        Assert.Equal(["Manual", "Авто-выбор", "DE", "US"], state.Servers.Select(s => s.Name));
        Assert.Equal(de.Id, state.Servers[2].Id);
        Assert.Equal(PingResult.Ms(42), state.Servers[2].LastPing);
        Assert.Equal(de.Id, state.SelectedServerId);
        Assert.Equal("Renamed", state.Subscriptions[0].Name);

        // Provider drops the selected server: selection moves to its first server.
        state = state.Apply(Parse("vless://id-3@us.example.com:443?security=tls#US"), sub.Id);
        Assert.Equal("US", state.SelectedServer?.Name);

        state = state.RemoveSubscription(sub.Id);
        Assert.Equal(["Manual"], state.Servers.Select(s => s.Name));
        Assert.Equal("Manual", state.SelectedServer?.Name);
    }

    private static List<ParsedServer> Named(params string[] names) =>
        names.Select(x => ShareLink.Parse($"trojan://pw@{x}.example.com:443#{x}")).ToList();

    private static SubscriptionResult Result(IEnumerable<ParsedServer> servers)
    {
        var result = new SubscriptionResult();
        result.Servers.AddRange(servers);
        return result;
    }

    private static (AppState State, Guid Id) Subscribed(IEnumerable<ParsedServer> servers, AppState? state = null)
    {
        state ??= new AppState();
        var sub = new Subscription { Name = "sub", Url = "https://s.example.com/x" };
        return ((state with { Subscriptions = [.. state.Subscriptions, sub] }).Apply(Result(servers), sub.Id), sub.Id);
    }

    [Fact]
    public void EverySubscriptionGetsAnAutoRow()
    {
        var (state, id) = Subscribed(Named("a", "b"));
        Assert.Equal(["Авто-выбор", "a", "b"], state.Servers.Select(s => s.Name));

        // A new subscription starts on it, and it lists the servers by name.
        var auto = state.Servers[0];
        Assert.True(auto.IsAuto);
        Assert.Equal(auto.Id, state.SelectedServerId);
        Assert.Null(auto.ShareLink);
        Assert.Equal("Самый быстрый из 2", auto.Subtitle);
        Assert.Equal(["a", "b"], auto.Members.Select(m => m.Name));
        Assert.Equal(2, state.ServerCount(id));

        // A refresh keeps the row and adds the new server to it.
        state = state.UpdateServer(auto.Id, s => s with { LastPing = PingResult.Ms(30) });
        state = state.Apply(Result(Named("a", "b", "c")), id);
        Assert.Equal(auto.Id, state.Servers[0].Id);
        Assert.Equal(PingResult.Ms(30), state.Servers[0].LastPing);
        Assert.Equal(["a", "b", "c"], state.Servers[0].Members.Select(m => m.Name));
        Assert.Equal(auto.Id, state.SelectedServerId);

        // With one server left there is nothing to choose from.
        state = state.Apply(Result(Named("c")), id);
        Assert.Equal(["c"], state.Servers.Select(s => s.Name));
        Assert.Equal("c", state.SelectedServer?.Name);
    }

    [Fact]
    public void AutoRowRunsAsAPlainGroup()
    {
        var (state, _) = Subscribed(Named("a", "b"));
        var outbounds = ConfigBuilder.Build(state.Servers[0], new AppSettings(), null)["outbounds"]?.AsArray ?? [];
        Assert.Equal(["proxy", "proxy-1", "proxy-2", "direct"], outbounds.Select(o => o["tag"]?.AsString).OfType<string>());
        Assert.Equal("urltest", outbounds[0]["type"]?.AsString);
        Assert.Equal("10m", outbounds[0]["interval"]?.AsString);
        Assert.Null(outbounds[0]["auto"]);
    }

    [Fact]
    public void ProviderGroupCoveringEverythingIsEnough()
    {
        var singles = Named("a", "b");
        var (state, id) = Subscribed([ServerGroup.Make(singles, "Группа"), .. singles]);
        Assert.Equal(["Группа", "a", "b"], state.Servers.Select(s => s.Name));
        Assert.Equal(2, state.ServerCount(id));

        // The same with nothing but the group in the subscription.
        var (only, onlyId) = Subscribed([ServerGroup.Make(singles, "Группа")]);
        Assert.Equal(["Группа"], only.Servers.Select(s => s.Name));
        Assert.Equal(2, only.ServerCount(onlyId));
    }

    [Fact]
    public void RowsShowingIncludeTheirGroups()
    {
        var (state, _) = Subscribed([ServerGroup.Make(Named("a", "b"), "Группа"), .. Named("a", "b", "c")]);
        Guid Id(string name) => state.Servers.Single(s => s.Name == name).Id;
        Assert.Equal(["Авто-выбор", "Группа", "a", "b", "c"], state.Servers.Select(s => s.Name));
        Assert.Equal(new HashSet<Guid> { Id("a"), Id("Группа"), Id("Авто-выбор") }, state.RowsShowing(new HashSet<Guid> { Id("a") }));
        Assert.Equal(new HashSet<Guid> { Id("c"), Id("Авто-выбор") }, state.RowsShowing(new HashSet<Guid> { Id("c") }));
        Assert.Empty(state.RowsShowing(new HashSet<Guid>()));
    }

    [Fact]
    public void ProviderGroupOverSomeServersGetsAnAutoRowOverAll()
    {
        var (state, id) = Subscribed([ServerGroup.Make(Named("a", "b"), "Группа"), .. Named("a", "c")]);
        Assert.Equal(["Авто-выбор", "Группа", "a", "c"], state.Servers.Select(s => s.Name));
        Assert.Equal(["a", "c", "b.example.com:443"], state.Servers[0].Members.Select(m => m.Name));
        Assert.Equal(3, state.ServerCount(id));
    }

    [Fact]
    public void SyncAddsAutoRowToOlderStateOnce()
    {
        var sub = new Subscription { Name = "sub", Url = "https://s.example.com/x" };
        var rows = Named("a", "b").Select(p => new Server(sub.Id, p)).ToList();
        var older = new AppState { Subscriptions = [sub], Servers = rows, SelectedServerId = rows[1].Id };

        var state = older.SyncAutoGroups();
        Assert.Equal(["Авто-выбор", "a", "b"], state.Servers.Select(s => s.Name));
        Assert.Equal("b", state.SelectedServer?.Name);

        Assert.Same(state, state.SyncAutoGroups());
    }

    [Fact]
    public void SortKeepsGroupsOnTop()
    {
        var (start, _) = Subscribed(Named("b", "a"));
        PingResult[] pings = [PingResult.Ms(10), PingResult.Ms(90), PingResult.Ms(10)];
        var state = start with { Servers = start.Servers.Select((s, i) => s with { LastPing = pings[i] }).ToList() };
        var byName = state with { Settings = state.Settings with { SortMode = SortMode.Name } };
        Assert.Equal(["Авто-выбор", "a", "b"], byName.Sorted(byName.Servers).Select(s => s.Name));
        var byPing = state with { Settings = state.Settings with { SortMode = SortMode.Ping } };
        Assert.Equal(["Авто-выбор", "a", "b"], byPing.Sorted(byPing.Servers).Select(s => s.Name));
    }

    [Fact]
    public void GroupIsPingedThroughItsRows()
    {
        var (state, id) = Subscribed(Named("a", "b"));
        Server auto = state.Servers[0], a = state.Servers[1], b = state.Servers[2];

        // Each server once, whatever is asked for.
        Assert.Equal([a.Id, b.Id], state.PingTargets([auto]).Select(s => s.Id));
        Assert.Equal([a.Id, b.Id], state.PingTargets(state.ServersOf(id)).Select(s => s.Id));
        Assert.Equal([b.Id], state.PingTargets([b]).Select(s => s.Id));

        // The group shows its fastest as results come in.
        state = state.SettingPing(PingResult.Ms(80), a.Id);
        Assert.Equal(PingResult.Ms(80), state.Servers[0].LastPing);
        state = state.SettingPing(PingResult.Ms(20), b.Id);
        Assert.Equal(PingResult.Ms(20), state.Servers[0].LastPing);
        state = state.SettingPing(PingResult.Failed, b.Id);
        Assert.Equal(PingResult.Ms(80), state.Servers[0].LastPing);
        state = state.SettingPing(PingResult.Failed, a.Id);
        Assert.Equal(PingResult.Failed, state.Servers[0].LastPing);
    }

    [Fact]
    public void GroupWithoutItsRowsIsPingedItself()
    {
        var group = ServerGroup.Make(Named("a", "b"), "Группа");
        var (state, _) = Subscribed([group]);
        var row = state.Servers[0];
        Assert.Equal([row.Id], state.PingTargets([row]).Select(s => s.Id));
        state = state.SettingPing(PingResult.Ms(50), row.Id);
        Assert.Equal(PingResult.Ms(50), state.Servers[0].LastPing);

        // Servers added by hand are never treated as a group's rows.
        var manual = new AppState().AddManual([group, .. Named("a", "b")]).State;
        Assert.Equal([manual.Servers[0].Id], manual.PingTargets([manual.Servers[0]]).Select(s => s.Id));
    }

    [Fact]
    public void SortByPingIsStable()
    {
        var state = new AppState().AddManual(new[] { "a", "b", "c", "d" }.Select(x => ShareLink.Parse($"trojan://pw@{x}.example.com:443#{x}"))).State;
        var ids = state.Servers.Select(s => s.Id).ToList();
        state = state
            .UpdateServer(ids[0], s => s with { LastPing = PingResult.Failed })
            .UpdateServer(ids[1], s => s with { LastPing = PingResult.Ms(80) })
            .UpdateServer(ids[3], s => s with { LastPing = PingResult.Ms(20) });
        state = state with { Settings = state.Settings with { SortMode = SortMode.Ping } };
        Assert.Equal(["d", "b", "a", "c"], state.Sorted(state.Servers).Select(s => s.Name));
    }

    [Fact]
    public void SortByNameIsNatural()
    {
        var state = new AppState().AddManual(new[] { "Server 10", "server 2", "Alpha" }.Select(x => ShareLink.Parse($"trojan://pw@h.example.com:443#{Uri.EscapeDataString(x)}"))).State;
        state = state with { Settings = state.Settings with { SortMode = SortMode.Name } };
        Assert.Equal(["Alpha", "server 2", "Server 10"], state.Sorted(state.Servers).Select(s => s.Name));
    }

    [Fact]
    public void StateRoundTripsThroughStore()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var store = new Store(dir);
            store.PrepareDirectories();
            Assert.Empty(store.LoadState().Servers);

            var state = new AppState().AddManual([ShareLink.Parse("hy2://pw@h.example.com:443?obfs=salamander&obfs-password=x#H")]).State;
            state = state with { Settings = state.Settings with { BlockAds = true } };
            store.Save(state);
            var loaded = store.LoadState();
            Assert.Equal(state.Servers, loaded.Servers);
            Assert.Equal(state.SelectedServerId, loaded.SelectedServerId);
            Assert.True(loaded.Settings.BlockAds);
            Assert.True(store.WriteConfig(loaded));
            Assert.Contains("\"salamander\"", store.ReadConfig());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void DeviceLimitHeaders()
    {
        const string placeholder = "vless://00000000-0000-0000-0000-000000000000@0.0.0.0:1#App not supported";
        Assert.Equal(SubscriptionErrorKind.DeviceIdRefused, Assert.Throws<SubscriptionException>(() =>
            Parse(placeholder, new() { ["X-Hwid-Active"] = "true", ["X-Hwid-Not-Supported"] = "true" })).Kind);
        Assert.Equal(SubscriptionErrorKind.DeviceLimit, Assert.Throws<SubscriptionException>(() =>
            Parse(placeholder, new() { ["x-hwid-max-devices-reached"] = "true" })).Kind);
        Parse(placeholder, new() { ["x-hwid-active"] = "true", ["x-hwid-not-supported"] = "false" });
    }

    [Fact]
    public void DeviceInfoHeaders()
    {
        var dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(dir);
        try
        {
            var device = DeviceInfo.Detect(dir);
            Assert.Matches("^[A-Za-z0-9=-]{10,64}$", device.Hwid);
            Assert.Equal(device.Hwid, device.Headers["x-hwid"]);
            Assert.Equal(device.Hwid, DeviceInfo.Detect(dir).Hwid);
            Assert.NotEmpty(device.Model);
            Assert.NotEmpty(device.OsVersion);
            Assert.All(device.Headers.Values, v => Assert.Matches("^[ -~]+$", v));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
        Assert.Equal(DeviceInfo.HwidFromSeed("a"), DeviceInfo.HwidFromSeed("a"));
        Assert.NotEqual(DeviceInfo.HwidFromSeed("a"), DeviceInfo.HwidFromSeed("b"));
        Assert.Equal("Ubuntu_24.04", DeviceInfo.Header("Ubuntu​24.04"));
    }

    [Fact]
    public void OsReleaseFields()
    {
        var fields = DeviceInfo.OsRelease("# c\nNAME=\"Ubuntu\"\nVERSION_ID='24.04'\nPRETTY_NAME=Ubuntu 24.04 LTS\n");
        Assert.Equal("Ubuntu", fields["NAME"]);
        Assert.Equal("24.04", fields["VERSION_ID"]);
        Assert.Equal("Ubuntu 24.04 LTS", fields["PRETTY_NAME"]);
    }

    [Fact]
    public void ImporterClassifies()
    {
        Assert.Equal(new ImportItem.SubscriptionLink("https://sub.example.com/abc", null), Importer.Classify(" https://sub.example.com/abc \n"));
        Assert.Equal(new ImportItem.SubscriptionLink("https://sub.example.com/abc", null),
            Importer.Classify("litetunnel://add/https%3A%2F%2Fsub.example.com%2Fabc"));
        Assert.Equal(new ImportItem.SubscriptionLink("https://s.example.com/1", "Home"),
            Importer.Classify("litetunnel://import?url=https%3A%2F%2Fs.example.com%2F1&name=Home"));
        var servers = Assert.IsType<ImportItem.Servers>(Importer.Classify("hy2://pw@h.example.com:443#H\nvless://id@v.example.com:443#V"));
        Assert.Equal([ProxyProtocol.Hysteria2, ProxyProtocol.Vless], servers.Parsed.Select(s => s.Proto));
        Assert.IsType<ImportItem.Nothing>(Importer.Classify("hello"));
    }

    [Fact]
    public void HeaderTextDecodes()
    {
        Assert.Equal("Мой VPN", SubscriptionParser.DecodeHeaderText("base64:" + Convert.ToBase64String(Utf8("Мой VPN"))));
        Assert.Equal("Plain", SubscriptionParser.DecodeHeaderText("Plain"));
        var usage = SubscriptionParser.ParseUserInfo("upload=1; download=2; total=0");
        Assert.Equal(3, usage?.Used);
        Assert.Null(usage?.Fraction);
        Assert.Null(usage?.Expire);
    }

    [Fact]
    public void SubscriptionIsDueAfterItsInterval()
    {
        var now = DateTimeOffset.UtcNow;
        var sub = new Subscription { Name = "s", Url = "https://s.example.com", UpdateIntervalHours = 12 };
        Assert.True(sub.IsDue(now));
        Assert.False((sub with { LastUpdated = now.AddHours(-11) }).IsDue(now));
        Assert.True((sub with { LastUpdated = now.AddHours(-13) }).IsDue(now));
    }

    [Theory]
    [InlineData("https://s.example.com/abc/", "https://s.example.com/abc/json")]
    [InlineData("https://s.example.com/abc", "https://s.example.com/abc/json")]
    [InlineData("https://s.example.com:8443/a%20b/?k=1", "https://s.example.com:8443/a%20b/json?k=1")]
    [InlineData("https://s.example.com/abc/json", null)]
    [InlineData("https://s.example.com/abc/json/", null)]
    [InlineData("https://s.example.com", null)]
    [InlineData("https://s.example.com/", null)]
    public void JsonFormatUrl(string address, string? json) =>
        Assert.Equal(json, SubscriptionClient.JsonFormatUrl(address));

    [Fact]
    public async Task BlankAnswerAsksForJson()
    {
        var asked = new List<string>();
        var answer = await SubscriptionClient.WithJsonFallback("https://s.example.com/abc/", url =>
        {
            asked.Add(url);
            if (asked.Count == 1) throw SubscriptionException.Empty();
            return Task.FromResult(7);
        });
        Assert.Equal(7, answer);
        Assert.Equal(["https://s.example.com/abc/", "https://s.example.com/abc/json"], asked);
    }

    [Fact]
    public async Task JsonFallbackKeepsTheErrorWorthTelling()
    {
        static async Task<(SubscriptionErrorKind Kind, int Count)> Outcome(string address, params SubscriptionException[] answers)
        {
            var count = 0;
            var e = await Assert.ThrowsAsync<SubscriptionException>(() =>
                SubscriptionClient.WithJsonFallback<int>(address, _ => throw answers[count++]));
            return (e.Kind, count);
        }
        const string plain = "https://s.example.com/abc/";
        Assert.Equal((SubscriptionErrorKind.Empty, 2), await Outcome(plain, SubscriptionException.Empty(), SubscriptionException.Http(404)));
        Assert.Equal((SubscriptionErrorKind.DeviceLimit, 2), await Outcome(plain, SubscriptionException.Empty(), SubscriptionException.DeviceLimit()));
        Assert.Equal((SubscriptionErrorKind.Http, 1), await Outcome(plain, SubscriptionException.Http(500)));
        Assert.Equal((SubscriptionErrorKind.Empty, 1), await Outcome(plain + "json", SubscriptionException.Empty()));
    }
}
