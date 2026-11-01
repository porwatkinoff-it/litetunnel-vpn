namespace LiteTunnel.Core;

/// <summary>
/// Xray JSON, as panels serve it to Happ and v2RayTun: an array of full Xray
/// configs, one per server, or a single config. Each proxy outbound becomes a
/// sing-box one through the same TLS and transport code as share links. A
/// config with a balancer is one entry: a ServerGroup of the balanced outbounds.
/// </summary>
internal static class XrayConfig
{
    /// <summary>Routing targets inside the config, not servers.</summary>
    private static readonly HashSet<string> Ignored = ["freedom", "blackhole", "dns", "loopback"];

    private static readonly Json Empty = Json.Object();

    public static void Parse(Json config, SubscriptionResult result)
    {
        var remarks = TextUtil.NonEmpty(config["remarks"]?.AsString);
        var proxies = (config["outbounds"]?.AsArray ?? [])
            .Where(o => !Ignored.Contains((o["protocol"]?.AsString ?? "freedom").ToLowerInvariant()))
            .ToList();
        var balanced = BalancedOutbounds(config, proxies);
        if (balanced.Count > 1)
        {
            ParseGroup(balanced, remarks ?? "Авто-выбор", result);
            return;
        }
        foreach (var outbound in proxies)
        {
            var tag = TextUtil.NonEmpty(outbound["tag"]?.AsString);
            var name = remarks ?? tag;
            if (proxies.Count > 1 && remarks is not null && tag is not null) name = $"{remarks} · {tag}";
            try
            {
                result.Servers.Add(Server(outbound, name));
            }
            catch (ShareLinkException e)
            {
                result.Skipped.Add(new SkippedEntry(name ?? "Xray", e.Message));
            }
        }
    }

    /// <summary>
    /// Proxy outbounds a routing balancer picks from: its <c>selector</c> lists tag
    /// prefixes, and as in Xray an empty one matches every tag. Empty when the
    /// config has no balancer.
    /// </summary>
    private static List<Json> BalancedOutbounds(Json config, List<Json> proxies)
    {
        var prefixes = (config["routing"]?["balancers"]?.AsArray ?? [])
            .SelectMany(b => b["selector"]?.AsArray ?? [])
            .Select(s => s.AsString)
            .OfType<string>()
            .ToList();
        if (prefixes.Count == 0) return [];
        return proxies.Where(o =>
        {
            var tag = o["tag"]?.AsString ?? "";
            return prefixes.Any(p => tag.StartsWith(p, StringComparison.Ordinal));
        }).ToList();
    }

    /// <summary>
    /// Members the core cannot run are left out and listed as skipped; the
    /// group stays as long as one of them is left.
    /// </summary>
    private static void ParseGroup(List<Json> outbounds, string name, SubscriptionResult result)
    {
        var members = new List<ParsedServer>();
        foreach (var outbound in outbounds)
        {
            var line = $"{name} · {outbound["tag"]?.AsString ?? "?"}";
            try
            {
                members.Add(Server(outbound, line));
            }
            catch (ShareLinkException e)
            {
                result.Skipped.Add(new SkippedEntry(line, e.Message));
            }
        }
        if (members.Count == 1) result.Servers.Add(members[0] with { Name = name });
        else if (members.Count > 1) result.Servers.Add(ServerGroup.Make(members, name));
    }

