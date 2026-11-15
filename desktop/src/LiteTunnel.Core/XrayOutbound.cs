namespace LiteTunnel.Core;

/// <summary>
/// Servers only Xray-core reaches: XHTTP, mKCP, VLESS Encryption, TCP with an
/// HTTP header, finalmask. They run as LiteTunnel's own sing-box outbound,
/// <c>{"type": "xray", "outbound": &lt;Xray outbound&gt;}</c> (scripts/xray), so Xray
/// talks to the server and sing-box does everything else. Every other server
/// keeps sing-box's own outbound, the one the battery figures were measured on.
/// </summary>
public static class XrayOutbound
{
    public const string Type = "xray";

    /// <summary>
    /// The Xray protocols linked into the core, and what the list calls them.
    /// Hysteria is version 2 only, as in Xray itself.
    /// </summary>
    private static readonly Dictionary<string, ProxyProtocol> Protocols = new()
    {
        ["vless"] = ProxyProtocol.Vless,
        ["vmess"] = ProxyProtocol.Vmess,
        ["trojan"] = ProxyProtocol.Trojan,
        ["shadowsocks"] = ProxyProtocol.Shadowsocks,
        ["socks"] = ProxyProtocol.Socks,
        ["http"] = ProxyProtocol.Http,
        ["hysteria"] = ProxyProtocol.Hysteria2,
    };

    private static readonly Json Empty = Json.Object();

    /// <summary>The sing-box outbound for an Xray one. The tag is the config's own and would make the same server look like two.</summary>
    public static Json Wrap(Json xray) => Json.Object(("type", Type), ("outbound", xray.With("tag", null)));

    /// <summary>The Xray outbound inside, or null for any other outbound.</summary>
    public static Json? Inner(Json outbound) => outbound["type"]?.AsString == Type ? outbound["outbound"] : null;

    public sealed record Endpoint(ProxyProtocol Proto, string Host, int Port);

    /// <summary>The server an Xray outbound connects to: in vnext or servers, or flat in settings as newer Xray allows.</summary>
    public static Endpoint? EndpointOfXray(Json xray)
    {
        if (!Protocols.TryGetValue((xray["protocol"]?.AsString ?? "").ToLowerInvariant(), out var proto)) return null;
        var settings = xray["settings"] ?? Empty;
        var endpoint = settings["vnext"]?[0] ?? settings["servers"]?[0] ?? settings;
        if (TextUtil.NonEmpty(endpoint["address"]?.AsString) is not { } host) return null;
        if (endpoint["port"]?.AsInt is not { } port || !ShareLink.ValidPort(port)) return null;
        return new Endpoint(proto, host, port);
    }

    /// <summary>Protocol, host and port of a sing-box outbound, an Xray one included.</summary>
    public static Endpoint? EndpointOf(Json outbound)
    {
        if (Inner(outbound) is { } xray) return EndpointOfXray(xray);
        if (ProxyProtocols.Of(outbound["type"]?.AsString) is not { } proto) return null;
        if (outbound["server"]?.AsString is not { } host) return null;
        if (outbound["server_port"]?.AsInt is not { } port) return null;
        return new Endpoint(proto, host, port);
    }

    /// <summary>An outbound from an Xray config, kept as it is.</summary>
    public static ParsedServer Server(Json xray, string? name)
    {
        var proto = (xray["protocol"]?.AsString ?? "").ToLowerInvariant();
        if (!Protocols.ContainsKey(proto)) throw ShareLinkException.Unsupported($"Xray {(proto.Length == 0 ? "без protocol" : proto)}");
        if (proto == "hysteria" && (xray["settings"]?["version"]?.AsInt ?? 2) != 2)
            throw ShareLinkException.Unsupported("Hysteria 1");
        var e = EndpointOfXray(xray) ?? throw ShareLinkException.Invalid("нет адреса или порта");
        CheckSecurity(xray, e.Host);
        return new ParsedServer(name ?? $"{e.Host}:{e.Port}", e.Proto, e.Host, e.Port, Wrap(xray), null);
    }

