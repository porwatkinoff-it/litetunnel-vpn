using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace LiteTunnel.App.ViewModels;

public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Raise([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>Every property may have changed.</summary>
    protected void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Raise(name);
        return true;
    }
}

/// <summary>A button action. Async ones run on the UI thread and keep the button off while they work.</summary>
public sealed class Command : ICommand
{
    private readonly Func<object?, Task> run;
    private readonly Func<bool>? canRun;
    private bool running;

    private Command(Func<object?, Task> run, Func<bool>? canRun)
    {
        this.run = run;
        this.canRun = canRun;
    }

    public static Command Of(Action action, Func<bool>? canRun = null) => new(_ => { action(); return Task.CompletedTask; }, canRun);

    public static Command Of<T>(Action<T> action) => new(p => { if (p is T t) action(t); return Task.CompletedTask; }, null);

    public static Command Async(Func<Task> action, Func<bool>? canRun = null) => new(_ => action(), canRun);

    public event EventHandler? CanExecuteChanged;

    public bool CanExecute(object? parameter) => !running && (canRun?.Invoke() ?? true);

    public async void Execute(object? parameter)
    {
        if (!CanExecute(parameter)) return;
        running = true;
        Refresh();
        try { await run(parameter); }
        finally
        {
            running = false;
            Refresh();
        }
    }

    public void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
