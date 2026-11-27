namespace Grepdesk.Core.Browse;

/// <summary>
/// Thread-safe cache of computed folder sizes, keyed by normalized folder path and
/// validated against the folder's last-modified timestamp.
/// </summary>
public sealed class FolderSizeCache
{
    private readonly record struct CachedEntry(long Size, DateTime ModifiedUtc);

    private readonly Dictionary<string, CachedEntry> _entries = new(PathUtil.Comparer);
    private readonly Lock _gate = new();

    private static DateTime NormalizeUtc(DateTime modified) =>
        modified == default ? default : modified.ToUniversalTime();

    public bool TryGet(string path, DateTime currentModified, out long size)
    {
        var normalized = PathUtil.Normalize(path);
        var utc = NormalizeUtc(currentModified);
        lock (_gate)
        {
            if (_entries.TryGetValue(normalized, out var entry) && entry.ModifiedUtc == utc)
            {
                size = entry.Size;
                return true;
            }
        }
        size = 0;
        return false;
    }

    public void Set(string path, long size, DateTime modified)
    {
        var normalized = PathUtil.Normalize(path);
        var utc = NormalizeUtc(modified);
        lock (_gate)
        {
            _entries[normalized] = new CachedEntry(size, utc);
        }
    }

    /// <summary>
    /// Removes <paramref name="path"/>, all cached descendants beneath it, and all
    /// cached ancestor folders up to the root so stale rolled-up totals are never reused.
    /// </summary>
    public void Invalidate(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = PathUtil.Normalize(path);
        var prefix = Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;

        lock (_gate)
        {
            _entries.Remove(normalized);

            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, PathUtil.Comparison)).ToList())
                _entries.Remove(key);

            var cur = PathUtil.Parent(normalized);
            while (cur is not null)
            {
                _entries.Remove(cur);
                cur = PathUtil.Parent(cur);
            }
        }
    }

    public void InvalidateMany(IEnumerable<string> paths)
    {
        foreach (var path in paths)
            Invalidate(path);
    }

    /// <summary>Removes <paramref name="folder"/> and every cached entry strictly under it.</summary>
    public void InvalidateChildrenOf(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        var normalized = PathUtil.Normalize(folder);
        var prefix = Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;

        lock (_gate)
        {
            _entries.Remove(normalized);
            foreach (var key in _entries.Keys.Where(k => k.StartsWith(prefix, PathUtil.Comparison)).ToList())
                _entries.Remove(key);
        }
    }
}
