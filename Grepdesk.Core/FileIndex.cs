using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Grepdesk.Core.Browse;

namespace Grepdesk.Core;

public readonly record struct IndexProgress(int IndexedFiles, double Fraction);

public class FileIndex : IDisposable
{
    // path → lowercase name (for fast search) plus the metadata needed to sort
    // every match, not just the ones that end up on screen.
    private readonly ConcurrentDictionary<string, IndexEntry> _index = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<FileSystemWatcher> _watchers = [];
    private long _isIndexing = 0;

    public bool IsIndexing => Interlocked.Read(ref _isIndexing) == 1;
    public int Count => _index.Count;

    /// <summary>The folders (or drive roots) the last scan started from, normalized.</summary>
    public IReadOnlyList<string> Roots { get; private set; } = [];

    internal IEnumerable<KeyValuePair<string, IndexEntry>> Entries => _index;

    public event Action<int>? ProgressChanged;   // indexed count
    public event Action<IndexProgress>? ScanProgressChanged;
    public event Action? IndexingComplete;

    // Drives to skip — these cause hangs or are irrelevant
    private static readonly HashSet<string> SkippedDriveTypes =
    [
        "CDRom", "Network" // can add more if needed
    ];

    public async Task BuildIndexAsync(IEnumerable<string>? roots = null, CancellationToken ct = default, bool watch = true)
    {
        if (Interlocked.Exchange(ref _isIndexing, 1) == 1) return;

        _index.Clear();
        StopWatchers();

        try
        {
            // Normalized ("C:\a\" -> "C:\a") so a root matches its children's
            // parent path when folder sizes are rolled up.
            var rootList = (roots?.ToList() ?? DriveInfo.GetDrives()
                .Where(d => d.IsReady && !SkippedDriveTypes.Contains(d.DriveType.ToString()))
                .Select(d => d.RootDirectory.FullName)
                .ToList())
                .Select(r => Path.TrimEndingDirectorySeparator(Path.GetFullPath(r)))
                .ToList();

            Roots = rootList;
            foreach (var root in rootList)
                TryAdd(new DirectoryInfo(root));

            var expectedBytes = ComputeExpectedBytes(rootList);
            await Task.Run(() =>
            {
                ParallelIndex(rootList, expectedBytes, ct);
                ComputeFolderSizes(ct, _index.Count);
            }, ct);

            if (!ct.IsCancellationRequested)
            {
                if (watch)
                    StartWatchers(rootList);
                ScanProgressChanged?.Invoke(new IndexProgress(_index.Count, 1.0));
                IndexingComplete?.Invoke();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isIndexing, 0);
        }
    }

