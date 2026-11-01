using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace LiteTunnel.Core;

/// <summary>How the core gets the rights it needs.</summary>
public enum CoreElevation
{
    /// <summary>As this app: system proxy mode, or TUN when the app or the binary already has the rights.</summary>
    None,

    /// <summary>Linux: through pkexec, which asks for the password.</summary>
    Pkexec,
}

/// <summary>
/// One running sing-box. It cannot outlive the app: on Windows it sits in a
/// job object that the system kills with the app, on Linux a small shell
/// watchdog stops it when this app's end of its stdin closes, crash or not.
/// </summary>
public sealed class CoreProcess : IDisposable
{
    /// <summary>
    /// Linux watchdog. sing-box gets no stdin; a background reader takes
    /// lines from ours: <c>reload</c> sends HUP, so the core reads its config
    /// again with the rights it has, and EOF sends TERM, so the core removes
    /// its routes as on a normal stop. The shell exits with the core's code.
    /// </summary>
    internal const string LinuxWatchdog =
        "exec 3<&0\n" +
        "\"$1\" run -c \"$2\" -D \"$3\" --disable-color </dev/null 3<&- &\n" +
        "pid=$!\n" +
        "{ while read -r line <&3; do [ \"$line\" = reload ] && kill -HUP \"$pid\" 2>/dev/null; done; kill -TERM \"$pid\" 2>/dev/null; } </dev/null >/dev/null 2>&1 &\n" +
        "exec 3<&-\n" +
        "wait \"$pid\"\n";

    private readonly Process process;
    private readonly CoreLog? log;
    private readonly StringBuilder lastLines = new();
    private bool stopping;

    private CoreProcess(Process process, CoreLog? log)
    {
        this.process = process;
        this.log = log;
    }

    /// <summary>Finishes with the exit code when the core stops, for any reason.</summary>
    public Task<int> Exited { get; private set; } = Task.FromResult(-1);

    public bool HasExited => Exited.IsCompleted;

    /// <summary>The last lines the core wrote: what to show when it fails to start.</summary>
    public string LastOutput
    {
        get { lock (lastLines) return lastLines.ToString().Trim(); }
    }

    public static CoreProcess Start(string executable, string configPath, string workDirectory, CoreElevation elevation = CoreElevation.None, CoreLog? log = null)
    {
        Directory.CreateDirectory(workDirectory);
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = workDirectory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows())
        {
            info.FileName = executable;
            foreach (var arg in new[] { "run", "-c", configPath, "-D", workDirectory, "--disable-color" }) info.ArgumentList.Add(arg);
        }
        else
        {
            if (elevation == CoreElevation.Pkexec)
            {
                info.FileName = "pkexec";
                info.ArgumentList.Add("/bin/sh");
            }
            else
            {
                info.FileName = "/bin/sh";
            }
            foreach (var arg in new[] { "-c", LinuxWatchdog, "sh", executable, configPath, workDirectory }) info.ArgumentList.Add(arg);
        }

        var process = new Process { StartInfo = info, EnableRaisingEvents = true };
        var core = new CoreProcess(process, log);
        process.OutputDataReceived += (_, e) => core.OnLine(e.Data);
        process.ErrorDataReceived += (_, e) => core.OnLine(e.Data);
        process.Start();
        // Only a started process can be waited for.
        core.Exited = process.WaitForExitAsync().ContinueWith(_ => process.ExitCode, TaskScheduler.Default);
        if (OperatingSystem.IsWindows()) WindowsJob.Assign(process);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        return core;
    }

    private void OnLine(string? line)
    {
        if (string.IsNullOrEmpty(line)) return;
        log?.Write(line);
        lock (lastLines)
        {
            lastLines.AppendLine(line);
            if (lastLines.Length > 4096) lastLines.Remove(0, lastLines.Length - 2048);
        }
    }

    /// <summary>
    /// Linux: has the core read its config file again in place, so pkexec
    /// does not ask for the password again. False on Windows, where the
    /// core has to be started anew, and when it is not running.
    /// </summary>
    public bool Reload()
    {
        if (OperatingSystem.IsWindows() || stopping || HasExited) return false;
        try
        {
            process.StandardInput.WriteLine("reload");
            process.StandardInput.Flush();
            return true;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Asks the core to stop and waits for it, then makes sure it is gone if it is ours to kill.</summary>
    public async Task StopAsync(TimeSpan grace)
    {
        if (stopping) { await Exited.ConfigureAwait(false); return; }
        stopping = true;
        if (HasExited) return;
        if (OperatingSystem.IsWindows())
        {
            KillTree();
        }
        else
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
        }
        if (await Task.WhenAny(Exited, Task.Delay(grace)).ConfigureAwait(false) != Exited) KillTree();
        await Task.WhenAny(Exited, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
    }

    private void KillTree()
    {
        try { process.Kill(entireProcessTree: true); }
        catch (Exception e) when (e is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }

    public void Dispose()
    {
        if (!HasExited)
        {
            try { process.StandardInput.Close(); } catch (IOException) { }
            KillTree();
        }
        process.Dispose();
    }

    /// <summary>pkexec: the user closed the password dialog (126) or is not allowed (127).</summary>
    public static bool IsPkexecRefusal(int exitCode) => exitCode is 126 or 127;

    /// <summary>A job object that kills what is in it when this app goes away.</summary>
    private static class WindowsJob
    {
        private static readonly Lazy<IntPtr> Handle = new(Create);

        public static void Assign(Process process)
        {
            if (Handle.Value != IntPtr.Zero) AssignProcessToJobObject(Handle.Value, process.Handle);
        }

        private static IntPtr Create()
        {
            var job = CreateJobObject(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JobObjectExtendedLimitInformation();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, buffer, false);
                SetInformationJobObject(job, 9, buffer, (uint)size); // JobObjectExtendedLimitInformation
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
            return job;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectBasicLimitInformation
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IoCounters
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JobObjectExtendedLimitInformation
        {
            public JobObjectBasicLimitInformation BasicLimitInformation;
            public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateJobObject(IntPtr attributes, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
    }
}

/// <summary>
/// The core's own output, kept in one file of about a megabyte: when it grows
/// past that, the file starts over and the previous one stays as <c>.old</c>.
/// </summary>
public sealed class CoreLog(string path, long limit = 1 << 20)
{
    private readonly object gate = new();

    public string Path => path;

    public void Write(string line)
    {
        lock (gate)
        {
            try
            {
                var file = new FileInfo(path);
                if (file.Exists && file.Length > limit) File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {line}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    public string Read()
    {
        lock (gate)
        {
            try { return File.Exists(path) ? File.ReadAllText(path) : ""; }
            catch (IOException) { return ""; }
        }
    }

    /// <summary>The end of the log, about <paramref name="bytes"/> long, from the start of a line.</summary>
    public string Tail(int bytes = 64 * 1024)
    {
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            long start = Math.Max(0, file.Length - bytes);
            file.Seek(start, SeekOrigin.Begin);
            using var reader = new StreamReader(file, Encoding.UTF8);
            var text = reader.ReadToEnd();
            if (start > 0 && text.IndexOf('\n') is int newline and >= 0) text = text[(newline + 1)..];
            return text;
        }
        catch (FileNotFoundException) { return ""; }
        catch (DirectoryNotFoundException) { return ""; }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    public void Clear()
    {
        lock (gate)
        {
            try
            {
                File.Delete(path);
                File.Delete(path + ".old");
            }
            catch (IOException) { }
        }
    }
}
