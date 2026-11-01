namespace LiteTunnel.Core;

public enum ShareLinkErrorKind { UnsupportedScheme, Unsupported, Invalid }

public sealed class ShareLinkException(ShareLinkErrorKind kind, string detail) : Exception(Describe(kind, detail))
{
    public ShareLinkErrorKind Kind { get; } = kind;
    public string Detail { get; } = detail;

    public static ShareLinkException UnsupportedScheme(string scheme) => new(ShareLinkErrorKind.UnsupportedScheme, scheme);
    public static ShareLinkException Unsupported(string what) => new(ShareLinkErrorKind.Unsupported, what);
    public static ShareLinkException Invalid(string why) => new(ShareLinkErrorKind.Invalid, why);

    private static string Describe(ShareLinkErrorKind kind, string detail) => kind switch
    {
        ShareLinkErrorKind.UnsupportedScheme => $"Протокол {detail}:// не поддерживается",
        ShareLinkErrorKind.Unsupported => $"Не поддерживается: {detail}",
        _ => $"Ссылка повреждена: {detail}",
    };
}

/// <summary>
/// Turns proxy share links (vless://, hysteria2://, trojan://, ss://, vmess://,
/// tuic://, socks://) into sing-box outbounds.
/// </summary>
public static class ShareLink
{
    private static readonly Dictionary<string, Func<string, ParsedServer>> Parsers = new()
    {
        ["vless"] = ParseVless,
        ["trojan"] = ParseTrojan,
        ["hysteria2"] = ParseHysteria2,
        ["hy2"] = ParseHysteria2,
        ["ss"] = ParseShadowsocks,
        ["vmess"] = ParseVmess,
        ["tuic"] = ParseTuic,
        ["socks"] = ParseSocks,
        ["socks5"] = ParseSocks,
    };

    /// <summary>Schemes we recognise as proxy links, including ones we can only report as unsupported.</summary>
    private static readonly HashSet<string> KnownSchemes =
        [.. Parsers.Keys, "wireguard", "wg", "hysteria", "ssr", "anytls", "naive+https", "juicity"];

    public static string? SchemeOf(string line)
    {
        int at = line.IndexOf("://", StringComparison.Ordinal);
        if (at < 0) return null;
        var scheme = line[..at].ToLowerInvariant();
        return KnownSchemes.Contains(scheme) ? scheme : null;
    }

    public static ParsedServer Parse(string raw)
    {
        var line = raw.Trim();
        var scheme = SchemeOf(line);
        if (scheme is null)
        {
            int at = line.IndexOf("://", StringComparison.Ordinal);
            throw ShareLinkException.UnsupportedScheme(at < 0 ? line : line[..at]);
        }
        if (!Parsers.TryGetValue(scheme, out var parser)) throw ShareLinkException.UnsupportedScheme(scheme);
        return parser(line);
    }

    // VLESS

    private static ParsedServer ParseVless(string line)
    {
        var uri = new LinkUri(line);
        var uuid = uri.User;
        if (string.IsNullOrEmpty(uuid)) throw ShareLinkException.Invalid("нет UUID");
        int port = uri.SinglePort();
        if (uri.Query.GetValueOrDefault("encryption") is { Length: > 0 } encryption && encryption != "none")
            throw ShareLinkException.Unsupported("VLESS Encryption");
        var outbound = Json.Object(
            ("type", "vless"),
            ("server", uri.Host),
            ("server_port", port),
            ("uuid", uuid),
            ("packet_encoding", "xudp"));
        if (uri.Query.GetValueOrDefault("flow") is { } flow && flow.StartsWith("xtls-rprx-vision", StringComparison.Ordinal))
            outbound = outbound.With("flow", "xtls-rprx-vision");
        outbound = outbound.With("tls", Tls(uri.Query.GetValueOrDefault("security") ?? "none", uri.Query, uri.Host));
        outbound = outbound.With("transport", Transport(uri.Query.GetValueOrDefault("type") ?? "tcp", uri.Query));
        return new ParsedServer(uri.Name, ProxyProtocol.Vless, uri.Host, port, outbound, line);
    }

    // Trojan

