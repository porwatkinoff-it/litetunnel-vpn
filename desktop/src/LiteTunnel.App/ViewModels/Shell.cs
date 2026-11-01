using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LiteTunnel.App.Model;

namespace LiteTunnel.App.ViewModels;

/// <summary>What the pages need from the window.</summary>
public interface IShell
{
    Task CopyAsync(string text);

    Task<string?> PasteAsync();

    void OpenUrl(string url);

    /// <summary>A picture the user picked; null when they picked none.</summary>
    Task<Stream?> PickImageAsync();
}

/// <summary>A button in the window header: an icon with a tip, or a word.</summary>
public sealed class ToolAction : Observable
{
    private string? icon;

    public ToolAction(ICommand command, string tip, string? icon = null, string? text = null)
    {
        Command = command;
        Tip = tip;
        this.icon = icon;
        Text = text;
    }

    public ICommand Command { get; }

    public string Tip { get; }

    public string? Text { get; }

    public bool HasText => Text is not null;

    public bool HasIcon => icon is not null;

    /// <summary>A key of Styles/Icons.axaml without the "Icon." prefix.</summary>
    public string? IconName
    {
        get => icon;
        set
        {
            if (Set(ref icon, value)) Raise(nameof(Icon));
        }
    }

    public Geometry? Icon => icon is not null && Application.Current?.FindResource("Icon." + icon) is Geometry geometry ? geometry : null;
}

/// <summary>One screen in the window. The window shows the top one of a stack, as the phone app does.</summary>
public abstract class PageViewModel(MainViewModel main) : Observable
{
    public MainViewModel Main { get; } = main;

    public AppModel Model => Main.Model;

    public abstract string Title { get; }

    /// <summary>A form with Отмена and Готово rather than a back arrow.</summary>
    public virtual bool IsSheet => false;

    public virtual IReadOnlyList<ToolAction> Actions => [];

    public virtual ToolAction? Done => null;

    /// <summary>On screen in a visible window. Live updates run only from here to <see cref="Disappeared"/>.</summary>
    public virtual void Appeared()
    {
    }

    public virtual void Disappeared()
    {
    }
}

/// <summary>A picker: the titles to show and the index of the current value.</summary>
public sealed class Choice : Observable
{
    private readonly Func<int> get;
    private readonly Action<int> set;

    private Choice(IReadOnlyList<string> items, Func<int> get, Action<int> set)
    {
        Items = items;
        this.get = get;
        this.set = set;
    }

    public static Choice Of<T>(T[] values, Func<T, string> title, Func<T> get, Action<T> set) where T : notnull =>
        new(values.Select(title).ToList(), () => Array.IndexOf(values, get()), i => set(values[i]));

    public IReadOnlyList<string> Items { get; }

    public int Index
    {
        get => get();
        set
        {
            if (value >= 0 && value < Items.Count && value != get()) set(value);
            Raise();
        }
    }

    public void Refresh() => Raise(nameof(Index));
}