    /// <summary>
    /// Whether an Xray config's outbound has to stay Xray even where sing-box
    /// has the protocol: finalmask changes every packet, and nothing in
    /// sing-box does the same.
    /// </summary>
    public static bool NeedsXray(Json xray)
    {
        var stream = xray["streamSettings"] ?? Empty;
        return stream["finalmask"] is not null || stream["finalMask"] is not null;
    }

    // Share links

    /// <summary>
    /// An Xray outbound from a share link that sing-box cannot run. <paramref name="user"/> is
    /// the protocol's own part: id and encryption for VLESS, a password for Trojan.
    /// </summary>
    public static Json FromLink(string proto, string host, int port, Json user,
        string network, string security, IReadOnlyDictionary<string, string> q)
    {
        var server = Json.Object(("address", host), ("port", port));
        Json settings;
        switch (proto)
        {
            case "vless" or "vmess":
                settings = Json.Object(("vnext", Json.Array(server.With("users", Json.Array(user)))));
                break;
            case "trojan":
                foreach (var (key, value) in user.AsObject ?? new Dictionary<string, Json>()) server = server.With(key, value);
                settings = Json.Object(("servers", Json.Array(server)));
                break;
            default:
                throw ShareLinkException.Unsupported(proto);
        }
        var xray = Json.Object(("protocol", proto), ("settings", settings), ("streamSettings", Stream(network, security, q, host)));
        CheckSecurity(xray, host);
        return xray;
    }

    /// <summary>
    /// Xray refuses VLESS without TLS or VLESS Encryption, and Trojan without
    /// TLS, to a server outside the local network: the id or the password would
    /// go out in the clear. It also runs Reality over RAW, XHTTP and gRPC only.
    /// Such a server is skipped with that reason, not added to fail on connect.
    /// </summary>
    public static void CheckSecurity(Json xray, string host)
    {
        var security = (xray["streamSettings"]?["security"]?.AsString ?? "").ToLowerInvariant();
        var network = (xray["streamSettings"]?["network"]?.AsString ?? "").ToLowerInvariant();
        if (security == "reality" && !RealityNetworks.Contains(network))
            throw ShareLinkException.Unsupported($"Reality с транспортом {network}");
        if ((security.Length > 0 && security != "none") || IsLocal(host)) return;
        switch ((xray["protocol"]?.AsString ?? "").ToLowerInvariant())
        {
            case "vless":
                var settings = xray["settings"] ?? Empty;
                var user = settings["vnext"]?[0]?["users"]?[0] ?? settings;
                var encryption = user["encryption"]?.AsString ?? "";
                if (encryption.Length == 0 || encryption == "none") throw ShareLinkException.Unsupported("VLESS без TLS и шифрования");
                break;
            case "trojan":
                throw ShareLinkException.Unsupported("Trojan без TLS");
        }
    }

    /// <summary>A private, loopback or link-local address, or a name Xray takes as local.</summary>
    public static bool IsLocal(string raw)
    {
        var host = raw.ToLowerInvariant().TrimEnd('.');
        if (host.Contains(':'))
            return host == "::1" || host.StartsWith("fc", StringComparison.Ordinal) || host.StartsWith("fd", StringComparison.Ordinal)
                || new[] { "fe8", "fe9", "fea", "feb" }.Any(p => host.StartsWith(p, StringComparison.Ordinal));
        if (ShareLink.IsIPAddress(host))
        {
            var b = host.Split('.').Select(int.Parse).ToArray();
            return b[0] == 10 || b[0] == 127 || (b[0] == 172 && b[1] is >= 16 and <= 31)
                || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }
        if (!host.Contains('.')) return true;
        return LocalSuffixes.Any(s => host == s || host.EndsWith("." + s, StringComparison.Ordinal));
    }

