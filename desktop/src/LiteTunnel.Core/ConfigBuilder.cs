namespace LiteTunnel.Core;

/// <summary>Builds the sing-box config the core runs.</summary>
/// <remarks>
/// Power rules this file enforces: no selector groups, a urltest group only
/// for a subscription's "Авто-выбор" entry and then tested every 10 minutes,
/// no remote rule-sets (they re-download on a timer), no clash API, log level
/// from settings (warn by default).
/// </remarks>
public static class ConfigBuilder
{
    public const string ProxyTag = "proxy";
    public const string DirectTag = "direct";
    internal const string RemoteDnsTag = "dns-remote";
    internal const string DirectDnsTag = "dns-direct";
    internal const string BootstrapDnsTag = "dns-bootstrap";
    internal static readonly string[] RussianSuffixes = ["ru", "su", "xn--p1ai"];

    /// <param name="ruleSetDirectory">
    /// Folder with downloaded <c>&lt;tag&gt;.srs</c> files. Rule-sets that are
    /// missing there are left out of the config.
    /// </param>
    public static Json Build(Server server, AppSettings settings, string? ruleSetDirectory)
    {
        var available = ruleSetDirectory is null ? [] : RuleSets.Available(ruleSetDirectory);

        // A group runs its members behind a urltest outbound with the proxy tag.
        var proxies = ServerGroup.Outbounds(server.Outbound, ProxyTag);
        if (settings.TlsFragment) FragmentTls(proxies);

        var usedSets = new List<string>();
        List<string> Use(params string[] tags)
        {
            var present = tags.Where(available.Contains).ToList();
            foreach (var tag in present.Where(t => !usedSets.Contains(t))) usedSets.Add(tag);
            return present;
        }

        // DNS: remote resolver through the proxy; the direct one only for
        // domains that go direct anyway and for the proxy server's own name.
        var dnsRules = new List<Json>();
        if (settings.Routing == RoutingMode.RussiaDirect)
        {
            dnsRules.Add(Json.Object(("domain_suffix", Json.Strings(RussianSuffixes)), ("server", DirectDnsTag)));
            var sets = Use(RuleSets.GeositeRu);
            if (sets.Count > 0) dnsRules.Add(Json.Object(("rule_set", Json.Strings(sets)), ("server", DirectDnsTag)));
        }
        if (settings.DirectDomains.Count > 0)
            dnsRules.Add(Json.Object(("domain_suffix", Json.Strings(settings.DirectDomains)), ("server", DirectDnsTag)));
        var dnsServers = new List<Json>
        {
            DnsServerJson(settings.RemoteDns, RemoteDnsTag, ProxyTag, DirectDnsTag),
            DnsServerJson(settings.DirectDns, DirectDnsTag, null, BootstrapDnsTag),
        };
        if (!ShareLink.IsIPAddress(settings.DirectDns.Address))
            dnsServers.Add(DnsServerJson(DnsServer.Yandex, BootstrapDnsTag, null, null));
        var dns = Json.Object(
            ("servers", Json.Array(dnsServers)),
            ("rules", Json.Array(dnsRules)),
            ("final", RemoteDnsTag),
            ("strategy", settings.Ipv6 ? "prefer_ipv4" : "ipv4_only"));

        var addresses = new List<string> { "172.19.0.1/30" };
        if (settings.Ipv6) addresses.Add("fdfe:dcba:9876::1/126");
        var tun = Json.Object(
            ("type", "tun"),
            ("tag", "tun-in"),
            ("address", Json.Strings(addresses)),
            ("auto_route", true),
            ("strict_route", true),
            ("stack", settings.TunStack.Raw()));
        if (settings.Mtu > 0) tun = tun.With("mtu", settings.Mtu);

        var rules = new List<Json>();
        if (settings.Sniffing) rules.Add(Json.Object(("action", "sniff")));
        rules.Add(Json.Object(("protocol", "dns"), ("action", "hijack-dns")));
        rules.Add(Json.Object(("port", 53), ("action", "hijack-dns")));
        rules.Add(Json.Object(("ip_is_private", true), ("outbound", DirectTag)));
        if (settings.BlockDomains.Count > 0)
            rules.Add(Json.Object(("domain_suffix", Json.Strings(settings.BlockDomains)), ("action", "reject")));
        if (settings.BlockAds)
        {
            var sets = Use(RuleSets.Ads);
            if (sets.Count > 0) rules.Add(Json.Object(("rule_set", Json.Strings(sets)), ("action", "reject")));
        }
        if (settings.ProxyDomains.Count > 0)
            rules.Add(Json.Object(("domain_suffix", Json.Strings(settings.ProxyDomains)), ("outbound", ProxyTag)));
        if (settings.DirectDomains.Count > 0)
            rules.Add(Json.Object(("domain_suffix", Json.Strings(settings.DirectDomains)), ("outbound", DirectTag)));
        if (settings.Routing == RoutingMode.RussiaDirect)
        {
            rules.Add(Json.Object(("domain_suffix", Json.Strings(RussianSuffixes)), ("outbound", DirectTag)));
            var sets = Use(RuleSets.GeositeRu, RuleSets.GeoipRu);
            if (sets.Count > 0) rules.Add(Json.Object(("rule_set", Json.Strings(sets)), ("outbound", DirectTag)));
        }

        var route = Json.Object(
            ("rules", Json.Array(rules)),
            ("final", ProxyTag),
            ("auto_detect_interface", true),
            ("default_domain_resolver", DirectDnsTag));
        if (ruleSetDirectory is not null && usedSets.Count > 0)
        {
            route = route.With("rule_set", Json.Array(usedSets.Select(tag => Json.Object(
                ("type", "local"),
                ("tag", tag),
                ("format", "binary"),
                ("path", Path.Combine(ruleSetDirectory, $"{tag}.srs"))))));
        }

        return Json.Object(
            ("log", Json.Object(("level", settings.LogLevel.Raw()), ("timestamp", true))),
            ("dns", dns),
            ("inbounds", Json.Array(tun)),
            ("outbounds", Json.Array([.. proxies, Json.Object(("type", "direct"), ("tag", DirectTag))])),
            ("route", route));
    }

