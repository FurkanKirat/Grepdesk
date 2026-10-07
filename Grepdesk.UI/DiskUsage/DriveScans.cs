using Avalonia.Threading;
using Grepdesk.Core;

namespace Grepdesk.UI.DiskUsage;

/// <summary>
/// The whole-drive scan shared by the Disk usage and Free up space pages, so a
/// drive scanned on one page is ready on the other. Holds one drive at a time:
/// a full scan of a large disk is millions of entries.
/// </summary>
public sealed class DriveScans
{
    private readonly FileIndex _index = new();
    private Task? _running;
    private string? _runningRoot;

    public DriveScans() =>
        _index.ProgressChanged += count => Dispatcher.UIThread.Post(() => Progress?.Invoke(count));

    /// <summary>Returns an index built elsewhere (the name search page) that covers a drive root, if any.</summary>
    public Func<string, FileIndex?>? FindExisting { get; set; }

    /// <summary>Files found so far by a running scan (on the UI thread).</summary>
    public event Action<int>? Progress;

    /// <summary>A scan of this root finished (on the UI thread), so pages can refresh.</summary>
    public event Action<string>? Scanned;

    /// <summary>A finished scan covering this drive, without scanning.</summary>
    public FileIndex? TryGet(string root)
    {
        if (!_index.IsIndexing && _index.Count > 0 && _index.Roots.Any(r => Same(r, root)))
            return _index;
        return FindExisting?.Invoke(root);
    }

    /// <summary>Scans the drive (again). If that drive is already being scanned, waits for that scan instead.</summary>
    public async Task<FileIndex> ScanAsync(string root, CancellationToken ct)
    {
        if (_running is { IsCompleted: false } running)
        {
            if (_runningRoot is not null && Same(_runningRoot, root))
            {
                await running.WaitAsync(ct);
                return _index;
            }
            await running.WaitAsync(ct); // one drive at a time
        }

        _runningRoot = root;
        _running = _index.BuildIndexAsync([root], ct);
        await _running;
        ct.ThrowIfCancellationRequested();

        Scanned?.Invoke(root);
        return _index;
    }

    public bool IsScanning => _running is { IsCompleted: false };

    private static bool Same(string a, string b) =>
        string.Equals(Path.TrimEndingDirectorySeparator(a), Path.TrimEndingDirectorySeparator(b), StringComparison.OrdinalIgnoreCase);
}
