using Avalonia.Threading;
using LiteTunnel.App.ViewModels;
using LiteTunnel.Core;

namespace LiteTunnel.App.Model;

public sealed record Banner(string Text, bool IsError);

/// <summary>
/// The app's state and everything that changes it, as in the phone and Mac
/// apps: every change is saved at once, and a config change reaches the
/// running core. Lives on the UI thread.
/// </summary>
public sealed class AppModel : Observable
{
    private readonly SubscriptionClient client;
    private readonly RuleSetUpdater ruleSets = new();
    private readonly HashSet<Guid> refreshing = [];
    private Banner? banner;
    private bool pinging;
    private Guid? pingingSubscription;
    private bool updatingRuleSets;
    private bool activating;
    private int bannerId;

    public AppModel(Store store, string singBox)
    {
        Store = store;
        try { store.PrepareDirectories(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        State = store.LoadState();
        client = new SubscriptionClient(DeviceInfo.Detect(store.Root).Headers);
        Connection = new Connection(store, singBox);
        Connection.Failed += text => Show(text, error: true);
    }

    public Store Store { get; }

    public Connection Connection { get; }

    public AppState State { get; private set; }

    public AppSettings Settings => State.Settings;

    /// <summary>After every change of <see cref="State"/>.</summary>
    public event Action? StateChanged;

    /// <summary>The app has to quit: an elevated copy takes over.</summary>
    public event Action? QuitRequested;

    public Banner? Banner
    {
        get => banner;
        private set => Set(ref banner, value);
    }

    public bool Pinging
    {
        get => pinging;
        private set => Set(ref pinging, value);
    }

    public Guid? PingingSubscription
    {
        get => pingingSubscription;
        private set => Set(ref pingingSubscription, value);
    }

    public bool UpdatingRuleSets
    {
        get => updatingRuleSets;
        private set => Set(ref updatingRuleSets, value);
    }

    public bool IsRefreshing(Guid id) => refreshing.Contains(id);

    public void Show(string text, bool error = false)
    {
        var shown = new Banner(text, error);
        Banner = shown;
        int id = ++bannerId;
        var delay = TimeSpan.FromSeconds(error && text.Length > 60 ? 7 : 3.5);
        DispatcherTimer.RunOnce(() =>
        {
            if (id == bannerId) Banner = null;
        }, delay);
    }

    public void HideBanner() => Banner = null;

    // MARK: saving

    private void Commit(Func<AppState, AppState> change)
    {
        State = change(State);
        Persist();
        StateChanged?.Invoke();
        ApplyConfig();
    }

    private void Persist()
    {
        try
        {
            Store.Save(State);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Не удалось сохранить: {e.Message}", error: true);
        }
    }

    private void ApplyConfig()
    {
        var before = Store.ReadConfig();
        try
        {
            Connection.WriteConfig(State);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Show($"Не удалось записать конфигурацию: {e.Message}", error: true);
            return;
        }
        var after = Store.ReadConfig();
        if (before == after || Connection.Status != ConnectionStatus.Connected) return;
        if (after is null)
        {
            // No server left to run.
            _ = Connection.StopAsync();
            return;
        }
        if (Connection.RunningMode != Settings.Mode)
        {
            _ = ReconnectAsync();
            return;
        }
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        if (await Connection.ReloadAsync(State) is { } problem) Show(problem, error: true);
    }

    private async Task ReconnectAsync()
    {
        await Connection.StopAsync();
        await ConnectAsync();
    }

    // MARK: connection

    public async Task ToggleConnectionAsync()
    {
        if (Connection.IsActive)
        {
            await Connection.StopAsync();
            return;
        }
        await ConnectAsync();
    }

    public async Task ConnectAsync()
    {
        if (State.SelectedServer is null)
        {
            Show("Сначала выберите сервер", error: true);
            return;
        }
        var elevation = CoreElevation.None;
        if (Settings.Mode == ConnectionMode.Tun)
        {
            switch (Elevation.ForTun(Connection.SingBox))
            {
                case TunAccess.Ready ready:
                    elevation = ready.Elevation;
                    break;
                case TunAccess.NeedsAdministrator:
                    RelaunchAsAdministrator();
                    return;
                case TunAccess.Unavailable unavailable:
                    Show(unavailable.Reason, error: true);
                    return;
            }
        }
        if (await Connection.StartAsync(State, elevation) is { Length: > 0 } error) Show(error, error: true);
    }

    /// <summary>Windows: TUN needs an administrator. A copy started that way connects once this one has quit.</summary>
    private void RelaunchAsAdministrator()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            Show("Для режима VPN запустите приложение от имени администратора", error: true);
            return;
        }
        if (Elevation.RelaunchAsAdministrator(exe, [StartOptions.After, Environment.ProcessId.ToString(), StartOptions.Connect]))
            QuitRequested?.Invoke();
        else
            Show("Для режима VPN нужны права администратора", error: true);
    }

