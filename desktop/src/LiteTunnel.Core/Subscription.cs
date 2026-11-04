using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;

namespace LiteTunnel.Core;

public sealed record SubscriptionMeta(
    string? Title = null,
    int? UpdateIntervalHours = null,
    Usage? Usage = null,
    string? SupportUrl = null,
    string? WebPageUrl = null,
    string? Announce = null);

public sealed record SkippedEntry(string Line, string Reason);

public sealed class SubscriptionResult
{
    public List<ParsedServer> Servers { get; } = [];
    public List<SkippedEntry> Skipped { get; } = [];
    public SubscriptionMeta Meta { get; set; } = new();

    /// <summary><c>happ://routing/...</c> lines found in the body; read later by the routing importer.</summary>
    public List<string> RoutingLines { get; } = [];
}

public enum SubscriptionErrorKind { Http, Empty, NoServers, NothingSupported, DeviceLimit, DeviceIdRefused }

public sealed class SubscriptionException : Exception
{
    public SubscriptionErrorKind Kind { get; }

    /// <summary>The HTTP status for <see cref="SubscriptionErrorKind.Http"/>.</summary>
    public int Code { get; }

    /// <summary>Why the first entry was skipped, for <see cref="SubscriptionErrorKind.NothingSupported"/>; what came, for <see cref="SubscriptionErrorKind.NoServers"/>.</summary>
    public string? Reason { get; }

    private SubscriptionException(SubscriptionErrorKind kind, string message, int code = 0, string? reason = null) : base(message)
    {
        Kind = kind;
        Code = code;
        Reason = reason;
    }

    public static SubscriptionException Http(int code) => new(SubscriptionErrorKind.Http, $"Сервер подписки ответил {code}", code: code);
    public static SubscriptionException Empty() => new(SubscriptionErrorKind.Empty, "Подписка без серверов: пустой ответ");
    public static SubscriptionException NoServers(string what) => new(SubscriptionErrorKind.NoServers, $"Подписка без серверов: {what}", reason: what);

    public static SubscriptionException NothingSupported(string reason) =>
        new(SubscriptionErrorKind.NothingSupported, $"В подписке нет поддерживаемых серверов ({reason})", reason: reason);

    public static SubscriptionException DeviceLimit() =>
        new(SubscriptionErrorKind.DeviceLimit, "Достигнут лимит устройств подписки: отвяжите старое устройство у провайдера");

    public static SubscriptionException DeviceIdRefused() =>
        new(SubscriptionErrorKind.DeviceIdRefused, "Провайдер не принял ID устройства и не выдал серверы");
}

public static partial class SubscriptionParser
{
    /// <summary>Splits like Foundation's CharacterSet.newlines.</summary>
    [GeneratedRegex("[\n\u000B\u000C\r\u0085\u2028\u2029]")]
    private static partial Regex Newlines();

    [GeneratedRegex("^[A-Za-z][A-Za-z0-9+.-]*://", RegexOptions.Multiline)]
    private static partial Regex LinkScheme();

    public static SubscriptionResult Parse(byte[] body, IReadOnlyDictionary<string, string> headers)
    {
        // Device-limit panels answer 200 with a placeholder server and say why
        // in these headers (docs.rw/docs/features/hwid-device-limit).
        bool Flag(string name) => headers.Any(h => h.Key.ToLowerInvariant() == name && h.Value.ToLowerInvariant() == "true");
        if (Flag("x-hwid-max-devices-reached")) throw SubscriptionException.DeviceLimit();
        if (Flag("x-hwid-not-supported")) throw SubscriptionException.DeviceIdRefused();
        var result = ParseBody(body);
        result.Meta = ParseHeaders(headers);
        if (result.Servers.Count == 0)
        {
            if (result.Skipped.Count > 0) throw SubscriptionException.NothingSupported(result.Skipped[0].Reason);
            throw NoServers(body);
        }
        return result;
    }

    /// <summary>
    /// What came instead of servers, told by its shape: a body can hold a token,
    /// so only a short line of plain words is quoted.
    /// </summary>
    private static SubscriptionException NoServers(byte[] body)
    {
        if (Base64Text.Utf8OrNull(body) is not { } decoded) return SubscriptionException.NoServers("пришли двоичные данные");
        var text = Unwrapped(decoded);
        if (text.Length == 0) return SubscriptionException.Empty();
        if (text.StartsWith('<')) return SubscriptionException.NoServers("пришла веб-страница");
        if (IsJson(text)) return SubscriptionException.NoServers("пришли настройки без прокси");
        var scheme = LinkScheme().Match(text);
        if (scheme.Success) return SubscriptionException.NoServers($"пришли ссылки {scheme.Value}");
        if (text.Length <= 120 && text.Contains(' ') && !Newlines().IsMatch(text))
            return SubscriptionException.NoServers($"сервер ответил «{text}»");
        return SubscriptionException.NoServers($"пришёл текст, {body.Length} байт");
    }

    private static bool IsJson(string text) => text.StartsWith('{') || text.StartsWith('[');

