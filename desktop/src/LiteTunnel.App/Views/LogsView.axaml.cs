using Avalonia.Controls;
using Avalonia.Threading;
using LiteTunnel.App.ViewModels;

namespace LiteTunnel.App.Views;

public sealed partial class LogsView : UserControl
{
    private LogsViewModel? page;

    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (page is not null) page.Updated -= ScrollToEnd;
            page = DataContext as LogsViewModel;
            if (page is not null) page.Updated += ScrollToEnd;
            ScrollToEnd();
        };
    }

    // The newest lines are at the end.
    private void ScrollToEnd() =>
        Dispatcher.UIThread.Post(() => this.FindControl<ScrollViewer>("Scroller")?.ScrollToEnd(), DispatcherPriority.Background);
}
