using System.Text;

namespace LiteTunnel.Core;

/// <summary>What pasted text, a QR code or a <c>litetunnel://</c> link turns out to be.</summary>
public abstract record ImportItem
{
    public sealed record SubscriptionLink(string Url, string? Name) : ImportItem;

    public sealed record Servers(IReadOnlyList<ParsedServer> Parsed, IReadOnlyList<SkippedEntry> Skipped) : ImportItem;

    public sealed record Nothing(string Reason) : ImportItem;
}

public static class Importer
{
    public const string Scheme = "litetunnel";

    public static ImportItem Classify(string text)
    {
        var raw = text.Trim();
        if (raw.Length == 0) return new ImportItem.Nothing("Пусто");
        if (raw.StartsWith($"{Scheme}:", StringComparison.OrdinalIgnoreCase)) return DeepLink(raw);
        if (!raw.Contains('\n') && !raw.Contains('\r') && IsWebUrl(raw)) return new ImportItem.SubscriptionLink(raw, null);
        var parsed = SubscriptionParser.ParseBody(Encoding.UTF8.GetBytes(raw));
        if (parsed.Servers.Count > 0) return new ImportItem.Servers(parsed.Servers, parsed.Skipped);
        if (parsed.Skipped.Count > 0) return new ImportItem.Nothing(parsed.Skipped[0].Reason);
        return new ImportItem.Nothing("Не похоже на ссылку сервера или подписки");
    }

    /// <summary>
    /// <c>litetunnel://add/&lt;url or share link&gt;</c>, as it is or percent-encoded, and
    /// <c>litetunnel://import?url=&lt;encoded&gt;&amp;name=&lt;name&gt;</c>.
    /// </summary>
    public static ImportItem DeepLink(string raw)
    {
        var addPrefix = $"{Scheme}://add/";
        if (raw.StartsWith(addPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var payload = raw[addPrefix.Length..];
            return Classify(payload.Contains("://", StringComparison.Ordinal) ? payload : TextUtil.PercentDecode(payload) ?? payload);
        }
        var importPrefix = $"{Scheme}://import";
        if (raw.StartsWith(importPrefix, StringComparison.OrdinalIgnoreCase)
            && (raw.Length == importPrefix.Length || "/?#".Contains(raw[importPrefix.Length])))
        {
            int qm = raw.IndexOf('?');
            var query = qm < 0 ? "" : raw[(qm + 1)..];
            int hash = query.IndexOf('#');
            if (hash >= 0) query = query[..hash];
            var fields = LinkUri.ParseQuery(query);
            if (fields.TryGetValue("url", out var target))
            {
                var item = Classify(target);
                return item is ImportItem.SubscriptionLink link ? new ImportItem.SubscriptionLink(link.Url, fields.GetValueOrDefault("name")) : item;
            }
        }
        return new ImportItem.Nothing($"Неизвестная ссылка {Scheme}://");
    }

    private static bool IsWebUrl(string s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;
}
