using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using LiteTunnel.App.Model;
using LiteTunnel.App.ViewModels;

namespace LiteTunnel.App;

/// <summary>
/// The one window. Closed while the tunnel is up, it only hides: the tray
/// icon brings it back, and a hidden window runs no timers.
/// </summary>
public sealed partial class MainWindow : Window, IShell
{
    private static readonly string[] OpenableSchemes = ["http", "https", "tg", "mailto"];

    private readonly AppModel model;

    public MainWindow() : this(null!)
    {
        // For the previewer only.
    }

    public MainWindow(AppModel model)
    {
        this.model = model;
        InitializeComponent();
        if (model is null) return;
        ViewModel = new MainViewModel(model, this);
        DataContext = ViewModel;
        this.FindControl<Border>("BannerBox")!.Tapped += (_, _) => model.HideBanner();
        this.FindControl<Panel>("Scrim")!.PointerPressed += OnScrimPressed;
        Opened += (_, _) => UpdateVisibility();
    }

    public MainViewModel ViewModel { get; } = null!;

    /// <summary>Set before a real quit, so that closing does not hide the window instead.</summary>
    public bool Quitting { get; set; }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IsVisibleProperty || change.Property == WindowStateProperty) UpdateVisibility();
    }

    private void UpdateVisibility()
    {
        if (ViewModel is null) return;
        ViewModel.IsWindowVisible = IsVisible && WindowState != WindowState.Minimized;
    }

    /// <summary>Brings the window up from the tray or from behind other windows.</summary>
    public void ShowAndActivate()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (!Quitting && model is not null && model.Connection.IsActive && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            // Keep the tunnel; the tray icon opens the window again.
            e.Cancel = true;
            Hide();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        if (ViewModel is not null) ViewModel.IsWindowVisible = false;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Handled || ViewModel is null || e.Key != Key.Escape) return;
        if (ViewModel.IsAsking || ViewModel.CanGoBack)
        {
            ViewModel.Back.Execute(null);
            e.Handled = true;
        }
    }

    private void OnScrimPressed(object? sender, PointerPressedEventArgs e)
    {
        // A press outside the question answers no.
        if (ReferenceEquals(e.Source, sender)) ViewModel.ConfirmNo.Execute(null);
    }

    // MARK: IShell

    public async Task CopyAsync(string text)
    {
        if (Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
    }

    public async Task<string?> PasteAsync() =>
        Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;

    public void OpenUrl(string url)
    {
        // Links come from the provider: only web pages, Telegram and mail open.
        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !OpenableSchemes.Contains(uri.Scheme.ToLowerInvariant()))
        {
            model.Show("Эту ссылку нельзя открыть", error: true);
            return;
        }
        _ = Launcher.LaunchUriAsync(uri);
    }

    public async Task<Stream?> PickImageAsync()
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Картинка с QR-кодом",
            AllowMultiple = false,
            FileTypeFilter = [FilePickerFileTypes.ImageAll],
        });
        if (files.Count == 0) return null;
        using var file = files[0];
        return await file.OpenReadAsync();
    }
}
