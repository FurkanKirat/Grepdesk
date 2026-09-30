using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Grepdesk.UI.Startup;

/// <summary>
/// Explorer starts one process per selected item, so selecting 20 files and
/// choosing "Compress" launches Grepdesk 20 times. The first process to take
/// the mutex becomes the primary and listens on a named pipe; the others hand
/// their command over the pipe and exit before Avalonia even starts. The
/// primary then merges commands of the same kind into one job (JobDispatcher).
/// </summary>
public sealed class SingleInstanceHost : IDisposable
{
    private static readonly string Id = MakeId();
    private static readonly string MutexName = $@"Local\Grepdesk.Jobs.{Id}";
    private static readonly string PipeName = $"Grepdesk.Jobs.{Id}";

    private readonly Mutex _mutex;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<AppCommand> _commands = Channel.CreateUnbounded<AppCommand>();
    private Task? _listener;
    private bool _stopped;

    private SingleInstanceHost(Mutex mutex) => _mutex = mutex;

    /// <summary>Commands forwarded by other processes, in arrival order.</summary>
    public ChannelReader<AppCommand> Commands => _commands.Reader;

    /// <summary>Returns the host if this process is the primary, otherwise null.</summary>
    public static SingleInstanceHost? TryAcquire()
    {
        var mutex = new Mutex(initiallyOwned: false, MutexName);
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // previous primary crashed; we own it now

        if (owned)
            return new SingleInstanceHost(mutex);

        mutex.Dispose();
        return null;
    }

    /// <summary>Hands a command to the primary. False if it can't be reached in time.</summary>
    public static bool TrySend(AppCommand command)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            // Connect waits for the pipe to appear, so this also covers a
            // primary that is still starting up.
            client.Connect(TimeSpan.FromSeconds(5));
            using var writer = new StreamWriter(client, new UTF8Encoding(false));
            writer.WriteLine(command.Serialize());
            writer.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public void StartListening() => _listener = Task.Run(() => ListenAsync(_cts.Token));

    private async Task ListenAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(PipeName, PipeDirection.In,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8);
                var line = await reader.ReadLineAsync(ct);
                if (line is not null && AppCommand.Deserialize(line) is { } command)
                    _commands.Writer.TryWrite(command);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException)
            {
                // A client that vanished mid-message; keep serving the others.
            }
        }
    }

    /// <summary>
    /// Stops accepting commands and releases the mutex, so the next Explorer
    /// click starts a fresh primary. Commands already received stay readable
    /// from <see cref="Commands"/>. Must run on the thread that created the host.
    /// </summary>
    public void StopAccepting()
    {
        if (_stopped)
            return;
        _stopped = true;

        _cts.Cancel();
        try { _listener?.Wait(TimeSpan.FromSeconds(1)); }
        catch (AggregateException) { }

        _commands.Writer.TryComplete();
        _mutex.ReleaseMutex();
    }

    public void Dispose()
    {
        StopAccepting();
        _mutex.Dispose();
        _cts.Dispose();
    }

    // Per-user names without putting the raw user name (which may hold
    // characters invalid in object names) into them.
    private static string MakeId()
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Environment.UserDomainName + "\\" + Environment.UserName));
        return Convert.ToHexString(hash)[..16];
    }
}