    private static long ComputeExpectedBytes(List<string> roots)
    {
        if (roots.Count == 0) return 0;
        long total = 0;
        foreach (var root in roots)
        {
            var pathRoot = Path.GetPathRoot(root);
            if (pathRoot is null) return 0;
            var normalizedRoot = Path.TrimEndingDirectorySeparator(pathRoot);
            if (!string.Equals(root, pathRoot, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(root, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                return 0;

            try
            {
                var drive = new DriveInfo(root);
                if (!drive.IsReady) continue;
                total += Math.Max(0, drive.TotalSize - drive.AvailableFreeSpace);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return total;
    }

    private void ParallelIndex(List<string> roots, long expectedBytes, CancellationToken ct)
    {
        const long TotalWeight = 1_000_000_000_000L;
        var queue = new ConcurrentQueue<(string Dir, long Weight)>();
        if (roots.Count > 0)
        {
            var perRoot = TotalWeight / roots.Count;
            var remainder = TotalWeight - perRoot * roots.Count;
            for (var i = 0; i < roots.Count; i++)
                queue.Enqueue((roots[i], perRoot + (i == 0 ? remainder : 0)));
        }

        var threads = Math.Max(2, Environment.ProcessorCount - 1);
        long reported = 0;
        long completedWeight = 0;
        long scannedBytes = 0;
        long lastReportTick = 0;
        long maxFractionBits = 0;

        // Folders queued or still being listed. The scan is over only when this
        // reaches zero: an empty queue alone just means every worker is busy
        // listing a big folder whose subfolders haven't been queued yet.
        long pending = roots.Count;

        double ClampMonotonic(double value)
        {
            while (true)
            {
                var curBits = Volatile.Read(ref maxFractionBits);
                var cur = BitConverter.Int64BitsToDouble(curBits);
                if (value <= cur) return cur;
                var newBits = BitConverter.DoubleToInt64Bits(value);
                if (Interlocked.CompareExchange(ref maxFractionBits, newBits, curBits) == curBits)
                    return value;
            }
        }

        void MaybeReport(long count, bool force = false)
        {
            var now = Environment.TickCount64;
            var prev = Volatile.Read(ref lastReportTick);
            if (!force && now - prev < 120) return;
            if (Interlocked.CompareExchange(ref lastReportTick, now, prev) != prev && !force) return;

            var treeFrac = Math.Clamp((double)Volatile.Read(ref completedWeight) / TotalWeight, 0.0, 1.0);
            var raw = expectedBytes > 0
                ? 0.65 * Math.Min(1.0, (double)Volatile.Read(ref scannedBytes) / expectedBytes) + 0.35 * treeFrac
                : treeFrac;
            var fraction = ClampMonotonic(Math.Clamp(raw * 0.95, 0.0, 0.95));
            ProgressChanged?.Invoke((int)count);
            ScanProgressChanged?.Invoke(new IndexProgress((int)count, fraction));
        }

        // NoBuffering: each worker takes one folder at a time, so no folder sits
        // in a worker's private buffer while the queue looks finished.
        var source = Partitioner.Create(PartitionQueue(queue, () => Interlocked.Read(ref pending) == 0, ct),
            EnumerablePartitionerOptions.NoBuffering);

        Parallel.ForEach(
            source,
            new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct },
            item =>
            {
                var (dir, weight) = item;
                List<string>? subDirs = null;
                long dirBytes = 0;
                try
                {
                    // One enumeration gives both files and subfolders, and the
                    // OS returns size and timestamps with each entry for free.
                    foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        var entry = IndexEntry.From(info);
                        _index[info.FullName] = entry;

                        if (info is DirectoryInfo)
                        {
                            // Junctions and symlinks point at folders that are indexed
                            // anyway (or loop back up the tree): list the link, don't
                            // walk it, so nothing is found or counted twice.
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
                                continue;

                            subDirs ??= [];
                            subDirs.Add(info.FullName);
                            continue;
                        }

                        if (!entry.IsCloudOnly && entry.Size > 0)
                            dirBytes += entry.Size;

                        var count = Interlocked.Increment(ref reported);
                        if ((count & 0x3FF) == 0)
                            MaybeReport(count);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                finally
                {
                    if (subDirs is { Count: > 0 })
                    {
                        var childWeight = weight / (subDirs.Count + 1);
                        var selfWeight = weight - childWeight * subDirs.Count;
                        foreach (var sub in subDirs)
                        {
                            Interlocked.Increment(ref pending);
                            queue.Enqueue((sub, childWeight));
                        }
                        Interlocked.Add(ref completedWeight, selfWeight);
                    }
                    else
                    {
                        Interlocked.Add(ref completedWeight, weight);
                    }

                    if (dirBytes > 0)
                        Interlocked.Add(ref scannedBytes, dirBytes);

                    Interlocked.Decrement(ref pending);
                    MaybeReport(Volatile.Read(ref reported));
                }
            });
    }

    /// <summary>
    /// Gives every folder the total size of the files beneath it. Each file
    /// is added to its parent, then folders are visited deepest first (a
    /// child path is always longer than its parent's), so each folder's total
    /// is complete before it is added to the folder above.
    /// Totals are as of the scan; the watchers keep names current, not sizes.
    /// </summary>
    private void ComputeFolderSizes(CancellationToken ct, int indexedFiles)
    {
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        foreach (var (path, entry) in _index)
        {
            // Online-only cloud files take no disk space, so they do not count toward folder totals.
            if (entry.IsDirectory || entry.Size == 0 || entry.IsCloudOnly) continue;
            if (Path.GetDirectoryName(path) is { } parent)
                totals[parent] = totals.GetValueOrDefault(parent) + entry.Size;
        }

        var folders = _index.Where(kv => kv.Value.IsDirectory)
            .Select(kv => kv.Key)
            .OrderByDescending(p => p.Length)
            .ToList();

        var lastReport = Environment.TickCount64;
        for (var i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var folder = folders[i];

            var total = totals.GetValueOrDefault(folder);
            if (_index.TryGetValue(folder, out var entry))
                _index[folder] = entry with { Size = total };

            if (total > 0 && Path.GetDirectoryName(folder) is { } parent && _index.ContainsKey(parent))
                totals[parent] = totals.GetValueOrDefault(parent) + total;

            if ((i & 0xFFF) == 0 && folders.Count > 0)
            {
                var now = Environment.TickCount64;
                if (now - lastReport >= 120)
                {
                    lastReport = now;
                    ScanProgressChanged?.Invoke(new IndexProgress(indexedFiles, 0.95 + 0.04 * (i + 1) / folders.Count));
                }
            }
        }
    }

    /// <summary>Direct children (files and folders) of an indexed folder.</summary>
    public List<SearchResult> ChildrenOf(string folder, CancellationToken ct = default)
    {
        var parent = Path.TrimEndingDirectorySeparator(folder);
        var prefixLength = parent.EndsWith(Path.DirectorySeparatorChar) ? parent.Length : parent.Length + 1; // "C:\" vs "C:\a"
        var children = new List<SearchResult>();
        var scanned = 0;

        foreach (var (path, e) in _index)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            if (path.Length > prefixLength
                && path.StartsWith(parent, StringComparison.OrdinalIgnoreCase)
                && path[prefixLength - 1] == Path.DirectorySeparatorChar
                && path.IndexOf(Path.DirectorySeparatorChar, prefixLength) < 0)
            {
                children.Add(new SearchResult(path, e.IsDirectory, e.Size, e.Modified));
            }
        }
        return children;
    }

    // The name is the end of the key; matching on that span avoids keeping a
    // second string per entry (millions of them on a whole-disk scan).
    private static ReadOnlySpan<char> NameOf(string path)
    {
        var name = Path.GetFileName(path.AsSpan());
        return name.IsEmpty ? path.AsSpan() : name; // drive roots ("C:\") have no file name
    }

    /// <summary>Drops a path (and, for a folder, everything under it) after it was deleted from Grepdesk.</summary>
    public void Remove(string path) => RemoveMany([path], []);

    /// <summary>
    /// Drops many deleted paths in a single pass over the index. The folder
    /// prefixes are sorted and de-nested, so for each entry one binary search
    /// finds the only prefix that could contain it: about log2(n) comparisons,
    /// no allocations, whatever the number of paths removed.
    /// </summary>
    /// <param name="removed">Gone entirely: the path and everything under it.</param>
    /// <param name="emptied">Folders that still exist but whose contents are gone.</param>
    public void RemoveMany(IEnumerable<string> removed, IEnumerable<string> emptied, CancellationToken ct = default)
    {
        var comparer = StringComparer.OrdinalIgnoreCase;
        var sep = Path.DirectorySeparatorChar;

        var gone = new HashSet<string>(removed.Select(PathUtil.Normalize), comparer);
        var cleared = emptied.Select(PathUtil.Normalize).ToList();
        if (gone.Count == 0 && cleared.Count == 0) return;

        // "C:\a\"-style prefixes. Sorted, a nested prefix comes right after its
        // parent and is dropped, leaving prefixes none of which contains another.
        var prefixes = new List<string>();
        foreach (var prefix in gone.Concat(cleared)
                     .Select(p => p.EndsWith(sep) ? p : p + sep)
                     .Order(comparer))
        {
            if (prefixes.Count == 0 || !prefix.StartsWith(prefixes[^1], StringComparison.OrdinalIgnoreCase))
                prefixes.Add(prefix);
        }
        var sorted = prefixes.ToArray();

        var removedBytesByParent = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        void RecordRemoved(string path, IndexEntry entry)
        {
            if (!entry.IsDirectory && !entry.IsCloudOnly && entry.Size > 0 && Path.GetDirectoryName(path) is { } parent)
                removedBytesByParent[parent] = removedBytesByParent.GetValueOrDefault(parent) + entry.Size;
        }

        var scanned = 0;
        foreach (var key in _index.Keys)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            if (gone.Contains(key))
            {
                if (_index.TryRemove(key, out var entry)) RecordRemoved(key, entry);
                continue;
            }

            // With disjoint prefixes, only the greatest prefix <= key can be an ancestor of it.
            var i = Array.BinarySearch(sorted, key, comparer);
            if (i < 0) i = ~i - 1;
            if (i >= 0 && key.Length > sorted[i].Length && key.StartsWith(sorted[i], StringComparison.OrdinalIgnoreCase))
            {
                if (_index.TryRemove(key, out var entry)) RecordRemoved(key, entry);
            }
        }

        foreach (var (dir, bytes) in removedBytesByParent)
        {
            var cur = dir;
            while (cur is not null)
            {
                if (_index.TryGetValue(cur, out var folderEntry) && folderEntry.IsDirectory)
                    _index[cur] = folderEntry with { Size = Math.Max(0, folderEntry.Size - bytes) };
                cur = Path.GetDirectoryName(cur);
            }
        }
    }

    // Yields folders from the queue until it is empty and no folder is still being listed.
    private static IEnumerable<(string Dir, long Weight)> PartitionQueue(ConcurrentQueue<(string Dir, long Weight)> queue, Func<bool> finished, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var item))
                yield return item;
            else if (finished())
                yield break;
            else
                Thread.Sleep(1);
        }
    }