    private static readonly HashSet<string> RealityNetworks = ["", "raw", "tcp", "xhttp", "splithttp", "grpc"];

    private static readonly string[] LocalSuffixes = ["lan", "localdomain", "example", "invalid", "localhost", "test", "local", "home.arpa", "internal"];

    /// <summary>streamSettings from the share-link keys Xray's own links use.</summary>
    public static Json Stream(string network, string securityRaw, IReadOnlyDictionary<string, string> q, string address)
    {
        string? Get(string key) => TextUtil.NonEmpty(q.GetValueOrDefault(key));
        var host = Get("host");
        var path = Get("path");
        var s = Empty;
        switch (network.ToLowerInvariant())
        {
            case "" or "tcp" or "raw":
                s = s.With("network", "raw");
                if (q.GetValueOrDefault("headertype") is { } header && header.Equals("http", StringComparison.OrdinalIgnoreCase))
                {
                    var request = Json.Object(("path", Json.Strings(ShareLink.SplitList(path ?? "/"))));
                    if (host is not null) request = request.With("headers", Json.Object(("Host", Json.Strings(ShareLink.SplitList(host)))));
                    s = s.With("rawSettings", Json.Object(("header", Json.Object(("type", "http"), ("request", request)))));
                }
                break;
            case "xhttp" or "splithttp":
            {
                // extra is the rest of xhttpSettings, as JSON.
                Json x = Empty;
                if (q.GetValueOrDefault("extra") is { } extra)
                {
                    try { if (Json.Parse(extra) is JObject o) x = o; } catch (JsonException) { }
                }
                x = x.With("path", path ?? "/");
                if (host is not null) x = x.With("host", host);
                x = x.With("mode", Get("mode") ?? "auto");
                s = s.With("network", "xhttp").With("xhttpSettings", x);
                break;
            }
            case "kcp" or "mkcp":
            {
                var k = Empty;
                if (Get("headertype") is { } type && type != "none") k = k.With("header", Json.Object(("type", type)));
                if (Get("seed") is { } seed) k = k.With("seed", seed);
                s = s.With("network", "kcp").With("kcpSettings", k);
                break;
            }
            case "ws" or "websocket":
            {
                var w = Json.Object(("path", path ?? "/"));
                if (host is not null) w = w.With("host", host);
                s = s.With("network", "ws").With("wsSettings", w);
                break;
            }
            case "grpc" or "gun":
            {
                var g = Json.Object(("serviceName", Get("servicename") ?? ""));
                if (q.GetValueOrDefault("mode")?.Equals("multi", StringComparison.OrdinalIgnoreCase) == true) g = g.With("multiMode", true);
                if (Get("authority") is { } authority) g = g.With("authority", authority);
                s = s.With("network", "grpc").With("grpcSettings", g);
                break;
            }
            case "httpupgrade":
            {
                var h = Json.Object(("path", path ?? "/"));
                if (host is not null) h = h.With("host", host);
                s = s.With("network", "httpupgrade").With("httpupgradeSettings", h);
                break;
            }
            default:
                throw ShareLinkException.Unsupported($"транспорт {network}");
        }

        var security = securityRaw.ToLowerInvariant();
        var sni = Get("sni") ?? Get("peer")
            ?? (host is not null ? ShareLink.SplitList(host).FirstOrDefault() : null)
            ?? (ShareLink.IsIPAddress(address) ? null : address);
        var fingerprint = Get("fp")?.ToLowerInvariant();
        switch (security)
        {
            case "" or "none":
                break;
            case "tls" or "xtls":
            {
                // allowInsecure is gone from Xray 26; the core drops it, and a pinned
                // certificate (pcs) or a name to check it by (vcn) stands in.
                var t = Empty;
                if (sni is not null) t = t.With("serverName", sni);
                if (fingerprint is not null) t = t.With("fingerprint", fingerprint);
                if (Get("alpn") is { } alpn) t = t.With("alpn", Json.Strings(ShareLink.SplitList(alpn)));
                if (Get("pcs") is { } pcs) t = t.With("pinnedPeerCertSha256", pcs);
                if (Get("vcn") is { } vcn) t = t.With("verifyPeerCertByName", vcn);
                if (Get("ech") is { } ech) t = t.With("echConfigList", ech);
                s = s.With("security", "tls").With("tlsSettings", t);
                break;
            }
            case "reality":
            {
                var pbk = Get("pbk") ?? throw ShareLinkException.Invalid("Reality без pbk");
                var r = Json.Object(("publicKey", pbk), ("fingerprint", fingerprint ?? "chrome"));
                if (sni is not null) r = r.With("serverName", sni);
                if (Get("sid") is { } sid) r = r.With("shortId", sid);
                if (Get("spx") is { } spx) r = r.With("spiderX", spx);
                if (Get("pqv") is { } pqv) r = r.With("mldsa65Verify", pqv);
                s = s.With("security", "reality").With("realitySettings", r);
                break;
            }
            default:
                throw ShareLinkException.Unsupported($"security={security}");
        }
        return s;
    }