    public const string LocalProxyTag = "mixed-in";

    /// <summary>
    /// The same config with a local SOCKS and HTTP proxy in place of the tun:
    /// the system proxy mode, which needs no administrator rights.
    /// </summary>
    public static Json WithLocalProxy(Json config, int port) =>
        config.With("inbounds", Json.Array(Json.Object(
            ("type", "mixed"),
            ("tag", LocalProxyTag),
            ("listen", "127.0.0.1"),
            ("listen_port", port))));

    /// <summary>
    /// The clash API on loopback, with a secret: where the app reads speed and
    /// totals while it shows them. Desktop only; mobile reads the core in process.
    /// </summary>
    public static Json WithStatusApi(Json config, StatusApi api) =>
        config.With("experimental", (config["experimental"] ?? Json.Object()).With("clash_api", Json.Object(
            ("external_controller", api.Controller),
            ("secret", api.Secret))));

    /// <summary>The tun adapter's name, so the app can find its traffic counters.</summary>
    public static string TunInterfaceName => OperatingSystem.IsWindows() ? "LiteTunnel" : "litetunnel0";

    internal static readonly string[] LocalNetworks = ["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16", "169.254.0.0/16", "fc00::/7", "fe80::/10"];

    /// <summary>
    /// Desktop additions to the tun inbound: a fixed adapter name and, when
    /// local networks bypass the VPN, routes for them left out of the tun,
    /// so a printer or a NAS is reached without the core.
    /// </summary>
    public static Json ForDesktopTun(Json config, AppSettings settings)
    {
        if (config["inbounds"]?.AsArray is not { } inbounds) return config;
        var patched = inbounds.Select(inbound =>
        {
            if (inbound["type"]?.AsString != "tun") return inbound;
            var tun = inbound.With("interface_name", TunInterfaceName);
            return settings.ExcludeLocalNetworks ? tun.With("route_exclude_address", Json.Strings(LocalNetworks)) : tun;
        });
        return config.With("inbounds", Json.Array(patched));
    }

    /// <summary>
    /// A config for pinging servers through themselves: a local proxy on
    /// <c>ports[i]</c> for each of <paramref name="servers"/>, routed to that
    /// server alone. No tun, no rule-sets, errors-only log. Groups are left
    /// out: ping their members.
    /// </summary>
    public static Json Probe(IReadOnlyList<Server> servers, IReadOnlyList<int> ports, AppSettings settings)
    {
        var inbounds = new List<Json>();
        var proxies = new List<Json>();
        var rules = new List<Json>();
        for (int index = 0; index < Math.Min(servers.Count, ports.Count); index++)
        {
            var server = servers[index];
            if (server.Members.Count > 0) continue;
            string inTag = $"in-{index}", outTag = $"probe-{index}";
            inbounds.Add(Json.Object(("type", "mixed"), ("tag", inTag), ("listen", "127.0.0.1"), ("listen_port", ports[index])));
            proxies.Add(server.Outbound.With("tag", outTag));
            rules.Add(Json.Object(("inbound", Json.Strings([inTag])), ("outbound", outTag)));
        }
        if (settings.TlsFragment) FragmentTls(proxies);

        var dnsServers = new List<Json> { DnsServerJson(settings.DirectDns, DirectDnsTag, null, BootstrapDnsTag) };
        if (!ShareLink.IsIPAddress(settings.DirectDns.Address))
            dnsServers.Add(DnsServerJson(DnsServer.Yandex, BootstrapDnsTag, null, null));
        return Json.Object(
            ("log", Json.Object(("level", "error"))),
            ("dns", Json.Object(
                ("servers", Json.Array(dnsServers)),
                ("final", DirectDnsTag),
                ("strategy", settings.Ipv6 ? "prefer_ipv4" : "ipv4_only"))),
            ("inbounds", Json.Array(inbounds)),
            ("outbounds", Json.Array([.. proxies, Json.Object(("type", "direct"), ("tag", DirectTag))])),
            ("route", Json.Object(
                ("rules", Json.Array(rules)),
                ("final", DirectTag),
                ("auto_detect_interface", true),
                ("default_domain_resolver", DirectDnsTag))),
            // The running core holds a lock on the default cache file; a
            // second core waits on it and fails to start.
            ("experimental", Json.Object(("cache_file", Json.Object(("enabled", true), ("path", "probe.db"))))));
    }

