using System.Security.Cryptography;
using Grepdesk.Core.Browse;

namespace Grepdesk.Core.Viewer;

/// <summary>What happened to an open file on disk outside the viewer.</summary>
public enum FileChangeKind
{
    None,
    Modified,
    Deleted,
}

/// <summary>
/// Point-in-time identity of a file on disk: existence, size, UTC modification time,
/// and an optional SHA-256 digest for files up to <see cref="MaxHashBytes"/>.
/// </summary>
public sealed record FileSnapshot(
    bool Exists,
    long Length,
    DateTime LastWriteTimeUtc,
    string? ContentHash,
    bool HashFailed = false)
{
    /// <summary>Maximum file size hashed for content comparison (2 MiB).</summary>
    public const long MaxHashBytes = 2 * 1024 * 1024;

    public static readonly FileSnapshot Missing = new(false, 0, default, null);

    /// <summary>True when the file appears to exist on disk but could not be read cleanly.</summary>
    public bool IsTransientReadError => Exists && (Length < 0 || HashFailed);

    /// <summary>Captures a snapshot of <paramref name="path"/> without blocking concurrent writers.</summary>
    public static FileSnapshot Capture(string path, bool computeHash = true)
    {
        if (string.IsNullOrWhiteSpace(path))
            return Missing;

        long length;
        DateTime writeUtc;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
                return Missing;

            length = info.Length;
            writeUtc = info.LastWriteTimeUtc;
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return SafeExists(path) ? new FileSnapshot(true, -1, default, null, HashFailed: true) : Missing;
        }

        if (!computeHash || length > MaxHashBytes)
            return new FileSnapshot(true, length, writeUtc, null);

        try
        {
            using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            Span<byte> digest = stackalloc byte[SHA256.HashSizeInBytes];
            SHA256.HashData(stream, digest);
            return new FileSnapshot(true, length, writeUtc, Convert.ToHexString(digest));
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException)
        {
            return Missing;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return new FileSnapshot(true, length, writeUtc, null, HashFailed: true);
        }
    }

    /// <summary>Compares existence, metadata, and content hash when both snapshots carry one.</summary>
    public bool Matches(FileSnapshot other)
    {
        if (Exists != other.Exists) return false;
        if (!Exists) return true;
        if (Length < 0 || other.Length < 0) return false;
        if (Length != other.Length || LastWriteTimeUtc != other.LastWriteTimeUtc) return false;

        if (ContentHash is not null && other.ContentHash is not null)
            return string.Equals(ContentHash, other.ContentHash, StringComparison.Ordinal);

        if (HashFailed || other.HashFailed)
            return false;

        return true;
    }

    /// <summary>Metadata-only comparison used as fallback when hashing repeatedly hits a file lock.</summary>
    internal bool MatchesMetadata(FileSnapshot other)
    {
        if (Exists != other.Exists) return false;
        if (!Exists) return true;
        if (Length < 0 || other.Length < 0) return false;
        return Length == other.Length && LastWriteTimeUtc == other.LastWriteTimeUtc;
    }

    private static bool SafeExists(string path)
    {
        try { return File.Exists(path); }
        catch { return false; }
    }
}

/// <summary>
/// Watches a single open file for external modifications or deletion, debounces noisy
/// filesystem events, suppresses self-saves, and supports per-revision dismissal.
/// </summary>
public sealed class FileChangeTracker : IDisposable
{
    private static readonly TimeSpan DefaultDebounce = TimeSpan.FromMilliseconds(250);
    private const int MaxTransientRetries = 4;

    private readonly string _path;
    private readonly string _fileName;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private readonly object _evalGate = new();
    private readonly object _notifyGate = new();

    private FileSnapshot _baseline;
    private FileSnapshot? _dismissed;
    private FileSnapshot? _latestObserved;
    private FileChangeKind _currentKind;
    private FileSystemWatcher? _watcher;
    private Timer? _timer;
    private bool _started;
    private bool _saving;
    private bool _disposed;
    private int _transientRetries;
    private long _stateVersion;
    private long _publishedVersion;

    public FileChangeTracker(string path, FileSnapshot baseline, TimeSpan? debounce = null)
    {
        _path = PathUtil.Normalize(path);
        _fileName = Path.GetFileName(_path);
        _baseline = baseline;
        _latestObserved = baseline;
        _debounce = debounce ?? DefaultDebounce;
    }

