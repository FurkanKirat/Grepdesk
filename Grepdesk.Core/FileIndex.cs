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
            var rootList = roots?.ToList() ?? DriveInfo.GetDrives()
                .Where(d => d.IsReady && !SkippedDriveTypes.Contains(d.DriveType.ToString()))
                .Select(d => d.RootDirectory.FullName)
                .ToList();

            foreach (var root in rootList)
                TryAdd(new DirectoryInfo(root));

            await Task.Run(() => ParallelIndex(rootList, ct), ct);

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

        Parallel.ForEach(
            PartitionQueue(queue, ct),
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
            });
    }

    // Yields items from queue until it's drained
    private static IEnumerable<string> PartitionQueue(ConcurrentQueue<string> queue, CancellationToken ct)
    {
        // Spin until queue is truly empty (subdirs keep getting added)
        var emptyStreak = 0;
        while (emptyStreak < 3 && !ct.IsCancellationRequested)
        {
            if (queue.TryDequeue(out var item))
            {
                emptyStreak = 0;
                yield return item;
            }
            else
            {
                emptyStreak++;
                Thread.Sleep(10);
            }
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
            if (matchAll || e.Name.Contains(query, StringComparison.OrdinalIgnoreCase))
                results.Add(new SearchResult(kv.Key, e.IsDirectory, e.Size, e.Modified, e.Name));
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
            if (info.Exists) _index[info.FullName] = IndexEntry.From(info);
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

// The name is kept as-is (matched case-insensitively) so search results can
// share the string instead of allocating a new one per hit.
internal readonly record struct IndexEntry(string Name, bool IsDirectory, long Size, DateTime Modified)
{
    public static IndexEntry From(FileSystemInfo info)
    {
        var name = info.Name.Length > 0 ? info.Name : info.FullName; // drive roots have no Name
        return info is FileInfo file
            ? new IndexEntry(name, false, file.Length, file.LastWriteTime)
            : new IndexEntry(name, true, 0, info.LastWriteTime);
    }
}

/// <param name="Size">File length in bytes; 0 for directories.</param>
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