    public void Select(Guid id)
    {
        if (State.SelectedServerId == id) return;
        Commit(s => s with { SelectedServerId = id });
    }

    // MARK: settings

    public void SetSettings(AppSettings next)
    {
        var old = Settings;
        if (old == next) return;
        Commit(s => s with { Settings = next });
        if (!RuleSets.Needed(old).SequenceEqual(RuleSets.Needed(next))) _ = UpdateRuleSetsAsync(force: false);
    }

    public void ChangeSettings(Func<AppSettings, AppSettings> change) => SetSettings(change(Settings));

    // MARK: import

    /// <summary>Pasted text, a scanned QR code or an opened link. True when something was added.</summary>
    public Task<bool> ImportTextAsync(string text) => HandleAsync(Importer.Classify(text));

    public Task<bool> OpenLinkAsync(string link) => HandleAsync(Importer.DeepLink(link));

    private async Task<bool> HandleAsync(ImportItem item)
    {
        switch (item)
        {
            case ImportItem.SubscriptionLink link:
                if (State.Subscriptions.FirstOrDefault(s => s.Url == link.Url) is { } existing)
                {
                    Show("Эта подписка уже добавлена, обновляю");
                    await RefreshAsync(existing.Id);
                    return true;
                }
                var host = Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) ? uri.Host : null;
                var sub = new Subscription { Name = link.Name ?? (string.IsNullOrEmpty(host) ? "Подписка" : host), Url = link.Url };
                Commit(s => s with { Subscriptions = [.. s.Subscriptions, sub] });
                await RefreshAsync(sub.Id);
                return true;
            case ImportItem.Servers servers:
                IReadOnlyList<Server> added = [];
                Commit(s =>
                {
                    var (next, list) = s.AddManual(servers.Parsed);
                    added = list;
                    return next;
                });
                var text = added.Count == 0 ? "Эти серверы уже есть" : $"Добавлено серверов: {added.Count}";
                if (servers.Skipped.Count > 0) text += $", пропущено: {servers.Skipped.Count}";
                Show(text, error: added.Count == 0);
                return added.Count > 0;
            case ImportItem.Nothing nothing:
                Show(nothing.Reason, error: true);
                return false;
        }
        return false;
    }

    // MARK: servers and subscriptions

    public void RemoveServer(Guid id) => Commit(s => s.RemoveServer(id));

    public void RemoveSubscription(Guid id) => Commit(s => s.RemoveSubscription(id));

    public void UpdateSubscription(Subscription sub) => Commit(s => s.UpdateSubscription(sub.Id, _ => sub));

    /// <summary>Moves a server one place up or down within its own block.</summary>
    public void MoveServer(Guid id, int offset) => Commit(state =>
    {
        if (state.Servers.FirstOrDefault(s => s.Id == id) is not { } server) return state;
        var block = state.ServersOf(server.SubscriptionId).ToList();
        int from = block.FindIndex(s => s.Id == id), to = from + offset;
        if (to < 0 || to >= block.Count) return state;
        (block[from], block[to]) = (block[to], block[from]);
        using var next = block.GetEnumerator();
        var servers = state.Servers.Select(s => s.SubscriptionId == server.SubscriptionId && next.MoveNext() ? next.Current : s).ToList();
        return state with { Servers = servers };
    });

    /// <param name="ping">False lets a caller that pings everything next skip the per-subscription ping.</param>
    public async Task RefreshAsync(Guid id, bool ping = true)
    {
        if (State.Subscriptions.FirstOrDefault(s => s.Id == id) is not { } sub || !refreshing.Add(id)) return;
        StateChanged?.Invoke();
        try
        {
            var result = await client.FetchAsync(sub);
            refreshing.Remove(id);
            Commit(s => s.Apply(result, id));
            if (ping && Settings.PingOnUpdate) await PingSubscriptionAsync(id);
        }
        catch (Exception e) when (e is SubscriptionException or IOException)
        {
            refreshing.Remove(id);
            Commit(s => s.UpdateSubscription(id, x => x with { LastError = e.Message }));
            Show($"{sub.Name}: {e.Message}", error: true);
        }
        finally
        {
            refreshing.Remove(id);
            StateChanged?.Invoke();
        }
    }

    /// <summary>Subscriptions whose interval has passed. Only at launch and when the window comes back: no timers.</summary>
    public async Task RefreshDueAsync(bool ping = true)
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var id in State.Subscriptions.Where(s => s.IsDue(now)).Select(s => s.Id).ToList()) await RefreshAsync(id, ping);
    }

    // MARK: ping

    public async Task PingAsync(IReadOnlyList<Server> servers)
    {
        if (Pinging || servers.Count == 0) return;
        Pinging = true;
        try
        {
            var settings = Settings;
            void OnResult(Guid id, PingResult? result) => Dispatcher.UIThread.Post(() => SetPing(id, result));
            // While the tunnel is up, probes go around it, not through it.
            var tunnel = Connection.RunningMode == ConnectionMode.Tun ? new PhysicalNetwork(ConfigBuilder.TunInterfaceName) : null;
            if (settings.PingMode.ThroughProxy())
            {
                var probe = new ProxyProbe(Connection.SingBox, Path.Combine(Store.WorkDirectory, "probe"));
                if (await Task.Run(() => probe.MeasureAllAsync(servers, settings, OnResult, tunnel?.InterfaceName)) is { } error)
                {
                    Show($"Пинг через сервер не запустился: {error}. Показан TCP.", error: true);
                    await Task.Run(() => Ping.MeasureAllAsync(servers, tunnel is null ? SocketPinger.Default : new SocketPinger(tunnel), OnResult));
                }
            }
            else
            {
                IPinger pinger = settings.PingMode == PingMode.Icmp
                    ? new IcmpPinger(tunnel ?? IProbeNetwork.Default)
                    : tunnel is null ? SocketPinger.Default : new SocketPinger(tunnel);
                await Task.Run(() => Ping.MeasureAllAsync(servers, pinger, OnResult));
            }
            // Results posted from the workers arrive before this continuation.
            await Dispatcher.UIThread.InvokeAsync(Persist, DispatcherPriority.Background);
        }
        finally
        {
            Pinging = false;
        }
    }

    public Task PingAllAsync() => PingAsync(State.Servers);

    public async Task PingSubscriptionAsync(Guid? id)
    {
        if (Pinging) return;
        PingingSubscription = id;
        try { await PingAsync(State.ServersOf(id)); }
        finally { PingingSubscription = null; }
    }

    private void SetPing(Guid id, PingResult? result)
    {
        if (State.Servers.All(s => s.Id != id)) return;
        State = State.UpdateServer(id, s => s with { LastPing = result });
        StateChanged?.Invoke();
    }

    // MARK: rule sets

    public async Task UpdateRuleSetsAsync(bool force)
    {
        var tags = RuleSets.Needed(Settings);
        if (tags.Count == 0 || UpdatingRuleSets) return;
        if (!force && !ruleSets.IsDue(tags, Store.RuleSetDirectory)) return;
        UpdatingRuleSets = true;
        try
        {
            var failed = await Task.Run(() => ruleSets.UpdateAsync(tags, Store.RuleSetDirectory));
            ApplyConfig();
            if (failed.Count > 0)
                Show($"Не удалось скачать базы: {string.Join(", ", failed)}", error: true);
            else if (force)
                Show("Базы маршрутизации обновлены");
        }
        finally
        {
            UpdatingRuleSets = false;
            StateChanged?.Invoke();
        }
    }

    public bool RuleSetsReady => RuleSets.Needed(Settings).All(RuleSets.Available(Store.RuleSetDirectory).Contains);

    // MARK: lifecycle

    /// <summary>At launch and when the window comes back from the tray.</summary>
    public async Task DidBecomeActiveAsync()
    {
        if (activating) return;
        activating = true;
        try
        {
            // Pinging everything right after covers the updated ones too.
            await RefreshDueAsync(ping: !Settings.PingOnOpen);
            await UpdateRuleSetsAsync(force: false);
            if (Settings.PingOnOpen) await PingAllAsync();
        }
        finally
        {
            activating = false;
        }
    }
}
