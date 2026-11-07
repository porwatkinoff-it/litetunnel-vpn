using LiteTunnel.Core;

namespace LiteTunnel.App.ViewModels;

/// <summary>One server: its QR code, what it is, and the outbound the core gets.</summary>
public sealed class ServerDetailViewModel : PageViewModel
{
    private readonly Guid id;
    private bool listening;
    private bool jsonOpen;
    private IReadOnlyList<MemberRow> members = [];

    public ServerDetailViewModel(MainViewModel main, Guid id) : base(main)
    {
        this.id = id;
        Select = Command.Of(() =>
        {
            Model.Select(id);
            Main.Close(this);
        }, () => Server is not null && !IsSelected);
        PingThis = Command.Async(() => Server is { } s ? Model.PingAsync([s]) : Task.CompletedTask, () => Server is not null && !Model.Pinging);
        CopyLink = Command.Async(async () =>
        {
            if (Server?.ShareLink is not { } link) return;
            await Main.Shell.CopyAsync(link);
            Model.Show("Ссылка скопирована");
        });
        ToggleJson = Command.Of(() =>
        {
            jsonOpen = !jsonOpen;
            Raise(nameof(IsJsonOpen));
            Raise(nameof(Json));
        });
        Delete = Command.Of(() =>
        {
            if (Server is not { } s) return;
            Main.Ask($"Удалить сервер «{s.Name}»?", "Удалить", () =>
            {
                Model.RemoveServer(id);
                Main.Close(this);
            });
        });
    }

    public override string Title => Server?.Name ?? "Сервер";

    private Server? Server => Model.State.Servers.FirstOrDefault(s => s.Id == id);

    public bool Exists => Server is not null;

    public bool IsMissing => Server is null;

    public string? ShareLink => Server?.ShareLink;

    public bool HasShareLink => ShareLink is not null;

    public string Protocol => Server?.Proto.Title() ?? "";

    public IReadOnlyList<MemberRow> Members => members;

    public bool HasMembers => Members.Count > 0;

    public bool IsSingle => Exists && !HasMembers;

    public string MembersTitle => $"Серверы: {Members.Count}";

    public string Address => Server is { } s ? (s.Host.Contains(':') ? $"[{s.Host}]:{s.Port}" : $"{s.Host}:{s.Port}") : "";

    public string Transport => Server?.Detail is { Length: > 0 } detail ? detail : "—";

    public string PingText => Server?.LastPing?.Label() ?? "—";

    public bool PingGood => Server?.LastPing?.Tone() == "Connected";

    public bool PingSlow => Server?.LastPing?.Tone() == "Busy";

    public bool PingBad => Server?.LastPing?.Tone() == "Danger";

    public bool PingUnknown => Server?.LastPing is null;

    /// <summary>Its ping is being measured: a spinner instead of the old result.</summary>
    public bool IsPinging => Model.PingingIds.Contains(id);

    public bool ShowPingText => !IsPinging;

    public string? SubscriptionName => Server?.SubscriptionId is { } subId ? Model.State.Subscriptions.FirstOrDefault(s => s.Id == subId)?.Name : null;

    public bool HasSubscription => SubscriptionName is not null;

    public bool IsSelected => Model.State.SelectedServerId == id;

    public string SelectTitle => IsSelected ? "Этот сервер выбран" : "Выбрать этот сервер";

    public bool CanDelete => Server is { SubscriptionId: null };

    public bool IsJsonOpen => jsonOpen;

    /// <summary>The outbound with its keys; built only while it is open.</summary>
    public string Json => jsonOpen && Server is { } s ? s.Outbound.ToJsonString(pretty: true) : "";

    public Command Select { get; }

    public Command PingThis { get; }

    public Command CopyLink { get; }

    public Command ToggleJson { get; }

    public Command Delete { get; }

    public override void Appeared()
    {
        if (!listening)
        {
            listening = true;
            Model.StateChanged += Refresh;
            Model.PropertyChanged += OnModelChanged;
        }
        Refresh();
    }

    public override void Disappeared()
    {
        if (!listening) return;
        listening = false;
        Model.StateChanged -= Refresh;
        Model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Model.PingingIds))
        {
            Raise(nameof(IsPinging));
            Raise(nameof(ShowPingText));
        }
        else if (e.PropertyName == nameof(Model.Pinging))
        {
            PingThis.Refresh();
        }
    }

    private void Refresh()
    {
        members = Server?.Members.Select(m => new MemberRow(m.Name, m.Subtitle)).ToList() ?? [];
        RaiseAll();
        Select.Refresh();
        PingThis.Refresh();
    }
}

public sealed record MemberRow(string Name, string Subtitle);
