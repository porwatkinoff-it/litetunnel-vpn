using LiteTunnel.App.Model;

namespace LiteTunnel.App.ViewModels;

/// <summary>The window: a stack of pages over the home screen, and a question when one is asked.</summary>
public sealed class MainViewModel : Observable
{
    private readonly List<PageViewModel> stack = [];
    private bool windowVisible;
    private string? question;
    private string answer = "";
    private Action? onYes;

    public MainViewModel(AppModel model, IShell shell)
    {
        Model = model;
        Shell = shell;
        Home = new HomeViewModel(this);
        stack.Add(Home);
        Back = Command.Of(Pop);
        ConfirmYes = Command.Of(() =>
        {
            var action = onYes;
            CloseQuestion();
            action?.Invoke();
        });
        ConfirmNo = Command.Of(CloseQuestion);
    }

    public AppModel Model { get; }

    public IShell Shell { get; }

    public HomeViewModel Home { get; }

    public PageViewModel Current => stack[^1];

    public bool CanGoBack => stack.Count > 1;

    public bool ShowBackArrow => CanGoBack && !Current.IsSheet;

    public bool ShowCancel => Current.IsSheet;

    public Command Back { get; }

    /// <summary>Shown and not minimised. Hidden, the pages stop every live update.</summary>
    public bool IsWindowVisible
    {
        get => windowVisible;
        set
        {
            if (!Set(ref windowVisible, value)) return;
            if (value) Current.Appeared();
            else Current.Disappeared();
        }
    }

    public void Push(PageViewModel page)
    {
        if (windowVisible) Current.Disappeared();
        stack.Add(page);
        RaiseStack();
        if (windowVisible) page.Appeared();
    }

    public void Pop()
    {
        if (question is not null)
        {
            CloseQuestion();
            return;
        }
        if (stack.Count < 2) return;
        if (windowVisible) Current.Disappeared();
        stack.RemoveAt(stack.Count - 1);
        RaiseStack();
        if (windowVisible) Current.Appeared();
    }

    /// <summary>Takes away every page above the home screen, as when a link opens.</summary>
    public void PopToRoot()
    {
        CloseQuestion();
        if (stack.Count < 2) return;
        if (windowVisible) Current.Disappeared();
        stack.RemoveRange(1, stack.Count - 1);
        RaiseStack();
        if (windowVisible) Current.Appeared();
    }

    /// <summary>Takes away <paramref name="page"/> if it is on top: a page that closes itself after the user is done.</summary>
    public void Close(PageViewModel page)
    {
        if (Current == page) Pop();
    }

    private void RaiseStack()
    {
        Raise(nameof(Current));
        Raise(nameof(CanGoBack));
        Raise(nameof(ShowBackArrow));
        Raise(nameof(ShowCancel));
    }

    // MARK: question

    public bool IsAsking => question is not null;

    public string? Question => question;

    public string Answer => answer;

    public Command ConfirmYes { get; }

    public Command ConfirmNo { get; }

    /// <summary>A question before something that cannot be undone: <paramref name="action"/> runs on yes.</summary>
    public void Ask(string text, string actionTitle, Action action)
    {
        question = text;
        answer = actionTitle;
        onYes = action;
        RaiseQuestion();
    }

    private void CloseQuestion()
    {
        if (question is null) return;
        question = null;
        onYes = null;
        RaiseQuestion();
    }

    private void RaiseQuestion()
    {
        Raise(nameof(IsAsking));
        Raise(nameof(Question));
        Raise(nameof(Answer));
    }
}
