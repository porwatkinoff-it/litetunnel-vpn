using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace LiteTunnel.App;

/// <summary>
/// One window per user. The first copy holds a lock file and listens on a
/// pipe; a second copy, started from a link or the menu, hands over what it
/// was given and quits. The pipe only waits: it costs nothing while idle.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    public const string Show = "show";

    private static readonly string PipeName = $"LiteTunnel-{Environment.UserName}";

    private readonly FileStream lockFile;
    private readonly CancellationTokenSource stop = new();

    private SingleInstance(FileStream lockFile) => this.lockFile = lockFile;

    /// <summary>Null when another copy runs.</summary>
    public static SingleInstance? Acquire(string dataDirectory)
    {
        Directory.CreateDirectory(dataDirectory);
        var path = Path.Combine(dataDirectory, "instance.lock");
        // Right after an elevated relaunch the old copy may still be letting go.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return new SingleInstance(new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None));
            }
            catch (IOException) when (attempt < 10)
            {
                Thread.Sleep(200);
            }
            catch (IOException)
            {
                return null;
            }
            catch (UnauthorizedAccessException)
            {
                return null;
            }
        }
    }

    /// <summary>Starts listening. <paramref name="onMessage"/> runs on a worker thread.</summary>
    public void Listen(Action<string> onMessage)
    {
        if (!OperatingSystem.IsWindows())
        {
            // A socket file left by a crash would stop the new server binding.
            try { File.Delete(Path.Combine(Path.GetTempPath(), "CoreFxPipe_" + PipeName)); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _ = Task.Run(() => LoopAsync(onMessage));
    }

    private async Task LoopAsync(Action<string> onMessage)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = CreateServer();
                await server.WaitForConnectionAsync(stop.Token);
                using var reader = new StreamReader(server, Encoding.UTF8);
                var message = await reader.ReadLineAsync(stop.Token);
                if (!string.IsNullOrWhiteSpace(message)) onMessage(message.Trim());
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (IOException)
            {
                await Task.Delay(500);
            }
            catch (UnauthorizedAccessException)
            {
                return;
            }
        }
    }

    private static NamedPipeServerStream CreateServer() =>
        OperatingSystem.IsWindows()
            ? CreateWindowsServer()
            : new NamedPipeServerStream(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    /// <summary>
    /// The copy running as administrator must still hear the plain one the
    /// user starts from a link, so the pipe lets this user in by name.
    /// </summary>
    [SupportedOSPlatform("windows")]
    private static NamedPipeServerStream CreateWindowsServer()
    {
        var security = new PipeSecurity();
        if (WindowsIdentity.GetCurrent().User is { } user)
            security.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite | PipeAccessRights.CreateNewInstance, AccessControlType.Allow));
        return NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
    }

    public static void Send(string message)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, OperatingSystem.IsWindows() ? PipeOptions.None : PipeOptions.CurrentUserOnly);
            client.Connect(3000);
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(message);
        }
        catch (Exception e) when (e is IOException or TimeoutException or UnauthorizedAccessException)
        {
            // The other copy is busy quitting: nothing to bring up.
        }
    }

    public void Dispose()
    {
        stop.Cancel();
        lockFile.Dispose();
    }
}
