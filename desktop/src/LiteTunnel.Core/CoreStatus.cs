using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LiteTunnel.Core;

/// <summary>Loopback ports for the core, and waiting for it to open them.</summary>
public static class LocalPorts
{
    public static List<int> Free(int count)
    {
        var listeners = Enumerable.Range(0, count).Select(_ =>
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            return l;
        }).ToList();
        var ports = listeners.Select(l => ((IPEndPoint)l.LocalEndpoint).Port).ToList();
        foreach (var l in listeners) l.Stop();
        return ports;
    }

    public static bool IsFree(int port)
    {
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            listener.Stop();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>
    /// Waits until every port takes connections. The error, when the core
    /// exits first or <paramref name="timeout"/> passes; null once it listens.
    /// </summary>
    public static async Task<string?> WaitAsync(CoreProcess core, IReadOnlyList<int> ports, TimeSpan timeout, CancellationToken cancellation = default)
    {
        var deadline = DateTime.UtcNow + timeout;
        var waiting = new HashSet<int>(ports);
        while (waiting.Count > 0)
        {
            if (core.HasExited) return FirstError(core.LastOutput) ?? $"ядро завершилось с кодом {core.Exited.Result}";
            if (DateTime.UtcNow > deadline) return $"ядро не ответило за {timeout.TotalSeconds:0} с";
            foreach (var port in waiting.ToList())
            {
                try
                {
                    using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    await socket.ConnectAsync(new IPEndPoint(IPAddress.Loopback, port), cancellation).ConfigureAwait(false);
                    waiting.Remove(port);
                }
                catch (SocketException)
                {
                    break;
                }
            }
            if (waiting.Count > 0) await Task.Delay(50, cancellation).ConfigureAwait(false);
        }
        return null;
    }

    /// <summary>The last FATAL or ERROR line of the core's output, without its prefix.</summary>
    public static string? FirstError(string output) =>
        output.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Contains("FATAL") || l.Contains("ERROR")) is { } line
            ? line[(line.IndexOf(']') + 1)..].Trim()
            : null;
}

/// <summary>
/// The running core's clash API on loopback: speed and totals. The app reads
/// it only while the window shows them; the core counts either way.
/// </summary>
public sealed record StatusApi(int Port, string Secret)
{
    public static StatusApi Create(int port) => new(port, Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16)));

    public string Controller => $"127.0.0.1:{Port}";

    /// <summary>Never through a proxy: the system one may be this very core.</summary>
    public static HttpClient Client() => new(new SocketsHttpHandler { UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };

    private HttpRequestMessage Request(string path)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"http://{Controller}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Secret);
        return request;
    }

    /// <summary>Bytes sent and received since the core started; null when it does not answer.</summary>
    public async Task<(long Up, long Down)?> TotalsAsync(HttpClient http, CancellationToken cancellation = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            using var response = await http.SendAsync(Request("/connections"), timeout.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;
            var json = Json.Parse(await response.Content.ReadAsStringAsync(timeout.Token).ConfigureAwait(false));
            return (json["uploadTotal"]?.AsLong ?? 0, json["downloadTotal"]?.AsLong ?? 0);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or JsonException || (e is OperationCanceledException && !cancellation.IsCancellationRequested))
        {
            return null;
        }
    }

    /// <summary>
    /// Calls <paramref name="onSecond"/> with the bytes per second up and down,
    /// once a second as the core sends them, until cancelled or the core goes.
    /// </summary>
    public async Task WatchTrafficAsync(HttpClient http, Action<long, long> onSecond, CancellationToken cancellation)
    {
        try
        {
            using var response = await http.SendAsync(Request("/traffic"), HttpCompletionOption.ResponseHeadersRead, cancellation).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return;
            using var reader = new StreamReader(await response.Content.ReadAsStreamAsync(cancellation).ConfigureAwait(false));
            while (await reader.ReadLineAsync(cancellation).ConfigureAwait(false) is { } line)
            {
                if (line.Length == 0) continue;
                Json json;
                try { json = Json.Parse(line); }
                catch (JsonException) { continue; }
                onSecond(json["up"]?.AsLong ?? 0, json["down"]?.AsLong ?? 0);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException)
        {
        }
    }
}