    // The list

    /// <summary>
    /// What a ping times for an Xray server. Hysteria and XHTTP over HTTP/3
    /// answer a QUIC handshake, unless finalmask hides it; mKCP answers nothing
    /// without the seed; everything else runs over TCP.
    /// </summary>
    public static Handshake? HandshakeOf(Json xray, string host)
    {
        var stream = xray["streamSettings"] ?? Empty;
        var tls = stream["tlsSettings"];
        var alpn = tls?["alpn"]?.AsArray?.Select(a => a.AsString).OfType<string>().ToList() ?? [];
        var quic = new Handshake.Quic(tls?["serverName"]?.AsString ?? host, alpn.Count == 0 ? ["h3"] : alpn);
        switch ((stream["network"]?.AsString ?? "raw").ToLowerInvariant())
        {
            case "kcp" or "mkcp":
                return null;
            case "hysteria":
                return NeedsXray(xray) ? null : quic;
            case "xhttp" or "splithttp":
                if (stream["security"]?.AsString != "tls" || alpn is not ["h3"]) return Handshake.Tcp.Instance;
                return NeedsXray(xray) ? null : quic;
            default:
                return Handshake.Tcp.Instance;
        }
    }

    /// <summary>Security, transport and what needs Xray, e.g. "Reality · XHTTP · Xray".</summary>
    public static string Detail(Json xray)
    {
        var stream = xray["streamSettings"] ?? Empty;
        var user = xray["settings"]?["vnext"]?[0]?["users"]?[0] ?? xray["settings"] ?? Empty;
        var parts = new List<string>();
        switch (stream["security"]?.AsString?.ToLowerInvariant())
        {
            case "tls": parts.Add("TLS"); break;
            case "reality": parts.Add("Reality"); break;
        }
        var network = (stream["network"]?.AsString ?? "raw").ToLowerInvariant();
        switch (network)
        {
            case "xhttp" or "splithttp": parts.Add("XHTTP"); break;
            case "kcp" or "mkcp": parts.Add("mKCP"); break;
            case "raw" or "tcp":
                var header = stream["rawSettings"]?["header"] ?? stream["tcpSettings"]?["header"];
                if (header?["type"]?.AsString == "http") parts.Add("HTTP");
                break;
            case "hysteria": break;
            default: parts.Add(network.ToUpperInvariant()); break;
        }
        if (user["flow"]?.AsString?.StartsWith("xtls-rprx-vision", StringComparison.Ordinal) == true) parts.Add("Vision");
        if (user["encryption"]?.AsString is { Length: > 0 } encryption && encryption != "none") parts.Add("Encryption");
        if (NeedsXray(xray)) parts.Add("finalmask");
        parts.Add("Xray");
        return string.Join(" · ", parts);
    }
}
