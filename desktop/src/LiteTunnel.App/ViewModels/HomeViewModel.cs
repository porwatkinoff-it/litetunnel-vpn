using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Threading;
using LiteTunnel.App.Model;
using LiteTunnel.Core;

namespace LiteTunnel.App.ViewModels;

/// <summary>
/// The connect card and the server list. The timer and the traffic stream
/// run only while this page is on screen and the tunnel is up.
/// </summary>
public sealed class HomeViewModel : PageViewModel
{
    private readonly ToolAction pingAll;
    private readonly Dictionary<Guid, SectionViewModel> sections = [];
    private readonly HashSet<Guid> folded;
    private readonly string foldFile;
    private DispatcherTimer? clock;
    private CancellationTokenSource? traffic;
    private bool onScreen;
    private long upSpeed, downSpeed, upTotal, downTotal;

    public HomeViewModel(MainViewModel main) : base(main)
    {
        pingAll = new ToolAction(Command.Async(Model.PingAllAsync, () => !Model.Pinging && Model.State.Servers.Count > 0), "Пинг всех", "bolt");
        Actions =
        [
            new ToolAction(Command.Of(OpenAdd), "Добавить", "add"),
            pingAll,
            new ToolAction(Command.Of(() => Main.Push(new SettingsViewModel(Main))), "Настройки", "settings"),
        ];
        Toggle = Command.Of(() => _ = Model.ToggleConnectionAsync(), () => Model.Connection.Status != ConnectionStatus.Disconnecting);
        Add = Command.Of(OpenAdd);
        foldFile = Path.Combine(Model.Store.Root, "home-folded.txt");
        folded = LoadFolded();
        Model.StateChanged += Rebuild;
        Model.PropertyChanged += OnModelChanged;
        Model.Connection.Changed += OnConnectionChanged;
        Rebuild();
    }

    public override string Title => "LiteTunnel";

    public override IReadOnlyList<ToolAction> Actions { get; }

    public ObservableCollection<SectionViewModel> Sections { get; } = [];

    public bool IsEmpty => Model.State.Servers.Count == 0 && Model.State.Subscriptions.Count == 0;

    public Command Add { get; }

    private void OpenAdd() => Main.Push(new AddViewModel(Main));

    // MARK: connect card

    public Command Toggle { get; }

    private ConnectionStatus Status => Model.Connection.Status;

    public bool IsOn => Status == ConnectionStatus.Connected;

    public bool IsBusy => Status is ConnectionStatus.Connecting or ConnectionStatus.Reasserting or ConnectionStatus.Disconnecting;

    public string PowerTip => Model.Connection.IsActive ? "Отключить VPN" : "Подключить VPN";

    public string StatusText => Status switch
    {
        ConnectionStatus.Connected => "Подключено",
        ConnectionStatus.Connecting => "Подключение…",
        ConnectionStatus.Reasserting => "Переподключение…",
        ConnectionStatus.Disconnecting => "Отключение…",
        _ => "Отключено",
    };

    public bool ShowElapsed => IsOn && Model.Connection.ConnectedSince is not null;

    public string Elapsed => Model.Connection.ConnectedSince is { } since ? Format.Duration(DateTimeOffset.Now - since) : "";

    public bool HasServer => Model.State.SelectedServer is not null;

    public string ServerName => Model.State.SelectedServer?.Name ?? "Сервер не выбран";

    public string ServerProto => Model.State.SelectedServer is { } server
        ? server.IsGroup ? server.Detail : server.Proto.Title()
        : "";

    public bool ShowTraffic => IsOn;

    public string UpSpeed => Format.Speed(upSpeed);

    public string DownSpeed => Format.Speed(downSpeed);

    public string UpTotal => Format.Size(upTotal);

    public string DownTotal => Format.Size(downTotal);

    private void RaiseCard()
    {
        Raise(nameof(IsOn));
        Raise(nameof(IsBusy));
        Raise(nameof(PowerTip));
        Raise(nameof(StatusText));
        Raise(nameof(ShowElapsed));
        Raise(nameof(Elapsed));
        Raise(nameof(HasServer));
        Raise(nameof(ServerName));
        Raise(nameof(ServerProto));
        Raise(nameof(ShowTraffic));
        Toggle.Refresh();
    }

