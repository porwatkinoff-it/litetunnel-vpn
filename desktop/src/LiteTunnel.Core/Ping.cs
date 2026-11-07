using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace LiteTunnel.Core;

/// <summary>
/// Measures one server, outside the tunnel. Replace it to change how ping
/// works; <see cref="Ping.MeasureAllAsync"/> takes any implementation.
/// Returns null when the server cannot be measured this way.
/// </summary>
public interface IPinger
{
    Task<PingResult?> MeasureAsync(Server server, int timeoutMs, CancellationToken cancellation = default);
}

/// <summary>
/// Which network the probes use. The default leaves it to the system. While
/// the tunnel is up the app passes <see cref="PhysicalNetwork"/> instead:
/// through the tunnel every handshake would end at the core and take about 1 ms.
/// </summary>
public interface IProbeNetwork
{
    async Task<IPAddress> ResolveAsync(string host, CancellationToken cancellation)
    {
        if (IPAddress.TryParse(host, out var literal)) return literal;
        var addresses = await Dns.GetHostAddressesAsync(host, cancellation).ConfigureAwait(false);
        return addresses.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
            ?? addresses.FirstOrDefault()
            ?? throw new SocketException((int)SocketError.HostNotFound);
    }

    /// <summary>Called before the socket connects, TCP and UDP alike.</summary>
    void Bind(Socket socket) { }

    public static readonly IProbeNetwork Default = new DefaultNetwork();

    private sealed class DefaultNetwork : IProbeNetwork;
}

/// <summary>
/// TCP servers get a TCP handshake. Hysteria2 and TUIC run over QUIC, so they
/// get a QUIC Initial instead and any answer counts; with salamander obfs the
/// server does not answer plain QUIC, and the result is null (not measurable).
/// The name is resolved first and only the handshake is timed, so the number
/// is the round trip to the server, not to the DNS resolver.
/// </summary>
public class SocketPinger(IProbeNetwork network) : IPinger
{
    public static readonly SocketPinger Default = new(IProbeNetwork.Default);

    public async Task<PingResult?> MeasureAsync(Server server, int timeoutMs, CancellationToken cancellation = default)
    {
        bool quic = server.Proto is ProxyProtocol.Hysteria2 or ProxyProtocol.Tuic;
        if (quic && server.Outbound["obfs"] is not null) return null;
        if (!ShareLink.ValidPort(server.Port)) return PingResult.Failed;
        IPAddress address;
        try
        {
            address = await network.ResolveAsync(server.Host, cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return PingResult.Failed;
        }
        var endpoint = new IPEndPoint(address, server.Port);
        if (quic)
        {
            var tls = server.Outbound["tls"];
            var alpn = tls?["alpn"]?.AsArray?.Select(a => a.AsString).OfType<string>().ToList() ?? [];
            if (alpn.Count == 0) alpn = ["h3"];
            var sni = tls?["server_name"]?.AsString ?? server.Host;
            return await QuicProbe.ProbeAsync(endpoint, sni, alpn, timeoutMs, network.Bind, cancellation).ConfigureAwait(false);
        }
        return await TcpAsync(endpoint, timeoutMs, cancellation).ConfigureAwait(false);
    }

    private async Task<PingResult> TcpAsync(IPEndPoint address, int timeoutMs, CancellationToken cancellation)
    {
        try
        {
            using var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
            network.Bind(socket);
            socket.NoDelay = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
            timeout.CancelAfter(timeoutMs);
            var clock = Stopwatch.StartNew();
            await socket.ConnectAsync(address, timeout.Token).ConfigureAwait(false);
            return PingResult.Ms(Math.Max(1, (int)clock.ElapsedMilliseconds));
        }
        catch (Exception e) when (e is SocketException or OperationCanceledException or IOException)
        {
            return PingResult.Failed;
        }
    }
}

/// <summary>Plain ICMP echo. Many servers do not answer it, then it is «нет связи».</summary>
public sealed class IcmpPinger(IProbeNetwork network) : IPinger
{
    public async Task<PingResult?> MeasureAsync(Server server, int timeoutMs, CancellationToken cancellation = default)
    {
        try
        {
            var address = await network.ResolveAsync(server.Host, cancellation).ConfigureAwait(false);
            return await Ping.IcmpAsync(address, timeoutMs).ConfigureAwait(false);
        }
        catch (Exception e) when (e is SocketException or ArgumentException)
        {
            return PingResult.Failed;
        }
    }
}

public static class Ping
{
    public const int TimeoutMs = 3_000;

    /// <summary>Requests through the server get longer: the whole request has to go through.</summary>
    public const int RequestTimeoutMs = 5_000;

