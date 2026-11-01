using System.Diagnostics;
using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

public sealed class ConfigBuilderTests : IDisposable
{
    private readonly string ruleDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public ConfigBuilderTests() => Directory.CreateDirectory(ruleDir);

    public void Dispose() => Directory.Delete(ruleDir, recursive: true);

    private static Server Server(string link) => new(null, ShareLink.Parse(link));

    private void WriteRuleSet(string tag) => File.WriteAllBytes(Path.Combine(ruleDir, $"{tag}.srs"), "SRS\u0001"u8.ToArray());

    [Fact]
    public void GlobalModeIsMinimal()
    {
        var settings = new AppSettings { Routing = RoutingMode.Global };
        var config = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), settings, ruleDir);

        Assert.Equal("warn", config["log"]?["level"]?.AsString);
        Assert.Null(config["experimental"]); // no clash API or cache file in the tunnel
        Assert.Null(config["route"]?["rule_set"]);
        Assert.Equal("proxy", config["route"]?["final"]?.AsString);
        Assert.True(config["route"]?["auto_detect_interface"]?.AsBool);

        var outbounds = config["outbounds"]?.AsArray ?? [];
        Assert.Equal(["proxy", "direct"], outbounds.Select(o => o["tag"]?.AsString).OfType<string>());
        Assert.DoesNotContain(outbounds, o => o["type"]?.AsString is "urltest" or "selector");

        var tun = config["inbounds"]?[0];
        Assert.Equal("tun", tun?["type"]?.AsString);
        Assert.Equal(Json.Strings(["172.19.0.1/30"]), tun?["address"]);
        Assert.Equal("mixed", tun?["stack"]?.AsString);
        Assert.Null(tun?["mtu"]);

        var dns = config["dns"];
        Assert.Equal("dns-remote", dns?["final"]?.AsString);
        Assert.Equal("ipv4_only", dns?["strategy"]?.AsString);
        Assert.Equal("proxy", dns?["servers"]?[0]?["detour"]?.AsString);
        Assert.Equal("https", dns?["servers"]?[0]?["type"]?.AsString);
        Assert.Empty(dns?["rules"]?.AsArray ?? [Json.Object()]);
    }

    [Fact]
    public void RussiaDirectUsesOnlyDownloadedRuleSets()
    {
        WriteRuleSet("geoip-ru");
        var settings = new AppSettings { BlockAds = true }; // ads list is not downloaded: rule must be left out
        var config = ConfigBuilder.Build(Server("vless://id@v.example.com:443?security=tls#V"), settings, ruleDir);

        var sets = config["route"]?["rule_set"]?.AsArray ?? [];
        Assert.Single(sets);
        Assert.Equal("geoip-ru", sets[0]["tag"]?.AsString);
        Assert.Equal("local", sets[0]["type"]?.AsString);
        Assert.Equal(Path.Combine(ruleDir, "geoip-ru.srs"), sets[0]["path"]?.AsString);
        Assert.True(Path.IsPathRooted(sets[0]["path"]?.AsString));

        var rules = config["route"]?["rules"]?.AsArray ?? [];
        Assert.DoesNotContain(rules, r => r["action"]?.AsString == "reject");
        Assert.Contains(rules, r => r["rule_set"] == Json.Strings(["geoip-ru"]) && r["outbound"]?.AsString == "direct");
        Assert.Contains(rules, r => r["domain_suffix"] == Json.Strings(["ru", "su", "xn--p1ai"]));

        // DNS rules may only use domain rule-sets; the geoip one must stay out.
        var dnsRules = config["dns"]?["rules"]?.AsArray ?? [];
        Assert.DoesNotContain(dnsRules, r => r["rule_set"] is not null);
        Assert.Equal("dns-direct", dnsRules.FirstOrDefault()?["server"]?.AsString);
    }

    [Fact]
    public void RuleOrderAndCustomDomains()
    {
        foreach (var tag in new[] { RuleSets.Ads, RuleSets.GeositeRu, RuleSets.GeoipRu }) WriteRuleSet(tag);
        var settings = new AppSettings
        {
            BlockAds = true,
            BlockDomains = ["tracker.example"],
            ProxyDomains = ["youtube.com"],
            DirectDomains = ["bank.example"],
        };
        var config = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), settings, ruleDir);
        var rules = (config["route"]?["rules"]?.AsArray ?? []).ToList();
        int Index(Func<Json, bool> match) => rules.FindIndex(r => match(r));

        int[] order =
        [
            Index(r => r["action"]?.AsString == "sniff"),
            Index(r => r["action"]?.AsString == "hijack-dns"),
            Index(r => r["domain_suffix"] == Json.Strings(["tracker.example"])),
            Index(r => r["rule_set"] == Json.Strings([RuleSets.Ads])),
            Index(r => r["domain_suffix"] == Json.Strings(["youtube.com"])),
            Index(r => r["domain_suffix"] == Json.Strings(["bank.example"])),
            Index(r => r["rule_set"] == Json.Strings([RuleSets.GeositeRu, RuleSets.GeoipRu])),
        ];
        Assert.Equal(order.Order(), order);
        Assert.DoesNotContain(-1, order);
        Assert.Equal(3, config["route"]?["rule_set"]?.AsArray?.Count);

        var dnsRules = config["dns"]?["rules"]?.AsArray ?? [];
        Assert.Contains(dnsRules, r => r["rule_set"] == Json.Strings([RuleSets.GeositeRu]));
        Assert.Contains(dnsRules, r => r["domain_suffix"] == Json.Strings(["bank.example"]));
    }

    [Fact]
    public void TlsFragmentSkipsRealityAndQuic()
    {
        var settings = new AppSettings { TlsFragment = true };
        var tls = ConfigBuilder.Build(Server("trojan://pw@t.example.com:443#T"), settings, null);
        Assert.True(tls["outbounds"]?[0]?["tls"]?["fragment"]?.AsBool);
        var reality = ConfigBuilder.Build(Server("vless://id@v.example.com:443?security=reality&pbk=k#R"), settings, null);
        Assert.Null(reality["outbounds"]?[0]?["tls"]?["fragment"]);
        var hy2 = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), settings, null);
        Assert.Null(hy2["outbounds"]?[0]?["tls"]?["fragment"]);
    }

    [Fact]
    public void DnsByNameGetsResolvers()
    {
        var settings = new AppSettings
        {
            RemoteDns = new DnsServer(DnsKind.Https, "dns.google"),
            DirectDns = new DnsServer(DnsKind.Tls, "common.dot.dns.yandex.net"),
            Ipv6 = true,
        };
        var config = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), settings, null);
        var servers = config["dns"]?["servers"]?.AsArray ?? [];
        Assert.Equal(["dns-remote", "dns-direct", "dns-bootstrap"], servers.Select(s => s["tag"]?.AsString));
        Assert.Equal("dns-direct", servers[0]["domain_resolver"]?.AsString);
        Assert.Equal("dns-bootstrap", servers[1]["domain_resolver"]?.AsString);
        Assert.Null(servers[2]["domain_resolver"]);
        Assert.Equal("prefer_ipv4", config["dns"]?["strategy"]?.AsString);
        Assert.Equal(2, config["inbounds"]?[0]?["address"]?.AsArray?.Count);
    }

    [Fact]
    public void OutputIsValidJson()
    {
        var config = ConfigBuilder.Build(Server("vless://id@v.example.com:443?security=reality&pbk=k&sni=a.com#V"), new AppSettings(), ruleDir);
        var text = config.ToJsonString(pretty: true);
        using (System.Text.Json.JsonDocument.Parse(text)) { }
        Assert.Equal(config, Json.Parse(text));
    }

    [Fact]
    public void LocalProxyReplacesOnlyTheTun()
    {
        var config = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), new AppSettings(), ruleDir);
        var local = ConfigBuilder.WithLocalProxy(config, 51080);

        var inbounds = local["inbounds"]?.AsArray ?? [];
        Assert.Single(inbounds);
        Assert.Equal("mixed", inbounds[0]["type"]?.AsString);
        Assert.Equal("127.0.0.1", inbounds[0]["listen"]?.AsString); // never reachable from the network
        Assert.Equal(51080, inbounds[0]["listen_port"]?.AsInt);

        foreach (var key in new[] { "log", "dns", "outbounds", "route" }) Assert.Equal(config[key], local[key]);
    }

    [Fact]
    public void DesktopTunNamesTheAdapterAndSkipsLocalNetworks()
    {
        var config = ConfigBuilder.Build(Server("hy2://pw@h.example.com:443#H"), new AppSettings(), ruleDir);
        var tun = ConfigBuilder.ForDesktopTun(config, new AppSettings())["inbounds"]?[0];
        Assert.Equal(ConfigBuilder.TunInterfaceName, tun?["interface_name"]?.AsString);
        Assert.Contains("192.168.0.0/16", tun?["route_exclude_address"]?.AsArray?.Select(a => a.AsString) ?? []);
        var all = ConfigBuilder.ForDesktopTun(config, new AppSettings { ExcludeLocalNetworks = false })["inbounds"]?[0];
        Assert.Null(all?["route_exclude_address"]);
    }

    [Fact]
    public void ProbeRoutesEachPortToItsServer()
    {
        var settings = new AppSettings { TlsFragment = true };
        Server[] servers =
        [
            Server("vless://11111111-1111-1111-1111-111111111111@a.example.com:443?security=tls&sni=a.example.com#A"),
            Server("hy2://pw@h.example.com:443#H"),
        ];
        var config = ConfigBuilder.Probe(servers, [20001, 20002], settings);

        Assert.Equal("error", config["log"]?["level"]?.AsString);
        Assert.Equal("probe.db", config["experimental"]?["cache_file"]?["path"]?.AsString); // the tunnel locks the default cache file
        Assert.Null(config["route"]?["rule_set"]);
        Assert.True(config["route"]?["auto_detect_interface"]?.AsBool);

        var inbounds = config["inbounds"]?.AsArray ?? [];
        Assert.Equal([20001, 20002], inbounds.Select(i => i["listen_port"]?.AsInt).OfType<int>());
        Assert.Equal(["mixed", "mixed"], inbounds.Select(i => i["type"]?.AsString).OfType<string>());
        Assert.Equal(["127.0.0.1", "127.0.0.1"], inbounds.Select(i => i["listen"]?.AsString).OfType<string>());

        Assert.Equal(Json.Array(
            Json.Object(("inbound", Json.Strings(["in-0"])), ("outbound", "probe-0")),
            Json.Object(("inbound", Json.Strings(["in-1"])), ("outbound", "probe-1"))), config["route"]?["rules"]);
        var outbounds = config["outbounds"]?.AsArray ?? [];
        Assert.Equal(["probe-0", "probe-1", "direct"], outbounds.Select(o => o["tag"]?.AsString).OfType<string>());
        Assert.Equal("a.example.com", outbounds[0]["server"]?.AsString);
        Assert.True(outbounds[0]["tls"]?["fragment"]?.AsBool);
        Assert.Null(outbounds[1]["tls"]?["fragment"]); // QUIC cannot fragment

        // Only the direct resolver: nothing goes through a remote one.
        Assert.Equal(["dns-direct"], (config["dns"]?["servers"]?.AsArray ?? []).Select(s => s["tag"]?.AsString).OfType<string>());
        Json.Parse(config.ToJsonString());
    }

    [Fact]
    public void ProbeSkipsGroups()
    {
        var members = new[] { ShareLink.Parse("hy2://pw@a.example.com:443#A"), ShareLink.Parse("hy2://pw@b.example.com:443#B") };
        var group = new Server(null, ServerGroup.Make(members, "Авто"));
        var config = ConfigBuilder.Probe([group, .. group.Members], [1, 2, 3], new AppSettings());
        Assert.Equal([2, 3], (config["inbounds"]?.AsArray ?? []).Select(i => i["listen_port"]?.AsInt).OfType<int>());
    }

    [Fact]
    public void StatusApiJoinsTheExperimentalBlock()
    {
        var api = StatusApi.Create(4242);
        Assert.Equal(32, api.Secret.Length);
        var config = ConfigBuilder.WithStatusApi(Json.Object(("experimental", Json.Object(("cache_file", Json.Object(("enabled", true)))))), api);
        Assert.Equal("127.0.0.1:4242", config["experimental"]?["clash_api"]?["external_controller"]?.AsString);
        Assert.Equal(api.Secret, config["experimental"]?["clash_api"]?["secret"]?.AsString);
        Assert.True(config["experimental"]?["cache_file"]?["enabled"]?.AsBool);
        Assert.NotNull(ConfigBuilder.WithStatusApi(Json.Object(), api)["experimental"]?["clash_api"]);
    }

    /// <summary>
    /// Runs <c>sing-box check</c> on every kind of config the app writes when
    /// SING_BOX names a sing-box binary for this machine; does nothing otherwise.
    /// RULE_SETS may name a folder with compiled rule sets to check those too.
    /// </summary>
    [Fact]
    public void SingBoxAcceptsTheConfigs()
    {
        if (Environment.GetEnvironmentVariable("SING_BOX") is not { Length: > 0 } singBox) return;
        if (Environment.GetEnvironmentVariable("RULE_SETS") is { Length: > 0 } ruleSets)
        {
            foreach (var tag in new[] { RuleSets.Ads, RuleSets.GeositeRu, RuleSets.GeoipRu })
                File.Copy(Path.Combine(ruleSets, $"{tag}.srs"), Path.Combine(ruleDir, $"{tag}.srs"));
        }
        string[] links =
        [
            "vless://11111111-2222-3333-4444-555555555555@v.example.com:443?security=reality&pbk=AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA&sid=ab&sni=a.com&flow=xtls-rprx-vision#R",
            "vless://11111111-2222-3333-4444-555555555555@v.example.com:443?security=tls&type=ws&path=%2Fws%3Fed%3D2048&host=cdn.example.com#W",
            "vless://11111111-2222-3333-4444-555555555555@g.example.com:443?security=tls&type=grpc&serviceName=api#G",
            "hy2://pw@h.example.com:443?mport=20000-30000&obfs=salamander&obfs-password=x&sni=h.example.com#H",
            "trojan://pw@t.example.com:443?type=httpupgrade&path=%2Fu#T",
            "ss://2022-blake3-aes-128-gcm:AAAAAAAAAAAAAAAAAAAAAA%3D%3D@s.example.com:8388#S",
        ];
        var servers = links.Select(Server).ToList();
        var group = new Server(null, ServerGroup.Make([ShareLink.Parse(links[0]), ShareLink.Parse(links[3])], "Авто-выбор"));
        var settings = new AppSettings
        {
            TlsFragment = true,
            BlockAds = true,
            BlockDomains = ["ads.example"],
            ProxyDomains = ["youtube.com"],
            DirectDomains = ["bank.example"],
            RemoteDns = new DnsServer(DnsKind.Https, "dns.google"),
            DirectDns = new DnsServer(DnsKind.Tls, "common.dot.dns.yandex.net"),
            Ipv6 = true,
            Mtu = 1400,
        };
        var configs = new List<Json>();
        foreach (var server in servers.Append(group))
        {
            var config = ConfigBuilder.Build(server, settings, ruleDir);
            configs.Add(ConfigBuilder.WithLocalProxy(config, 51080));
            configs.Add(ConfigBuilder.WithStatusApi(ConfigBuilder.ForDesktopTun(config, settings), StatusApi.Create(51090)));
        }
        configs.Add(ConfigBuilder.Probe([.. servers, .. group.Members], Enumerable.Range(20001, servers.Count + 2).ToList(), settings));

        for (int i = 0; i < configs.Count; i++)
        {
            var file = Path.Combine(ruleDir, $"config-{i}.json");
            File.WriteAllText(file, configs[i].ToJsonString());
            using var check = Process.Start(new ProcessStartInfo(singBox, ["check", "-c", file, "-D", ruleDir])
            {
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            })!;
            var output = check.StandardError.ReadToEnd() + check.StandardOutput.ReadToEnd();
            check.WaitForExit();
            Assert.True(check.ExitCode == 0, $"config {i}: {output}");
        }
    }
}
