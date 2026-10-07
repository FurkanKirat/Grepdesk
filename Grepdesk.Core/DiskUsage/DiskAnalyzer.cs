using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Grepdesk.Core.DiskUsage;

public sealed record CategoryUsage(DiskCategory Category, long Bytes, int Files);

public sealed record LargeFile(SearchResult File, DiskCategory Category);

/// <param name="TotalBytes">Drive capacity (0 when the root is a folder, not a drive).</param>
/// <param name="FreeBytes">Free space reported by the OS.</param>
/// <param name="ScannedBytes">Sum of the files the scan could read (online-only cloud files excluded).</param>
/// <param name="CloudOnlyBytes">Logical size of online-only cloud files: listed, but not using this disk.</param>
/// <param name="OtherExtensions">The biggest extensions inside "Other", so that bucket isn't a mystery.</param>
public sealed record DiskReport(
    string Root,
    long TotalBytes,
    long FreeBytes,
    long ScannedBytes,
    long CloudOnlyBytes,
    IReadOnlyList<CategoryUsage> Categories,
    IReadOnlyList<LargeFile> LargestFiles,
    IReadOnlyList<(string Extension, long Bytes)> OtherExtensions)
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
    /// <param name="classify">Category of a full path; defaults to <see cref="DiskClassifier.Classify"/>.</param>
    public static DiskReport Analyze(FileIndex index, string root, int largestPerCategory, CancellationToken ct,
        Func<string, DiskCategory>? classify = null)
    {
        classify ??= DiskClassifier.Classify;
        var normalizedRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar) ? normalizedRoot : normalizedRoot + Path.DirectorySeparatorChar;

        var count = Enum.GetValues<DiskCategory>().Length;
        var bytes = new long[count];
        var files = new int[count];
        long cloudOnly = 0;
        var otherByExtension = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

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

            var category = classify(path);
            var c = (int)category;
            bytes[c] += e.Size;
            files[c]++;

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

        return new DiskReport(normalizedRoot, total, free, bytes.Sum(), cloudOnly, categories, largestFiles, otherExtensions);
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(Path.GetPathRoot(path), path, StringComparison.OrdinalIgnoreCase)
        || string.Equals(Path.TrimEndingDirectorySeparator(Path.GetPathRoot(path) ?? ""), path, StringComparison.OrdinalIgnoreCase);
}
