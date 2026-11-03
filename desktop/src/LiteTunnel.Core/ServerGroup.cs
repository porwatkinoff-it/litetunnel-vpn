namespace LiteTunnel.Core;

/// <summary>
/// Several servers behind one entry: what an Xray config with a balancer
/// ("Авто-выбор") is. It runs as a sing-box urltest group, which picks the
/// fastest member and moves off one that stops answering.
/// </summary>
/// <remarks>
/// Stored in <c>Outbound</c> as <c>{"type": "urltest", "outbounds": [&lt;member&gt;...]}</c>
/// with the member outbounds inline; ConfigBuilder gives them tags.
/// </remarks>
public static class ServerGroup
{
    public const string Type = "urltest";

    /// <summary>
    /// sing-box tests every 3 minutes by default, one request per member.
    /// Rarer tests cost fewer wake-ups; a dead member is still left on the
    /// next test or the next failed connection.
    /// </summary>
    public const string Interval = "10m";

    public const string AutoName = "Авто-выбор";

    /// <summary>Marks the row the app adds to a subscription. The config never sees it.</summary>
    public const string AutoKey = "auto";

    /// <summary>What says two outbounds are the same server: all of it but the name.</summary>
    public static Json Key(Json outbound) => outbound.With("tag", null);

    /// <summary>
    /// Every server of a list once: the single rows first, named after their
    /// row, then group members those do not cover. Auto rows add nothing.
    /// </summary>
    public static List<Json> UniqueMembers(IReadOnlyList<Server> list)
    {
        var seen = new HashSet<Json>();
        var result = new List<Json>();
        foreach (var server in list)
            if (!server.IsGroup && seen.Add(Key(server.Outbound))) result.Add(server.Outbound.With("tag", server.Name));
        foreach (var server in list)
        {
            if (!server.IsGroup || server.IsAuto) continue;
            foreach (var member in Members(server.Outbound) ?? [])
                if (seen.Add(Key(member))) result.Add(member);
        }
        return result;
    }

    /// <summary>
    /// The auto row for a subscription's servers. Null with fewer than two
    /// servers, or when the provider's own group already covers them all.
    /// </summary>
    public static ParsedServer? Auto(IReadOnlyList<Server> list)
    {
        var all = UniqueMembers(list);
        if (all.Count < 2) return null;
        bool covered = list.Any(server =>
            !server.IsAuto
            && Members(server.Outbound) is { } members
            && members.Select(Key).ToHashSet().Count == all.Count);
        if (covered) return null;
        foreach (var member in all)
        {
            if (ProxyProtocols.Of(member["type"]?.AsString) is not { } proto) continue;
            if (member["server"]?.AsString is not { } host) continue;
            if (member["server_port"]?.AsInt is not { } port) continue;
            var outbound = Json.Object(("type", Type), (AutoKey, true), ("outbounds", Json.Array(all)));
            return new ParsedServer(AutoName, proto, host, port, outbound, null);
        }
        return null;
    }

    public static ParsedServer Make(IReadOnlyList<ParsedServer> members, string name)
    {
        var first = members[0];
        var outbound = Json.Object(("type", Type), ("outbounds", Json.Array(members.Select(m => m.Outbound))));
        return new ParsedServer(name, first.Proto, first.Host, first.Port, outbound, null);
    }

    /// <summary>The member outbounds, or null when the outbound is a single server.</summary>
    public static IReadOnlyList<Json>? Members(Json outbound) =>
        outbound["type"]?.AsString == Type ? outbound["outbounds"]?.AsArray : null;

    /// <summary>The outbounds to put in the config, the first of them tagged <paramref name="tag"/>.</summary>
    public static List<Json> Outbounds(Json outbound, string tag)
    {
        if (Members(outbound) is not { } members) return [outbound.With("tag", tag)];
        var tagged = members.Select((member, index) => member.With("tag", $"{tag}-{index + 1}")).ToList();
        var group = Json.Object(
            ("type", Type),
            ("tag", tag),
            ("outbounds", Json.Strings(tagged.Select(m => m["tag"]!.AsString!))),
            ("interval", Interval));
        return [group, .. tagged];
    }
}
