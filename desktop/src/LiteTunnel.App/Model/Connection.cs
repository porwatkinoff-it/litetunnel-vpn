using LiteTunnel.Core;

namespace LiteTunnel.App.Model;

public enum ConnectionStatus { Disconnected, Connecting, Connected, Reasserting, Disconnecting }

/// <summary>
/// The running core and, in proxy mode, the system proxy pointed at it. Every
/// method runs on the UI thread; one step at a time.
/// </summary>
public sealed class Connection
{
    /// <summary>What <see cref="StartAsync"/> returns when the user closed the password prompt: no error to show.</summary>
    public const string Cancelled = "";

    private readonly Store store;
    private readonly SystemProxy proxy;
    private readonly SemaphoreSlim gate = new(1, 1);
    private CancellationTokenSource? starting;
    private CoreProcess? core;
    private CoreElevation elevation;
    private ConnectionMode mode;
    private bool excludeLocal;
    private int proxyPort;
    private bool isGroup;
    private int groupGeneration;
    private DateTimeOffset lastGroupTest;

    public Connection(Store store, string singBox)
    {
        this.store = store;
        SingBox = singBox;
        proxy = SystemProxy.ForThisSystem(store.Root, Environment.ProcessPath);
        Log = new CoreLog(Path.Combine(store.Root, "core.log"));
    }

    public string SingBox { get; }

    public CoreLog Log { get; }

    public ConnectionStatus Status { get; private set; }

    public DateTimeOffset? ConnectedSince { get; private set; }

    /// <summary>The core's loopback status API while it runs.</summary>
    public StatusApi? Api { get; private set; }

    public bool IsActive => Status != ConnectionStatus.Disconnected;

    /// <summary>The mode the core runs in now; null when it does not run.</summary>
    public ConnectionMode? RunningMode => IsActive ? mode : null;

    public event Action? Changed;

    /// <summary>The core stopped on its own; the text says why.</summary>
    public event Action<string>? Failed;

    /// <summary>Puts the proxy settings back after a crash of the last run, if they were left changed.</summary>
    public void RestoreLeftovers()
    {
        if (proxy.HasBackup) proxy.Restore();
    }

    /// <summary>Writes the core config: with this run's ports while it runs.</summary>
    public bool WriteConfig(AppState state) =>
        IsActive && Api is not null && mode == state.Settings.Mode
            ? store.WriteConfig(state, mode == ConnectionMode.SystemProxy ? proxyPort : null, Api)
            : store.WriteConfig(state);