    private static ParsedServer ParseTrojan(string line)
    {
        var uri = new LinkUri(line);
        var password = uri.Userinfo;
        if (string.IsNullOrEmpty(password)) throw ShareLinkException.Invalid("нет пароля");
        int port = uri.SinglePort();
        var outbound = Json.Object(
            ("type", "trojan"),
            ("server", uri.Host),
            ("server_port", port),
            ("password", password));
        outbound = outbound.With("tls", Tls(uri.Query.GetValueOrDefault("security") ?? "tls", uri.Query, uri.Host));
        outbound = outbound.With("transport", Transport(uri.Query.GetValueOrDefault("type") ?? "tcp", uri.Query));
        return new ParsedServer(uri.Name, ProxyProtocol.Trojan, uri.Host, port, outbound, line);
    }

    // Hysteria2

    private static ParsedServer ParseHysteria2(string line)
    {
        var uri = new LinkUri(line);
        var q = uri.Query;
        int? single = null;
        var ranges = new List<string>();
        var portParts = uri.PortSpec.Split(',', StringSplitOptions.RemoveEmptyEntries)
            .Concat((q.GetValueOrDefault("mport") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries));
        foreach (var part in portParts)
        {
            var bounds = SwiftSplit(part, '-', 1).Select(b => TextUtil.ParseInt(b.Trim())).ToList();
            if (bounds.Count == 2 && bounds[0] is { } lo && bounds[1] is { } hi && ValidPort(lo) && ValidPort(hi) && lo <= hi)
                ranges.Add($"{lo}:{hi}");
            else if (bounds.Count == 1 && bounds[0] is { } p && ValidPort(p))
            {
                if (single is null) single = p; else ranges.Add($"{p}:{p}");
            }
            else throw ShareLinkException.Invalid($"порт {part}");
        }
        if (single is null && ranges.Count == 0) single = 443;
        int port = single ?? int.Parse(ranges[0].Split(':')[0]);

        var outbound = Json.Object(
            ("type", "hysteria2"),
            ("server", uri.Host),
            ("server_port", port),
            ("password", uri.Userinfo ?? ""));
        if (ranges.Count > 0)
        {
            // server_port is ignored once server_ports is set, so the main port goes in the list too.
            if (single is { } s) ranges.Insert(0, $"{s}:{s}");
            outbound = outbound.With("server_ports", Json.Strings(ranges));
            var interval = q.GetValueOrDefault("mporthopint") ?? q.GetValueOrDefault("hopinterval") ?? q.GetValueOrDefault("hop_interval");
            if (!string.IsNullOrEmpty(interval))
                outbound = outbound.With("hop_interval", TextUtil.ParseInt(interval) is not null ? $"{interval}s" : interval);
        }
        if (q.GetValueOrDefault("obfs") is { Length: > 0 } obfs && obfs != "none")
        {
            if (obfs != "salamander") throw ShareLinkException.Unsupported($"obfs {obfs}");
            outbound = outbound.With("obfs", Json.Object(("type", "salamander"), ("password", q.GetValueOrDefault("obfs-password") ?? "")));
        }
        if (TextUtil.ParseInt(q.GetValueOrDefault("upmbps")) is { } up) outbound = outbound.With("up_mbps", up);
        if (TextUtil.ParseInt(q.GetValueOrDefault("downmbps")) is { } down) outbound = outbound.With("down_mbps", down);

        var tls = Json.Object(("enabled", true));
        if ((TextUtil.NonEmpty(q.GetValueOrDefault("sni")) ?? (IsIPAddress(uri.Host) ? null : uri.Host)) is { } sni)
            tls = tls.With("server_name", sni);
        if (IsTrue(q.GetValueOrDefault("insecure")) || IsTrue(q.GetValueOrDefault("allowinsecure"))) tls = tls.With("insecure", true);
        if (TextUtil.NonEmpty(q.GetValueOrDefault("alpn")) is { } alpn) tls = tls.With("alpn", Json.Strings(SplitList(alpn)));
        outbound = outbound.With("tls", tls);
        return new ParsedServer(uri.Name, ProxyProtocol.Hysteria2, uri.Host, port, outbound, line);
    }

    // TUIC

