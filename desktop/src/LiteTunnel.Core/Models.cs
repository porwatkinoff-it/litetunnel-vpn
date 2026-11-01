using System.Text.RegularExpressions;

namespace LiteTunnel.Core;

public enum ProxyProtocol { Vless, Vmess, Trojan, Shadowsocks, Hysteria2, Tuic, Socks, Http }

public static class ProxyProtocols
{
    public static string Raw(this ProxyProtocol p) => p switch
    {
        ProxyProtocol.Vless => "vless",
        ProxyProtocol.Vmess => "vmess",
        ProxyProtocol.Trojan => "trojan",
        ProxyProtocol.Shadowsocks => "shadowsocks",
        ProxyProtocol.Hysteria2 => "hysteria2",
        ProxyProtocol.Tuic => "tuic",
        ProxyProtocol.Socks => "socks",
        _ => "http",
    };

    public static string Title(this ProxyProtocol p) => p switch
    {
        ProxyProtocol.Vless => "VLESS",
        ProxyProtocol.Vmess => "VMess",
        ProxyProtocol.Trojan => "Trojan",
        ProxyProtocol.Shadowsocks => "Shadowsocks",
        ProxyProtocol.Hysteria2 => "Hysteria2",
        ProxyProtocol.Tuic => "TUIC",
        ProxyProtocol.Socks => "SOCKS5",
        _ => "HTTP",
    };

    public static ProxyProtocol? Of(string? raw) =>
        Enum.GetValues<ProxyProtocol>().Cast<ProxyProtocol?>().FirstOrDefault(p => p!.Value.Raw() == raw);
}

/// <summary>What a share link or a subscription entry turns into before it gets an ID.</summary>
/// <param name="Outbound">sing-box outbound without <c>tag</c>.</param>
public sealed record ParsedServer(string Name, ProxyProtocol Proto, string Host, int Port, Json Outbound, string? ShareLink)
{
    /// <summary>Survives a subscription refresh as long as the provider keeps the server.</summary>
    public string MatchKey => $"{Proto.Raw()}|{Host}|{Port}|{Name}";
}