    /// <summary>
    /// Splits the TLS ClientHello of outbounds that run TLS over TCP. Reality
    /// and QUIC protocols cannot take it.
    /// </summary>
    internal static void FragmentTls(List<Json> outbounds)
    {
        for (int i = 0; i < outbounds.Count; i++)
        {
            var tls = outbounds[i]["tls"];
            if (tls?["enabled"]?.AsBool != true || tls["reality"] is not null) continue;
            var type = outbounds[i]["type"]?.AsString;
            if (type != ProxyProtocol.Hysteria2.Raw() && type != ProxyProtocol.Tuic.Raw())
                outbounds[i] = outbounds[i].With("tls", tls.With("fragment", true));
        }
    }

    internal static Json DnsServerJson(DnsServer server, string tag, string? detour, string? resolver)
    {
        var json = Json.Object(("type", server.Kind.Raw()), ("tag", tag), ("server", server.Address));
        if (detour is not null) json = json.With("detour", detour);
        // A DNS server given by name needs another server to find its address.
        if (resolver is not null && !ShareLink.IsIPAddress(server.Address)) json = json.With("domain_resolver", resolver);
        return json;
    }
}

public static class RuleSets
{
    public const string GeoipRu = "geoip-ru";
    public const string GeositeRu = "geosite-category-ru";
    public const string Ads = "geosite-category-ads-all";

    /// <summary>Primary source first, then a CDN mirror.</summary>
    public static IReadOnlyList<string> Urls(string tag)
    {
        var repo = tag.StartsWith("geoip-", StringComparison.Ordinal) ? "sing-geoip" : "sing-geosite";
        return
        [
            $"https://raw.githubusercontent.com/SagerNet/{repo}/rule-set/{tag}.srs",
            $"https://cdn.jsdelivr.net/gh/SagerNet/{repo}@rule-set/{tag}.srs",
        ];
    }

    public static IReadOnlyList<string> Needed(AppSettings settings)
    {
        var tags = new List<string>();
        if (settings.Routing == RoutingMode.RussiaDirect) tags.AddRange([GeositeRu, GeoipRu]);
        if (settings.BlockAds) tags.Add(Ads);
        return tags;
    }

    public static HashSet<string> Available(string directory)
    {
        try
        {
            return Directory.EnumerateFiles(directory, "*.srs").Select(f => Path.GetFileNameWithoutExtension(f)).ToHashSet();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>Binary rule-sets start with "SRS".</summary>
    internal static bool LooksValid(ReadOnlySpan<byte> data) => data.Length > 3 && data[..3].SequenceEqual("SRS"u8);
}

/// <summary>Downloads binary rule-sets once a week, when the app needs them. Never on a timer.</summary>
public sealed class RuleSetUpdater
{
    public TimeSpan MaxAge { get; init; } = TimeSpan.FromDays(7);

    /// <summary>Whether any needed rule-set is missing or old.</summary>
    public bool IsDue(IEnumerable<string> tags, string directory, DateTimeOffset? now = null)
    {
        var at = now ?? DateTimeOffset.UtcNow;
        return tags.Any(tag =>
        {
            var file = Path.Combine(directory, $"{tag}.srs");
            return !File.Exists(file) || at - File.GetLastWriteTimeUtc(file) >= MaxAge;
        });
    }

    /// <summary>Fetches the given rule-sets. Returns the tags that failed.</summary>
    public async Task<IReadOnlyList<string>> UpdateAsync(IEnumerable<string> tags, string directory, CancellationToken cancel = default)
    {
        Directory.CreateDirectory(directory);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(SubscriptionClient.DefaultUserAgent);
        var failed = new List<string>();
        foreach (var tag in tags)
        {
            var saved = false;
            foreach (var url in RuleSets.Urls(tag))
            {
                try
                {
                    using var response = await http.GetAsync(url, cancel).ConfigureAwait(false);
                    if ((int)response.StatusCode != 200) continue;
                    var data = await response.Content.ReadAsByteArrayAsync(cancel).ConfigureAwait(false);
                    if (!RuleSets.LooksValid(data)) continue;
                    Store.WriteAtomically(Path.Combine(directory, $"{tag}.srs"), data);
                    saved = true;
                    break;
                }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException or IOException or UnauthorizedAccessException)
                {
                    if (cancel.IsCancellationRequested) throw;
                }
            }
            if (!saved) failed.Add(tag);
        }
        return failed;
    }
}
