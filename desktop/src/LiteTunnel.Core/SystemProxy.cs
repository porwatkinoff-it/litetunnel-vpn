using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

namespace LiteTunnel.Core;

public sealed class SystemProxyException(string message) : Exception(message);

/// <summary>
/// One place the system keeps its proxy: WinINet on Windows, GNOME or KDE
/// settings on Linux. <see cref="Read"/> takes what is there now so
/// <see cref="Write"/> can put it back.
/// </summary>
public interface IProxyBackend
{
    string Name { get; }

    Json Read();

    void Point(int port, IReadOnlyList<string> bypass);

    void Write(Json saved);

    /// <summary>Whether the setting still points at our port, so restoring it does not undo a change the user made since.</summary>
    bool PointsAt(int port);
}

/// <summary>
/// Switches the system proxy to the local core and back. What was there
/// before goes to <c>proxy-backup.json</c> first, so it can be put back after
/// a crash: by the watchdog (a copy of this app that waits for ours to go),
/// at the next launch, or at the next login (RunOnce on Windows, autostart
/// on Linux) if the machine lost power.
/// </summary>
public sealed class SystemProxy(string backupPath, IReadOnlyList<IProxyBackend> backends, string? executablePath)
{
    public const string WatchdogArgument = "--watchdog";
    public const string RestoreArgument = "--restore-proxy";

    private Process? watchdog;

    public static SystemProxy ForThisSystem(string dataDirectory, string? executablePath) =>
        new(Path.Combine(dataDirectory, "proxy-backup.json"), DetectBackends(), executablePath);

    /// <summary>Why there is nowhere to set the proxy, when there is not.</summary>
    public string? Unsupported => backends.Count > 0 ? null
        : "На этом рабочем столе системный прокси не настраивается. Включите режим VPN (TUN) или укажите в программах прокси 127.0.0.1.";

    public bool HasBackup => File.Exists(backupPath);

    /// <summary>Hosts the system sends past the proxy, in each system's notation.</summary>
    public static List<string> Bypass(bool excludeLocalNetworks, bool windows)
    {
        var hosts = windows
            ? new List<string> { "localhost", "127.*", "::1", "*.local", "169.254.*", "<local>" }
            : ["localhost", "127.0.0.0/8", "::1", "*.local", "169.254.0.0/16"];
        if (excludeLocalNetworks)
        {
            if (windows)
            {
                hosts.Add("10.*");
                hosts.AddRange(Enumerable.Range(16, 16).Select(n => $"172.{n}.*"));
                hosts.Add("192.168.*");
            }
            else
            {
                hosts.AddRange(["10.0.0.0/8", "172.16.0.0/12", "192.168.0.0/16"]);
            }
        }
        return hosts;
    }

    /// <summary>Saves what is set now, then points every backend at the port. Undoes itself if any step fails.</summary>
    public void Enable(int port, bool excludeLocalNetworks)
    {
        if (Unsupported is { } reason) throw new SystemProxyException(reason);
        Restore(); // leftovers first, so they are not taken for the user's settings
        var saved = Json.Object(
            ("port", port),
            ("backends", Json.Object(backends.Select(b => (b.Name, (Json?)b.Read())).ToArray())));
        try
        {
            Store.WriteAtomically(backupPath, Encoding.UTF8.GetBytes(saved.ToJsonString()));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw new SystemProxyException($"Не удалось сохранить текущие настройки прокси: {e.Message}");
        }
        StartWatchdog();
        MarkForNextLogin(true);
        try
        {
            var bypass = Bypass(excludeLocalNetworks, OperatingSystem.IsWindows());
            foreach (var backend in backends) backend.Point(port, bypass);
        }
        catch (Exception e)
        {
            Restore();
            throw e as SystemProxyException ?? new SystemProxyException($"Не удалось включить системный прокси: {e.Message}");
        }
    }

    /// <summary>Puts back what the backup holds and removes it, then lets the watchdog go. Does nothing without a backup.</summary>
    public void Restore()
    {
        RestoreFrom(backupPath, backends);
        MarkForNextLogin(false);
        StopWatchdog();
    }