    /// <summary>
    /// Every indexed entry whose name contains the query, or every entry when
    /// the query is empty (browse mode: "largest files in this folder").
    /// Not capped: the caller sorts the full set before deciding how much to display.
    /// </summary>
    public List<SearchResult> Search(string query, CancellationToken ct = default)
    {
        var matchAll = string.IsNullOrWhiteSpace(query);
        var results = new List<SearchResult>(matchAll ? _index.Count : 0);

        var scanned = 0;
        foreach (var kv in _index)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var e = kv.Value;
            if (matchAll || NameOf(kv.Key).Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(new SearchResult(kv.Key, e.IsDirectory, e.Size, e.Modified));
        }
        return results;
    }

    /// <summary>True if the current index has finished scanning and covers <paramref name="folder"/>.</summary>
    public bool CoversFolder(string folder)
    {
        if (Count == 0 || IsIndexing || string.IsNullOrWhiteSpace(folder)) return false;
        return Roots.Any(root => PathUtil.IsSameOrInside(folder, root));
    }

    /// <summary>
    /// Searches only entries strictly under <paramref name="folder"/> in the already-built index.
    /// </summary>
    public List<SearchResult> SearchUnder(string folder, string query, CancellationToken ct = default)
    {
        var normalized = PathUtil.Normalize(folder);
        var prefix = Path.EndsInDirectorySeparator(normalized) ? normalized : normalized + Path.DirectorySeparatorChar;
        var matchAll = string.IsNullOrWhiteSpace(query);
        var results = new List<SearchResult>();

        var scanned = 0;
        foreach (var kv in _index)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            if (kv.Key.Length <= prefix.Length || !kv.Key.StartsWith(prefix, PathUtil.Comparison))
                continue;

            var e = kv.Value;
            if (matchAll || NameOf(kv.Key).Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(new SearchResult(kv.Key, e.IsDirectory, e.Size, e.Modified));
        }
        return results;
    }

