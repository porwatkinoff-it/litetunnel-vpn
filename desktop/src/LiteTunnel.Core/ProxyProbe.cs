using System.Net;
using System.Net.Sockets;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>
/// Ping through the servers themselves: a second, short-lived core with a
/// local proxy port per server, and one request to the test address through
/// each. Shows that a server passes traffic, which a handshake cannot.
/// </summary>
public sealed class ProxyProbe(string singBoxPath, string workDirectory)
{
    public const int Concurrency = 8;

    /// <summary>
    /// Calls <paramref name="onResult"/> once per server. Returns an error when
    /// the probe core does not start; then nothing was measured.
    /// </summary>
    /// <param name="bindInterface">The physical interface while the tunnel is up, so the probes do not go through it.</param>
    public async Task<string?> MeasureAllAsync(
        IReadOnlyList<Server> servers,
        AppSettings settings,
        Action<Guid, PingResult?> onResult,
        string? bindInterface = null,
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

        var ports = LocalPorts.Free(targets.Count);
        var config = ConfigBuilder.Probe(targets, ports, settings);
        if (bindInterface is not null && config["route"] is { } route)
            config = config.With("route", route.With("default_interface", bindInterface));
        Directory.CreateDirectory(workDirectory);
        var configPath = System.IO.Path.Combine(workDirectory, "probe.json");
        await File.WriteAllTextAsync(configPath, config.ToJsonString(), Encoding.UTF8, cancellation).ConfigureAwait(false);

        CoreProcess core;
        try
        {
            core = CoreProcess.Start(singBoxPath, configPath, workDirectory);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return e.Message;
        }
        try
        {
            if (await LocalPorts.WaitAsync(core, ports, TimeSpan.FromSeconds(5), cancellation).ConfigureAwait(false) is { } error) return error;

            var url = Ping.TestUrl(settings.PingUrl);
            bool head = settings.PingMode == PingMode.ProxyHead;
            using var slots = new SemaphoreSlim(Concurrency);
            var times = await Task.WhenAll(targets.Select(async (_, index) =>
            {
                await slots.WaitAsync(cancellation).ConfigureAwait(false);
                try
                {
                    return await Ping.RequestAsync(url, ports[index], head, cancellation: cancellation).ConfigureAwait(false);
                }
                finally
                {
                    slots.Release();
                }
            })).ConfigureAwait(false);

            for (int i = 0; i < servers.Count; i++)
            {
                var results = Enumerable.Range(0, targets.Count).Where(t => owners[t] == i).Select(t => (PingResult?)Ping.Result(times[t], settings.PingMode));
                onResult(servers[i].Id, Ping.Fastest(results));
            }
            return null;
        }
        finally
        {
            await core.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            core.Dispose();
            try { File.Delete(configPath); } catch (IOException) { }
        }
    }
}