    private static ParsedServer ParseTuic(string line)
    {
        var uri = new LinkUri(line);
        var uuid = uri.User;
        if (string.IsNullOrEmpty(uuid)) throw ShareLinkException.Invalid("нет UUID");
        int port = uri.SinglePort();
        var q = uri.Query;
        var outbound = Json.Object(
            ("type", "tuic"),
            ("server", uri.Host),
            ("server_port", port),
            ("uuid", uuid),
            ("password", uri.Password ?? ""));
        if (TextUtil.NonEmpty(q.GetValueOrDefault("congestion_control")) is { } cc) outbound = outbound.With("congestion_control", cc);
        if (TextUtil.NonEmpty(q.GetValueOrDefault("udp_relay_mode")) is { } mode) outbound = outbound.With("udp_relay_mode", mode);
        var tls = Json.Object(("enabled", true));
        if ((TextUtil.NonEmpty(q.GetValueOrDefault("sni")) ?? (IsIPAddress(uri.Host) ? null : uri.Host)) is { } sni)
            tls = tls.With("server_name", sni);
        if (IsTrue(q.GetValueOrDefault("allow_insecure")) || IsTrue(q.GetValueOrDefault("insecure")) || IsTrue(q.GetValueOrDefault("allowinsecure")))
            tls = tls.With("insecure", true);
        tls = tls.With("alpn", Json.Strings(SplitList(TextUtil.NonEmpty(q.GetValueOrDefault("alpn")) ?? "h3")));
        outbound = outbound.With("tls", tls);
        return new ParsedServer(uri.Name, ProxyProtocol.Tuic, uri.Host, port, outbound, line);
    }

    // SOCKS

    private static ParsedServer ParseSocks(string line)
    {
        var uri = new LinkUri(line);
        int port = uri.SinglePort();
        var outbound = Json.Object(
            ("type", "socks"),
            ("server", uri.Host),
            ("server_port", port),
            ("version", "5"));
        if (uri.Userinfo is { Length: > 0 } userinfo)
        {
            // Either user:pass, or base64(user:pass) as v2rayN writes it.
            var plain = userinfo.Contains(':') ? userinfo : (Base64Text.DecodeString(userinfo) ?? userinfo);
            var parts = SwiftSplit(plain, ':', 1);
            if (parts.Count > 0) outbound = outbound.With("username", parts[0]);
            if (parts.Count > 1) outbound = outbound.With("password", parts[1]);
        }
        return new ParsedServer(uri.Name, ProxyProtocol.Socks, uri.Host, port, outbound, line);
    }

    // Shadowsocks

    private static ParsedServer ParseShadowsocks(string line)
    {
        var body = line["ss://".Length..];
        string? name = null;
        int hash = body.IndexOf('#');
        if (hash >= 0)
        {
            name = DecodeName(body[(hash + 1)..]);
            body = body[..hash];
        }
        var query = new Dictionary<string, string>();
        int qm = body.IndexOf('?');
        if (qm >= 0)
        {
            query = LinkUri.ParseQuery(body[(qm + 1)..]);
            body = body[..qm];
        }
        body = body.TrimEnd('/');

        string methodPassword;
        string hostPort;
        int at = body.LastIndexOf('@');
        if (at >= 0)
        {
            // SIP002: userinfo is base64url(method:password), or percent-encoded for 2022 ciphers.
            var rawUser = body[..at];
            hostPort = body[(at + 1)..];
            var decoded = TextUtil.PercentDecode(rawUser) ?? rawUser;
            if (decoded.Contains(':')) methodPassword = decoded;
            else methodPassword = Base64Text.DecodeString(rawUser) ?? throw ShareLinkException.Invalid("userinfo");
        }
        else
        {
            // Legacy: base64(method:password@host:port)
            var decoded = Base64Text.DecodeString(body);
            int dat = decoded?.LastIndexOf('@') ?? -1;
            if (decoded is null || dat < 0) throw ShareLinkException.Invalid("base64");
            methodPassword = decoded[..dat];
            hostPort = decoded[(dat + 1)..];
        }
        var mp = SwiftSplit(methodPassword, ':', 1);
        if (mp.Count != 2 || mp[0].Length == 0) throw ShareLinkException.Invalid("метод и пароль");
        var (host, portSpec) = LinkUri.SplitHostPort(hostPort);
        if (TextUtil.ParseInt(portSpec) is not { } port || !ValidPort(port)) throw ShareLinkException.Invalid("порт");

        var outbound = Json.Object(
            ("type", "shadowsocks"),
            ("server", host),
            ("server_port", port),
            ("method", mp[0].ToLowerInvariant()),
            ("password", mp[1]));
        if (TextUtil.NonEmpty(query.GetValueOrDefault("plugin")) is { } plugin)
        {
            var parts = SwiftSplit(plugin, ';', 1);
            var kind = parts.Count > 0 ? parts[0] : plugin;
            outbound = kind switch
            {
                "obfs-local" or "simple-obfs" => outbound.With("plugin", "obfs-local"),
                "v2ray-plugin" => outbound.With("plugin", "v2ray-plugin"),
                _ => throw ShareLinkException.Unsupported($"плагин {kind}"),
            };
            if (parts.Count > 1) outbound = outbound.With("plugin_opts", parts[1]);
        }
        return new ParsedServer(name ?? $"{host}:{port}", ProxyProtocol.Shadowsocks, host, port, outbound, line);
    }