    /// <summary>
    /// All indexed file (not directory) paths that live under any of the given root
    /// folders. Used by content search, which needs a candidate file list rather
    /// than a name match.
    /// </summary>
    public IEnumerable<string> GetIndexedFilesUnder(IEnumerable<string> roots)
    {
        var rootList = roots.ToList();
        foreach (var path in _index.Keys)
        {
            if (_index.TryGetValue(path, out var e) && e.IsDirectory) continue; // skip directory entries
            if (rootList.Any(r => path.StartsWith(r, StringComparison.OrdinalIgnoreCase)))
                yield return path;
        }
    }

    // FileSystemWatcher — keep index live after initial scan
    private void StartWatchers(List<string> roots)
    {
        foreach (var root in roots)
        {
            try
            {
                var w = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                                 | NotifyFilters.Size | NotifyFilters.LastWrite,
                    EnableRaisingEvents = true
                };

                w.Created += (_, e) => TryAdd(e.FullPath);
                w.Changed += (_, e) => { if (_index.ContainsKey(e.FullPath)) TryAdd(e.FullPath); };
                w.Deleted += (_, e) =>
                {
                    if (_index.TryGetValue(e.FullPath, out var existing))
                    {
                        if (existing.IsDirectory)
                        {
                            Remove(e.FullPath);
                        }
                        else if (_index.TryRemove(e.FullPath, out var removed) && !removed.IsCloudOnly && removed.Size > 0)
                        {
                            var cur = Path.GetDirectoryName(e.FullPath);
                            while (cur is not null)
                            {
                                if (_index.TryGetValue(cur, out var folderEntry) && folderEntry.IsDirectory)
                                    _index[cur] = folderEntry with { Size = Math.Max(0, folderEntry.Size - removed.Size) };
                                cur = Path.GetDirectoryName(cur);
                            }
                        }
                    }
                };
                w.Renamed += (_, e) =>
                {
                    _index.TryRemove(e.OldFullPath, out var _unused2);
                    TryAdd(e.FullPath);
                };

                w.Error += (_, e) => { }; // silently ignore watcher errors

                _watchers.Add(w);
            }
            catch { }
        }
    }

    private void TryAdd(string path)
    {
        try
        {
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            TryAdd(info);
        }
        catch { /* vanished or inaccessible between the event and now */ }
    }

    private void TryAdd(FileSystemInfo info)
    {
        try
        {
            if (!info.Exists) return;

            var entry = IndexEntry.From(info);
            // A folder's own timestamp changes when its contents do; keep the rolled-up size.
            if (entry.IsDirectory && _index.TryGetValue(info.FullName, out var old))
                entry = entry with { Size = old.Size };
            _index[info.FullName] = entry;
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }

    public void Dispose() => StopWatchers();
}

