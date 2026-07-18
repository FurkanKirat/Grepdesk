using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace FileSearch.Core;

public class FileIndex
{
    // path → fileName (lowercase for fast search)
    private readonly ConcurrentDictionary<string, string> _index = new(StringComparer.OrdinalIgnoreCase);
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
                    // Add the directory itself
                    _index[dir] = Path.GetFileName(dir).ToLowerInvariant();

                    // Add files inside
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        _index[file] = Path.GetFileName(file).ToLowerInvariant();

                        var count = Interlocked.Increment(ref reported);
                        if (count % 5000 == 0)
                            ProgressChanged?.Invoke((int)count);
                    }

                    // Enqueue subdirs
                    foreach (var sub in Directory.EnumerateDirectories(dir))
                        queue.Enqueue(sub);
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

    public IEnumerable<SearchResult> Search(string query, int maxResults = 500)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        var lower = query.ToLowerInvariant();
        var count = 0;

        foreach (var kv in _index)
        {
            if (kv.Value.Contains(lower))
            {
                yield return new SearchResult(kv.Key);
                if (++count >= maxResults) yield break;
            }
        }
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
            if (Directory.Exists(path)) continue; // skip directory entries
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
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName,
                    EnableRaisingEvents = true
                };

                w.Created += (_, e) => _index[e.FullPath] = Path.GetFileName(e.FullPath).ToLowerInvariant();
                w.Deleted += (_, e) => _index.TryRemove(e.FullPath, out var _unused1);
                w.Renamed += (_, e) =>
                {
                    _index.TryRemove(e.OldFullPath, out var _unused2);
                    _index[e.FullPath] = Path.GetFileName(e.FullPath).ToLowerInvariant();
                };

                w.Error += (_, e) => { }; // silently ignore watcher errors

                _watchers.Add(w);
            }
            catch { }
        }
    }

    private void StopWatchers()
    {
        foreach (var w in _watchers) w.Dispose();
        _watchers.Clear();
    }
}

public record SearchResult(string FullPath)
{
    public string FileName => Path.GetFileName(FullPath);
    public string Directory => Path.GetDirectoryName(FullPath) ?? FullPath;
    public bool IsDirectory => System.IO.Directory.Exists(FullPath);
}