    /// <summary>Restores from the backup file, if any; for the watchdog and <c>--restore-proxy</c>.</summary>
    public static void RestoreFrom(string backupPath, IReadOnlyList<IProxyBackend> backends)
    {
        Json saved;
        try
        {
            if (!File.Exists(backupPath)) return;
            saved = Json.Parse(File.ReadAllText(backupPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            try { File.Delete(backupPath); } catch (IOException) { }
            return;
        }
        int port = saved["port"]?.AsInt ?? 0;
        foreach (var backend in backends)
        {
            if (saved["backends"]?[backend.Name] is not { } values) continue;
            try
            {
                if (backend.PointsAt(port)) backend.Write(values);
            }
            catch (Exception e) when (e is SystemProxyException or IOException or System.ComponentModel.Win32Exception)
            {
                // Best effort: the next launch tries again only if the file stays, and it should not loop forever.
            }
        }
        try { File.Delete(backupPath); } catch (IOException) { }
    }

    /// <summary>
    /// The watchdog: a second copy of the app that blocks on its stdin. When
    /// this app goes, crash or not, the pipe closes and it restores whatever
    /// the backup still holds. After a normal stop there is no backup left.
    /// </summary>
    private void StartWatchdog()
    {
        if (executablePath is null || watchdog is { HasExited: false }) return;
        try
        {
            var info = new ProcessStartInfo(executablePath, [WatchdogArgument, backupPath])
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
            };
            watchdog = Process.Start(info);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException)
        {
            watchdog = null; // the next launch still restores
        }
    }

    /// <summary>Entry point for <c>--watchdog</c>: waits for the parent, then restores.</summary>
    public static int RunWatchdog(string backupPath)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            var buffer = new byte[64];
            while (input.Read(buffer, 0, buffer.Length) > 0) { }
        }
        catch (IOException) { }
        RestoreFrom(backupPath, DetectBackends());
        if (!File.Exists(backupPath)) RemoveLoginEntry();
        return 0;
    }

    /// <summary>Lets the watchdog see the parent go away now: with no backup left it just exits.</summary>
    private void StopWatchdog()
    {
        try { watchdog?.StandardInput.Close(); } catch (IOException) { }
        watchdog?.Dispose();
        watchdog = null;
    }

    private void MarkForNextLogin(bool on)
    {
        if (executablePath is null) return;
        try
        {
            if (on) AddLoginEntry(executablePath);
            else RemoveLoginEntry();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
    }

    private const string LoginEntryName = "LiteTunnelRestoreProxy";

    private static string AutostartFile => Path.Combine(
        Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } config ? config
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"),
        "autostart", "litetunnel-restore-proxy.desktop");

    private static void AddLoginEntry(string executable)
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce");
            key.SetValue(LoginEntryName, $"\"{executable}\" {RestoreArgument}");
        }
        else if (OperatingSystem.IsLinux())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AutostartFile)!);
            File.WriteAllText(AutostartFile,
                "[Desktop Entry]\nType=Application\nName=LiteTunnel: вернуть прокси\nNoDisplay=true\n" +
                $"Exec=\"{executable.Replace("\\", "\\\\").Replace("\"", "\\\"")}\" {RestoreArgument}\n");
        }
    }

    private static void RemoveLoginEntry()
    {
        if (OperatingSystem.IsWindows())
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\RunOnce", writable: true);
            key?.DeleteValue(LoginEntryName, throwOnMissingValue: false);
        }
        else if (OperatingSystem.IsLinux())
        {
            File.Delete(AutostartFile);
        }
    }

    public static List<IProxyBackend> DetectBackends()
    {
        var list = new List<IProxyBackend>();
        if (OperatingSystem.IsWindows())
        {
            list.Add(new WinInetProxy());
        }
        else if (OperatingSystem.IsLinux())
        {
            if (KdeProxy.Available() is { } kde) list.Add(kde);
            if (GnomeProxy.Available()) list.Add(new GnomeProxy());
        }
        return list;
    }
}

/// <summary>Runs small settings tools and waits for them.</summary>
internal static class Tool
{
    public static (int Code, string Output) Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file, args)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
        };
        using var process = Process.Start(info) ?? throw new IOException($"{file} не запустился");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10_000))
        {
            try { process.Kill(); } catch (InvalidOperationException) { }
            throw new IOException($"{file} не ответил за 10 с");
        }
        return (process.ExitCode, process.ExitCode == 0 ? output.Result.Trim() : error.Result.Trim());
    }

    public static string Check(string file, params string[] args)
    {
        var (code, output) = Run(file, args);
        if (code != 0) throw new SystemProxyException($"{file}: {output}");
        return output;
    }

    public static string? Find(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "/usr/bin:/bin").Split(Path.PathSeparator)
            .Select(dir => Path.Combine(dir, name))
            .FirstOrDefault(File.Exists);
}

/// <summary>GNOME and the desktops that share its settings (Cinnamon, Budgie, MATE in part).</summary>
public sealed class GnomeProxy : IProxyBackend
{
    private const string Schema = "org.gnome.system.proxy";
    private static readonly (string Schema, string Key)[] Keys =
    [
        (Schema, "mode"), (Schema, "ignore-hosts"),
        (Schema + ".http", "host"), (Schema + ".http", "port"),
        (Schema + ".https", "host"), (Schema + ".https", "port"),
        (Schema + ".socks", "host"), (Schema + ".socks", "port"),
    ];

