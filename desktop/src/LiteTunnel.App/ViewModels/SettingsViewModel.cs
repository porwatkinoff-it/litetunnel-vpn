using System.ComponentModel;
using LiteTunnel.Core;

namespace LiteTunnel.App.ViewModels;

/// <summary>
/// Settings, as on the Mac. Each switch saves at once; the text fields save
/// on Enter or when they lose focus, and an empty one goes back to what it was.
/// </summary>
public sealed class SettingsViewModel : PageViewModel
{
    private string remoteAddress = "", directAddress = "", pingUrl = "", mtu = "";
    private string? setcapHint;
    private bool listening;

    public SettingsViewModel(MainViewModel main) : base(main)
    {
        Routing = Choice.Of(Enum.GetValues<RoutingMode>(), m => m.Title(), () => S.Routing, v => Change(s => s with { Routing = v }));
        RemoteKind = Choice.Of(Enum.GetValues<DnsKind>(), k => k.Title(), () => S.RemoteDns.Kind, v => Change(s => s with { RemoteDns = s.RemoteDns with { Kind = v } }));
        DirectKind = Choice.Of(Enum.GetValues<DnsKind>(), k => k.Title(), () => S.DirectDns.Kind, v => Change(s => s with { DirectDns = s.DirectDns with { Kind = v } }));
        Mode = Choice.Of(Enum.GetValues<ConnectionMode>(), m => m.Title(), () => S.Mode, v => Change(s => s with { Mode = v }));
        Sort = Choice.Of(Enum.GetValues<SortMode>(), m => m.Title(), () => S.SortMode, v => Change(s => s with { SortMode = v }));
        PingMethod = Choice.Of(Enum.GetValues<PingMode>(), m => m.Title(), () => S.PingMode, v => Change(s => s with { PingMode = v }));
        PingView = Choice.Of(Enum.GetValues<PingDisplay>(), d => d.Title(), () => S.PingDisplay, v => Change(s => s with { PingDisplay = v }));
        Stack = Choice.Of(Enum.GetValues<TunStack>(), t => t.Title(), () => S.TunStack, v => Change(s => s with { TunStack = v }));
        Log = Choice.Of(Enum.GetValues<LogLevel>(), l => l.Raw(), () => S.LogLevel, v => Change(s => s with { LogLevel = v }));
        OpenDomainRules = Command.Of(() => Main.Push(new DomainRulesViewModel(Main)));
        UpdateRuleSets = Command.Async(() => Model.UpdateRuleSetsAsync(force: true), () => !Model.UpdatingRuleSets);
        OpenLogs = Command.Of(() => Main.Push(new LogsViewModel(Main)));
        OpenCoreSite = Command.Of(() => Main.Shell.OpenUrl("https://github.com/SagerNet/sing-box"));
        ResetFields();
    }

    public override string Title => "Настройки";

    private AppSettings S => Model.Settings;

    private void Change(Func<AppSettings, AppSettings> change) => Model.ChangeSettings(change);

    // MARK: routing

    public Choice Routing { get; }

    public bool BlockAds
    {
        get => S.BlockAds;
        set => Change(s => s with { BlockAds = value });
    }

    public Command OpenDomainRules { get; }

    public string DomainRulesCount
    {
        get
        {
            int n = S.ProxyDomains.Count + S.DirectDomains.Count + S.BlockDomains.Count;
            return n == 0 ? "" : n.ToString();
        }
    }

    public Command UpdateRuleSets { get; }

    /// <summary>Only a mode that uses the domain and address bases offers to update them.</summary>
    public bool ShowRuleSets => RuleSets.Needed(S).Any();

    public bool IsUpdatingRuleSets => Model.UpdatingRuleSets;

    public bool RuleSetsMissing => !Model.UpdatingRuleSets && !Model.RuleSetsReady;

    // MARK: DNS

    public Choice RemoteKind { get; }

    public Choice DirectKind { get; }

    public string RemoteAddress
    {
        get => remoteAddress;
        set => Set(ref remoteAddress, value);
    }

    public string DirectAddress
    {
        get => directAddress;
        set => Set(ref directAddress, value);
    }

    // MARK: connection

    public Choice Mode { get; }

    public bool IsTun => S.Mode == ConnectionMode.Tun;

    public bool IsProxy => S.Mode == ConnectionMode.SystemProxy;

    public bool KillSwitch
    {
        get => S.KillSwitch;
        set => Change(s => s with { KillSwitch = value });
    }

