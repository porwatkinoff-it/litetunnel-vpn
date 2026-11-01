using System.Security.Cryptography;
using System.Text;
using Microsoft.Win32;

namespace LiteTunnel.Core;

/// <summary>
/// What a subscription request says about this device. Panels with a device
/// limit (Remnawave and others) count devices by <c>x-hwid</c> and give clients
/// that do not send it a placeholder server instead of the list.
/// https://docs.rw/docs/features/hwid-device-limit
/// </summary>
/// <param name="Os">"Windows" or "Linux".</param>
/// <param name="OsVersion">"10.0.26100" or "Ubuntu 24.04".</param>
/// <param name="Model">The board or laptop model the firmware reports.</param>
public sealed record DeviceInfo(string Hwid, string Os, string OsVersion, string Model)
{
    public IReadOnlyDictionary<string, string> Headers => new Dictionary<string, string>
    {
        ["x-hwid"] = Hwid,
        ["x-device-os"] = Header(Os),
        ["x-ver-os"] = Header(OsVersion),
        ["x-device-model"] = Header(Model),
    };

    /// <summary>
    /// A stable ID from the machine ID (MachineGuid on Windows, /etc/machine-id
    /// on Linux), so a reinstall does not take another of the subscription's
    /// device slots. Hashed: the provider gets an ID only for this app, never
    /// the machine ID. 32 hex characters fit the panel's rule (10-64 of A-Z,
    /// a-z, 0-9, <c>=</c>, <c>-</c>).
    /// </summary>
    public static string HwidFromSeed(string seed)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes($"LiteTunnel:{seed}"));
        return Convert.ToHexStringLower(digest, 0, 16);
    }

    /// <summary>HTTP header values must be printable ASCII.</summary>
    internal static string Header(string value)
    {
        var s = new string(value.Select(c => c is >= ' ' and <= '~' ? c : '_').ToArray()).Trim();
        return s.Length == 0 ? "unknown" : s;
    }

    /// <summary>
    /// This machine. Without a machine ID the app makes a random one and keeps
    /// it in <paramref name="dataDirectory"/>.
    /// </summary>
    public static DeviceInfo Detect(string dataDirectory)
    {
        var seed = MachineId();
        var hwid = seed is not null ? HwidFromSeed(seed) : SavedRandomId(Path.Combine(dataDirectory, "hwid"));
        if (OperatingSystem.IsWindows())
        {
            var v = Environment.OSVersion.Version;
            return new DeviceInfo(hwid, "Windows", $"{v.Major}.{v.Minor}.{v.Build}", WindowsModel() ?? "PC");
        }
        if (OperatingSystem.IsLinux()) return new DeviceInfo(hwid, "Linux", LinuxVersion(), LinuxModel() ?? "PC");
        if (OperatingSystem.IsMacOS())
        {
            var v = Environment.OSVersion.Version;
            return new DeviceInfo(hwid, "macOS", $"{v.Major}.{v.Minor}", "Mac");
        }
        return new DeviceInfo(hwid, Environment.OSVersion.Platform.ToString(), Environment.OSVersion.VersionString, "PC");
    }

    private static string? MachineId()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Cryptography");
                return TextUtil.NonEmpty((key?.GetValue("MachineGuid") as string)?.Trim());
            }
            if (OperatingSystem.IsLinux())
            {
                foreach (var path in new[] { "/etc/machine-id", "/var/lib/dbus/machine-id" })
                    if (File.Exists(path) && TextUtil.NonEmpty(File.ReadAllText(path).Trim()) is { } id) return id;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
        }
        return null;
    }

    private static string SavedRandomId(string file)
    {
        try
        {
            if (File.Exists(file) && File.ReadAllText(file).Trim() is { Length: >= 10 and <= 64 } saved) return saved;
        }
        catch (IOException)
        {
        }
        var id = Guid.NewGuid().ToString().ToUpperInvariant();
        try
        {
            Store.WriteAtomically(file, Encoding.UTF8.GetBytes(id));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return id;
    }

    private static string? WindowsModel()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            using var key = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\BIOS");
            return TextUtil.NonEmpty((key?.GetValue("SystemProductName") as string)?.Trim());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return null;
        }
    }

    private static string? LinuxModel()
    {
        try
        {
            const string path = "/sys/class/dmi/id/product_name";
            return File.Exists(path) ? TextUtil.NonEmpty(File.ReadAllText(path).Trim()) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>"Ubuntu 24.04" from os-release, or the kernel version.</summary>
    private static string LinuxVersion()
    {
        try
        {
            foreach (var path in new[] { "/etc/os-release", "/usr/lib/os-release" })
            {
                if (!File.Exists(path)) continue;
                var fields = OsRelease(File.ReadAllText(path));
                var name = fields.GetValueOrDefault("NAME");
                var version = fields.GetValueOrDefault("VERSION_ID");
                var text = string.Join(" ", new[] { name, version }.Where(s => !string.IsNullOrEmpty(s)));
                if (text.Length > 0) return text;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        return Environment.OSVersion.Version.ToString();
    }

    internal static Dictionary<string, string> OsRelease(string text)
    {
        var fields = new Dictionary<string, string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var (key, value) = TextUtil.SplitOnce(line, '=');
            if (value is null) continue;
            value = value.Trim();
            if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0]) value = value[1..^1];
            fields[key.Trim()] = value;
        }
        return fields;
    }
}