    /// <summary>
    /// The body without what panels put around it: <c>#profile-title: ...</c> lines
    /// above JSON, or base64 around all of it.
    /// </summary>
    private static string Unwrapped(string body)
    {
        var text = body.TrimStart('\uFEFF').Trim();
        var uncommented = WithoutLeadingComments(text);
        if (IsJson(uncommented)) return uncommented;
        if (!text.Contains("://", StringComparison.Ordinal) && Base64Text.DecodeString(text) is { } decoded)
        {
            var inner = Unwrapped(decoded);
            if (inner.Contains("://", StringComparison.Ordinal) || IsJson(inner)) return inner;
        }
        return text;
    }

    private static string WithoutLeadingComments(string text)
    {
        var rest = text;
        while (rest.StartsWith('#') || rest.StartsWith("//", StringComparison.Ordinal))
        {
            var end = Newlines().Match(rest);
            if (!end.Success) return "";
            rest = rest[end.Index..].TrimStart();
        }
        return rest;
    }

    public static SubscriptionResult ParseBody(byte[] body)
    {
        var result = new SubscriptionResult();
        if (Base64Text.Utf8OrNull(body) is not { } decoded) return result;
        var text = Unwrapped(decoded);
        if (text.Length == 0) return result;

        if (IsJson(text))
        {
            ParseJson(text, result);
            return result;
        }
        foreach (var raw in Newlines().Split(text))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith("//", StringComparison.Ordinal)) continue;
            if (line.StartsWith("happ://routing/", StringComparison.OrdinalIgnoreCase))
            {
                result.RoutingLines.Add(line);
                continue;
            }
            if (ShareLink.SchemeOf(line) is null)
            {
                // Some panels put a base64 block per line.
                if (Base64Text.DecodeString(line) is { } block && block.Contains("://", StringComparison.Ordinal))
                {
                    var inner = ParseBody(Encoding.UTF8.GetBytes(block));
                    result.Servers.AddRange(inner.Servers);
                    result.Skipped.AddRange(inner.Skipped);
                    result.RoutingLines.AddRange(inner.RoutingLines);
                }
                continue;
            }
            try
            {
                result.Servers.Add(ShareLink.Parse(line));
            }
            catch (ShareLinkException e)
            {
                result.Skipped.Add(new SkippedEntry(line, e.Message));
            }
        }
        return result;
    }

    /// <summary>
    /// sing-box JSON: proxy outbounds taken as they are. Xray JSON (an array of
    /// configs, one per server, or one config): converted by XrayConfig.
    /// </summary>
    private static void ParseJson(string text, SubscriptionResult result)
    {
        var unknown = new SkippedEntry(text.Length > 80 ? text[..80] : text, "Неизвестный формат JSON");
        Json json;
        try
        {
            json = Json.Parse(text);
        }
        catch (JsonException)
        {
            result.Skipped.Add(unknown);
            return;
        }
        var configs = json.AsArray ?? [json];
        if (configs.All(c => c["outbounds"]?.AsArray is null)) result.Skipped.Add(unknown);
        foreach (var config in configs)
        {
            var outbounds = config["outbounds"]?.AsArray ?? [];
            if (outbounds.Any(o => o["protocol"] is not null)) XrayConfig.Parse(config, result);
            else ParseSingBox(outbounds, result);
        }
    }

    private static void ParseSingBox(IReadOnlyList<Json> outbounds, SubscriptionResult result)
    {
        foreach (var outbound in outbounds)
        {
            if (ProxyProtocols.Of(outbound["type"]?.AsString) is not { } proto) continue;
            if (outbound["server"]?.AsString is not { } host) continue;
            var port = outbound["server_port"]?.AsInt
                ?? TextUtil.ParseInt(outbound["server_ports"]?[0]?.AsString?.Split(':')[0]);
            if (port is not { } p) continue;
            var stripped = outbound.With("tag", null).With("detour", null);
            var name = outbound["tag"]?.AsString ?? $"{host}:{p}";
            result.Servers.Add(new ParsedServer(name, proto, host, p, stripped, null));
        }
    }

    public static SubscriptionMeta ParseHeaders(IReadOnlyDictionary<string, string> headers)
    {
        var lower = new Dictionary<string, string>();
        foreach (var (key, value) in headers) lower[key.ToLowerInvariant()] = value;
        string? Get(string key) => lower.GetValueOrDefault(key);
        var announce = Get("announce") is { } a ? DecodeHeaderText(a) : null;
        return new SubscriptionMeta(
            Title: Get("profile-title") is { } t ? DecodeHeaderText(t) : null,
            UpdateIntervalHours: TextUtil.ParseInt(Get("profile-update-interval")?.Trim()) is { } h ? Math.Max(1, h) : null,
            Usage: Get("subscription-userinfo") is { } u ? ParseUserInfo(u) : null,
            SupportUrl: UrlOrNull(Get("support-url")),
            WebPageUrl: UrlOrNull(Get("profile-web-page-url")),
            Announce: string.IsNullOrEmpty(announce) ? null : announce);
    }

    /// <summary><c>upload=1; download=2; total=3; expire=1700000000</c></summary>
    internal static Usage? ParseUserInfo(string value)
    {
        var fields = new Dictionary<string, long>();
        foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = ShareLink.SwiftSplit(part, '=', 1);
            if (kv.Count != 2) continue;
            var key = kv[0].Trim().ToLowerInvariant();
            var number = kv[1].Trim();
            long? parsed = TextUtil.ParseLong(number);
            if (parsed is null && TextUtil.ParseDouble(number) is { } d && double.IsFinite(d))
                parsed = d >= long.MaxValue ? long.MaxValue : d <= long.MinValue ? long.MinValue : (long)d;
            if (parsed is { } n) fields[key] = n;
            else fields.Remove(key);
        }
        if (fields.Count == 0) return null;
        long seconds = fields.GetValueOrDefault("expire");
        DateTimeOffset? expire = seconds > 0 ? DateTimeOffset.FromUnixTimeSeconds(Math.Min(seconds, 253402300799)) : null;
        return new Usage(fields.GetValueOrDefault("upload"), fields.GetValueOrDefault("download"), fields.GetValueOrDefault("total"), expire);
    }

    /// <summary>Header text is plain, percent-encoded, or <c>base64:...</c>.</summary>
    internal static string DecodeHeaderText(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("base64:", StringComparison.OrdinalIgnoreCase))
            return Base64Text.DecodeString(trimmed[7..])?.Trim() ?? trimmed;
        return TextUtil.PercentDecode(trimmed) ?? trimmed;
    }

    private static string? UrlOrNull(string? value)
    {
        var s = value?.Trim();
        if (string.IsNullOrEmpty(s)) return null;
        return Uri.TryCreate(s, UriKind.RelativeOrAbsolute, out _) ? s : null;
    }
}

