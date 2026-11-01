using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Threading;
using LiteTunnel.App.Model;
using LiteTunnel.Core;

namespace LiteTunnel.App;

/// <summary>
/// Starts the model, the window and the tray icon. Nothing here ticks: the
/// tray icon changes only when the connection does.
/// </summary>
public sealed partial class App : Application
{
    /// <summary>Set by <see cref="Program"/> before the app starts.</summary>
    public static Launch Launch { get; set; } = new(null, false, null);

    /// <summary>Set by <see cref="Program"/> before the app starts.</summary>
    public static SingleInstance? Instance { get; set; }

    private AppModel? model;
    private MainWindow? window;
    private TrayIcon? tray;
    private NativeMenuItem? toggleItem;
    private WindowIcon? iconOn;
    private WindowIcon? iconOff;
    private bool quitting;

    public override void Initialize()
    {
        AvaloniaXamlLoader.Load(this);
        Resources["Font.Tabular"] = new FontFeatureCollection { FontFeature.Parse("tnum") };
        Resources["Font.Mono"] = new FontFamily("Cascadia Mono, Consolas, DejaVu Sans Mono, Liberation Mono, Noto Sans Mono, monospace");
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
        {
            base.OnFrameworkInitializationCompleted();
            return;
        }

        model = new AppModel(new Store(Store.DefaultRoot), SingBoxPath());
        try { model.Connection.RestoreLeftovers(); }
        catch (SystemProxyException e) { model.Show($"Не удалось вернуть настройки прокси: {e.Message}", error: true); }
        if (OperatingSystem.IsWindows()) RegisterLinkScheme();

        window = new MainWindow(model);
        desktop.MainWindow = window;
        window.Closed += (_, _) => { if (!quitting) desktop.Shutdown(); };
        window.PropertyChanged += (_, e) =>
        {
            // Back from the tray: refresh what is due, as at launch.
            if (e.Property == Visual.IsVisibleProperty && e.NewValue is true) _ = model.DidBecomeActiveAsync();
        };

        desktop.ShutdownRequested += (_, _) => model.Connection.ShutdownNow();
        desktop.Exit += (_, _) => model.Connection.ShutdownNow();
        model.QuitRequested += () =>
        {
            // An elevated copy waits for this one to quit.
            quitting = true;
            window.Quitting = true;
            desktop.Shutdown();
        };

        CreateTray();
        model.Connection.Changed += UpdateTray;

        Instance?.Listen(message => Dispatcher.UIThread.Post(() => OnMessage(message)));

        window.Show();
        _ = StartAsync();
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync()
    {
        if (model is null) return;
        if (Launch.Link is { } link) await model.OpenLinkAsync(link);
        if (Launch.Connect) await model.ConnectAsync();
        else await model.DidBecomeActiveAsync();
    }

    /// <summary>From a second copy: a link to open, or just "show".</summary>
    private void OnMessage(string message)
    {
        if (model is null || window is null) return;
        window.ShowAndActivate();
        if (message == SingleInstance.Show) return;
        window.ViewModel.PopToRoot();
        _ = model.OpenLinkAsync(message);
    }

    /// <summary>Next to the app, or where <c>LITETUNNEL_SING_BOX</c> points.</summary>
    private static string SingBoxPath()
    {
        if (Environment.GetEnvironmentVariable("LITETUNNEL_SING_BOX") is { Length: > 0 } custom) return custom;
        return Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "sing-box.exe" : "sing-box");
    }

    // MARK: tray

    private void CreateTray()
    {
        iconOn = LoadIcon("tray-on.png");
        iconOff = LoadIcon("tray-off.png");

        var open = new NativeMenuItem("Открыть LiteTunnel");
        open.Click += (_, _) => window?.ShowAndActivate();
        toggleItem = new NativeMenuItem("Подключить");
        toggleItem.Click += (_, _) => { if (model is not null) _ = model.ToggleConnectionAsync(); };
        var quit = new NativeMenuItem("Выход");
        quit.Click += (_, _) => _ = QuitAsync();

        tray = new TrayIcon
        {
            Menu = [open, toggleItem, new NativeMenuItemSeparator(), quit],
        };
        tray.Clicked += (_, _) => window?.ShowAndActivate();
        TrayIcon.SetIcons(this, [tray]);
        UpdateTray();
    }

    private void UpdateTray()
    {
        if (model is null || tray is null || toggleItem is null) return;
        var status = model.Connection.Status;
        tray.Icon = status == ConnectionStatus.Connected ? iconOn : iconOff;
        tray.ToolTipText = status switch
        {
            ConnectionStatus.Connected => "LiteTunnel: подключено",
            ConnectionStatus.Connecting or ConnectionStatus.Reasserting => "LiteTunnel: подключение…",
            ConnectionStatus.Disconnecting => "LiteTunnel: отключение…",
            _ => "LiteTunnel: отключено",
        };
        toggleItem.Header = model.Connection.IsActive ? "Отключить" : "Подключить";
        toggleItem.IsEnabled = status is ConnectionStatus.Disconnected or ConnectionStatus.Connected;
    }

    private static WindowIcon LoadIcon(string name)
    {
        using var stream = AssetLoader.Open(new Uri($"avares://LiteTunnel/Assets/{name}"));
        return new WindowIcon(stream);
    }

    /// <summary>"Выход" in the tray: the core stops and the proxy goes back before the app quits.</summary>
    private async Task QuitAsync()
    {
        if (quitting || model is null) return;
        quitting = true;
        if (window is not null) window.Quitting = true;
        await model.Connection.StopAsync();
        if (tray is not null) tray.IsVisible = false;
        (ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown();
    }

    // MARK: links

    /// <summary>Lets litetunnel:// links from the browser open this app, for this user only.</summary>
    [SupportedOSPlatform("windows")]
    private static void RegisterLinkScheme()
    {
        if (Environment.ProcessPath is not { } exe) return;
        var command = $"\"{exe}\" \"%1\"";
        try
        {
            using var root = Microsoft.Win32.Registry.CurrentUser.CreateSubKey($@"Software\Classes\{Importer.Scheme}");
            using (var existing = root.OpenSubKey(@"shell\open\command"))
            {
                if (existing?.GetValue(null) as string == command) return;
            }
            root.SetValue(null, "URL:LiteTunnel");
            root.SetValue("URL Protocol", "");
            using (var icon = root.CreateSubKey("DefaultIcon")) icon.SetValue(null, $"\"{exe}\",0");
            using (var open = root.CreateSubKey(@"shell\open\command")) open.SetValue(null, command);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            // Links just won't open the app.
        }
    }
}
