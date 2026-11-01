using System.Net;
using System.Net.Sockets;
using System.Text;
using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

public sealed class CoreProcessTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

    public CoreProcessTests() => Directory.CreateDirectory(dir);

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static async Task Until(Func<bool> condition, string what)
    {
        for (int i = 0; i < 100 && !condition(); i++) await Task.Delay(50);
        Assert.True(condition(), what);
    }

    /// <summary>The shell watchdog, with a stand-in for sing-box that reports the signals it gets.</summary>
    [Fact]
    public async Task WatchdogReloadsAndStopsTheCore()
    {
        if (OperatingSystem.IsWindows()) return;
        var fake = Path.Combine(dir, "fake-core");
        File.WriteAllText(fake, "#!/bin/sh\ntrap 'echo reloaded' HUP\ntrap 'echo stopped; exit 0' TERM\necho started\nwhile :; do sleep 0.05; done\n");
        File.SetUnixFileMode(fake, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);

        using var core = CoreProcess.Start(fake, Path.Combine(dir, "config.json"), Path.Combine(dir, "work"));
        await Until(() => core.LastOutput.Contains("started"), "the core started");
        Assert.True(core.Reload());
        await Until(() => core.LastOutput.Contains("reloaded"), "HUP reached the core");
        Assert.False(core.HasExited);

        await core.StopAsync(TimeSpan.FromSeconds(3));
        Assert.True(core.HasExited);
        Assert.Contains("stopped", core.LastOutput); // TERM, not a kill
        Assert.Equal(0, await core.Exited);
        Assert.False(core.Reload());
    }

    [Fact]
    public async Task StatusApiReadsTotalsAndSpeed()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var api = StatusApi.Create(((IPEndPoint)listener.LocalEndpoint).Port);
        var requests = new List<string>();
        async Task Serve(int count)
        {
            for (int n = 0; n < count; n++)
            {
                using var client = await listener.AcceptTcpClientAsync();
                var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
                var line = await reader.ReadLineAsync() ?? "";
                string? auth = null;
                while (await reader.ReadLineAsync() is { Length: > 0 } header)
                    if (header.StartsWith("Authorization:", StringComparison.OrdinalIgnoreCase)) auth = header[14..].Trim();
                lock (requests) requests.Add($"{line.Split(' ')[1]} {auth}");
                var body = line.Contains("/connections")
                    ? """{"downloadTotal":2048,"uploadTotal":512,"connections":[]}"""
                    : "{\"up\":10,\"down\":20}\n{\"up\":30,\"down\":40}\n";
                await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n{body}"));
            }
        }
        var server = Serve(2);
        try
        {
            using var http = StatusApi.Client();
            Assert.Equal((512L, 2048L), await api.TotalsAsync(http));
            var seconds = new List<(long, long)>();
            await api.WatchTrafficAsync(http, (up, down) => seconds.Add((up, down)), CancellationToken.None);
            Assert.Equal([(10L, 20L), (30L, 40L)], seconds);
            await server;
        }
        finally
        {
            listener.Stop();
        }
        Assert.Equal([$"/connections Bearer {api.Secret}", $"/traffic Bearer {api.Secret}"], requests);
        Assert.Null(await api.TotalsAsync(StatusApi.Client())); // nobody listens now
    }
}
