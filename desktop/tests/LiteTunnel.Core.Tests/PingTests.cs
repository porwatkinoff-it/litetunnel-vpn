using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

public class PingTests
{
    private static Server Server(string link) => new(null, ShareLink.Parse(link));

    [Fact]
    public void RealModeScalesToPlainPing()
    {
        Assert.Equal(PingResult.Ms(100), Ping.Result(330, PingMode.Real));
        Assert.Equal(PingResult.Ms(1), Ping.Result(1, PingMode.Real));
        Assert.Equal(PingResult.Ms(330), Ping.Result(330, PingMode.ProxyGet));
        Assert.Equal(PingResult.Failed, Ping.Result(null, PingMode.Real));
    }

    [Fact]
    public void FastestPrefersTimesOverFailures()
    {
        Assert.Equal(PingResult.Ms(40), Ping.Fastest([PingResult.Failed, PingResult.Ms(80), null, PingResult.Ms(40)]));
        Assert.Equal(PingResult.Failed, Ping.Fastest([null, PingResult.Failed]));
        Assert.Null(Ping.Fastest([null]));
    }

    [Fact]
    public void OldSettingsGetPingDefaults()
    {
        var settings = StateCodec.Decode(Json.Parse("""{"settings":{"pingOnOpen":true}}""")).Settings;
        Assert.True(settings.PingOnOpen);
        Assert.Equal(PingMode.Real, settings.PingMode);
        Assert.Equal(AppSettings.DefaultPingUrl, settings.PingUrl);
        Assert.Equal(PingDisplay.Time, settings.PingDisplay);
        Assert.False(settings.PingOnUpdate);
    }

    [Fact]
    public async Task IcmpReachesLoopback()
    {
        // macOS and Windows allow unprivileged echo; on Linux .NET falls back to the ping tool.
        Assert.IsType<PingMs>(await Ping.IcmpAsync(IPAddress.Loopback, 2000));
    }

    [Fact]
    public void QuicInitialKeysMatchRfc9001()
    {
        var keys = QuicProbe.ClientKeys(Convert.FromHexString("8394c8f03e515708"));
        Assert.Equal("1f369613dd76d5467730efcbe3b1a22d", Convert.ToHexStringLower(keys.Key));
        Assert.Equal("fa044b2f42a3fd3b46fb255c", Convert.ToHexStringLower(keys.Iv));
        Assert.Equal("9f50449e04a0e810283a1e9933adedd2", Convert.ToHexStringLower(keys.Hp));
    }

    [Fact]
    public void QuicHeaderMaskMatchesRfc9001()
    {
        var keys = QuicProbe.ClientKeys(Convert.FromHexString("8394c8f03e515708"));
        var mask = QuicProbe.HeaderMask(keys.Hp, Convert.FromHexString("d1b1c98dd7689fb8ec11d242b123dc9b"));
        Assert.StartsWith("437b9aec36", Convert.ToHexStringLower(mask));
    }

