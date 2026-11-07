using LiteTunnel.Core;

namespace LiteTunnel.Core.Tests;

/// <summary>
/// The whole check through a stand-in for sing-box: it listens on the probe
/// ports and answers as the server behind each would. ok.example.com works,
/// stall.example.com freezes after 1 KB, and the core refuses
/// bad.example.com, as it does a link it cannot take.
/// </summary>
public sealed class ProxyProbeTests : IDisposable
{
    private const string FakeCore = """
        #!/usr/bin/env python3
        import json, os, socket, sys, threading, time
        command, path, work = sys.argv[1], sys.argv[sys.argv.index("-c") + 1], sys.argv[sys.argv.index("-D") + 1]
        config = json.load(open(path))
        hosts = {o["tag"]: o.get("server") for o in config["outbounds"]}
        broken = os.path.exists(os.path.join(work, "broken-core")) or "bad.example.com" in hosts.values()
        if command == "check":
            sys.exit(1 if broken else 0)
        if broken:
            print("FATAL[0000] start service: outbound refused", flush=True)
            sys.exit(1)
        routes = {r["inbound"][0]: r["outbound"] for r in config["route"]["rules"]}

        def serve(connection, host):
            data = b""
            while b"\r\n\r\n" not in data:
                chunk = connection.recv(4096)
                if not chunk:
                    return
                data += chunk
            if b"generate_204" in data.split(b"\r\n")[0]:
                connection.sendall(b"HTTP/1.1 204 No Content\r\nContent-Length: 0\r\nConnection: close\r\n\r\n")
            elif host == "stall.example.com":
                connection.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 65536\r\nConnection: close\r\n\r\n" + bytes(1024))
                time.sleep(30)
            else:
                connection.sendall(b"HTTP/1.1 206 Partial Content\r\nContent-Length: 65536\r\nConnection: close\r\n\r\n" + bytes(65536))
            connection.close()

        def listen(inbound):
            host = hosts[routes[inbound["tag"]]]
            listener = socket.socket()
            listener.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
            listener.bind(("127.0.0.1", inbound["listen_port"]))
            listener.listen(16)
            while True:
                connection, _ = listener.accept()
                threading.Thread(target=serve, args=(connection, host), daemon=True).start()

        for inbound in config["inbounds"]:
            threading.Thread(target=listen, args=(inbound,), daemon=True).start()
        while True:
            time.sleep(1)
        """;

    private readonly string dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    private readonly string core;

    public ProxyProbeTests()
    {
        Directory.CreateDirectory(dir);
        core = Path.Combine(dir, "fake-sing-box");
        File.WriteAllText(core, FakeCore.ReplaceLineEndings("\n") + "\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(core, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public void Dispose() => Directory.Delete(dir, recursive: true);

    private static Server Server(string host) => new(null, ShareLink.Parse($"trojan://pw@{host}:443#{host}"));

    private static readonly Uri File64 = new("http://check.invalid/file.bin");

    [Fact]
    public async Task OnlyWorkingServersGetATime()
    {
        if (OperatingSystem.IsWindows()) return;
        var ok = Server("ok.example.com");
        var stall = Server("stall.example.com");
        var bad = Server("bad.example.com");
        var mixed = new Server(null, ServerGroup.Make([ShareLink.Parse("trojan://pw@bad.example.com:443#b"), ShareLink.Parse("trojan://pw@ok.example.com:443#o")], "Группа"));
        var results = new Dictionary<Guid, PingResult?>();

        var probe = new ProxyProbe(core, Path.Combine(dir, "work"));
        var error = await probe.MeasureAllAsync([ok, stall, bad, mixed], new AppSettings(), (id, r) => { lock (results) results[id] = r; }, checkUrl: File64);

        Assert.Null(error);
        Assert.IsType<PingMs>(results[ok.Id]);
        Assert.Equal(PingResult.Failed, results[stall.Id]);
        Assert.Equal(PingResult.Failed, results[bad.Id]);
        Assert.IsType<PingMs>(results[mixed.Id]);
        Assert.Empty(Directory.GetFiles(Path.Combine(dir, "work"), "probe*.json"));
    }

    [Fact]
    public async Task ACoreThatDoesNotStartMeasuresNothing()
    {
        if (OperatingSystem.IsWindows()) return;
        var work = Path.Combine(dir, "work");
        Directory.CreateDirectory(work);
        File.WriteAllText(Path.Combine(work, "broken-core"), "");
        var results = new Dictionary<Guid, PingResult?>();

        var probe = new ProxyProbe(core, work);
        var error = await probe.MeasureAllAsync([Server("ok.example.com")], new AppSettings(), (id, r) => results[id] = r, checkUrl: File64);

        Assert.Equal("start service: outbound refused", error);
        Assert.Empty(results);
    }
}
