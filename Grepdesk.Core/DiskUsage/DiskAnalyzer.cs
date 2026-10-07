using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Grepdesk.Core.DiskUsage;

public sealed record CategoryUsage(DiskCategory Category, long Bytes, int Files);

public sealed record LargeFile(SearchResult File, DiskCategory Category);

/// <summary>One game, app, node_modules folder... with everything inside it added up.</summary>
public sealed record GroupUsage(DiskCategory Category, string Path, long Bytes, int Files, DateTime LastModified);

/// <param name="TotalBytes">Drive capacity (0 when the root is a folder, not a drive).</param>
/// <param name="FreeBytes">Free space reported by the OS.</param>
/// <param name="ScannedBytes">Sum of the files the scan could read (online-only cloud files excluded).</param>
/// <param name="CloudOnlyBytes">Logical size of online-only cloud files: listed, but not using this disk.</param>
/// <param name="OtherExtensions">The biggest extensions inside "Other", so that bucket isn't a mystery.</param>
/// <param name="Groups">Per category, its largest items (games, apps...), for categories decided by location.</param>
public sealed record DiskReport(
    string Root,
    long TotalBytes,
    long FreeBytes,
    long ScannedBytes,
    long CloudOnlyBytes,
    IReadOnlyList<CategoryUsage> Categories,
    IReadOnlyList<LargeFile> LargestFiles,
    IReadOnlyList<(string Extension, long Bytes)> OtherExtensions,
    IReadOnlyList<GroupUsage> Groups)
{
    public long UsedBytes => TotalBytes > 0 ? TotalBytes - FreeBytes : ScannedBytes;

    /// <summary>
    /// Used space the scan couldn't account for: other users' folders, System
    /// Volume Information, shadow copies, file system metadata. Hard links (common
    /// under Windows\WinSxS) are counted once per link, so the scan can also come
    /// out above the real usage; then this is 0.
    /// </summary>
    public long UnreadableBytes => Math.Max(0, UsedBytes - ScannedBytes);
}

public static class DiskAnalyzer
{
    /// <summary>
    /// Groups everything under <paramref name="root"/> in the index by category
    /// and keeps the largest files (per category too, so filtering the list by
    /// category still shows a full page).
    /// </summary>
    /// <param name="classify">Classification of a full path; defaults to <see cref="DiskClassifier.ClassifyDetailed"/>.</param>
    public static DiskReport Analyze(FileIndex index, string root, int largestPerCategory, CancellationToken ct,
        Func<string, Classification>? classify = null)
    {
        classify ??= DiskClassifier.ClassifyDetailed;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;

        var count = Enum.GetValues<DiskCategory>().Length;
        var bytes = new long[count];
        var files = new int[count];
        long cloudOnly = 0;
        var otherByExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

        // Per category: item folder -> running totals. Looked up by span so the
        // millions of files inside games and apps don't each allocate a key.
        var groups = Enumerable.Range(0, count)
            .Select(_ => new Dictionary<string, GroupTotals>(StringComparer.OrdinalIgnoreCase))
            .ToArray();
        var groupLookups = groups.Select(g => g.GetAlternateLookup<ReadOnlySpan<char>>()).ToArray();

        // Min-heap per category of its largest files: the root is the smallest kept.
        var largest = Enumerable.Range(0, count)
            .Select(_ => new PriorityQueue<SearchResult, long>(largestPerCategory + 1))
            .ToArray();

        var scanned = 0;
        foreach (var (path, e) in index.Entries)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;

            if (e.IsCloudOnly)
            {
                cloudOnly += e.Size;
                continue;
            }

            var (category, groupLength) = classify(path);
            var c = (int)category;
            bytes[c] += e.Size;
            files[c]++;

            if (groupLength > 0)
            {
                var key = path.AsSpan(0, groupLength);
                var lookup = groupLookups[c];
                lookup.TryGetValue(key, out var totals);
                lookup[key] = totals.Add(e.Size, e.Modified);
            }

            if (category == DiskCategory.Other)
            {
                var ext = Path.GetExtension(path);
                otherByExtension[ext] = otherByExtension.GetValueOrDefault(ext) + e.Size;
            }

            var heap = largest[c];
            if (heap.Count < largestPerCategory)
                heap.Enqueue(new SearchResult(path, false, e.Size, e.Modified), e.Size);
            else if (heap.TryPeek(out _, out var smallest) && e.Size > smallest)
                heap.EnqueueDequeue(new SearchResult(path, false, e.Size, e.Modified), e.Size);
        }

        var categories = Enumerable.Range(0, count)
            .Select(i => new CategoryUsage((DiskCategory)i, bytes[i], files[i]))
            .ToList();

        var largestFiles = Enumerable.Range(0, count)
            .SelectMany(i => largest[i].UnorderedItems.Select(x => new LargeFile(x.Element, (DiskCategory)i)))
            .OrderByDescending(f => f.File.Size)
            .ToList();

        long total = 0, free = 0;
        if (IsDriveRoot(normalizedRoot))
        {
            try
            {
                var drive = new DriveInfo(normalizedRoot);
                total = drive.TotalSize;
                free = drive.AvailableFreeSpace;
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        var otherExtensions = otherByExtension
            .OrderByDescending(kv => kv.Value)
            .Take(5)
            .Select(kv => (kv.Key, kv.Value))
            .ToList();

        var topGroups = Enumerable.Range(0, count)
            .SelectMany(i => groups[i]
                .OrderByDescending(kv => kv.Value.Bytes)
                .Take(largestPerCategory)
                .Select(kv => new GroupUsage((DiskCategory)i, kv.Key, kv.Value.Bytes, kv.Value.Files, kv.Value.LastModified)))
            .ToList();

        return new DiskReport(normalizedRoot, total, free, bytes.Sum(), cloudOnly, categories, largestFiles, otherExtensions, topGroups);
    }

    private readonly record struct GroupTotals(long Bytes, int Files, DateTime LastModified)
    {
        public GroupTotals Add(long size, DateTime modified) =>
            new(Bytes + size, Files + 1, modified > LastModified ? modified : LastModified);
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), path, StringComparison.OrdinalIgnoreCase);
}