    /// <summary>Starts the core. Null when it runs; otherwise the error, or <see cref="Cancelled"/>.</summary>
    public async Task<string?> StartAsync(AppState state, CoreElevation tunElevation)
    {
        await gate.WaitAsync();
        try
        {
            if (IsActive) return null;
            return await StartLockedAsync(state, tunElevation);
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<string?> StartLockedAsync(AppState state, CoreElevation tunElevation)
    {
        var settings = state.Settings;
        if (settings.Mode == ConnectionMode.SystemProxy && proxy.Unsupported is { } unsupported) return unsupported;
        RestoreLeftovers();

        mode = settings.Mode;
        elevation = mode == ConnectionMode.Tun ? tunElevation : CoreElevation.None;
        excludeLocal = settings.ExcludeLocalNetworks;
        proxyPort = LocalPorts.IsFree(settings.LocalProxyPort) ? settings.LocalProxyPort : LocalPorts.Free(1)[0];
        var api = StatusApi.Create(LocalPorts.Free(1)[0]);
        Api = api;
        if (!store.WriteConfig(state, mode == ConnectionMode.SystemProxy ? proxyPort : null, api))
        {
            Api = null;
            return "Сначала выберите сервер";
        }

        Log.Clear();
        SetStatus(ConnectionStatus.Connecting);
        using var cancel = new CancellationTokenSource();
        starting = cancel;
        try
        {
            if (await LaunchAsync(cancel.Token) is { } error)
            {
                Reset();
                return error;
            }
            if (mode == ConnectionMode.SystemProxy)
            {
                try
                {
                    proxy.Enable(proxyPort, excludeLocal);
                }
                catch (SystemProxyException e)
                {
                    await StopCoreAsync();
                    Reset();
                    return e.Message;
                }
            }
        }
        finally
        {
            starting = null;
        }
        ConnectedSince = DateTimeOffset.Now;
        SetStatus(ConnectionStatus.Connected);
        GroupChanged(state);
        return null;
    }

    /// <summary>
    /// The core's first test of a group can run before the network is up: then
    /// every member fails and the first one stays picked until the next test.
    /// So test again a moment after each start and reload.
    /// </summary>
    private void GroupChanged(AppState state)
    {
        isGroup = state.SelectedServer?.IsGroup == true;
        int generation = ++groupGeneration;
        if (!isGroup) return;
        foreach (int seconds in new[] { 3, 20 }) _ = TestGroupAfterAsync(TimeSpan.FromSeconds(seconds), generation);
    }

    private async Task TestGroupAfterAsync(TimeSpan delay, int generation)
    {
        await Task.Delay(delay);
        if (generation == groupGeneration) TestGroup();
    }

    /// <summary>The window came back: test the group again, once a minute at most.</summary>
    public void RefreshGroup()
    {
        if (DateTimeOffset.UtcNow - lastGroupTest > TimeSpan.FromMinutes(1)) TestGroup();
    }

    private async void TestGroup()
    {
        if (!isGroup || Status != ConnectionStatus.Connected || Api is not { } api) return;
        lastGroupTest = DateTimeOffset.UtcNow;
        using var http = StatusApi.Client();
        await api.TestGroupAsync(http, ConfigBuilder.ProxyTag);
        GroupTested?.Invoke();
    }

    /// <summary>The core tested the group anew: its pick may have changed.</summary>
    public event Action? GroupTested;

    /// <summary>Starts the process and waits until it listens. The error otherwise; the core is stopped then.</summary>
    private async Task<string?> LaunchAsync(CancellationToken cancellation)
    {
        var workDir = Path.Combine(store.WorkDirectory, mode == ConnectionMode.Tun ? "tunnel" : "proxy");
        Directory.CreateDirectory(workDir);
        CoreProcess started;
        try
        {
            started = CoreProcess.Start(SingBox, store.ConfigFile, workDir, elevation, Log);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return $"Не удалось запустить ядро: {e.Message}";
        }
        core = started;
        List<int> ports = mode == ConnectionMode.SystemProxy ? [proxyPort, Api!.Port] : [Api!.Port];
        // pkexec waits for the password first.
        var timeout = TimeSpan.FromSeconds(elevation == CoreElevation.Pkexec ? 120 : 15);
        string? error;
        try
        {
            error = await LocalPorts.WaitAsync(started, ports, timeout, cancellation);
            if (error is null)
            {
                // A core that dies on a bad outbound does so right after it opens its ports.
                await Task.Delay(500, cancellation);
                if (started.HasExited) error = LocalPorts.FirstError(started.LastOutput) ?? $"ядро завершилось с кодом {started.Exited.Result}";
            }
        }
        catch (OperationCanceledException)
        {
            error = Cancelled;
        }
        if (error is not null)
        {
            if (elevation == CoreElevation.Pkexec && started.HasExited && CoreProcess.IsPkexecRefusal(started.Exited.Result)) error = Cancelled;
            await StopCoreAsync();
            return error;
        }
        _ = WatchAsync(started);
        return null;
    }

    private async Task WatchAsync(CoreProcess watched)
    {
        int code = await watched.Exited;
        if (watched != core || Status is ConnectionStatus.Disconnecting or ConnectionStatus.Reasserting) return;
        if (mode == ConnectionMode.SystemProxy) proxy.Restore();
        core = null;
        watched.Dispose();
        Reset();
        Failed?.Invoke(LocalPorts.FirstError(watched.LastOutput) is { } line ? $"Ядро остановилось: {line}" : $"Ядро остановилось (код {code})");
    }

    public async Task StopAsync()
    {
        starting?.Cancel();
        await gate.WaitAsync();
        try
        {
            if (!IsActive) return;
            SetStatus(ConnectionStatus.Disconnecting);
            if (mode == ConnectionMode.SystemProxy)
            {
                try { proxy.Restore(); }
                catch (SystemProxyException) { }
            }
            await StopCoreAsync();
            Reset();
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Applies a new config to the running core: Linux reads it again on a
    /// signal, Windows starts the core anew. The mode stays: changing it
    /// takes a new connection. The error, when the core did not come back.
    /// </summary>
    public async Task<string?> ReloadAsync(AppState state)
    {
        await gate.WaitAsync();
        try
        {
            if (Status != ConnectionStatus.Connected) return null;
            var settings = state.Settings;
            if (mode == ConnectionMode.SystemProxy && settings.ExcludeLocalNetworks != excludeLocal)
            {
                excludeLocal = settings.ExcludeLocalNetworks;
                try { proxy.Enable(proxyPort, excludeLocal); }
                catch (SystemProxyException e) { return e.Message; }
            }
            if (core is { } running && running.Reload())
            {
                GroupChanged(state);
                return null;
            }

            SetStatus(ConnectionStatus.Reasserting);
            await StopCoreAsync();
            if (await LaunchAsync(CancellationToken.None) is { } error)
            {
                if (mode == ConnectionMode.SystemProxy) proxy.Restore();
                Reset();
                return error.Length == 0 ? null : error;
            }
            SetStatus(ConnectionStatus.Connected);
            GroupChanged(state);
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// The system is logging off or shutting down: no time to wait. Puts the
    /// proxy back and kills the core, right away.
    /// </summary>
    public void ShutdownNow()
    {
        starting?.Cancel();
        if (!IsActive) return;
        if (mode == ConnectionMode.SystemProxy)
        {
            try { proxy.Restore(); }
            catch (SystemProxyException) { }
        }
        core?.Dispose();
        core = null;
        Reset();
    }

    private async Task StopCoreAsync()
    {
        if (core is not { } running) return;
        core = null;
        await running.StopAsync(TimeSpan.FromSeconds(3));
        running.Dispose();
    }

    private void Reset()
    {
        isGroup = false;
        groupGeneration++;
        Api = null;
        ConnectedSince = null;
        SetStatus(ConnectionStatus.Disconnected);
    }

    private void SetStatus(ConnectionStatus status)
    {
        if (Status == status) return;
        Status = status;
        Changed?.Invoke();
    }
}
