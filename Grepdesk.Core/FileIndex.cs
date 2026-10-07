using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grepdesk.Core;

public class FileIndex
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
    public event Action? IndexingComplete;

    // Drives to skip — these cause hangs or are irrelevant
    private static readonly HashSet<string> SkippedDriveTypes =
    [
        "CDRom", "Network" // can add more if needed
    ];

    public async Task BuildIndexAsync(IEnumerable<string>? roots = null, CancellationToken ct = default)
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

            await Task.Run(() =>
            {
                ParallelIndex(rootList, ct);
                ComputeFolderSizes(ct);
            }, ct);

            if (!ct.IsCancellationRequested)
            {
                StartWatchers(rootList);
                IndexingComplete?.Invoke();
            }
        }
        finally
        {
            Interlocked.Exchange(ref _isIndexing, 0);
        }
    }

    private void ParallelIndex(List<string> roots, CancellationToken ct)
    {
        var queue = new ConcurrentQueue<string>(roots);
        var threads = Math.Max(2, Environment.ProcessorCount - 1);
        long reported = 0;

        // Folders queued or still being listed. The scan is over only when this
        // reaches zero: an empty queue alone just means every worker is busy
        // listing a big folder whose subfolders haven't been queued yet.
        long pending = roots.Count;

        // NoBuffering: each worker takes one folder at a time, so no folder sits
        // in a worker's private buffer while the queue looks finished.
        var source = Partitioner.Create(PartitionQueue(queue, () => Interlocked.Read(ref pending) == 0, ct),
            EnumerablePartitionerOptions.NoBuffering);

        Parallel.ForEach(
            source,
            new ParallelOptions { MaxDegreeOfParallelism = threads, CancellationToken = ct },
            dir =>
            {
                try
                {
                    // One enumeration gives both files and subfolders, and the
                    // OS returns size and timestamps with each entry for free.
                    foreach (var info in new DirectoryInfo(dir).EnumerateFileSystemInfos())
                    {
                        _index[info.FullName] = IndexEntry.From(info);

                        if (info is DirectoryInfo)
                        {
                            // Junctions and symlinks point at folders that are indexed
                            // anyway (or loop back up the tree): list the link, don't
                            // walk it, so nothing is found or counted twice.
                            if (info.Attributes.HasFlag(FileAttributes.ReparsePoint) && info.LinkTarget is not null)
                                continue;

                            Interlocked.Increment(ref pending);
                            queue.Enqueue(info.FullName);
                            continue;
                        }

                        var count = Interlocked.Increment(ref reported);
                        if (count % 5000 == 0)
                            ProgressChanged?.Invoke((int)count);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
                finally
                {
                    Interlocked.Decrement(ref pending);
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
    private void ComputeFolderSizes(CancellationToken ct)
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

        foreach (var folder in folders)
        {
            ct.ThrowIfCancellationRequested();

            var total = totals.GetValueOrDefault(folder);
            if (_index.TryGetValue(folder, out var entry))
                _index[folder] = entry with { Size = total };

            if (total > 0 && Path.GetDirectoryName(folder) is { } parent && _index.ContainsKey(parent))
                totals[parent] = totals.GetValueOrDefault(parent) + total;
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
    public void Remove(string path)
    {
        _index.TryRemove(path, out _);

        var prefix = Path.TrimEndingDirectorySeparator(path) + Path.DirectorySeparatorChar;
        foreach (var key in _index.Keys)
            if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                _index.TryRemove(key, out _);
    }

    // Yields folders from the queue until it is empty and no folder is still being listed.
    private static IEnumerable<string> PartitionQueue(ConcurrentQueue<string> queue, Func<bool> finished, CancellationToken ct)
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
                w.Deleted += (_, e) => _index.TryRemove(e.FullPath, out var _unused1);
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
