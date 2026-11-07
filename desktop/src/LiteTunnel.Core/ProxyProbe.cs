using System.ComponentModel;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>
/// Ping through the servers themselves: a second, short-lived core with a
/// local proxy port per server. Each one gets the request to the test address
/// and the check download through it (<see cref="Ping.ThroughServerAsync"/>),
/// so a time means the server passes traffic, which a handshake cannot show.
/// </summary>
public sealed class ProxyProbe(string singBoxPath, string workDirectory)
{
    public const int Concurrency = 8;

    /// <summary>
    /// Calls <paramref name="onResult"/> once per server, as soon as all of
    /// its members are measured. A server the core does not accept is
    /// «нет связи», and the core starts with the rest. Returns an error when
    /// the probe core does not start even so; then nothing was measured.
    /// </summary>
    /// <param name="direct">What TCP and ICMP measure a working server with; null for the other modes.</param>
    /// <param name="bindInterface">The physical interface while the tunnel is up, so the probes do not go through it.</param>
    /// <param name="checkUrl">What the check downloads; <see cref="Ping.CheckUrl"/> when null.</param>
    public async Task<string?> MeasureAllAsync(
        IReadOnlyList<Server> servers,
        AppSettings settings,
        Action<Guid, PingResult?> onResult,
        IPinger? direct = null,
        string? bindInterface = null,
        Uri? checkUrl = null,
        CancellationToken cancellation = default)
    {
        var targets = new List<Server>();
        var owners = new List<int>();
        for (int i = 0; i < servers.Count; i++)
        {
            foreach (var target in servers[i].Members.Count > 0 ? servers[i].Members : [servers[i]])
            {
                targets.Add(target);
                owners.Add(i);
            }
        }
        if (targets.Count == 0) return null;
        Directory.CreateDirectory(workDirectory);

        // Indexes into targets of the servers the core runs.
        var live = Enumerable.Range(0, targets.Count).ToList();
        var (running, error) = await StartAsync(targets, settings, bindInterface, cancellation).ConfigureAwait(false);
        if (error is not null)
        {
            var broken = await BrokenAsync(targets, settings, bindInterface, cancellation).ConfigureAwait(false);
            if (broken.Count == 0) return error;
            live = live.Where(i => !broken.Contains(i)).ToList();
            if (live.Count > 0)
            {
                (running, error) = await StartAsync(live.Select(i => targets[i]).ToList(), settings, bindInterface, cancellation).ConfigureAwait(false);
                if (error is not null) return error;
            }
        }

        await using (running)
        {
            var portOf = new int?[targets.Count];
            for (int k = 0; k < live.Count && running is not null; k++) portOf[live[k]] = running.Ports[k];
            var url = Ping.TestUrl(settings.PingUrl);
            var results = new PingResult?[targets.Count];
            var left = new int[servers.Count];
            foreach (var owner in owners) left[owner]++;
            var gate = new object();
            using var slots = new SemaphoreSlim(Concurrency);
            await Task.WhenAll(Enumerable.Range(0, targets.Count).Select(async t =>
            {
                PingResult result = PingResult.Failed;
                if (portOf[t] is { } port)
                {
                    await slots.WaitAsync(cancellation).ConfigureAwait(false);
                    try
                    {
                        result = await Ping.ThroughServerAsync(targets[t], port, url, settings.PingMode, direct, checkUrl, cancellation: cancellation).ConfigureAwait(false);
                    }
                    finally
                    {
                        slots.Release();
                    }
                }
                int owner = owners[t];
                PingResult? fastest;
                lock (gate)
                {
                    results[t] = result;
                    if (--left[owner] > 0) return;
                    fastest = Ping.Fastest(Enumerable.Range(0, targets.Count).Where(i => owners[i] == owner).Select(i => results[i]));
                }
                onResult(servers[owner].Id, fastest);
            })).ConfigureAwait(false);
        }
        return null;
    }

    private Json Config(IReadOnlyList<Server> targets, IReadOnlyList<int> ports, AppSettings settings, string? bindInterface)
    {
        var config = ConfigBuilder.Probe(targets, ports, settings);
        if (bindInterface is not null && config["route"] is { } route)
            config = config.With("route", route.With("default_interface", bindInterface));
        return config;
    }

    private async Task<(Running? Running, string? Error)> StartAsync(IReadOnlyList<Server> targets, AppSettings settings, string? bindInterface, CancellationToken cancellation)
    {
        var ports = LocalPorts.Free(targets.Count);
        var configPath = System.IO.Path.Combine(workDirectory, "probe.json");
        await WriteAsync(configPath, Config(targets, ports, settings, bindInterface), cancellation).ConfigureAwait(false);
        CoreProcess core;
        try
        {
            core = CoreProcess.Start(singBoxPath, configPath, workDirectory);
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            Delete(configPath);
            return (null, e.Message);
        }
        var running = new Running(core, ports, configPath);
        string? error;
        try
        {
            error = await LocalPorts.WaitAsync(core, ports, TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false);
        }
        catch
        {
            await running.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        if (error is null) return (running, null);
        await running.DisposeAsync().ConfigureAwait(false);
        return (null, error);
    }

    /// <summary>
    /// The targets the core does not accept, each checked alone. Empty when
    /// it does not accept even a config without servers: then the trouble is
    /// not in them, and every server would fail the same way.
    /// </summary>
    private async Task<HashSet<int>> BrokenAsync(IReadOnlyList<Server> targets, AppSettings settings, string? bindInterface, CancellationToken cancellation)
    {
        var broken = new HashSet<int>();
        if (!await AcceptsAsync([], "probe-check.json", settings, bindInterface, cancellation).ConfigureAwait(false)) return broken;
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(Enumerable.Range(0, targets.Count).Select(async i =>
        {
            await slots.WaitAsync(cancellation).ConfigureAwait(false);
            try
            {
                if (!await AcceptsAsync([targets[i]], $"probe-check-{i}.json", settings, bindInterface, cancellation).ConfigureAwait(false))
                    lock (broken) broken.Add(i);
            }
            finally
            {
                slots.Release();
            }
        })).ConfigureAwait(false);
        return broken;
    }

    private async Task<bool> AcceptsAsync(IReadOnlyList<Server> targets, string name, AppSettings settings, string? bindInterface, CancellationToken cancellation)
    {
        var path = System.IO.Path.Combine(workDirectory, name);
        await WriteAsync(path, Config(targets, LocalPorts.Free(targets.Count), settings, bindInterface), cancellation).ConfigureAwait(false);
        try
        {
            return await CoreProcess.CheckAsync(singBoxPath, path, workDirectory, TimeSpan.FromSeconds(10), cancellation).ConfigureAwait(false);
        }
        catch (Exception e) when (e is Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
        finally
        {
            Delete(path);
        }
    }

    /// <summary>UTF-8 without a byte order mark, which the core's JSON reader does not take.</summary>
    private static Task WriteAsync(string path, Json config, CancellationToken cancellation) =>
        File.WriteAllBytesAsync(path, Encoding.UTF8.GetBytes(config.ToJsonString()), cancellation);

    private static void Delete(string path)
    {
        try { File.Delete(path); } catch (IOException) { }
    }

    /// <summary>The probe core and the ports it listens on, in the order of its servers.</summary>
    private sealed class Running(CoreProcess core, IReadOnlyList<int> ports, string configPath) : IAsyncDisposable
    {
        public IReadOnlyList<int> Ports => ports;

        public async ValueTask DisposeAsync()
        {
            await core.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            core.Dispose();
            Delete(configPath);
        }
    }
}