    public bool ExcludeLocal
    {
        get => S.ExcludeLocalNetworks;
        set => Change(s => s with { ExcludeLocalNetworks = value });
    }

    public bool Ipv6
    {
        get => S.Ipv6;
        set => Change(s => s with { Ipv6 = value });
    }

    public bool TlsFragment
    {
        get => S.TlsFragment;
        set => Change(s => s with { TlsFragment = value });
    }

    public string ConnectionFooter => IsTun
        ? "Kill switch не выпускает трафик мимо туннеля, даже пока он переподключается. Фрагментация TLS помогает, когда провайдер режет соединения по имени сайта."
        : "Системный прокси работает без прав администратора. Программы, которые его не учитывают, идут напрямую; для всех программ выберите VPN (TUN). Фрагментация TLS помогает, когда провайдер режет соединения по имени сайта.";

    /// <summary>Linux, TUN mode, no rights yet: the command after which pkexec stops asking.</summary>
    public string? SetcapHint => IsTun ? setcapHint : null;

    public bool HasSetcapHint => SetcapHint is not null;

    // MARK: list and ping

    public Choice Sort { get; }

    public Choice PingMethod { get; }

    public Choice PingView { get; }

    public bool ShowPingUrl => S.PingMode.ThroughProxy();

    public string PingUrl
    {
        get => pingUrl;
        set => Set(ref pingUrl, value);
    }

    public string PingUrlPlaceholder => AppSettings.DefaultPingUrl;

    public bool PingOnOpen
    {
        get => S.PingOnOpen;
        set => Change(s => s with { PingOnOpen = value });
    }

    public bool PingOnUpdate
    {
        get => S.PingOnUpdate;
        set => Change(s => s with { PingOnUpdate = value });
    }

    public string PingFooter => S.PingMode.Footer();

    // MARK: advanced

    public bool Sniffing
    {
        get => S.Sniffing;
        set => Change(s => s with { Sniffing = value });
    }

    public Choice Stack { get; }

    public string Mtu
    {
        get => mtu;
        set => Set(ref mtu, value);
    }

    public Choice Log { get; }

    public Command OpenLogs { get; }

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version is { } v ? $"{v.Major}.{v.Minor}.{v.Build}" : "0.1.0";

    public Command OpenCoreSite { get; }

    // MARK: text fields

    /// <summary>Saves what was typed in the fields; an empty or wrong value goes back to the saved one.</summary>
    public void CommitFields()
    {
        var remote = remoteAddress.Trim();
        var direct = directAddress.Trim();
        var url = pingUrl.Trim();
        int? mtuValue = mtu.Trim() is "" or "0" or "авто" ? 0 : int.TryParse(mtu.Trim(), out var n) ? Math.Clamp(n, 1280, 9000) : null;
        Change(s => s with
        {
            RemoteDns = remote.Length > 0 ? s.RemoteDns with { Address = remote } : s.RemoteDns,
            DirectDns = direct.Length > 0 ? s.DirectDns with { Address = direct } : s.DirectDns,
            PingUrl = url.Length > 0 ? Ping.TestUrl(url).OriginalString : AppSettings.DefaultPingUrl,
            Mtu = mtuValue ?? s.Mtu,
        });
        ResetFields();
    }

    private void ResetFields()
    {
        RemoteAddress = S.RemoteDns.Address;
        DirectAddress = S.DirectDns.Address;
        PingUrl = S.PingUrl == AppSettings.DefaultPingUrl ? "" : S.PingUrl;
        Mtu = S.Mtu == 0 ? "" : S.Mtu.ToString();
    }

    // MARK: updates

    public override void Appeared()
    {
        if (!listening)
        {
            listening = true;
            Model.StateChanged += Refresh;
            Model.PropertyChanged += OnModelChanged;
        }
        setcapHint = OperatingSystem.IsLinux() && !Elevation.IsRoot() && !Elevation.HasFileCapabilities(Model.Connection.SingBox)
            ? Elevation.SetcapCommand(Model.Connection.SingBox)
            : null;
        Refresh();
    }

    public override void Disappeared()
    {
        CommitFields();
        if (!listening) return;
        listening = false;
        Model.StateChanged -= Refresh;
        Model.PropertyChanged -= OnModelChanged;
    }

    private void OnModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(Model.UpdatingRuleSets)) Refresh();
    }

    private void Refresh()
    {
        RaiseAll();
        foreach (var choice in new[] { Routing, RemoteKind, DirectKind, Mode, Sort, PingMethod, PingView, Stack, Log }) choice.Refresh();
        UpdateRuleSets.Refresh();
    }
}