// Kept small on purpose: a whole-disk scan holds millions of these. The name
// isn't stored, it is the tail of the dictionary key (the full path).
internal readonly record struct IndexEntry(bool IsDirectory, long Size, DateTime Modified, bool IsCloudOnly = false)
{
    // Not in the FileAttributes enum: set on OneDrive/iCloud placeholders whose
    // contents live only online. Their Length is the logical size, but they
    // take no space on the disk until opened.
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;

    public static IndexEntry From(FileSystemInfo info)
    {
        if (info is not FileInfo file)
            return new IndexEntry(true, 0, info.LastWriteTime);

        var cloudOnly = (file.Attributes & (RecallOnDataAccess | FileAttributes.Offline)) != 0;
        return new IndexEntry(false, file.Length, file.LastWriteTime, cloudOnly);
    }
}

/// <param name="Size">File length in bytes; for directories, the total of everything beneath it.</param>
/// <param name="Name">File name if already known (from the index); derived from the path otherwise.</param>
public record SearchResult(string FullPath, bool IsDirectory, long Size, DateTime Modified, string? Name = null)
{
    // Stored, not computed: sorting compares names many times per result.
    public string FileName { get; } = Name ?? (Path.GetFileName(FullPath) is { Length: > 0 } n ? n : FullPath);
    public string Directory => Path.GetDirectoryName(FullPath) ?? FullPath;

    /// <summary>Reads the metadata from disk, for paths that did not come from the index.</summary>
    public static SearchResult FromDisk(string path)
    {
        var file = new FileInfo(path);
        if (file.Exists)
            return new SearchResult(path, false, file.Length, file.LastWriteTime);

        var dir = new DirectoryInfo(path);
        return new SearchResult(path, dir.Exists, 0, dir.Exists ? dir.LastWriteTime : default);
    }
}
