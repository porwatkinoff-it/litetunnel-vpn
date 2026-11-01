using System.Globalization;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>
/// The app's files, in one folder (%LOCALAPPDATA%\LiteTunnel, ~/.local/share/LiteTunnel):
/// <c>state.json</c> (servers, subscriptions, settings), <c>config.json</c> (what the
/// core runs), <c>rulesets/</c>, and the core's <c>work/</c>.
/// </summary>
public sealed class Store(string root)
{
    public string Root { get; } = root;
    public string StateFile => Path.Combine(Root, "state.json");
    public string ConfigFile => Path.Combine(Root, "config.json");
    public string RuleSetDirectory => Path.Combine(Root, "rulesets");
    public string WorkDirectory => Path.Combine(Root, "work");

    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData, Environment.SpecialFolderOption.Create), "LiteTunnel");

    public void PrepareDirectories()
    {
        foreach (var dir in new[] { Root, RuleSetDirectory, WorkDirectory }) Directory.CreateDirectory(dir);
        // Configs hold keys: only this user may read the folder.
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(Root, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    /// <summary>A missing or broken file gives an empty state rather than a crash.</summary>
    public AppState LoadState()
    {
        try
        {
            return StateCodec.Decode(Json.Parse(File.ReadAllText(StateFile)));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppState();
        }
    }

    public void Save(AppState state) =>
        WriteAtomically(StateFile, Encoding.UTF8.GetBytes(StateCodec.Encode(state).ToJsonString(pretty: true)));

    /// <summary>Writes the core config for the selected server; false (and no file) when none is selected.</summary>
    public bool WriteConfig(AppState state) => WriteConfig(state, null, null);

    /// <summary>
    /// The same, for the running core: <paramref name="localProxyPort"/> in place of
    /// the one in settings when that one is taken, and the status API.
    /// </summary>
    public bool WriteConfig(AppState state, int? localProxyPort, StatusApi? api)
    {
        if (state.SelectedServer is not { } server)
        {
            File.Delete(ConfigFile);
            return false;
        }
        var config = ConfigBuilder.Build(server, state.Settings, RuleSetDirectory);
        config = state.Settings.Mode == ConnectionMode.SystemProxy
            ? ConfigBuilder.WithLocalProxy(config, localProxyPort ?? state.Settings.LocalProxyPort)
            : ConfigBuilder.ForDesktopTun(config, state.Settings);
        if (api is not null) config = ConfigBuilder.WithStatusApi(config, api);
        WriteAtomically(ConfigFile, Encoding.UTF8.GetBytes(config.ToJsonString()));
        return true;
    }

    public string? ReadConfig()
    {
        try
        {
            return File.ReadAllText(ConfigFile);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Temp file and rename, so a crash mid-write never leaves half a file.</summary>
    public static void WriteAtomically(string file, byte[] data)
    {
        var directory = Path.GetDirectoryName(Path.GetFullPath(file))!;
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $".{Path.GetFileName(file)}.tmp");
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(data);
            stream.Flush(flushToDisk: true);
        }
        File.Move(temp, file, overwrite: true);
    }
}

/// <summary>
/// AppState as JSON. Reading is lenient: a missing key takes its default and a
/// broken server or subscription is dropped, so an older or newer file still loads.
/// </summary>
public static class StateCodec
{
    public static Json Encode(AppState state) => Json.Object(
        ("subscriptions", Json.Array(state.Subscriptions.Select(EncodeSubscription))),
        ("servers", Json.Array(state.Servers.Select(EncodeServer))),
        ("selectedServerId", state.SelectedServerId?.ToString()),
        ("settings", EncodeSettings(state.Settings)));

    public static AppState Decode(Json json)
    {
        var subscriptions = (json["subscriptions"]?.AsArray ?? []).Select(DecodeSubscription).OfType<Subscription>().ToList();
        var servers = (json["servers"]?.AsArray ?? []).Select(DecodeServer).OfType<Server>().ToList();
        var selected = Uuid(json["selectedServerId"]);
        return new AppState
        {
            Subscriptions = subscriptions,
            Servers = servers,
            SelectedServerId = selected is { } id && servers.Any(s => s.Id == id) ? id : null,
            Settings = json["settings"] is { } settings ? DecodeSettings(settings) : new AppSettings(),
        };
    }

    private static Json EncodeServer(Server s) => Json.Object(
        ("id", s.Id.ToString()),
        ("subscriptionId", s.SubscriptionId?.ToString()),
        ("name", s.Name),
        ("proto", s.Proto.Raw()),
        ("host", s.Host),
        ("port", s.Port),
        ("outbound", s.Outbound),
        ("shareLink", s.ShareLink),
        ("lastPing", s.LastPing switch
        {
            PingMs ms => (Json)ms.Value,
            PingFailed => (Json)"failed",
            _ => null,
        }));

    private static Server? DecodeServer(Json json)
    {
        if (ProxyProtocols.Of(json["proto"]?.AsString) is not { } proto) return null;
        if (json["outbound"] is not JObject outbound) return null;
        if (json["name"]?.AsString is not { } name) return null;
        if (json["host"]?.AsString is not { } host) return null;
        if (json["port"]?.AsInt is not { } port) return null;
        PingResult? lastPing = json["lastPing"] switch
        {
            JString { Value: "failed" } => PingResult.Failed,
            { AsInt: { } ms } => PingResult.Ms(ms),
            _ => null,
        };
        return new Server(Uuid(json["id"]) ?? Guid.NewGuid(), Uuid(json["subscriptionId"]), name, proto, host, port, outbound, json["shareLink"]?.AsString, lastPing);
    }

    private static Json EncodeSubscription(Subscription s) => Json.Object(
        ("id", s.Id.ToString()),
        ("name", s.Name),
        ("url", s.Url),
        ("userAgent", s.UserAgent),
        ("updateIntervalHours", s.UpdateIntervalHours),
        ("lastUpdated", DateJson(s.LastUpdated)),
        ("lastError", s.LastError),
        ("usage", s.Usage is { } u
            ? Json.Object(("upload", u.Upload), ("download", u.Download), ("total", u.Total), ("expire", DateJson(u.Expire)))
            : null),
        ("supportUrl", s.SupportUrl),
        ("webPageUrl", s.WebPageUrl),
        ("announce", s.Announce),
        ("skippedCount", s.SkippedCount),
        ("skippedReasons", Json.Strings(s.SkippedReasons)));

    private static Subscription? DecodeSubscription(Json json)
    {
        if (Uuid(json["id"]) is not { } id) return null;
        if (json["url"]?.AsString is not { } url) return null;
        var usage = json["usage"] is { } u
            ? new Usage(u["upload"]?.AsLong ?? 0, u["download"]?.AsLong ?? 0, u["total"]?.AsLong ?? 0, ParseDate(u["expire"]))
            : null;
        var defaults = new Subscription { Name = "", Url = "" };
        return new Subscription
        {
            Id = id,
            Name = json["name"]?.AsString ?? "",
            Url = url,
            UserAgent = json["userAgent"]?.AsString,
            UpdateIntervalHours = json["updateIntervalHours"]?.AsInt ?? defaults.UpdateIntervalHours,
            LastUpdated = ParseDate(json["lastUpdated"]),
            LastError = json["lastError"]?.AsString,
            Usage = usage,
            SupportUrl = json["supportUrl"]?.AsString,
            WebPageUrl = json["webPageUrl"]?.AsString,
            Announce = json["announce"]?.AsString,
            SkippedCount = json["skippedCount"]?.AsInt ?? 0,
            SkippedReasons = (json["skippedReasons"]?.AsArray ?? []).Select(r => r.AsString).OfType<string>().ToList(),
        };
    }

    private static Json EncodeSettings(AppSettings s) => Json.Object(
        ("routing", s.Routing.Raw()),
        ("directDomains", Json.Strings(s.DirectDomains)),
        ("proxyDomains", Json.Strings(s.ProxyDomains)),
        ("blockDomains", Json.Strings(s.BlockDomains)),
        ("remoteDns", EncodeDns(s.RemoteDns)),
        ("directDns", EncodeDns(s.DirectDns)),
        ("killSwitch", s.KillSwitch),
        ("excludeLocalNetworks", s.ExcludeLocalNetworks),
        ("smartSleep", s.SmartSleep),
        ("tlsFragment", s.TlsFragment),
        ("blockAds", s.BlockAds),
        ("sniffing", s.Sniffing),
        ("ipv6", s.Ipv6),
        ("mtu", s.Mtu),
        ("tunStack", s.TunStack.Raw()),
        ("logLevel", s.LogLevel.Raw()),
        ("sortMode", s.SortMode.Raw()),
        ("pingOnOpen", s.PingOnOpen),
        ("pingMode", s.PingMode.Raw()),
        ("pingUrl", s.PingUrl),
        ("pingDisplay", s.PingDisplay.Raw()),
        ("pingOnUpdate", s.PingOnUpdate),
        ("mode", s.Mode.Raw()),
        ("localProxyPort", s.LocalProxyPort));

    private static AppSettings DecodeSettings(Json json)
    {
        var d = new AppSettings();
        bool Bool(string key, bool fallback) => json[key]?.AsBool ?? fallback;
        List<string> List(string key) => (json[key]?.AsArray ?? []).Select(v => v.AsString).OfType<string>().ToList();
        T Pick<T>(string key, T fallback, Func<T, string> raw) where T : struct, Enum =>
            Enum.GetValues<T>().Cast<T?>().FirstOrDefault(v => raw(v!.Value) == json[key]?.AsString) ?? fallback;
        var port = json["localProxyPort"]?.AsInt;
        return new AppSettings
        {
            Routing = Pick("routing", d.Routing, EnumRaw.Raw),
            DirectDomains = List("directDomains"),
            ProxyDomains = List("proxyDomains"),
            BlockDomains = List("blockDomains"),
            RemoteDns = DecodeDns(json["remoteDns"]) ?? d.RemoteDns,
            DirectDns = DecodeDns(json["directDns"]) ?? d.DirectDns,
            KillSwitch = Bool("killSwitch", d.KillSwitch),
            ExcludeLocalNetworks = Bool("excludeLocalNetworks", d.ExcludeLocalNetworks),
            SmartSleep = Bool("smartSleep", d.SmartSleep),
            TlsFragment = Bool("tlsFragment", d.TlsFragment),
            BlockAds = Bool("blockAds", d.BlockAds),
            Sniffing = Bool("sniffing", d.Sniffing),
            Ipv6 = Bool("ipv6", d.Ipv6),
            Mtu = json["mtu"]?.AsInt ?? d.Mtu,
            TunStack = Pick("tunStack", d.TunStack, EnumRaw.Raw),
            LogLevel = Pick("logLevel", d.LogLevel, EnumRaw.Raw),
            SortMode = Pick("sortMode", d.SortMode, EnumRaw.Raw),
            PingOnOpen = Bool("pingOnOpen", d.PingOnOpen),
            PingMode = Pick("pingMode", d.PingMode, EnumRaw.Raw),
            PingUrl = TextUtil.NonEmpty(json["pingUrl"]?.AsString) ?? d.PingUrl,
            PingDisplay = Pick("pingDisplay", d.PingDisplay, EnumRaw.Raw),
            PingOnUpdate = Bool("pingOnUpdate", d.PingOnUpdate),
            Mode = Pick("mode", d.Mode, EnumRaw.Raw),
            LocalProxyPort = port is { } p && ShareLink.ValidPort(p) ? p : d.LocalProxyPort,
        };
    }

    private static Json EncodeDns(DnsServer d) => Json.Object(("kind", d.Kind.Raw()), ("address", d.Address));

    private static DnsServer? DecodeDns(Json? json)
    {
        var raw = json?["kind"]?.AsString;
        if (Enum.GetValues<DnsKind>().Cast<DnsKind?>().FirstOrDefault(k => k!.Value.Raw() == raw) is not { } kind) return null;
        if (TextUtil.NonEmpty(json?["address"]?.AsString) is not { } address) return null;
        return new DnsServer(kind, address);
    }

    private static Guid? Uuid(Json? json) => Guid.TryParse(json?.AsString, out var id) ? id : null;

    private static Json? DateJson(DateTimeOffset? date) =>
        date?.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'", CultureInfo.InvariantCulture);

    private static DateTimeOffset? ParseDate(Json? json) =>
        DateTimeOffset.TryParse(json?.AsString, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var d) ? d : null;
}