    // VMess

    private static ParsedServer ParseVmess(string line)
    {
        var body = line["vmess://".Length..];
        int cut = body.IndexOfAny(['?', '#']);
        if (cut >= 0) body = body[..cut];
        Json? json = null;
        if (Base64Text.DecodeString(body) is { } text)
        {
            try { json = Json.Parse(text); } catch (JsonException) { }
        }
        if (json?.AsObject is null) throw ShareLinkException.Invalid("vmess base64");
        Json root = json;

        string? Field(string key) => root[key] switch
        {
            JString s => s.Value.Length == 0 ? null : s.Value,
            JInt i => i.Value.ToString(System.Globalization.CultureInfo.InvariantCulture),
            JDouble d => ((long)d.Value).ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => null,
        };

        var host = Field("add") ?? throw ShareLinkException.Invalid("нет адреса");
        if (TextUtil.ParseInt(Field("port")) is not { } port || !ValidPort(port)) throw ShareLinkException.Invalid("порт");
        var uuid = Field("id") ?? throw ShareLinkException.Invalid("нет UUID");

        var outbound = Json.Object(
            ("type", "vmess"),
            ("server", host),
            ("server_port", port),
            ("uuid", uuid),
            ("security", Field("scy") ?? "auto"),
            ("alter_id", TextUtil.ParseInt(Field("aid")) ?? 0),
            ("packet_encoding", "xudp"));
        var query = new Dictionary<string, string>();
        void Put(string key, string? value) { if (value is not null) query[key] = value; }
        Put("host", Field("host"));
        Put("path", Field("path"));
        Put("servicename", Field("path"));
        Put("headertype", Field("type"));
        Put("sni", Field("sni"));
        Put("alpn", Field("alpn"));
        Put("fp", Field("fp"));
        Put("allowinsecure", Field("allowInsecure") ?? Field("insecure"));
        outbound = outbound.With("tls", Tls(Field("tls") ?? "none", query, host));
        outbound = outbound.With("transport", Transport(Field("net") ?? "tcp", query));
        return new ParsedServer(Field("ps") ?? $"{host}:{port}", ProxyProtocol.Vmess, host, port, outbound, line);
    }

    // Shared pieces

    private static readonly HashSet<string> UtlsFingerprints =
        ["chrome", "firefox", "edge", "safari", "360", "qq", "ios", "android", "random", "randomized"];

