using LiteTunnel.Core;

namespace LiteTunnel.App.ViewModels;

/// <summary>A subscription's name, address and refresh, with what the provider last sent.</summary>
public sealed class SubscriptionEditViewModel : PageViewModel
{
    private readonly Guid id;
    private string name, url, userAgent;
    private decimal? interval;
    private bool listening;

    public SubscriptionEditViewModel(MainViewModel main, Guid id) : base(main)
    {
        this.id = id;
        var sub = Subscription;
        name = sub?.Name ?? "";
        url = sub?.Url ?? "";
        userAgent = sub?.UserAgent ?? "";
        interval = sub?.UpdateIntervalHours ?? 24;
        Done = new ToolAction(Command.Of(() =>
        {
            Save();
            Main.Close(this);
        }, () => IsUrlValid), "Сохранить", text: "Готово");
        RefreshNow = Command.Async(async () =>
        {
            Save();
            await Model.RefreshAsync(id);
        }, () => IsUrlValid && !IsRefreshing);
        OpenSupport = Command.Of(() =>
        {
            if (Subscription?.SupportUrl is { } link) Main.Shell.OpenUrl(link);
        });
        OpenSite = Command.Of(() =>
        {
            if (Subscription?.WebPageUrl is { } link) Main.Shell.OpenUrl(link);
        });
        Delete = Command.Of(() => Main.Ask("Удалить подписку и её серверы?", "Удалить", () =>
        {
            Model.RemoveSubscription(id);
            Main.Close(this);
        }));
    }

    public override string Title => "Подписка";

    public override bool IsSheet => true;

    public override ToolAction Done { get; }

    private Subscription? Subscription => Model.State.Subscriptions.FirstOrDefault(s => s.Id == id);

    // MARK: fields

    public string Name
    {
        get => name;
        set => Set(ref name, value);
    }

    public string Url
    {
        get => url;
        set
        {
            if (!Set(ref url, value)) return;
            Raise(nameof(IsUrlValid));
            Raise(nameof(ShowUrlError));
            ((Command)Done.Command).Refresh();
            RefreshNow.Refresh();
        }
    }

    public bool IsUrlValid =>
        Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
        && uri.Host.Length > 0;

    public bool ShowUrlError => !IsUrlValid;

    /// <summary>Hours, 1 to 168.</summary>
    public decimal? Interval
    {
        get => interval;
        set => Set(ref interval, value);
    }

    public string UserAgent
    {
        get => userAgent;
        set => Set(ref userAgent, value);
    }

    public string UserAgentPlaceholder => SubscriptionClient.DefaultUserAgent;

    // MARK: state

    public int ServerCount => Model.State.ServersOf(id).Count;

    public int Skipped => Subscription?.SkippedCount ?? 0;

    public bool HasSkipped => Skipped > 0;

    /// <summary>Why the skipped lines were left out, without the lines.</summary>
    public string SkippedReasons => string.Join("\n", Subscription?.SkippedReasons ?? Array.Empty<string>());

    public bool HasSkippedReasons => HasSkipped && SkippedReasons.Length > 0;

    public string Updated => Subscription?.LastUpdated is { } date ? Format.Date(date) : "ещё нет";

    public string? Error => Subscription?.LastError;

    public bool HasError => !string.IsNullOrEmpty(Error);

    public UsageLine? Usage => Subscription?.Usage is { } usage ? new UsageLine(usage) : null;

    public bool HasUsage => Subscription?.Usage is not null;

    public bool HasSupport => Subscription?.SupportUrl is not null;

    public bool HasSite => Subscription?.WebPageUrl is not null;

    public bool IsRefreshing => Model.IsRefreshing(id);

    public Command RefreshNow { get; }

    public Command OpenSupport { get; }

    public Command OpenSite { get; }

    public Command Delete { get; }

    /// <summary>Puts what was typed into the subscription. A new address gets its servers right away.</summary>
    private void Save()
    {
        if (Subscription is not { } sub) return;
        var next = sub with
        {
            Name = string.IsNullOrWhiteSpace(name) ? sub.Name : name.Trim(),
            Url = url.Trim(),
            UpdateIntervalHours = (int)Math.Clamp(interval ?? sub.UpdateIntervalHours, 1, 168),
            UserAgent = string.IsNullOrWhiteSpace(userAgent) ? null : userAgent.Trim(),
        };
        if (next == sub) return;
        Model.UpdateSubscription(next);
        if (next.Url != sub.Url) _ = Model.RefreshAsync(id);
    }

    public override void Appeared()
    {
        if (!listening)
        {
            listening = true;
            Model.StateChanged += OnStateChanged;
        }
        OnStateChanged();
    }

    public override void Disappeared()
    {
        if (!listening) return;
        listening = false;
        Model.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged()
    {
        if (Subscription is null)
        {
            Main.Close(this);
            return;
        }
        foreach (var property in new[]
        {
            nameof(ServerCount), nameof(Skipped), nameof(HasSkipped), nameof(SkippedReasons), nameof(HasSkippedReasons),
            nameof(Updated), nameof(Error), nameof(HasError), nameof(Usage), nameof(HasUsage), nameof(HasSupport),
            nameof(HasSite), nameof(IsRefreshing),
        }) Raise(property);
        RefreshNow.Refresh();
    }
}
