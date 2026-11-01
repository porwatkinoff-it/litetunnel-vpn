using System.Diagnostics;
using Avalonia;
using LiteTunnel.Core;

namespace LiteTunnel.App;

/// <summary>Command line arguments the app passes to itself.</summary>
public static class StartOptions
{
    /// <summary>Followed by a process id: start once that process has quit.</summary>
    public const string After = "--after";

    /// <summary>Connect right after launch.</summary>
    public const string Connect = "--connect";
}

/// <summary>What this launch was asked to do.</summary>
public sealed record Launch(int? AfterPid, bool Connect, string? Link)
{
    public static Launch Parse(IReadOnlyList<string> args)
    {
        int? after = null;
        bool connect = false;
        string? link = null;
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i] == StartOptions.After && i + 1 < args.Count && int.TryParse(args[i + 1], out var pid))
            {
                after = pid;
                i++;
            }
            else if (args[i] == StartOptions.Connect)
            {
                connect = true;
            }
            else if (args[i].StartsWith(Importer.Scheme + ":", StringComparison.OrdinalIgnoreCase))
            {
                link = args[i];
            }
        }
        return new Launch(after, connect, link);
    }
}

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (args is [SystemProxy.WatchdogArgument, var backup]) return SystemProxy.RunWatchdog(backup);
        if (args is [SystemProxy.RestoreArgument])
        {
            try { SystemProxy.ForThisSystem(Store.DefaultRoot, Environment.ProcessPath).Restore(); }
            catch (SystemProxyException) { return 1; }
            return 0;
        }

        var launch = Launch.Parse(args);
        if (launch.AfterPid is { } pid) WaitForExit(pid);

        var instance = SingleInstance.Acquire(Store.DefaultRoot);
        if (instance is null)
        {
            // Another window is open: bring it up, with the link if there is one.
            SingleInstance.Send(launch.Link ?? SingleInstance.Show);
            return 0;
        }
        using (instance)
        {
            App.Launch = launch;
            App.Instance = instance;
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
        }
    }

    private static void WaitForExit(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            process.WaitForExit(TimeSpan.FromSeconds(15));
        }
        catch (ArgumentException)
        {
            // Gone already.
        }
        catch (InvalidOperationException)
        {
        }
    }

    // The previewer uses this too.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .With(new X11PlatformOptions { WmClass = "LiteTunnel" })
            .LogToTrace();
}
