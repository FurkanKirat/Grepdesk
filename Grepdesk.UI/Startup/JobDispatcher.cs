using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Startup;

/// <summary>
/// Runs in the primary process. Collects commands of the same kind that
/// arrive close together (one per selected file, from Explorer) into a single
/// job window, and shuts the process down when the last job window closes.
/// </summary>
internal sealed class JobDispatcher(IClassicDesktopStyleApplicationLifetime desktop, SingleInstanceHost? host)
{
    // Explorer starts the processes almost at once, but each needs ~100 ms of
    // .NET startup before it can connect; wait until arrivals go quiet.
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(2);

    private sealed class PendingBatch(AppCommand first)
    {
        public CommandKind Kind { get; } = first.Kind;
        public List<string> Paths { get; } = [.. first.Paths];
        public bool Benchmark { get; set; } = first.Benchmark;
        public DateTime FirstArrival { get; } = DateTime.UtcNow;
        public DateTime LastArrival { get; set; } = DateTime.UtcNow;
    }

    private readonly Dictionary<CommandKind, PendingBatch> _pending = [];
    private readonly DispatcherTimer _flushTimer = new() { Interval = TimeSpan.FromMilliseconds(50) };
    private int _openWindows;

    public void Start(AppCommand first)
    {
        _flushTimer.Tick += (_, _) => FlushDueBatches();
        Add(first);

        if (host is not null)
            _ = PumpAsync(host);
    }

    private async Task PumpAsync(SingleInstanceHost source)
    {
        await foreach (var command in source.Commands.ReadAllAsync())
            Dispatcher.UIThread.Post(() => Add(command));
    }

    private void Add(AppCommand command)
    {
        if (!command.IsJob)
            return;

        // Every paste has its own destination; never merge them.
        if (command.Kind == CommandKind.Paste)
        {
            Open(command);
            return;
        }

        if (_pending.TryGetValue(command.Kind, out var batch))
        {
            foreach (var path in command.Paths)
                if (!batch.Paths.Contains(path, StringComparer.OrdinalIgnoreCase))
                    batch.Paths.Add(path);
            batch.Benchmark |= command.Benchmark;
            batch.LastArrival = DateTime.UtcNow;
        }
        else
        {
            _pending[command.Kind] = new PendingBatch(command);
            _flushTimer.Start();
        }
    }

    private void FlushDueBatches()
    {
        var now = DateTime.UtcNow;
        foreach (var batch in _pending.Values.ToList())
        {
            if (now - batch.LastArrival < QuietPeriod && now - batch.FirstArrival < MaxWait)
                continue;

            _pending.Remove(batch.Kind);
            Open(new AppCommand(batch.Kind, batch.Paths, batch.Benchmark));
        }

        if (_pending.Count == 0)
            _flushTimer.Stop();
    }

    private void Open(AppCommand command)
    {
        var window = new JobWindow(JobSpecs.From(command));
        _openWindows++;
        window.Closed += (_, _) =>
        {
            _openWindows--;
            ShutdownIfIdle();
        };
        window.Show();
    }

    private void ShutdownIfIdle()
    {
        if (_openWindows > 0 || _pending.Count > 0)
            return;

        if (host is not null)
        {
            // Release the pipe first so a click arriving now starts a new
            // primary, then pick up anything that slipped in just before.
            host.StopAccepting();
            var any = false;
            while (host.Commands.TryRead(out var late))
            {
                Add(late);
                any = true;
            }
            if (any)
                return;
        }

        // Low priority: a command the pump already posted runs first and
        // opens its window, and then we are no longer idle.
        Dispatcher.UIThread.Post(() =>
        {
            if (_openWindows == 0 && _pending.Count == 0)
                desktop.Shutdown();
        }, DispatcherPriority.Background);
    }
}
