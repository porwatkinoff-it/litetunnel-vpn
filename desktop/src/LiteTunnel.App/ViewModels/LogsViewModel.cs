using Avalonia.Threading;
using LiteTunnel.App.Model;

namespace LiteTunnel.App.ViewModels;

/// <summary>
/// The end of the core's log. Read again every 2 s only while this page is
/// on screen and the core runs; otherwise once, when the page appears.
/// </summary>
public sealed class LogsViewModel : PageViewModel
{
    private const int MaxLines = 400;
    private DispatcherTimer? timer;
    private bool listening;
    private string text = "";

    public LogsViewModel(MainViewModel main) : base(main)
    {
        Actions = [new ToolAction(Command.Async(CopyAsync), "Копировать журнал", text: "Копировать")];
    }

    public override string Title => "Журнал";

    public override IReadOnlyList<ToolAction> Actions { get; }

    public IReadOnlyList<LogLine> Lines { get; private set; } = [];

    public bool IsEmpty => Lines.Count == 0;

    /// <summary>New lines arrived: the view scrolls to the end.</summary>
    public event Action? Updated;

    private async Task CopyAsync()
    {
        await Main.Shell.CopyAsync(Model.Connection.Log.Read());
        Model.Show("Журнал скопирован");
    }

    public override void Appeared()
    {
        if (!listening)
        {
            listening = true;
            Model.Connection.Changed += UpdateTimer;
        }
        Read();
        UpdateTimer();
    }

    public override void Disappeared()
    {
        if (listening)
        {
            listening = false;
            Model.Connection.Changed -= UpdateTimer;
        }
        timer?.Stop();
        timer = null;
    }

    private void UpdateTimer()
    {
        bool wanted = listening && Model.Connection.IsActive;
        if (wanted && timer is null)
        {
            timer = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Background, (_, _) => Read());
            timer.Start();
        }
        else if (!wanted && timer is not null)
        {
            timer.Stop();
            timer = null;
            Read();
        }
    }

    private void Read()
    {
        var next = Model.Connection.Log.Tail();
        if (next == text) return;
        text = next;
        var lines = next.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Lines = lines.Skip(Math.Max(0, lines.Length - MaxLines)).Select(LogLine.Of).ToList();
        Raise(nameof(Lines));
        Raise(nameof(IsEmpty));
        Updated?.Invoke();
    }
}

public sealed record LogLine(string Text, bool IsError, bool IsWarning)
{
    public static LogLine Of(string text) =>
        new(text,
            text.Contains("error", StringComparison.OrdinalIgnoreCase) || text.Contains("fatal", StringComparison.OrdinalIgnoreCase),
            text.Contains("warn", StringComparison.OrdinalIgnoreCase));

    public bool IsPlain => !IsError && !IsWarning;

    public bool IsWarningOnly => IsWarning && !IsError;
}