    public string FilePath => _path;

    public FileChangeKind CurrentKind
    {
        get { lock (_gate) return _currentKind; }
    }

    /// <summary>Raised outside internal locks whenever <see cref="CurrentKind"/> transitions.</summary>
    public event Action<FileChangeKind>? StateChanged;

    /// <summary>Begins watching the parent folder for changes to <see cref="FilePath"/>.</summary>
    public void Start()
    {
        lock (_gate)
        {
            if (_disposed || _started) return;
            _started = true;
            _timer = new Timer(_ => Evaluate(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }

        var directory = Path.GetDirectoryName(_path);
        if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(_fileName) || !Directory.Exists(directory))
            return;

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.CreationTime,
            };
            watcher.Changed += OnWatcherChanged;
            watcher.Created += OnWatcherChanged;
            watcher.Deleted += OnWatcherChanged;
            watcher.Renamed += OnWatcherRenamed;
            watcher.Error += OnWatcherError;
            watcher.EnableRaisingEvents = true;

            var keep = false;
            lock (_gate)
            {
                if (!_disposed)
                {
                    _watcher = watcher;
                    keep = true;
                }
            }
            if (!keep)
                DisposeWatcher(watcher);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Locations without watcher support (e.g. certain network shares) fall back to CheckNow() on focus/reload.
            if (watcher is not null)
                DisposeWatcher(watcher);
        }
    }

    /// <summary>Synchronously checks the file on disk against the baseline.</summary>
    public void CheckNow() => Evaluate();

    /// <summary>
    /// Returns true if the file currently exists on disk with different content/metadata than
    /// the baseline captured on load or last save (even if a banner was dismissed).
    /// </summary>
    public bool HasDiskDivergedFromBaseline()
    {
        FileSnapshot baseline;
        lock (_gate)
        {
            if (_disposed) return false;
            baseline = _baseline;
        }

        var current = FileSnapshot.Capture(_path, computeHash: true);
        if (!current.Exists || current.Length < 0)
            return false;

        if (current.HashFailed || baseline.HashFailed)
            return !current.MatchesMetadata(baseline);

        return !current.Matches(baseline);
    }

    /// <summary>Suspends change evaluation while the viewer itself writes to disk.</summary>
    public void BeginInternalSave()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _saving = true;
            _timer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>
    /// Resumes change evaluation after a viewer save. When <paramref name="saved"/> is true,
    /// refreshes the baseline and clears any active notification.
    /// </summary>
    public void EndInternalSave(bool saved)
    {
        FileSnapshot? fresh = null;
        if (saved)
        {
            fresh = FileSnapshot.Capture(_path, computeHash: true);
            if (fresh.Length < 0)
                fresh = FileSnapshot.Capture(_path, computeHash: false);
        }

        var shouldPublish = false;
        var scheduleCheck = false;
        lock (_gate)
        {
            _saving = false;
            if (_disposed) return;

            if (saved && fresh is { Exists: true, Length: >= 0 })
            {
                _baseline = fresh;
                _dismissed = null;
                _latestObserved = fresh;
                _transientRetries = 0;
                if (_currentKind != FileChangeKind.None)
                {
                    _currentKind = FileChangeKind.None;
                    _stateVersion++;
                    shouldPublish = true;
                }
            }
            else if (!saved)
            {
                scheduleCheck = true;
            }
        }

        if (shouldPublish)
            PublishState();
        if (scheduleCheck)
            ScheduleDebouncedCheck();
    }

    /// <summary>Suppresses the notification for the currently observed revision on disk.</summary>
    public void Dismiss()
    {
        var shouldPublish = false;
        lock (_gate)
        {
            if (_disposed) return;

            _dismissed = _latestObserved ?? (_currentKind == FileChangeKind.Deleted ? FileSnapshot.Missing : null);
            if (_currentKind != FileChangeKind.None)
            {
                _currentKind = FileChangeKind.None;
                _stateVersion++;
                shouldPublish = true;
            }
        }

        if (shouldPublish)
            PublishState();
    }

    public void Dispose()
    {
        FileSystemWatcher? watcher;
        Timer? timer;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            watcher = _watcher;
            timer = _timer;
            _watcher = null;
            _timer = null;
        }

        timer?.Dispose();
        if (watcher is not null)
            DisposeWatcher(watcher);
    }

    private void OnWatcherChanged(object sender, FileSystemEventArgs e)
    {
        if (IsTargetName(e.Name))
            ScheduleDebouncedCheck();
    }

    private void OnWatcherRenamed(object sender, RenamedEventArgs e)
    {
        if (IsTargetName(e.Name) || IsTargetName(e.OldName))
            ScheduleDebouncedCheck();
    }

    private void OnWatcherError(object sender, ErrorEventArgs e) => ScheduleDebouncedCheck();

    private bool IsTargetName(string? name) =>
        !string.IsNullOrEmpty(name) && string.Equals(Path.GetFileName(name), _fileName, PathUtil.Comparison);

    private void ScheduleDebouncedCheck()
    {
        lock (_gate)
        {
            if (_disposed || _saving || _timer is null) return;
            try
            {
                _timer.Change(_debounce, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private void Evaluate()
    {
        lock (_evalGate)
        {
            FileSnapshot baseline;
            FileSnapshot? dismissed;
            lock (_gate)
            {
                if (_disposed || _saving) return;
                baseline = _baseline;
                dismissed = _dismissed;
            }

            var current = FileSnapshot.Capture(_path, computeHash: true);

            var shouldPublish = false;
            var retry = false;
            lock (_gate)
            {
                if (_disposed || _saving || !ReferenceEquals(baseline, _baseline))
                    return;

                var metadataOnly = false;
                if (current.IsTransientReadError)
                {
                    if (_transientRetries < MaxTransientRetries)
                    {
                        _transientRetries++;
                        retry = true;
                    }
                    else
                    {
                        _transientRetries = 0;
                        if (current.Length < 0)
                            return; // still locked without readable stat: avoid false deletion
                        metadataOnly = true;
                    }
                }
                else
                {
                    _transientRetries = 0;
                }

                if (!retry)
                {
                    // If the baseline itself lacked a hash (e.g. locked on open) and the file is now readable
                    // with identical metadata, promote the snapshot so future same-length edits can be hashed.
                    if (!metadataOnly && baseline.HashFailed && current.Exists && current.MatchesMetadata(baseline))
                    {
                        _baseline = current;
                        baseline = current;
                    }

                    var matchesBaseline = metadataOnly || baseline.HashFailed
                        ? current.MatchesMetadata(baseline)
                        : current.Matches(baseline);

                    var matchesDismissed = dismissed is not null && (metadataOnly || dismissed.HashFailed
                        ? current.MatchesMetadata(dismissed)
                        : current.Matches(dismissed));

                    FileChangeKind nextKind;
                    if (!current.Exists)
                    {
                        nextKind = dismissed is { Exists: false } ? FileChangeKind.None : FileChangeKind.Deleted;
                    }
                    else if (matchesBaseline)
                    {
                        nextKind = FileChangeKind.None;
                        _dismissed = null;
                    }
                    else if (matchesDismissed)
                    {
                        nextKind = FileChangeKind.None;
                    }
                    else
                    {
                        nextKind = FileChangeKind.Modified;
                    }

                    _latestObserved = current;
                    if (nextKind != _currentKind)
                    {
                        _currentKind = nextKind;
                        _stateVersion++;
                        shouldPublish = true;
                    }
                }
            }

            if (retry)
            {
                ScheduleDebouncedCheck();
                return;
            }

            if (shouldPublish)
                PublishState();
        }
    }

    private void PublishState()
    {
        lock (_notifyGate)
        {
            while (true)
            {
                FileChangeKind kind;
                lock (_gate)
                {
                    if (_disposed || _publishedVersion == _stateVersion)
                        return;
                    _publishedVersion = _stateVersion;
                    kind = _currentKind;
                }
                StateChanged?.Invoke(kind);
            }
        }
    }

    private void DisposeWatcher(FileSystemWatcher watcher)
    {
        try
        {
            watcher.EnableRaisingEvents = false;
            watcher.Changed -= OnWatcherChanged;
            watcher.Created -= OnWatcherChanged;
            watcher.Deleted -= OnWatcherChanged;
            watcher.Renamed -= OnWatcherRenamed;
            watcher.Error -= OnWatcherError;
        }
        catch
        {
        }
        try
        {
            watcher.Dispose();
        }
        catch
        {
        }
    }
}