/// <summary>
/// Fetches a subscription. One request per refresh, a second only after a blank
/// answer, and no cache, so a refresh costs one wake-up of the network.
/// </summary>
public sealed class SubscriptionClient(IReadOnlyDictionary<string, string> deviceHeaders, TimeSpan? timeout = null)
{
    public const string DefaultUserAgent = "LiteTunnel/0.1";

    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.All,
        // A refresh is rare: keep no idle connection open between them.
        PooledConnectionIdleTimeout = TimeSpan.FromSeconds(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    private readonly TimeSpan _timeout = timeout ?? TimeSpan.FromSeconds(15);

    public Task<SubscriptionResult> FetchAsync(Subscription subscription, CancellationToken cancellationToken = default) =>
        WithJsonFallback(subscription.Url, url => LoadAsync(subscription, url, cancellationToken));

    /// <summary>
    /// Some Remnawave panels answer blank on the plain address and serve the
    /// servers at <c>&lt;address&gt;/json</c> (docs.rs/crate/remnawave). A panel without
    /// that format answers with an HTTP error: then the blank answer is the news.
    /// </summary>
    internal static async Task<T> WithJsonFallback<T>(string url, Func<string, Task<T>> load)
    {
        try
        {
            return await load(url).ConfigureAwait(false);
        }
        catch (SubscriptionException blank) when (blank.Kind == SubscriptionErrorKind.Empty && JsonFormatUrl(url) is not null)
        {
            try
            {
                return await load(JsonFormatUrl(url)!).ConfigureAwait(false);
            }
            catch (SubscriptionException e) when (e.Kind == SubscriptionErrorKind.Http)
            {
                throw SubscriptionException.Empty();
            }
        }
    }

    /// <summary>Null when the address already asks for JSON or has no path to add it to.</summary>
    internal static string? JsonFormatUrl(string url)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)) return null;
        var path = uri.AbsolutePath.TrimEnd('/');
        if (path.Length == 0 || path.EndsWith("/json", StringComparison.Ordinal)) return null;
        return $"{uri.GetLeftPart(UriPartial.Authority)}{path}/json{uri.Query}";
    }

    private async Task<SubscriptionResult> LoadAsync(Subscription subscription, string url, CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new IOException("Неверный адрес подписки");
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.TryAddWithoutValidation("User-Agent", subscription.UserAgent ?? DefaultUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
        foreach (var (name, value) in deviceHeaders) request.Headers.TryAddWithoutValidation(name, value);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        try
        {
            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
            int code = (int)response.StatusCode;
            if (code is < 200 or > 299) throw SubscriptionException.Http(code);
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, values) in response.Headers) headers[key] = string.Join(", ", values);
            foreach (var (key, values) in response.Content.Headers) headers[key] = string.Join(", ", values);
            var body = await response.Content.ReadAsByteArrayAsync(cts.Token).ConfigureAwait(false);
            return SubscriptionParser.Parse(body, headers);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException("Сервер подписки не ответил вовремя");
        }
        catch (HttpRequestException e)
        {
            throw new IOException("Не удалось связаться с сервером подписки", e);
        }
    }
}