    internal static Json? Tls(string security, IReadOnlyDictionary<string, string> q, string host)
    {
        security = security.ToLowerInvariant();
        switch (security)
        {
            case "" or "none": return null;
            case "tls" or "xtls" or "reality": break;
            default: throw ShareLinkException.Unsupported($"security={security}");
        }
        var tls = Json.Object(("enabled", true));
        var sni = TextUtil.NonEmpty(q.GetValueOrDefault("sni"))
            ?? TextUtil.NonEmpty(q.GetValueOrDefault("peer"))
            ?? (TextUtil.NonEmpty(q.GetValueOrDefault("host")) is { } h ? SplitList(h).FirstOrDefault() : null)
            ?? (IsIPAddress(host) ? null : host);
        if (sni is not null) tls = tls.With("server_name", sni);
        if (IsTrue(q.GetValueOrDefault("allowinsecure")) || IsTrue(q.GetValueOrDefault("insecure"))) tls = tls.With("insecure", true);
        if (TextUtil.NonEmpty(q.GetValueOrDefault("alpn")) is { } alpn) tls = tls.With("alpn", Json.Strings(SplitList(alpn)));
        var fingerprint = TextUtil.NonEmpty(q.GetValueOrDefault("fp"))?.ToLowerInvariant();
        if (fingerprint is not null || security == "reality")
        {
            var fp = fingerprint ?? "chrome";
            if (fp == "randomizednoalpn") fp = "randomized";
            if (!UtlsFingerprints.Contains(fp)) fp = "chrome";
            tls = tls.With("utls", Json.Object(("enabled", true), ("fingerprint", fp)));
        }
        if (security == "reality")
        {
            var pbk = TextUtil.NonEmpty(q.GetValueOrDefault("pbk")) ?? throw ShareLinkException.Invalid("Reality без pbk");
            tls = tls.With("reality", Json.Object(("enabled", true), ("public_key", pbk), ("short_id", q.GetValueOrDefault("sid") ?? "")));
        }
        if (q.ContainsKey("fragment")) tls = tls.With("fragment", true);
        return tls;
    }

    internal static Json? Transport(string type, IReadOnlyDictionary<string, string> q)
    {
        var host = TextUtil.NonEmpty(q.GetValueOrDefault("host"));
        switch (type.ToLowerInvariant())
        {
            case "" or "tcp" or "raw":
                if (q.GetValueOrDefault("headertype") is { } header && header.Equals("http", StringComparison.OrdinalIgnoreCase))
                    throw ShareLinkException.Unsupported("TCP с HTTP-маскировкой");
                return null;
            case "ws" or "websocket":
            {
                var t = Json.Object(("type", "ws"));
                var path = TextUtil.NonEmpty(q.GetValueOrDefault("path")) ?? "/";
                int ed = path.IndexOf("?ed=", StringComparison.Ordinal);
                if (ed >= 0)
                {
                    var digits = new string(path[(ed + 4)..].TakeWhile(char.IsAsciiDigit).ToArray());
                    if (TextUtil.ParseInt(digits) is { } n)
                    {
                        t = t.With("max_early_data", n);
                        t = t.With("early_data_header_name", "Sec-WebSocket-Protocol");
                    }
                    path = path[..ed];
                }
                t = t.With("path", path);
                if (host is not null) t = t.With("headers", Json.Object(("Host", host)));
                return t;
            }
            case "grpc" or "gun":
                return Json.Object(("type", "grpc"), ("service_name", TextUtil.NonEmpty(q.GetValueOrDefault("servicename")) ?? ""));
            case "httpupgrade":
            {
                var t = Json.Object(("type", "httpupgrade"), ("path", TextUtil.NonEmpty(q.GetValueOrDefault("path")) ?? "/"));
                if (host is not null) t = t.With("host", host);
                return t;
            }
            case "http" or "h2":
            {
                var t = Json.Object(("type", "http"), ("path", TextUtil.NonEmpty(q.GetValueOrDefault("path")) ?? "/"));
                if (host is not null) t = t.With("host", Json.Strings(SplitList(host)));
                return t;
            }
            case "xhttp" or "splithttp":
                throw ShareLinkException.Unsupported("XHTTP (нет в ядре sing-box)");
            case "kcp" or "mkcp":
                throw ShareLinkException.Unsupported("mKCP");
            default:
                throw ShareLinkException.Unsupported($"транспорт {type}");
        }
    }

    internal static bool ValidPort(int port) => port is >= 1 and <= 65535;

    internal static bool IsTrue(string? value) =>
        value is not null && (value == "1" || value.Equals("true", StringComparison.OrdinalIgnoreCase));

    internal static List<string> SplitList(string value) =>
        value.Split(',').Select(s => s.Trim()).Where(s => s.Length > 0).ToList();

    internal static string? DecodeName(string raw)
    {
        var name = (TextUtil.PercentDecode(raw) ?? raw).Trim();
        return name.Length == 0 ? null : name;
    }