public sealed record Server(
    Guid Id,
    Guid? SubscriptionId,
    string Name,
    ProxyProtocol Proto,
    string Host,
    int Port,
    Json Outbound,
    string? ShareLink,
    PingResult? LastPing = null)
{
    public Server(Guid? subscriptionId, ParsedServer parsed, Guid? id = null)
        : this(id ?? Guid.NewGuid(), subscriptionId, parsed.Name, parsed.Proto, parsed.Host, parsed.Port, parsed.Outbound, parsed.ShareLink) { }

    public string MatchKey => $"{Proto.Raw()}|{Host}|{Port}|{Name}";

    /// <summary>Transport and security in one short label, e.g. "Reality · Vision".</summary>
    public string Detail
    {
        get
        {
            if (ServerGroup.Members(Outbound) is { } members) return $"Авто-выбор из {members.Count}";
            var parts = new List<string>();
            var tls = Outbound["tls"];
            if (tls is not null && tls["enabled"]?.AsBool == true)
                parts.Add(tls["reality"]?["enabled"]?.AsBool == true ? "Reality" : "TLS");
            if (Outbound["transport"]?["type"]?.AsString is { } type) parts.Add(type.ToUpperInvariant());
            if (Outbound["flow"]?.AsString?.StartsWith("xtls-rprx-vision", StringComparison.Ordinal) == true) parts.Add("Vision");
            if (Outbound["obfs"] is not null) parts.Add("obfs");
            if (Outbound["server_ports"] is not null) parts.Add("hopping");
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The servers of a group, each as its own Server; empty for a single server.</summary>
    public IReadOnlyList<Server> Members
    {
        get
        {
            var list = new List<Server>();
            foreach (var member in ServerGroup.Members(Outbound) ?? [])
            {
                if (ProxyProtocols.Of(member["type"]?.AsString) is not { } proto) continue;
                if (member["server"]?.AsString is not { } host) continue;
                if (member["server_port"]?.AsInt is not { } port) continue;
                list.Add(new Server(SubscriptionId, new ParsedServer($"{host}:{port}", proto, host, port, member, null)));
            }
            return list;
        }
    }
}

public abstract record PingResult
{
    public static PingResult Ms(int value) => new PingMs(value);
    public static readonly PingResult Failed = new PingFailed();

    public int SortValue => this is PingMs m ? m.Value : int.MaxValue - 1;
}

public sealed record PingMs(int Value) : PingResult;

public sealed record PingFailed : PingResult;

/// <param name="Total">0 means unlimited.</param>
public sealed record Usage(long Upload, long Download, long Total, DateTimeOffset? Expire)
{
    public long Used => Upload + Download;

    public double? Fraction => Total > 0 ? Math.Min(1.0, (double)Used / Total) : null;
}

public sealed record Subscription
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public required string Name { get; init; }
    public required string Url { get; init; }
    public string? UserAgent { get; init; }
    public int UpdateIntervalHours { get; init; } = 24;
    public DateTimeOffset? LastUpdated { get; init; }
    public string? LastError { get; init; }
    public Usage? Usage { get; init; }
    public string? SupportUrl { get; init; }
    public string? WebPageUrl { get; init; }
    public string? Announce { get; init; }
    public int SkippedCount { get; init; }

    /// <summary>Why entries were skipped, without the entries: those hold keys.</summary>
    public IReadOnlyList<string> SkippedReasons { get; init; } = [];

    public bool IsDue(DateTimeOffset now) =>
        LastUpdated is not { } last || now - last >= TimeSpan.FromHours(Math.Max(1, UpdateIntervalHours));
}

public enum RoutingMode
{
    /// <summary>Everything through the proxy except private networks.</summary>
    Global,

    /// <summary>Russian domains and IPs go direct, the rest through the proxy.</summary>
    RussiaDirect,
}

public enum DnsKind { Udp, Tls, Https }

public sealed record DnsServer(DnsKind Kind, string Address)
{
    public static readonly DnsServer Cloudflare = new(DnsKind.Https, "1.1.1.1");
    public static readonly DnsServer Yandex = new(DnsKind.Udp, "77.88.8.8");
}

public enum TunStack { System, Gvisor, Mixed }

public enum LogLevel { Error, Warn, Info, Debug }

public enum SortMode { None, Ping, Name }

/// <summary>
/// How a ping is measured. The first three send an HTTP request through the
/// server itself, so a server that answers but does not pass traffic fails.
/// </summary>
public enum PingMode
{
    /// <summary>A GET through the server, its time divided by 3.3 so it reads like a plain ping.</summary>
    Real,
    ProxyGet,
    ProxyHead,

    /// <summary>A TCP handshake with the server; a QUIC one for Hysteria2 and TUIC.</summary>
    Tcp,

    /// <summary>An ICMP echo to the server's address.</summary>
    Icmp,
}

public enum PingDisplay
{
    /// <summary>Milliseconds.</summary>
    Time,

    /// <summary>A signal-strength scale.</summary>
    Bars,
    Both,

    /// <summary>A dot: answered or not.</summary>
    Dots,
}

/// <summary>How traffic reaches the core on the desktop.</summary>
public enum ConnectionMode
{
    /// <summary>A mixed inbound on 127.0.0.1 and the system proxy. No admin rights.</summary>
    SystemProxy,

    /// <summary>A TUN interface: all apps. Needs admin on Windows, CAP_NET_ADMIN on Linux.</summary>
    Tun,
}

public static class EnumRaw
{
    public static string Raw(this RoutingMode m) => m == RoutingMode.Global ? "global" : "russiaDirect";
    public static string Raw(this DnsKind k) => k switch { DnsKind.Udp => "udp", DnsKind.Tls => "tls", _ => "https" };
    public static string Raw(this TunStack s) => s switch { TunStack.System => "system", TunStack.Gvisor => "gvisor", _ => "mixed" };
    public static string Raw(this LogLevel l) => l switch { LogLevel.Error => "error", LogLevel.Warn => "warn", LogLevel.Info => "info", _ => "debug" };
    public static string Raw(this SortMode s) => s switch { SortMode.None => "none", SortMode.Ping => "ping", _ => "name" };
    public static string Raw(this ConnectionMode m) => m == ConnectionMode.Tun ? "tun" : "systemProxy";

    public static string Raw(this PingMode m) => m switch
    {
        PingMode.Real => "real",
        PingMode.ProxyGet => "proxyGet",
        PingMode.ProxyHead => "proxyHead",
        PingMode.Tcp => "tcp",
        _ => "icmp",
    };

    public static string Raw(this PingDisplay d) => d switch
    {
        PingDisplay.Time => "time",
        PingDisplay.Bars => "bars",
        PingDisplay.Both => "both",
        _ => "dots",
    };

    public static bool ThroughProxy(this PingMode m) => m is PingMode.Real or PingMode.ProxyGet or PingMode.ProxyHead;
}

public sealed record AppSettings
{
    public RoutingMode Routing { get; init; } = RoutingMode.RussiaDirect;
    public IReadOnlyList<string> DirectDomains { get; init; } = [];
    public IReadOnlyList<string> ProxyDomains { get; init; } = [];
    public IReadOnlyList<string> BlockDomains { get; init; } = [];
    public DnsServer RemoteDns { get; init; } = DnsServer.Cloudflare;
    public DnsServer DirectDns { get; init; } = DnsServer.Yandex;
    public bool KillSwitch { get; init; }
    public bool ExcludeLocalNetworks { get; init; } = true;
    public bool SmartSleep { get; init; } = true;
    public bool TlsFragment { get; init; }
    public bool BlockAds { get; init; }
    public bool Sniffing { get; init; } = true;
    public bool Ipv6 { get; init; }

    /// <summary>0 lets the core pick.</summary>
    public int Mtu { get; init; }

    public TunStack TunStack { get; init; } = TunStack.Mixed;
    public LogLevel LogLevel { get; init; } = LogLevel.Warn;
    public SortMode SortMode { get; init; } = SortMode.None;
    public bool PingOnOpen { get; init; }
    public PingMode PingMode { get; init; } = PingMode.Real;

    /// <summary>What the proxy pings request. Plain HTTP: no TLS handshake in the time.</summary>
    public string PingUrl { get; init; } = DefaultPingUrl;

    public PingDisplay PingDisplay { get; init; } = PingDisplay.Time;

    /// <summary>Ping a subscription's servers after it updates.</summary>
    public bool PingOnUpdate { get; init; }

    public const string DefaultPingUrl = "http://www.gstatic.com/generate_204";

    /// <summary>Desktop only: system proxy (default, no admin) or TUN.</summary>
    public ConnectionMode Mode { get; init; } = ConnectionMode.SystemProxy;

    /// <summary>Desktop only: the port of the local mixed inbound.</summary>
    public int LocalProxyPort { get; init; } = 51080;
}

/// <summary>Everything the app keeps. Immutable: each change returns a new state.</summary>
public sealed record AppState
{
    public IReadOnlyList<Subscription> Subscriptions { get; init; } = [];
    public IReadOnlyList<Server> Servers { get; init; } = [];
    public Guid? SelectedServerId { get; init; }
    public AppSettings Settings { get; init; } = new();

    public Server? SelectedServer => Servers.FirstOrDefault(s => s.Id == SelectedServerId);

    public IReadOnlyList<Server> ServersOf(Guid? subscriptionId) => Servers.Where(s => s.SubscriptionId == subscriptionId).ToList();

    /// <summary>Adds servers that are not already in the list. Returns the new state and the added servers.</summary>
    public (AppState State, IReadOnlyList<Server> Added) AddManual(IEnumerable<ParsedServer> parsed)
    {
        var existing = Servers.Where(s => s.SubscriptionId is null).Select(s => s.MatchKey).ToHashSet();
        var added = new List<Server>();
        foreach (var p in parsed)
        {
            if (!existing.Add(p.MatchKey)) continue;
            added.Add(new Server(null, p));
        }
        var next = this with
        {
            Servers = [.. Servers, .. added],
            SelectedServerId = SelectedServerId ?? added.FirstOrDefault()?.Id,
        };
        return (next, added);
    }

    /// <summary>
    /// Replaces a subscription's servers with a fresh list, keeping IDs (and so
    /// the selection and last ping) for servers that are still there.
    /// </summary>
    public AppState Apply(SubscriptionResult result, Guid subscriptionId, DateTimeOffset? date = null)
    {
        int index = Subscriptions.ToList().FindIndex(s => s.Id == subscriptionId);
        if (index < 0) return this;
        var meta = result.Meta;
        var old = Subscriptions[index];
        var sub = old with
        {
            Name = string.IsNullOrEmpty(meta.Title) ? old.Name : meta.Title,
            UpdateIntervalHours = meta.UpdateIntervalHours ?? old.UpdateIntervalHours,
            Usage = meta.Usage ?? old.Usage,
            SupportUrl = meta.SupportUrl ?? old.SupportUrl,
            WebPageUrl = meta.WebPageUrl ?? old.WebPageUrl,
            Announce = meta.Announce,
            LastUpdated = date ?? DateTimeOffset.UtcNow,
            LastError = null,
            SkippedCount = result.Skipped.Count,
            SkippedReasons = result.Skipped.Select(s => s.Reason).Distinct().Take(5).ToList(),
        };
        var subs = Subscriptions.ToList();
        subs[index] = sub;

        var oldByKey = new Dictionary<string, Server>();
        foreach (var server in Servers)
            if (server.SubscriptionId == subscriptionId) oldByKey.TryAdd(server.MatchKey, server);
        var fresh = result.Servers.Select(parsed =>
        {
            var server = new Server(subscriptionId, parsed);
            return oldByKey.Remove(parsed.MatchKey, out var previous)
                ? server with { Id = previous.Id, LastPing = previous.LastPing }
                : server;
        }).ToList();

        // Keep the subscription's block where it was in the list.
        int found = Servers.ToList().FindIndex(s => s.SubscriptionId == subscriptionId);
        int insertAt = found >= 0 ? found : Servers.Count;
        var before = Servers.Take(insertAt).Where(s => s.SubscriptionId != subscriptionId);
        var after = Servers.Skip(insertAt).Where(s => s.SubscriptionId != subscriptionId);
        var list = before.Concat(fresh).Concat(after).ToList();

        var selected = SelectedServerId;
        if (selected is not null && list.All(s => s.Id != selected))
        {
            // The selected server left the subscription: fall back to its first server.
            selected = fresh.FirstOrDefault()?.Id ?? list.FirstOrDefault()?.Id;
        }
        selected ??= fresh.FirstOrDefault()?.Id;
        return this with { Subscriptions = subs, Servers = list, SelectedServerId = selected };
    }

    public AppState RemoveSubscription(Guid id)
    {
        var list = Servers.Where(s => s.SubscriptionId != id).ToList();
        var selected = SelectedServerId is { } s0 && list.Any(s => s.Id == s0) ? s0 : list.FirstOrDefault()?.Id;
        return this with
        {
            Subscriptions = Subscriptions.Where(s => s.Id != id).ToList(),
            Servers = list,
            SelectedServerId = selected,
        };
    }

    public AppState RemoveServer(Guid id)
    {
        var list = Servers.Where(s => s.Id != id).ToList();
        var selected = SelectedServerId == id ? list.FirstOrDefault()?.Id : SelectedServerId;
        return this with { Servers = list, SelectedServerId = selected };
    }

    public AppState UpdateServer(Guid id, Func<Server, Server> change) =>
        this with { Servers = Servers.Select(s => s.Id == id ? change(s) : s).ToList() };

    public AppState UpdateSubscription(Guid id, Func<Subscription, Subscription> change) =>
        this with { Subscriptions = Subscriptions.Select(s => s.Id == id ? change(s) : s).ToList() };

    public IReadOnlyList<Server> Sorted(IEnumerable<Server> list) => Settings.SortMode switch
    {
        SortMode.Name => list.OrderBy(s => s.Name, NaturalOrder.Instance).ToList(),
        // OrderBy is stable: servers without a result keep their order at the end.
        SortMode.Ping => list.OrderBy(s => s.LastPing?.SortValue ?? int.MaxValue).ToList(),
        _ => list.ToList(),
    };
}

/// <summary>
/// Finder-style order, like localizedStandardCompare: case-insensitive, and
/// numbers compare by value, so "Server 2" comes before "Server 10".
/// </summary>
public sealed partial class NaturalOrder : IComparer<string>
{
    public static readonly NaturalOrder Instance = new();

    [GeneratedRegex("[0-9]+|[^0-9]+")]
    private static partial Regex Chunk();

    public int Compare(string? a, string? b)
    {
        var x = Chunk().Matches(a ?? "").Select(m => m.Value).ToList();
        var y = Chunk().Matches(b ?? "").Select(m => m.Value).ToList();
        for (int i = 0; i < Math.Min(x.Count, y.Count); i++)
        {
            string p = x[i], q = y[i];
            int c;
            if (char.IsAsciiDigit(p[0]) && char.IsAsciiDigit(q[0]))
            {
                var pt = p.TrimStart('0');
                var qt = q.TrimStart('0');
                c = pt.Length.CompareTo(qt.Length);
                if (c == 0) c = string.CompareOrdinal(pt, qt);
            }
            else
            {
                c = string.Compare(p, q, StringComparison.CurrentCultureIgnoreCase);
            }
            if (c != 0) return c;
        }
        return x.Count.CompareTo(y.Count);
    }
}