    /// <summary>
    /// What the check downloads through each server: a big, stable file on a
    /// CDN that answers range requests. Only its first <see cref="CheckBytes"/> are read.
    /// </summary>
    public static readonly Uri CheckUrl = new("https://dl.google.com/linux/direct/google-chrome-stable_current_amd64.deb");

    /// <summary>
    /// More than some networks let through before they freeze a connection
    /// they do not like, so such a server fails instead of showing a time.
    /// </summary>
    public const int CheckBytes = 64 * 1024;

    /// <summary>How long the check may wait for the next bytes.</summary>
    public const int CheckIdleMs = 5_000;

    /// <summary>How long the whole check may take.</summary>
    public const int CheckTotalMs = 8_000;

    public static PingResult? Fastest(IEnumerable<PingResult?> results) =>
        results.OfType<PingResult>().OrderBy(r => r.SortValue).FirstOrDefault();

    /// <summary>
    /// Times one request to <paramref name="url"/> through the HTTP proxy on
    /// 127.0.0.1:<paramref name="port"/>, up to the response headers. Null
    /// when it fails or the answer is not the one <see cref="Passes"/> wants.
    /// </summary>
    public static async Task<int?> RequestAsync(Uri url, int port, bool head, int timeoutMs = RequestTimeoutMs, CancellationToken cancellation = default)
    {
        using var client = Client(port, timeoutMs);
        using var request = new HttpRequestMessage(head ? HttpMethod.Head : HttpMethod.Get, url);
        request.Headers.ConnectionClose = true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(timeoutMs);
        try
        {
            var clock = Stopwatch.StartNew();
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            int ms = (int)clock.ElapsedMilliseconds;
            return Passes((int)response.StatusCode, url) ? Math.Max(1, ms) : null;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether <paramref name="status"/> is the test address's own answer. A
    /// generate_204 address answers 204 and nothing else: a 200 there is a
    /// page from someone on the way, a stub or a captive portal.
    /// </summary>
    public static bool Passes(int status, Uri url) =>
        url.AbsolutePath.EndsWith("generate_204", StringComparison.Ordinal) ? status == 204 : status is >= 200 and <= 399;

    /// <summary>
    /// Downloads the first <see cref="CheckBytes"/> of <paramref name="url"/>
    /// through the proxy on 127.0.0.1:<paramref name="port"/>. True only when
    /// all of them arrive: a server that answers a short request but stalls on
    /// real traffic fails here.
    /// </summary>
    public static async Task<bool> CheckAsync(int port, Uri? url = null, int idleMs = CheckIdleMs, int totalMs = CheckTotalMs, CancellationToken cancellation = default)
    {
        using var client = Client(port, idleMs);
        using var request = new HttpRequestMessage(HttpMethod.Get, url ?? CheckUrl);
        request.Headers.ConnectionClose = true;
        request.Headers.Range = new RangeHeaderValue(0, CheckBytes - 1);
        request.Headers.AcceptEncoding.Add(new StringWithQualityHeaderValue("identity"));
        using var total = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        total.CancelAfter(totalMs);
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(total.Token);
        idle.CancelAfter(idleMs);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
            if ((int)response.StatusCode is not (200 or 206)) return false;
            await using var body = await response.Content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
            var buffer = new byte[16 * 1024];
            int received = 0;
            while (received < CheckBytes)
            {
                idle.CancelAfter(idleMs);
                int read = await body.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, CheckBytes - received)), idle.Token).ConfigureAwait(false);
                if (read == 0) return false;
                received += read;
            }
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or IOException)
        {
            return false;
        }
    }

    /// <summary>
    /// One server through the probe core's port: the request to
    /// <paramref name="url"/>, then the check. Failed when either fails, so a
    /// time means the server passes traffic. TCP and ICMP show the direct
    /// measurement of a working server, or the request time as Real when the
    /// server cannot be measured that way.
    /// </summary>
    public static async Task<PingResult> ThroughServerAsync(
        Server server,
        int port,
        Uri url,
        PingMode mode,
        IPinger? direct,
        Uri? checkUrl = null,
        int checkIdleMs = CheckIdleMs,
        CancellationToken cancellation = default)
    {
        if (await RequestAsync(url, port, head: mode == PingMode.ProxyHead, cancellation: cancellation).ConfigureAwait(false) is not { } ms)
            return PingResult.Failed;
        if (!await CheckAsync(port, checkUrl, checkIdleMs, cancellation: cancellation).ConfigureAwait(false)) return PingResult.Failed;
        if (mode.ThroughProxy() || direct is null) return Result(ms, mode.ThroughProxy() ? mode : PingMode.Real);
        return await direct.MeasureAsync(server, TimeoutMs, cancellation).ConfigureAwait(false) is PingMs measured
            ? measured
            : Result(ms, PingMode.Real);
    }

    private static HttpClient Client(int port, int connectTimeoutMs) => new(new SocketsHttpHandler
    {
        Proxy = new WebProxy(new Uri($"http://127.0.0.1:{port}")),
        UseProxy = true,
        AllowAutoRedirect = false,
        UseCookies = false,
        AutomaticDecompression = DecompressionMethods.None,
        ConnectTimeout = TimeSpan.FromMilliseconds(connectTimeoutMs),
    }, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };

    /// <summary>What a proxied request time shows as in <paramref name="mode"/>.</summary>
    public static PingResult Result(int? ms, PingMode mode)
    {
        if (ms is not { } value) return PingResult.Failed;
        if (mode != PingMode.Real) return PingResult.Ms(value);
        return PingResult.Ms(Math.Max(1, (int)Math.Round(value / 3.3, MidpointRounding.AwayFromZero)));
    }

    /// <summary>The address from settings when it is a web one, else the default.</summary>
    public static Uri TestUrl(string text)
    {
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            && uri.Host.Length > 0)
            return uri;
        return new Uri(AppSettings.DefaultPingUrl);
    }

    public static async Task<PingResult> IcmpAsync(IPAddress address, int timeoutMs)
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(address, timeoutMs).ConfigureAwait(false);
            return reply.Status == IPStatus.Success ? PingResult.Ms(Math.Max(1, (int)reply.RoundtripTime)) : PingResult.Failed;
        }
        catch (Exception e) when (e is PingException or SocketException or InvalidOperationException)
        {
            return PingResult.Failed;
        }
    }

    /// <summary>A group is as fast as its fastest member: the one the core picks.</summary>
    public static async Task<PingResult?> MeasureAsync(Server server, IPinger? pinger = null, int timeoutMs = TimeoutMs)
    {
        PingResult? result = null;
        await MeasureAllAsync([server], pinger, (_, r) => result = r, timeoutMs: timeoutMs).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Pings many servers, at most <paramref name="concurrency"/> probes at a
    /// time, and finishes when all are done. Group members are probes of their
    /// own. <paramref name="onResult"/> is called once per server, from a
    /// worker thread.
    /// </summary>
    public static async Task MeasureAllAsync(
        IReadOnlyList<Server> servers,
        IPinger? pinger,
        Action<Guid, PingResult?> onResult,
        int concurrency = 8,
        int timeoutMs = TimeoutMs,
        CancellationToken cancellation = default)
    {
        pinger ??= SocketPinger.Default;
        using var slots = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = servers.Select(async server =>
        {
            var targets = server.Members.Count > 0 ? server.Members : [server];
            var results = await Task.WhenAll(targets.Select(async target =>
            {
                await slots.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    return await pinger.MeasureAsync(target, timeoutMs, cancellation).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            })).ConfigureAwait(false);
            onResult(server.Id, Fastest(results));
        }).ToList();
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }
}