    public string Name => "gnome";

    public static bool Available()
    {
        if (Tool.Find("gsettings") is null) return false;
        try
        {
            return Tool.Run("gsettings", "list-keys", Schema).Code == 0;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    // Values stay in GVariant text form, exactly as gsettings prints and takes them.
    public Json Read() =>
        Json.Object(Keys.Select(k => ($"{k.Schema} {k.Key}", (Json?)Tool.Check("gsettings", "get", k.Schema, k.Key))).ToArray());

    public void Point(int port, IReadOnlyList<string> bypass)
    {
        foreach (var kind in new[] { "http", "https", "socks" })
        {
            Tool.Check("gsettings", "set", $"{Schema}.{kind}", "host", "'127.0.0.1'");
            Tool.Check("gsettings", "set", $"{Schema}.{kind}", "port", port.ToString());
        }
        Tool.Check("gsettings", "set", Schema, "ignore-hosts", "[" + string.Join(", ", bypass.Select(h => $"'{h}'")) + "]");
        Tool.Check("gsettings", "set", Schema, "mode", "'manual'");
    }

    public void Write(Json saved)
    {
        // The mode first, so nothing uses half-restored hosts.
        foreach (var (schema, key) in Keys)
        {
            if (saved[$"{schema} {key}"]?.AsString is { } value) Tool.Run("gsettings", "set", schema, key, value);
        }
    }

    public bool PointsAt(int port) =>
        Tool.Check("gsettings", "get", Schema, "mode") == "'manual'"
        && Tool.Check("gsettings", "get", Schema + ".http", "host") == "'127.0.0.1'"
        && Tool.Check("gsettings", "get", Schema + ".http", "port") == port.ToString();
}

/// <summary>KDE Plasma 5 and 6: kioslaverc, then a signal so running programs reread it.</summary>
public sealed class KdeProxy(string write, string read) : IProxyBackend
{
    private const string Group = "Proxy Settings";
    private static readonly string[] Keys = ["ProxyType", "httpProxy", "httpsProxy", "socksProxy", "NoProxyFor"];

    public string Name => "kde";

    public static KdeProxy? Available()
    {
        var desktop = Environment.GetEnvironmentVariable("XDG_CURRENT_DESKTOP") ?? "";
        if (!desktop.Contains("KDE", StringComparison.OrdinalIgnoreCase)) return null;
        foreach (var version in new[] { "6", "5" })
        {
            if (Tool.Find($"kwriteconfig{version}") is not null && Tool.Find($"kreadconfig{version}") is not null)
                return new KdeProxy($"kwriteconfig{version}", $"kreadconfig{version}");
        }
        return null;
    }

    private string Get(string key) => Tool.Check(read, "--file", "kioslaverc", "--group", Group, "--key", key);

    private void Set(string key, string? value)
    {
        if (value is null) Tool.Check(write, "--file", "kioslaverc", "--group", Group, "--key", key, "--delete");
        else Tool.Check(write, "--file", "kioslaverc", "--group", Group, "--key", key, value);
    }

    public Json Read() => Json.Object(Keys.Select(k => (k, (Json?)Get(k))).ToArray());

    public void Point(int port, IReadOnlyList<string> bypass)
    {
        Set("httpProxy", $"http://127.0.0.1 {port}");
        Set("httpsProxy", $"http://127.0.0.1 {port}");
        Set("socksProxy", $"socks://127.0.0.1 {port}");
        Set("NoProxyFor", string.Join(",", bypass));
        Set("ProxyType", "1");
        Notify();
    }

    public void Write(Json saved)
    {
        foreach (var key in Keys) // ProxyType first: off before the hosts change
        {
            var value = saved[key]?.AsString;
            Set(key, string.IsNullOrEmpty(value) ? null : value);
        }
        Notify();
    }

    public bool PointsAt(int port) => Get("ProxyType") == "1" && Get("httpProxy").Contains($"127.0.0.1 {port}");

    private static void Notify()
    {
        try
        {
            Tool.Run("dbus-send", "--type=signal", "/KIO/Scheduler", "org.kde.KIO.Scheduler.reparseSlaveConfiguration", "string:");
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception)
        {
            // Programs started from now on read the new file anyway.
        }
    }
}

/// <summary>
/// The per-user proxy of WinINet: what Settings → Proxy shows, and what
/// Edge, Chrome and most programs use. Changed through the API, so running
/// programs are told about it.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WinInetProxy : IProxyBackend
{
    private const int PerConnectionOption = 75, SettingsChanged = 39, Refresh = 37;
    private const int Flags = 1, ProxyServer = 2, ProxyBypass = 3, AutoConfigUrl = 4, FlagsUi = 10;
    private const int Direct = 1, ProxyOn = 2;

    public string Name => "wininet";

    public Json Read()
    {
        var (flags, server, bypass, autoconfig) = Query();
        return Json.Object(("flags", flags), ("server", server), ("bypass", bypass), ("autoconfig", autoconfig));
    }

    public void Point(int port, IReadOnlyList<string> bypass) =>
        Set(Direct | ProxyOn, $"127.0.0.1:{port}", string.Join(";", bypass), null);

    public void Write(Json saved) =>
        Set(saved["flags"]?.AsInt ?? Direct, saved["server"]?.AsString, saved["bypass"]?.AsString, saved["autoconfig"]?.AsString);

    public bool PointsAt(int port)
    {
        var (flags, server, _, _) = Query();
        return (flags & ProxyOn) != 0 && server == $"127.0.0.1:{port}";
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct Option
    {
        [FieldOffset(0)] public int Id;
        [FieldOffset(8)] public IntPtr Value;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct OptionList
    {
        public int Size;
        public IntPtr Connection;
        public int OptionCount;
        public int OptionError;
        public IntPtr Options;
    }

    private static (int Flags, string? Server, string? Bypass, string? AutoConfig) Query()
    {
        // FLAGS_UI is what Windows itself reads; older systems only know FLAGS.
        return TryQuery(FlagsUi) ?? TryQuery(Flags) ?? throw new SystemProxyException($"Не удалось прочитать настройки прокси (код {Marshal.GetLastWin32Error()})");
    }

    private static (int, string?, string?, string?)? TryQuery(int flagsOption)
    {
        int[] ids = [flagsOption, ProxyServer, ProxyBypass, AutoConfigUrl];
        int optionSize = Marshal.SizeOf<Option>();
        var options = Marshal.AllocHGlobal(optionSize * ids.Length);
        try
        {
            for (int i = 0; i < ids.Length; i++) Marshal.StructureToPtr(new Option { Id = ids[i] }, options + i * optionSize, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), OptionCount = ids.Length, Options = options };
            int size = list.Size;
            if (!InternetQueryOption(IntPtr.Zero, PerConnectionOption, ref list, ref size)) return null;
            var values = Enumerable.Range(0, ids.Length).Select(i => Marshal.PtrToStructure<Option>(options + i * optionSize).Value).ToArray();
            string? Text(IntPtr p)
            {
                if (p == IntPtr.Zero) return null;
                var s = Marshal.PtrToStringUni(p);
                GlobalFree(p);
                return string.IsNullOrEmpty(s) ? null : s;
            }
            return ((int)values[0], Text(values[1]), Text(values[2]), Text(values[3]));
        }
        finally
        {
            Marshal.FreeHGlobal(options);
        }
    }

    private static void Set(int flags, string? server, string? bypass, string? autoconfig)
    {
        var strings = new[] { server, bypass, autoconfig }.Select(s => s is null ? IntPtr.Zero : Marshal.StringToHGlobalUni(s)).ToArray();
        int optionSize = Marshal.SizeOf<Option>();
        var options = Marshal.AllocHGlobal(optionSize * 4);
        try
        {
            Marshal.StructureToPtr(new Option { Id = Flags, Value = flags }, options, false);
            Marshal.StructureToPtr(new Option { Id = ProxyServer, Value = strings[0] }, options + optionSize, false);
            Marshal.StructureToPtr(new Option { Id = ProxyBypass, Value = strings[1] }, options + 2 * optionSize, false);
            Marshal.StructureToPtr(new Option { Id = AutoConfigUrl, Value = strings[2] }, options + 3 * optionSize, false);
            var list = new OptionList { Size = Marshal.SizeOf<OptionList>(), OptionCount = 4, Options = options };
            if (!InternetSetOption(IntPtr.Zero, PerConnectionOption, ref list, list.Size))
                throw new SystemProxyException($"Windows не приняла настройки прокси (код {Marshal.GetLastWin32Error()})");
            InternetSetOption(IntPtr.Zero, SettingsChanged, IntPtr.Zero, 0);
            InternetSetOption(IntPtr.Zero, Refresh, IntPtr.Zero, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(options);
            foreach (var s in strings) if (s != IntPtr.Zero) Marshal.FreeHGlobal(s);
        }
    }

    [DllImport("wininet.dll", EntryPoint = "InternetQueryOptionW", SetLastError = true)]
    private static extern bool InternetQueryOption(IntPtr handle, int option, ref OptionList buffer, ref int length);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, ref OptionList buffer, int length);

    [DllImport("wininet.dll", EntryPoint = "InternetSetOptionW", SetLastError = true)]
    private static extern bool InternetSetOption(IntPtr handle, int option, IntPtr buffer, int length);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalFree(IntPtr memory);
}
