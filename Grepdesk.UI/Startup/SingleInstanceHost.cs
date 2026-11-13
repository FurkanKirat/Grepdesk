using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Grepdesk.UI.Startup;

/// <summary>
/// Keeps a single primary GUI process per user session and forwards commands
/// from subsequent launches over a named pipe before Avalonia starts.
/// Handles both plain launches (<see cref="CommandKind.OpenSearch"/>,
/// <see cref="CommandKind.OpenFolder"/>, <see cref="CommandKind.OpenFile"/>)
/// and Explorer shell-extension jobs (merged by <see cref="JobDispatcher"/>).
/// </summary>
public sealed class SingleInstanceHost : IDisposable
{
    private static readonly string DefaultChannel = $"Grepdesk.Instance.{MakeId()}";

    private readonly Mutex _mutex;
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<AppCommand> _commands = Channel.CreateUnbounded<AppCommand>();
    private Task? _listener;
    private bool _stopped;

    private SingleInstanceHost(Mutex mutex, string pipeName)
    {
        _mutex = mutex;
        _pipeName = pipeName;
    }

    /// <summary>Commands forwarded by other processes, in arrival order.</summary>
    public ChannelReader<AppCommand> Commands => _commands.Reader;

    /// <summary>Returns the host if this process is the primary, otherwise null.</summary>
    public static SingleInstanceHost? TryAcquire() => TryAcquire(DefaultChannel);

    internal static SingleInstanceHost? TryAcquire(string channelName)
    {
        var mutex = new Mutex(initiallyOwned: false, $@"Local\{channelName}");
        bool owned;
        try { owned = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { owned = true; } // previous primary crashed; we own it now

        if (owned)
            return new SingleInstanceHost(mutex, channelName);

        mutex.Dispose();
        return null;
    }

    /// <summary>Hands a command to the primary. False if it can't be reached in time.</summary>
    public static bool TrySend(AppCommand command) => TrySend(command, DefaultChannel);

    internal static bool TrySend(AppCommand command, string channelName)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", channelName, PipeDirection.InOut, PipeOptions.CurrentUserOnly);
            // Connect waits for the pipe to appear, so this also covers a
            // primary that is still starting up.
            client.Connect(TimeSpan.FromSeconds(5));
            using (var writer = new StreamWriter(client, new UTF8Encoding(false), leaveOpen: true))
            {
                writer.WriteLine(command.Serialize());
                writer.Flush();
            }
            // Wait for the primary's 1-byte ack so we know it queued the
            // command before it began shutting down.
            return client.ReadByte() == 1;
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
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.InOut,
                    NamedPipeServerStream.MaxAllowedServerInstances, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(ct);

                using var reader = new StreamReader(server, Encoding.UTF8, leaveOpen: true);
                var line = await reader.ReadLineAsync(ct);
                if (line is not null && AppCommand.Deserialize(line) is { } command && _commands.Writer.TryWrite(command))
                {
                    server.WriteByte(1);
                    await server.FlushAsync(ct);
                    // Keep the server pipe open until the client reads the ack and closes.
                    using var drainCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    drainCts.CancelAfter(500);
                    try { await server.ReadExactlyAsync(new byte[1], drainCts.Token); }
                    catch (EndOfStreamException) { }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
                }
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