    public static bool IsIPAddress(string host)
    {
        if (host.Contains(':')) return true;
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => byte.TryParse(p, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _));
    }

    /// <summary>
    /// Swift's <c>split(separator:maxSplits:)</c>: empty pieces are dropped and do not
    /// count towards <paramref name="maxSplits"/>.
    /// </summary>
    internal static List<string> SwiftSplit(string s, char separator, int maxSplits)
    {
        var result = new List<string>();
        int start = 0, i = 0;
        while (i < s.Length)
        {
            if (s[i] == separator)
            {
                bool appended = i > start;
                if (appended) result.Add(s[start..i]);
                i++;
                start = i;
                if (appended && result.Count == maxSplits) break;
                continue;
            }
            i++;
        }
        if (start < s.Length) result.Add(s[start..]);
        return result;
    }
}

/// <summary>
/// <c>scheme://userinfo@host:port/path?query#name</c>, parsed by hand: share links
/// carry port lists and raw passwords that <see cref="Uri"/> rejects.
/// </summary>
internal sealed class LinkUri
{
    public string Scheme { get; }
    public string? Userinfo { get; }
    public string Host { get; }
    public string PortSpec { get; }
    public Dictionary<string, string> Query { get; }
    public string? Fragment { get; }

    public LinkUri(string line)
    {
        int schemeEnd = line.IndexOf("://", StringComparison.Ordinal);
        if (schemeEnd < 0) throw ShareLinkException.Invalid("нет схемы");
        Scheme = line[..schemeEnd].ToLowerInvariant();
        var rest = line[(schemeEnd + 3)..];
        int hash = rest.IndexOf('#');
        if (hash >= 0)
        {
            Fragment = ShareLink.DecodeName(rest[(hash + 1)..]);
            rest = rest[..hash];
        }
        Query = new Dictionary<string, string>();
        int qm = rest.IndexOf('?');
        if (qm >= 0)
        {
            Query = ParseQuery(rest[(qm + 1)..]);
            rest = rest[..qm];
        }
        var authority = rest;
        int at = authority.LastIndexOf('@');
        int hostStart = at >= 0 ? at + 1 : 0;
        int slash = authority.IndexOf('/', hostStart);
        if (slash >= 0) authority = authority[..slash];
        if (at >= 0)
        {
            var raw = authority[..at];
            Userinfo = TextUtil.PercentDecode(raw) ?? raw;
            authority = authority[(at + 1)..];
        }
        (Host, PortSpec) = SplitHostPort(authority);
        if (Host.Length == 0) throw ShareLinkException.Invalid("нет адреса");
    }

    public string Name => Fragment ?? $"{Host}:{PortSpec}";

    /// <summary>The part of userinfo before the first <c>:</c>.</summary>
    public string? User => Userinfo is null ? null : TextUtil.SplitOnce(Userinfo, ':').Head;

    public string? Password => Userinfo is null ? null : TextUtil.SplitOnce(Userinfo, ':').Tail;

    public int SinglePort()
    {
        if (TextUtil.ParseInt(PortSpec) is { } port && ShareLink.ValidPort(port)) return port;
        throw ShareLinkException.Invalid(PortSpec.Length == 0 ? "нет порта" : $"порт {PortSpec}");
    }

    public static (string Host, string Port) SplitHostPort(string s)
    {
        if (s.StartsWith('['))
        {
            int close = s.IndexOf(']');
            if (close < 0) throw ShareLinkException.Invalid("IPv6-адрес");
            var host = s[1..close];
            var after = s[(close + 1)..];
            return (host, after.StartsWith(':') ? after[1..] : "");
        }
        int colon = s.IndexOf(':');
        return colon >= 0 ? (s[..colon], s[(colon + 1)..]) : (s, "");
    }

    /// <summary>
    /// Keys are lowercased; values are percent-decoded. <c>+</c> stays <c>+</c>:
    /// passwords and keys in these links use it literally.
    /// </summary>
    public static Dictionary<string, string> ParseQuery(string s)
    {
        var result = new Dictionary<string, string>();
        foreach (var pair in s.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var (rawKey, rawValue) = TextUtil.SplitOnce(pair, '=');
            var key = (TextUtil.PercentDecode(rawKey) ?? rawKey).ToLowerInvariant();
            var raw = rawValue ?? "";
            result.TryAdd(key, TextUtil.PercentDecode(raw) ?? raw);
        }
        return result;
    }
}