    [Fact]
    public void QuicInitialPacketDecrypts()
    {
        var dcid = Convert.FromHexString("0102030405060708");
        var packet = QuicProbe.InitialPacket(dcid, Convert.FromHexString("1112131415161718"), "h.example.com", ["h3"]);
        Assert.True(packet.Length >= 1200);
        Assert.NotEqual(0, packet[0] & 0x80);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }, packet[1..5]);

        var keys = QuicProbe.ClientKeys(dcid);
        int pnOffset = 1 + 4 + 1 + 8 + 1 + 8 + 1 + 2; // flags, version, dcid, scid, empty token, 2-byte length
        var mask = QuicProbe.HeaderMask(keys.Hp, packet.AsSpan(pnOffset + 4, 16));
        var header = packet[..(pnOffset + 4)];
        header[0] ^= (byte)(mask[0] & 0x0f);
        for (int i = 0; i < 4; i++) header[pnOffset + i] ^= mask[1 + i];
        Assert.Equal(3, header[0] & 3);
        Assert.Equal(new byte[4], header[pnOffset..]);
        Assert.Equal(packet.Length - pnOffset, ((header[pnOffset - 2] & 0x3f) << 8) | header[pnOffset - 1]);

        var cipher = packet[(pnOffset + 4)..^16];
        var payload = new byte[cipher.Length];
        using (var aes = new AesGcm(keys.Key, 16))
            aes.Decrypt(keys.Iv, cipher, packet[^16..], payload, header); // packet number 0: the nonce is the IV
        Assert.Equal(6, payload[0]); // CRYPTO frame
        Assert.Equal(0, payload[1]); // at offset 0
        int helloAt = (payload[2] & 0xc0) == 0x40 ? 4 : 3;
        Assert.Equal(1, payload[helloAt]); // ClientHello
        Assert.Contains("h.example.com", Encoding.Latin1.GetString(payload));
    }

    [Fact]
    public async Task TcpPingMeasuresAnOpenPortAndFailsOnAClosedOne()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        try
        {
            var open = await SocketPinger.Default.MeasureAsync(Server($"trojan://pw@127.0.0.1:{port}#T"), 1000);
            Assert.True(open is PingMs { Value: >= 1 });
        }
        finally
        {
            listener.Stop();
        }
        var closed = await SocketPinger.Default.MeasureAsync(Server($"trojan://pw@127.0.0.1:{port}#T"), 1000);
        Assert.Equal(PingResult.Failed, closed);
    }

    private sealed class RecordingNetwork : IProbeNetwork
    {
        public readonly List<string> Calls = [];

        public Task<IPAddress> ResolveAsync(string host, CancellationToken cancellation)
        {
            lock (Calls) Calls.Add($"resolve {host}");
            return Task.FromResult(IPAddress.Loopback);
        }

        public void Bind(Socket socket)
        {
            lock (Calls) Calls.Add(socket.ProtocolType == ProtocolType.Udp ? "udp" : "tcp");
        }
    }

    [Fact]
    public async Task ProbesUseTheGivenNetwork()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var network = new RecordingNetwork();
        var pinger = new SocketPinger(network);
        try
        {
            await pinger.MeasureAsync(Server($"trojan://pw@far.example.com:{port}#T"), 1000);
            await pinger.MeasureAsync(Server("hy2://pw@quic.example.com:9#H"), 100);
        }
        finally
        {
            listener.Stop();
        }
        Assert.Equal(["resolve far.example.com", "tcp", "resolve quic.example.com", "udp"], network.Calls);
    }

    [Fact]
    public async Task ObfuscatedHysteriaIsNotMeasurable() =>
        Assert.Null(await SocketPinger.Default.MeasureAsync(Server("hy2://pw@127.0.0.1:443?obfs=salamander&obfs-password=x#H"), 1000));

    private sealed class FakePinger(Func<Server, PingResult?> result) : IPinger
    {
        public Task<PingResult?> MeasureAsync(Server server, int timeoutMs, CancellationToken cancellation = default) =>
            Task.FromResult(result(server));
    }

    [Fact]
    public async Task GroupTakesItsFastestMember()
    {
        var members = new[] { "a", "b", "c" }.Select(x => ShareLink.Parse($"trojan://pw@{x}.example.com:443#{x}")).ToList();
        var group = new Server(null, ServerGroup.Make(members, "Авто-выбор"));
        var single = Server("trojan://pw@d.example.com:443#d");
        var byHost = new FakePinger(s => s.Host switch
        {
            "a.example.com" => PingResult.Ms(80),
            "b.example.com" => PingResult.Ms(30),
            "d.example.com" => PingResult.Ms(50),
            _ => PingResult.Failed,
        });
        var results = new Dictionary<Guid, PingResult?>();
        await Ping.MeasureAllAsync([group, single], byHost, (id, r) => { lock (results) results[id] = r; });
        Assert.Equal(PingResult.Ms(30), results[group.Id]);
        Assert.Equal(PingResult.Ms(50), results[single.Id]);

        var dead = new Server(null, ServerGroup.Make(members.Take(2).ToList(), "Мёртвая"));
        Assert.Equal(PingResult.Failed, await Ping.MeasureAsync(dead, new FakePinger(_ => PingResult.Failed)));
        Assert.Null(await Ping.MeasureAsync(dead, new FakePinger(_ => null)));
    }

    [Fact]
    public void TestUrlTakesOnlyWebAddresses()
    {
        Assert.Equal("https://cp.example.com/x", Ping.TestUrl(" https://cp.example.com/x ").OriginalString);
        Assert.Equal("http://1.1.1.1", Ping.TestUrl("http://1.1.1.1").OriginalString);
        foreach (var bad in new[] { "", "cp.example.com", "ftp://cp.example.com/", "http://", "http://bad host/" })
            Assert.Equal(AppSettings.DefaultPingUrl, Ping.TestUrl(bad).OriginalString);
    }

    /// <summary>An HTTP proxy for one request: keeps the request line and answers with <paramref name="status"/>.</summary>
    private static (int Port, Task<string> RequestLine) OneShotProxy(int status)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        async Task<string> Serve()
        {
            try
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync() ?? "";
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync())) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"));
                return line;
            }
            finally
            {
                listener.Stop();
            }
        }
        return (port, Serve());
    }

    [Fact]
    public async Task RequestGoesThroughTheProxy()
    {
        var url = new Uri(AppSettings.DefaultPingUrl);
        var ok = OneShotProxy(204);
        Assert.True(await Ping.RequestAsync(url, ok.Port, head: true) is >= 1);
        Assert.Equal("HEAD http://www.gstatic.com/generate_204 HTTP/1.1", await ok.RequestLine);

        var bad = OneShotProxy(502);
        Assert.Null(await Ping.RequestAsync(url, bad.Port, head: false));
        await bad.RequestLine;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int closed = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        Assert.Null(await Ping.RequestAsync(url, closed, head: false, timeoutMs: 1000));
    }

    // MARK: the check through a server

    private static readonly Uri File = new("http://check.invalid/file.bin");

    [Fact]
    public async Task Generate204NeedsA204()
    {
        var url = new Uri("http://check.invalid/generate_204");
        using (var stub = new FakeProxy(_ => new(200, 2)))
            Assert.Null(await Ping.RequestAsync(url, stub.Port, head: false, timeoutMs: 3000));
        using var real = new FakeProxy(_ => new(204, 0));
        Assert.True(await Ping.RequestAsync(url, real.Port, head: false, timeoutMs: 3000) is >= 1);
    }

    [Fact]
    public void OtherAddressesPassWith200To399()
    {
        var url = new Uri("https://example.com/");
        Assert.True(Ping.Passes(200, url));
        Assert.True(Ping.Passes(302, url));
        Assert.False(Ping.Passes(404, url));
        Assert.False(Ping.Passes(500, url));
        var generate = new Uri("https://www.gstatic.com/generate_204");
        Assert.True(Ping.Passes(204, generate));
        Assert.False(Ping.Passes(200, generate));
        Assert.False(Ping.Passes(302, generate));
    }

    [Fact]
    public async Task CheckPassesOnTheWholeDownload()
    {
        string? head = null;
        using var proxy = new FakeProxy(h =>
        {
            head = h;
            return new(206, Ping.CheckBytes);
        });
        Assert.True(await Ping.CheckAsync(proxy.Port, File, idleMs: 1000, totalMs: 3000));
        Assert.Contains($"range: bytes=0-{Ping.CheckBytes - 1}", head?.ToLowerInvariant());
    }

    [Fact]
    public async Task CheckFailsWhenTheConnectionStalls()
    {
        // About what some networks let through before they freeze a connection.
        using var proxy = new FakeProxy(_ => new(200, 16 * 1024, Length: Ping.CheckBytes, KeepOpen: true));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await Ping.CheckAsync(proxy.Port, File, idleMs: 1000, totalMs: 3000));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task CheckEndsAtItsTotalTime()
    {
        // Never idle for long, but far too slow to finish.
        using var proxy = new FakeProxy(_ => new(200, Ping.CheckBytes, Trickle: true));
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Assert.False(await Ping.CheckAsync(proxy.Port, File, idleMs: 1000, totalMs: 1500));
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task CheckFailsOnAnErrorPage()
    {
        using var proxy = new FakeProxy(_ => new(403, Ping.CheckBytes));
        Assert.False(await Ping.CheckAsync(proxy.Port, File, idleMs: 1000, totalMs: 3000));
    }

    [Fact]
    public async Task CheckStopsWhenTheRangeIsIgnored()
    {
        using var proxy = new FakeProxy(_ => new(200, 4 * Ping.CheckBytes));
        Assert.True(await Ping.CheckAsync(proxy.Port, File, idleMs: 1000, totalMs: 3000));
    }

    [Fact]
    public async Task ATimeMeansTheServerPassedTheCheck()
    {
        var url = new Uri("http://check.invalid/generate_204");
        var server = Server("trojan://pw@a.example.com:443#a");
        using var working = new FakeProxy(h => h.Contains("generate_204") ? new(204, 0) : new(206, Ping.CheckBytes));
        using var stalling = new FakeProxy(h => h.Contains("generate_204") ? new(204, 0) : new(200, 1024, Length: Ping.CheckBytes, KeepOpen: true));

        Assert.IsType<PingMs>(await Ping.ThroughServerAsync(server, working.Port, url, PingMode.Real, null, File, checkIdleMs: 1000));
        Assert.Equal(PingResult.Failed, await Ping.ThroughServerAsync(server, stalling.Port, url, PingMode.Real, null, File, checkIdleMs: 1000));

        // TCP and ICMP show their own time, but only for a server that works.
        var direct = new FakePinger(_ => PingResult.Ms(42));
        Assert.Equal(PingResult.Ms(42), await Ping.ThroughServerAsync(server, working.Port, url, PingMode.Tcp, direct, File, checkIdleMs: 1000));
        Assert.Equal(PingResult.Failed, await Ping.ThroughServerAsync(server, stalling.Port, url, PingMode.Icmp, direct, File, checkIdleMs: 1000));
        var silent = new FakePinger(_ => PingResult.Failed);
        Assert.IsType<PingMs>(await Ping.ThroughServerAsync(server, working.Port, url, PingMode.Icmp, silent, File, checkIdleMs: 1000));
    }

    /// <summary>
    /// An HTTP proxy on loopback that answers every request itself, the way a
    /// broken server or a network on the way might, until disposed.
    /// </summary>
    private sealed class FakeProxy : IDisposable
    {
        /// <param name="Length">The Content-Length sent, when it is not the body's.</param>
        /// <param name="KeepOpen">Leaves the connection open after the body, as a stalled one.</param>
        /// <param name="Trickle">Sends the body 1 KB at a time, 200 ms apart.</param>
        public sealed record Reply(int Status, int Body, int? Length = null, bool KeepOpen = false, bool Trickle = false);

        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource stop = new();
        private readonly List<TcpClient> clients = [];

        public FakeProxy(Func<string, Reply> answer)
        {
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            _ = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    TcpClient client;
                    try
                    {
                        client = await listener.AcceptTcpClientAsync(stop.Token);
                    }
                    catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException)
                    {
                        return;
                    }
                    lock (clients) clients.Add(client);
                    _ = ServeAsync(client, answer);
                }
            });
        }

        public int Port { get; }

        private async Task ServeAsync(TcpClient client, Func<string, Reply> answer)
        {
            try
            {
                var stream = client.GetStream();
                var head = new StringBuilder();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                for (string? line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(stop.Token));) head.Append(line).Append('\n');
                var reply = answer(head.ToString());
                var status = $"HTTP/1.1 {reply.Status} X\r\nContent-Length: {reply.Length ?? reply.Body}\r\nConnection: close\r\n\r\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes(status), stop.Token);
                var body = new byte[reply.Body];
                if (!reply.Trickle) await stream.WriteAsync(body, stop.Token);
                for (int at = 0; reply.Trickle && at < body.Length; at += 1024)
                {
                    await stream.WriteAsync(body.AsMemory(at, Math.Min(1024, body.Length - at)), stop.Token);
                    await Task.Delay(200, stop.Token);
                }
                if (!reply.KeepOpen) client.Dispose();
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            listener.Stop();
            lock (clients) foreach (var client in clients) client.Dispose();
        }
    }
}
