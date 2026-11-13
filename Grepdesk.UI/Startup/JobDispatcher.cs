using Avalonia.Threading;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Startup;

/// <summary>
/// Collects Explorer shell-extension job commands of the same kind that arrive
/// close together (one per selected file) into a single job window.
/// </summary>
internal sealed class JobDispatcher(Action<JobWindow> openWindow)
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
    private bool _tickWired;

    public bool HasPending => _pending.Count > 0;

    public void Enqueue(AppCommand command)
    {
        if (!command.IsJob)
            return;

        if (!_tickWired)
        {
            _flushTimer.Tick += (_, _) => FlushDueBatches();
            _tickWired = true;
        }

        // Every paste or drop has its own destination; never merge them.
        if (command.Kind is CommandKind.Paste or CommandKind.Copy or CommandKind.Move)
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

    private void Open(AppCommand command) => openWindow(new JobWindow(JobSpecs.From(command)));
}
