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