/// <summary>
/// The network interface that carries the default route, not the tunnel.
/// Windows pins sockets to it with IP_UNICAST_IF, Linux with SO_BINDTODEVICE.
/// Best effort: when it cannot be done the socket is left as it is.
/// </summary>
public sealed class PhysicalNetwork(string tunInterfaceName) : IProbeNetwork
{
    private readonly NetworkInterface? physical = Find(tunInterfaceName);

    public string? InterfaceName => physical?.Name;

    public void Bind(Socket socket)
    {
        if (physical is null) return;
        try
        {
            if (OperatingSystem.IsWindows())
            {
                const int IpUnicastIf = 31;
                if (socket.AddressFamily == AddressFamily.InterNetwork)
                {
                    int index = physical.GetIPProperties().GetIPv4Properties().Index;
                    socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)IpUnicastIf, IPAddress.HostToNetworkOrder(index));
                }
                else
                {
                    int index = physical.GetIPProperties().GetIPv6Properties().Index;
                    socket.SetSocketOption(SocketOptionLevel.IPv6, (SocketOptionName)IpUnicastIf, index);
                }
            }
            else if (OperatingSystem.IsLinux())
            {
                const int SolSocket = 1, SoBindToDevice = 25;
                socket.SetRawSocketOption(SolSocket, SoBindToDevice, System.Text.Encoding.ASCII.GetBytes(physical.Name + "\0"));
            }
        }
        catch (Exception e) when (e is SocketException or NetworkInformationException or PlatformNotSupportedException)
        {
            // Measured through the tunnel then; still shows that the core is up.
        }
    }

    private static NetworkInterface? Find(string tunInterfaceName)
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                    && n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel)
                    && !string.Equals(n.Name, tunInterfaceName, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault(n => n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any) && !g.Address.Equals(IPAddress.IPv6Any)));
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