    public static ParsedServer Server(Json outbound, string? name)
    {
        var proto = (outbound["protocol"]?.AsString ?? "").ToLowerInvariant();
        var settings = outbound["settings"] ?? Empty;
        // Nested in vnext/servers, or flat in settings as newer Xray allows.
        var endpoint = settings["vnext"]?[0] ?? settings["servers"]?[0] ?? settings;
        var user = endpoint["users"]?[0] ?? endpoint;
        var host = TextUtil.NonEmpty(endpoint["address"]?.AsString) ?? throw ShareLinkException.Invalid("нет адреса");
        var port = endpoint["port"]?.AsInt is { } p && ShareLink.ValidPort(p) ? p : throw ShareLinkException.Invalid("порт");
        var stream = outbound["streamSettings"] ?? Empty;
        var security = stream["security"]?.AsString ?? "none";
        var network = stream["network"]?.AsString ?? "tcp";
        var q = Query(stream, security, network);
        var label = name ?? $"{host}:{port}";

        var o = Json.Object(("server", host), ("server_port", port));
        ProxyProtocol kind;
        switch (proto)
        {
            case "vless":
            {
                var id = TextUtil.NonEmpty(user["id"]?.AsString) ?? throw ShareLinkException.Invalid("нет UUID");
                var encryption = TextUtil.NonEmpty(user["encryption"]?.AsString);
                if (encryption is not null && encryption != "none") throw ShareLinkException.Unsupported("VLESS Encryption");
                kind = ProxyProtocol.Vless;
                o = o.With("type", "vless").With("uuid", id).With("packet_encoding", "xudp");
                if (user["flow"]?.AsString?.StartsWith("xtls-rprx-vision", StringComparison.Ordinal) == true)
                    o = o.With("flow", "xtls-rprx-vision");
                break;
            }
            case "vmess":
            {
                var id = TextUtil.NonEmpty(user["id"]?.AsString) ?? throw ShareLinkException.Invalid("нет UUID");
                kind = ProxyProtocol.Vmess;
                o = o.With("type", "vmess")
                    .With("uuid", id)
                    .With("security", TextUtil.NonEmpty(user["security"]?.AsString) ?? "auto")
                    .With("alter_id", user["alterId"]?.AsInt ?? 0)
                    .With("packet_encoding", "xudp");
                break;
            }
            case "trojan":
            {
                var password = TextUtil.NonEmpty(user["password"]?.AsString) ?? throw ShareLinkException.Invalid("нет пароля");
                kind = ProxyProtocol.Trojan;
                o = o.With("type", "trojan").With("password", password);
                break;
            }
            case "shadowsocks":
            {
                var method = TextUtil.NonEmpty(user["method"]?.AsString) ?? throw ShareLinkException.Invalid("нет метода");
                o = o.With("type", "shadowsocks")
                    .With("method", method.ToLowerInvariant())
                    .With("password", user["password"]?.AsString ?? "");
                return new ParsedServer(label, ProxyProtocol.Shadowsocks, host, port, o, null);
            }
            case "socks":
            {
                o = o.With("type", "socks").With("version", "5");
                if (TextUtil.NonEmpty(user["user"]?.AsString) is { } username) o = o.With("username", username);
                if (TextUtil.NonEmpty(user["pass"]?.AsString) is { } pass) o = o.With("password", pass);
                return new ParsedServer(label, ProxyProtocol.Socks, host, port, o, null);
            }
            case "hysteria":
            {
                if ((settings["version"]?.AsInt ?? 2) != 2) throw ShareLinkException.Unsupported("Hysteria 1");
                // Salamander and the like live in finalmask, whose shape is not documented yet.
                if (stream["finalmask"] is not null || stream["finalMask"] is not null)
                    throw ShareLinkException.Unsupported("Hysteria с finalmask");
                o = o.With("type", "hysteria2")
                    .With("password", stream["hysteriaSettings"]?["auth"]?.AsString ?? "");
                // QUIC: no uTLS fingerprint and no Reality.
                o = o.With("tls", ShareLink.Tls("tls", q, host)?.With("utls", null));
                return new ParsedServer(label, ProxyProtocol.Hysteria2, host, port, o, null);
            }
            default:
                throw ShareLinkException.Unsupported($"Xray {(proto.Length == 0 ? "без protocol" : proto)}");
        }
        o = o.With("tls", ShareLink.Tls(security, q, host));
        o = o.With("transport", ShareLink.Transport(network, q));
        return new ParsedServer(label, kind, host, port, o, null);
    }

    /// <summary>streamSettings as the share-link query keys ShareLink.Tls and .Transport read.</summary>
    private static Dictionary<string, string> Query(Json stream, string security, string network)
    {
        var q = new Dictionary<string, string>();
        void Put(string key, string? value)
        {
            if (value is not null) q[key] = value;
        }
        var tls = (security.ToLowerInvariant() == "reality" ? stream["realitySettings"] : stream["tlsSettings"]) ?? Empty;
        Put("sni", tls["serverName"]?.AsString);
        Put("fp", tls["fingerprint"]?.AsString);
        Put("alpn", List(tls["alpn"]));
        if (tls["allowInsecure"]?.AsBool == true) q["allowinsecure"] = "1";
        Put("pbk", tls["publicKey"]?.AsString ?? tls["password"]?.AsString);
        Put("sid", tls["shortId"]?.AsString);

        var transport = network.ToLowerInvariant() switch
        {
            "tcp" or "raw" => stream["rawSettings"] ?? stream["tcpSettings"],
            "ws" or "websocket" => stream["wsSettings"],
            "grpc" or "gun" => stream["grpcSettings"],
            "httpupgrade" => stream["httpupgradeSettings"],
            "h2" or "http" => stream["httpSettings"],
            _ => null,
        };
        if (transport is null) return q;
        Put("path", transport["path"]?.AsString);
        Put("host", List(transport["host"]) ?? transport["headers"]?["Host"]?.AsString);
        Put("servicename", transport["serviceName"]?.AsString);
        Put("headertype", transport["header"]?["type"]?.AsString);
        return q;
    }

    /// <summary>A string, or an array of strings joined by commas as share links write them.</summary>
    private static string? List(Json? value)
    {
        if (value?.AsString is { } s) return s;
        return value?.AsArray is { } items ? string.Join(",", items.Select(i => i.AsString).OfType<string>()) : null;
    }
}
