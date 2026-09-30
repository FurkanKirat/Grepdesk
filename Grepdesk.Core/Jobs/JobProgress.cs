namespace Grepdesk.Core.Jobs;

/// <summary>
/// Thread-safe progress counters. Engines update these from worker threads;
/// the UI polls <see cref="Snapshot"/> on a timer instead of being pushed
/// one event per buffer, which would flood the dispatcher.
/// </summary>
public sealed class JobProgress
{
    private long _totalBytes;
    private long _doneBytes;
    private int _totalFiles;
    private int _doneFiles;
    private volatile bool _totalFinal;
    private volatile string? _currentItem;

    public void AddToTotal(long bytes, int files = 1)
    {
        Interlocked.Add(ref _totalBytes, bytes);
        Interlocked.Add(ref _totalFiles, files);
    }

    /// <summary>Called once enumeration is finished and the totals no longer grow.</summary>
    public void MarkTotalFinal() => _totalFinal = true;

    public void AddDoneBytes(long bytes) => Interlocked.Add(ref _doneBytes, bytes);

    public void FileDone() => Interlocked.Increment(ref _doneFiles);

    public void SetCurrent(string item) => _currentItem = item;

    public ProgressSnapshot Snapshot() => new(
        Interlocked.Read(ref _totalBytes),
        Interlocked.Read(ref _doneBytes),
        Volatile.Read(ref _totalFiles),
        Volatile.Read(ref _doneFiles),
        _totalFinal,
        _currentItem);
}

public readonly record struct ProgressSnapshot(
    long TotalBytes,
    long DoneBytes,
    int TotalFiles,
    int DoneFiles,
    bool TotalFinal,
    string? CurrentItem)
{
    public double Fraction => TotalBytes > 0 ? Math.Clamp((double)DoneBytes / TotalBytes, 0, 1) : 0;
}
