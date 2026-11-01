using LiteTunnel.Core;

namespace LiteTunnel.App.ViewModels;

/// <summary>Domains the user sends through the VPN, past it or nowhere.</summary>
public sealed class DomainRulesViewModel : PageViewModel
{
    public DomainRulesViewModel(MainViewModel main) : base(main)
    {
        Lists =
        [
            new DomainList(this, "Через VPN", "Например, youtube.com", s => s.ProxyDomains, (s, v) => s with { ProxyDomains = v }),
            new DomainList(this, "Напрямую", "Например, bank.ru", s => s.DirectDomains, (s, v) => s with { DirectDomains = v }),
            new DomainList(this, "Блокировать", "Например, ads.example.com", s => s.BlockDomains, (s, v) => s with { BlockDomains = v }),
        ];
    }

    public override string Title => "Свои правила";

    public IReadOnlyList<DomainList> Lists { get; }

    /// <summary>
    /// The domain in what was typed: a bare name or the host of a link,
    /// without "www." and a leading dot. Null when it is not a domain.
    /// </summary>
    public static string? Normalize(string text)
    {
        var value = text.Trim().ToLowerInvariant();
        if (value.Contains("://") && Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Host.Length > 0) value = uri.Host;
        if (value.StartsWith("www.", StringComparison.Ordinal)) value = value[4..];
        value = value.TrimStart('.');
        if (!value.Contains('.') || value.EndsWith('.') || value.Any(c => char.IsWhiteSpace(c) || c == '/')) return null;
        return value;
    }
}

public sealed class DomainList : Observable
{
    private readonly DomainRulesViewModel page;
    private readonly Func<AppSettings, IReadOnlyList<string>> get;
    private readonly Func<AppSettings, IReadOnlyList<string>, AppSettings> set;
    private string draft = "";

    public DomainList(DomainRulesViewModel page, string title, string hint, Func<AppSettings, IReadOnlyList<string>> get, Func<AppSettings, IReadOnlyList<string>, AppSettings> set)
    {
        this.page = page;
        this.get = get;
        this.set = set;
        Title = title;
        Hint = hint;
        Add = Command.Of(AddDraft, () => DomainRulesViewModel.Normalize(draft) is not null);
        Items = Build();
    }

    public string Title { get; }

    public string Hint { get; }

    public IReadOnlyList<DomainItem> Items { get; private set; }

    public bool HasItems => Items.Count > 0;

    public string Draft
    {
        get => draft;
        set
        {
            if (Set(ref draft, value)) Add.Refresh();
        }
    }

    public Command Add { get; }

    private IReadOnlyList<string> Current => get(page.Model.Settings);

    private void AddDraft()
    {
        if (DomainRulesViewModel.Normalize(draft) is not { } domain) return;
        if (!Current.Contains(domain)) Save([.. Current, domain]);
        Draft = "";
    }

    private void Remove(string domain) => Save(Current.Where(d => d != domain).ToList());

    private void Save(IReadOnlyList<string> list)
    {
        page.Model.ChangeSettings(s => set(s, list));
        Items = Build();
        Raise(nameof(Items));
        Raise(nameof(HasItems));
    }

    private IReadOnlyList<DomainItem> Build() => Current.Select(d => new DomainItem(d, Command.Of(() => Remove(d)))).ToList();
}

public sealed record DomainItem(string Domain, Command Delete);