    private void RaiseTraffic()
    {
        Raise(nameof(UpSpeed));
        Raise(nameof(DownSpeed));
        Raise(nameof(UpTotal));
        Raise(nameof(DownTotal));
    }

    private void OnConnectionChanged()
    {
        RaiseCard();
        UpdateLive();
    }

    public override void Appeared()
    {
        onScreen = true;
        RaiseCard();
        UpdateLive();
    }

    public override void Disappeared()
    {
        onScreen = false;
        UpdateLive();
    }

    /// <summary>The 1 s clock and the core's traffic stream, only while both are worth looking at.</summary>
    private void UpdateLive()
    {
        bool wanted = onScreen && IsOn && Model.Connection.Api is not null;
        if (wanted && clock is null)
        {
            clock = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => Raise(nameof(Elapsed)));
            clock.Start();
            WatchTraffic(Model.Connection.Api!);
        }
        else if (!wanted && clock is not null)
        {
            clock.Stop();
            clock = null;
            traffic?.Cancel();
            traffic = null;
        }
    }

    private async void WatchTraffic(StatusApi api)
    {
        var cancel = new CancellationTokenSource();
        traffic = cancel;
        upSpeed = downSpeed = 0;
        using var http = StatusApi.Client();
        if (await api.TotalsAsync(http, cancel.Token) is { } totals)
        {
            if (cancel.IsCancellationRequested) return;
            (upTotal, downTotal) = totals;
        }
        else
        {
            upTotal = downTotal = 0;
        }
        RaiseTraffic();
        await api.WatchTrafficAsync(http, (up, down) => Dispatcher.UIThread.Post(() =>
        {
            if (cancel.IsCancellationRequested) return;
            upSpeed = up;
            downSpeed = down;
            upTotal += up;
            downTotal += down;
            RaiseTraffic();
        }), cancel.Token);
    }

    // MARK: list

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(AppModel.Pinging):
                pingAll.IconName = Model.Pinging ? "bolt_fill" : "bolt";
                ((Command)pingAll.Command).Refresh();
                foreach (var section in Sections) section.RaiseBusy();
                break;
            case nameof(AppModel.PingingSubscription):
                foreach (var section in Sections) section.RaiseBusy();
                break;
        }
    }

    private void Rebuild()
    {
        var state = Model.State;
        var target = new List<SectionViewModel>();
        foreach (var sub in state.Subscriptions) target.Add(SectionFor(sub.Id));
        if (state.Servers.Any(s => s.SubscriptionId is null)) target.Add(SectionFor(null));
        foreach (var key in sections.Keys.Except(target.Select(s => s.Key)).ToList()) sections.Remove(key);
        Sync(Sections, target);
        foreach (var section in target) section.Update();
        Raise(nameof(IsEmpty));
        RaiseCard();
        ((Command)pingAll.Command).Refresh();
    }

    private SectionViewModel SectionFor(Guid? subscriptionId)
    {
        var key = subscriptionId ?? Guid.Empty;
        if (!sections.TryGetValue(key, out var section))
            sections[key] = section = new SectionViewModel(this, subscriptionId, folded.Contains(key));
        return section;
    }

    /// <summary>Makes <paramref name="list"/> hold <paramref name="target"/>, moving the rows it has rather than making them anew.</summary>
    internal static void Sync<T>(ObservableCollection<T> list, IReadOnlyList<T> target) where T : class
    {
        for (int i = 0; i < target.Count; i++)
        {
            if (i < list.Count && ReferenceEquals(list[i], target[i])) continue;
            int at = -1;
            for (int j = i + 1; j < list.Count; j++)
            {
                if (ReferenceEquals(list[j], target[i]))
                {
                    at = j;
                    break;
                }
            }
            if (at >= 0) list.Move(at, i);
            else list.Insert(i, target[i]);
        }
        while (list.Count > target.Count) list.RemoveAt(list.Count - 1);
    }

    // MARK: folding

    private HashSet<Guid> LoadFolded()
    {
        try
        {
            return File.Exists(foldFile)
                ? File.ReadAllLines(foldFile).Select(line => Guid.TryParse(line, out var id) ? id : (Guid?)null).OfType<Guid>().ToHashSet()
                : [];
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    internal void SetFolded(Guid key, bool value)
    {
        if (value ? !folded.Add(key) : !folded.Remove(key)) return;
        try
        {
            File.WriteAllLines(foldFile, folded.Select(id => id.ToString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>A subscription with its servers, or the servers added by hand.</summary>
public sealed class SectionViewModel : Observable
{
    private readonly HomeViewModel home;
    private readonly Dictionary<Guid, ServerRowViewModel> rows = [];
    private bool isFolded;
    private int count;
    private int serverCount;

    public SectionViewModel(HomeViewModel home, Guid? subscriptionId, bool folded)
    {
        this.home = home;
        SubscriptionId = subscriptionId;
        isFolded = folded && subscriptionId is not null;
        ToggleFold = Command.Of(() => IsFolded = !IsFolded);
        Ping = Command.Async(() => Model.PingSubscriptionAsync(SubscriptionId), () => !Model.Pinging && count > 0);
        Refresh = Command.Async(() => SubscriptionId is { } id ? Model.RefreshAsync(id) : Task.CompletedTask, () => !IsRefreshing);
        Edit = Command.Of(() =>
        {
            if (SubscriptionId is { } id) home.Main.Push(new SubscriptionEditViewModel(home.Main, id));
        });
        OpenSupport = Command.Of(() =>
        {
            if (Subscription?.SupportUrl is { } url) home.Main.Shell.OpenUrl(url);
        });
        OpenSite = Command.Of(() =>
        {
            if (Subscription?.WebPageUrl is { } url) home.Main.Shell.OpenUrl(url);
        });
        Delete = Command.Of(() =>
        {
            if (Subscription is not { } sub) return;
            home.Main.Ask($"Удалить подписку «{sub.Name}» и её серверы?", "Удалить", () => Model.RemoveSubscription(sub.Id));
        });
    }

    private AppModel Model => home.Model;

    public Guid? SubscriptionId { get; }

    public Guid Key => SubscriptionId ?? Guid.Empty;

    public bool IsSubscription => SubscriptionId is not null;

    public bool IsManual => SubscriptionId is null;

    public Subscription? Subscription { get; private set; }

    public string Title => Subscription?.Name ?? "Мои серверы";

    public ObservableCollection<ServerRowViewModel> Rows { get; } = [];

    public bool IsFolded
    {
        get => isFolded;
        set
        {
            if (!Set(ref isFolded, value)) return;
            home.SetFolded(Key, value);
            RaiseAll();
        }
    }

    public bool IsOpen => !isFolded;

    public string FoldState => isFolded ? "свёрнута" : "развёрнута";

    public string? Announce => Subscription?.Announce;

    public bool HasAnnounce => IsOpen && !string.IsNullOrWhiteSpace(Announce);

    public UsageLine? Usage => Subscription?.Usage is { } usage ? new UsageLine(usage) : null;

    public bool HasUsage => Subscription?.Usage is not null;

    public string? Error => Subscription?.LastError;

    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>When it was updated; folded, also how many servers hide under it.</summary>
    public string? Updated =>
        HasError || Subscription?.LastUpdated is not { } date
            ? null
            : isFolded ? $"Обновлена {Format.Relative(date)} · {Format.Servers(serverCount)}" : $"Обновлена {Format.Relative(date)}";

    public bool HasUpdated => Updated is not null;

    public bool HasSupport => Subscription?.SupportUrl is not null;

    public bool HasSite => Subscription?.WebPageUrl is not null;

    public bool IsPinging => Model.Pinging && SubscriptionId is not null && Model.PingingSubscription == SubscriptionId;

    public bool IsRefreshing => SubscriptionId is { } id && Model.IsRefreshing(id);

    public bool ShowPingIcon => !IsPinging;

    public bool ShowRefreshIcon => !IsRefreshing;

    public Command ToggleFold { get; }

    public Command Ping { get; }

    public Command Refresh { get; }

    public Command Edit { get; }

    public Command OpenSupport { get; }

    public Command OpenSite { get; }

    public Command Delete { get; }

    internal void RaiseBusy()
    {
        Raise(nameof(IsPinging));
        Raise(nameof(ShowPingIcon));
        Ping.Refresh();
    }

    internal void Update()
    {
        var state = Model.State;
        Subscription = SubscriptionId is { } id ? state.Subscriptions.FirstOrDefault(s => s.Id == id) : null;
        var servers = state.Sorted(state.ServersOf(SubscriptionId));
        count = servers.Count;
        serverCount = state.ServerCount(SubscriptionId);
        var display = state.Settings.PingDisplay;
        var target = new List<ServerRowViewModel>(servers.Count);
        foreach (var server in servers)
        {
            if (!rows.TryGetValue(server.Id, out var row)) rows[server.Id] = row = new ServerRowViewModel(home, server);
            row.Update(server, server.Id == state.SelectedServerId, display);
            target.Add(row);
        }
        foreach (var gone in rows.Keys.Except(servers.Select(s => s.Id)).ToList()) rows.Remove(gone);
        HomeViewModel.Sync(Rows, target);
        RaiseAll();
        Ping.Refresh();
        Refresh.Refresh();
    }
}

/// <summary>One server in the list.</summary>
public sealed class ServerRowViewModel : Observable
{
    private readonly HomeViewModel home;
    private Server server;
    private bool isSelected;
    private PingDisplay display;

    public ServerRowViewModel(HomeViewModel home, Server server)
    {
        this.home = home;
        this.server = server;
        Select = Command.Of(() => home.Model.Select(Server.Id));
        Info = Command.Of(() => home.Main.Push(new ServerDetailViewModel(home.Main, Server.Id)));
        PingThis = Command.Async(() => home.Model.PingAsync([Server]), () => !home.Model.Pinging);
        CopyLink = Command.Async(async () =>
        {
            if (Server.ShareLink is not { } link) return;
            await home.Main.Shell.CopyAsync(link);
            home.Model.Show("Ссылка скопирована");
        });
        Delete = Command.Of(() => home.Model.RemoveServer(Server.Id));
    }

    public Server Server => server;

    public string Name => server.Name;

    public string Subtitle => server.Subtitle;

    public PingResult? Ping => server.LastPing;

    public PingDisplay Display => display;

    public bool IsSelected => isSelected;

    public bool IsNotSelected => !isSelected;

    public bool HasShareLink => server.ShareLink is not null;

    public bool CanDelete => server.SubscriptionId is null;

    public Command Select { get; }

    public Command Info { get; }

    public Command PingThis { get; }

    public Command CopyLink { get; }

    public Command Delete { get; }

    internal void Update(Server next, bool selected, PingDisplay pingDisplay)
    {
        if (!ReferenceEquals(server, next))
        {
            server = next;
            Raise(nameof(Server));
            Raise(nameof(Name));
            Raise(nameof(Subtitle));
            Raise(nameof(Ping));
            Raise(nameof(HasShareLink));
            Raise(nameof(CanDelete));
        }
        if (isSelected != selected)
        {
            isSelected = selected;
            Raise(nameof(IsSelected));
            Raise(nameof(IsNotSelected));
        }
        if (display != pingDisplay)
        {
            display = pingDisplay;
            Raise(nameof(Display));
        }
        PingThis.Refresh();
    }
}

/// <summary>Traffic used of the plan and when it ends, as under a subscription.</summary>
public sealed class UsageLine(Usage usage)
{
    public bool HasBar => usage.Fraction is not null;

    public double Percent => (usage.Fraction ?? 0) * 100;

    public bool IsHigh => usage.Fraction > 0.9;

    public string Text => usage.Total > 0
        ? $"{Format.Size(usage.Used)} из {Format.Size(usage.Total)}"
        : $"{Format.Size(usage.Used)} · без лимита";

    public string? Expiry => usage.Expire is { } date ? Format.Expiry(date) : null;

    public bool HasExpiry => usage.Expire is not null;

    public bool IsExpired => usage.Expire < DateTimeOffset.Now;
}
