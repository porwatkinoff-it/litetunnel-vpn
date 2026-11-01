using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace LiteTunnel.Core;

/// <summary>What TUN mode needs to run here, and how to get it.</summary>
public abstract record TunAccess
{
    /// <summary>The core can create the adapter as it is.</summary>
    public sealed record Ready(CoreElevation Elevation) : TunAccess;

    /// <summary>Windows: the app has to run as administrator.</summary>
    public sealed record NeedsAdministrator : TunAccess;

    /// <summary>Linux without pkexec and without the rights on the binary.</summary>
    public sealed record Unavailable(string Reason) : TunAccess;
}

public static class Elevation
{
    /// <summary>
    /// Windows needs an administrator for the Wintun adapter. Linux needs
    /// CAP_NET_ADMIN: root, the rights set on the sing-box file once with
    /// setcap, or pkexec, which asks for the password on every connect.
    /// </summary>
    public static TunAccess ForTun(string singBoxPath)
    {
        if (OperatingSystem.IsWindows()) return IsAdministrator() ? new TunAccess.Ready(CoreElevation.None) : new TunAccess.NeedsAdministrator();
        if (IsRoot() || HasFileCapabilities(singBoxPath)) return new TunAccess.Ready(CoreElevation.None);
        if (Tool.Find("pkexec") is not null) return new TunAccess.Ready(CoreElevation.Pkexec);
        return new TunAccess.Unavailable($"Для режима VPN нужны права на сетевые настройки. Выполните один раз в терминале:\n{SetcapCommand(singBoxPath)}");
    }

    /// <summary>The command that lets the core make the adapter without a password from then on.</summary>
    public static string SetcapCommand(string singBoxPath) =>
        $"sudo setcap cap_net_admin,cap_net_raw,cap_net_bind_service+ep '{singBoxPath.Replace("'", "'\\''")}'";

    public static bool IsAdministrator()
    {
        if (!OperatingSystem.IsWindows()) return false;
        return WindowsAdministrator();
    }

    [SupportedOSPlatform("windows")]
    private static bool WindowsAdministrator()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
    }

    public static bool IsRoot()
    {
        if (!OperatingSystem.IsLinux()) return false;
        try { return geteuid() == 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>Whether setcap has been run on the file (it keeps them in the security.capability attribute).</summary>
    public static bool HasFileCapabilities(string path)
    {
        if (!OperatingSystem.IsLinux() || !File.Exists(path)) return false;
        try { return getxattr(path, "security.capability", IntPtr.Zero, 0) > 0; }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return false; }
    }

    /// <summary>
    /// Starts this app again as administrator (Windows asks first) with
    /// <paramref name="arguments"/>. False when the user said no.
    /// </summary>
    public static bool RelaunchAsAdministrator(string executable, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = true,
            Verb = "runas",
            Arguments = string.Join(" ", arguments.Select(a => a.Contains(' ') ? $"\"{a}\"" : a)),
        };
        try
        {
            using var process = Process.Start(info);
            return process is not null;
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 1223) // ERROR_CANCELLED
        {
            return false;
        }
    }

    [DllImport("libc.so.6")]
    private static extern uint geteuid();

    [DllImport("libc.so.6", SetLastError = true)]
    private static extern nint getxattr([MarshalAs(UnmanagedType.LPUTF8Str)] string path, [MarshalAs(UnmanagedType.LPUTF8Str)] string name, IntPtr value, nuint size);
}
