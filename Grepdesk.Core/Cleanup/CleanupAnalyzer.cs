using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Grepdesk.Core.DiskUsage;

namespace Grepdesk.Core.Cleanup;

/// <summary>
/// Finds space that can be won back, from an already-built index (no extra
/// disk reads, so this part is instant). Duplicates need file contents and
/// are found separately by <see cref="DuplicateFinder"/>.
/// </summary>
public static class CleanupAnalyzer
{
    private static readonly TimeSpan TempMinAge = TimeSpan.FromDays(1);
    private static readonly TimeSpan StaleNodeModules = TimeSpan.FromDays(90);
    private static readonly TimeSpan OldDownload = TimeSpan.FromDays(90);
    private static readonly TimeSpan OldLargeFile = TimeSpan.FromDays(365);
    private const long LargeFileBytes = 500L * 1024 * 1024;
    private const int MaxItems = 1000;

    // Caches apps rebuild on their own. Their folders stay; only the contents go.
    private static readonly string[] CacheMarkers =
    [
        @"\Cache\Cache_Data\", @"\Code Cache\", @"\GPUCache\", @"\Service Worker\CacheStorage\",
        @"\NVIDIA\DXCache\", @"\NVIDIA\GLCache\", @"\NVIDIA Corporation\NV_Cache\", @"\D3DSCache\",
        @"\AMD\DxCache\", @"\AMD\DxcCache\", @"\INetCache\", @"\Mozilla\Firefox\Profiles\*\cache2\",
    ];

    // Package manager download caches: everything in them is downloaded again on demand.
    private static readonly string[] DeveloperCacheMarkers =
    [
        @"\AppData\Local\npm-cache\", @"\.npm\_cacache\", @"\AppData\Local\pip\cache\", @"\.cache\pip\",
        @"\AppData\Local\NuGet\v3-cache\", @"\AppData\Local\NuGet\plugins-cache\", @"\AppData\Local\Yarn\Cache\",
        @"\.gradle\caches\", @"\AppData\Local\pnpm-cache\", @"\.cache\yarn\",
    ];

    /// <param name="classify">Classification of a full path; defaults to <see cref="DiskClassifier.ClassifyDetailed"/>.</param>
    public static List<CleanupSuggestion> Analyze(FileIndex index, string root, CleanupContext ctx, CancellationToken ct,
        Func<string, Classification>? classify = null)
    {
        classify ??= DiskClassifier.ClassifyDetailed;
        var sep = Path.DirectorySeparatorChar;
        string Norm(string marker) => sep == '\\' ? marker : marker.Replace('\\', '/');
        var cacheMarkers = CacheMarkers.Select(Norm).ToArray();
        var devCacheMarkers = DeveloperCacheMarkers.Select(Norm).ToArray();

        var rootPrefix = WithSeparator(Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)));
        var tempPrefix = WithSeparator(ctx.TempFolder);
        var windowsTempPrefix = WithSeparator(Path.Combine(Path.GetPathRoot(rootPrefix) ?? rootPrefix, "Windows", "Temp"));
        var downloadsPrefix = WithSeparator(ctx.DownloadsFolder);

        var temp = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var caches = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var devCaches = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var nodeModules = new Dictionary<string, Totals>(StringComparer.OrdinalIgnoreCase);
        var downloads = new List<CleanupItem>();
        var largeOld = new List<CleanupItem>();
        CleanupItem? hibernation = null;

        var scanned = 0;
        foreach (var (path, e) in index.Entries)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || e.IsCloudOnly || !path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase)) continue;

            // Temp: grouped by the temp folder's direct children (an installer's
            // unpacked folder is one item, not 3,000 files).
            var tempItem = ChildOf(path, tempPrefix) ?? ChildOf(path, windowsTempPrefix);
            if (tempItem is not null)
            {
                Add(temp, tempItem, e.Size, e.Modified);
                continue;
            }

            if (FolderAt(path, cacheMarkers) is { } cache)
            {
                Add(caches, cache, e.Size, e.Modified);
                continue;
            }

            if (FolderAt(path, devCacheMarkers) is { } devCache)
            {
                Add(devCaches, devCache, e.Size, e.Modified);
                continue;
            }

            var nm = path.IndexOf(sep + "node_modules" + sep, StringComparison.OrdinalIgnoreCase);
            if (nm >= 0)
            {
                Add(nodeModules, path[..(nm + "node_modules".Length + 1)], e.Size, e.Modified);
                continue;
            }

            if (string.Equals(Path.GetFileName(path), "hiberfil.sys", StringComparison.OrdinalIgnoreCase))
            {
                hibernation = new CleanupItem(path, e.Size, false, e.Modified, false);
                continue;
            }

            // The user's own files only: the rest belongs to apps, games, the system.
            var category = classify(path);
            if (category.GroupLength > 0) continue;

            var age = ctx.Now - e.Modified;
            if (path.StartsWith(downloadsPrefix, StringComparison.OrdinalIgnoreCase))
            {
                if (age > OldDownload)
                    downloads.Add(new CleanupItem(path, e.Size, false, e.Modified, false));
            }
            else if (e.Size >= LargeFileBytes && age > OldLargeFile)
            {
                largeOld.Add(new CleanupItem(path, e.Size, false, e.Modified, false));
            }
        }

        var devItems = Items(devCaches, _ => true, contentsOnly: true)
            .Concat(Items(nodeModules, t => ctx.Now - t.LastModified > StaleNodeModules))
            .ToList();

        var suggestions = new List<CleanupSuggestion>
        {
            Capped(CleanupKind.TempFiles, CleanupAction.DeletePermanently, Items(temp, t => ctx.Now - t.LastModified > TempMinAge)),
            Capped(CleanupKind.Caches, CleanupAction.DeletePermanently, CacheItems(caches)),
            Capped(CleanupKind.DeveloperCaches, CleanupAction.DeletePermanently, devItems),
            Capped(CleanupKind.OldDownloads, CleanupAction.MoveToTrash, downloads),
            Capped(CleanupKind.LargeOldFiles, CleanupAction.MoveToTrash, largeOld),
        };

        if (hibernation is not null)
            suggestions.Add(new CleanupSuggestion(CleanupKind.Hibernation, CleanupAction.None, [hibernation]));

        return suggestions.Where(s => s.Items.Count > 0).ToList();
    }

    private static string WithSeparator(string path) =>
        path.EndsWith(Path.DirectorySeparatorChar) ? path : path + Path.DirectorySeparatorChar;

    /// <summary>The direct child of <paramref name="prefix"/> that contains <paramref name="path"/>.</summary>
    private static string? ChildOf(string path, string prefix)
    {
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
        var next = path.IndexOf(Path.DirectorySeparatorChar, prefix.Length);
        return next < 0 ? path : path[..next];
    }

    /// <summary>The folder a marker names (a '*' segment matches any one folder name).</summary>
    private static string? FolderAt(string path, string[] markers)
    {
        foreach (var marker in markers)
        {
            var star = marker.IndexOf('*');
            if (star < 0)
            {
                var i = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
                if (i >= 0) return path[..(i + marker.Length - 1)];
                continue;
            }

            // "\Mozilla\Firefox\Profiles\*\cache2\": find the head, skip one folder, expect the tail.
            var head = marker[..star];
            var tail = marker[(star + 1)..];
            var h = path.IndexOf(head, StringComparison.OrdinalIgnoreCase);
            if (h < 0) continue;
            var afterName = path.IndexOf(Path.DirectorySeparatorChar, h + head.Length);
            if (afterName < 0) continue;
            if (string.Compare(path, afterName, tail, 0, tail.Length, StringComparison.OrdinalIgnoreCase) == 0)
                return path[..(afterName + tail.Length - 1)];
        }
        return null;
    }

    private static void Add(Dictionary<string, Totals> map, string key, long size, DateTime modified)
    {
        map.TryGetValue(key, out var t);
        map[key] = new Totals(t.Bytes + size, modified > t.LastModified ? modified : t.LastModified);
    }

    private static List<CleanupItem> Items(Dictionary<string, Totals> map, Func<Totals, bool> selected, bool contentsOnly = false) =>
        map.Where(kv => kv.Value.Bytes > 0)
            .Select(kv => new CleanupItem(kv.Key, kv.Value.Bytes, IsDirectory: false, kv.Value.LastModified,
                selected(kv.Value), contentsOnly))
            .ToList();

    // Caches are selected, except web apps' offline storage, which may be data
    // the user expects to keep; shader caches carry a note about the stutter.
    private static List<CleanupItem> CacheItems(Dictionary<string, Totals> caches) =>
        Items(caches, _ => true, contentsOnly: true)
            .Select(i =>
            {
                if (i.Path.Contains("Service Worker", StringComparison.OrdinalIgnoreCase))
                    return i with { SelectedByDefault = false, Note = CleanupNotes.OfflineWebData };
                if (i.Path.Contains("DXCache", StringComparison.OrdinalIgnoreCase)
                    || i.Path.Contains("GLCache", StringComparison.OrdinalIgnoreCase)
                    || i.Path.Contains("DxcCache", StringComparison.OrdinalIgnoreCase)
                    || i.Path.Contains("NV_Cache", StringComparison.OrdinalIgnoreCase)
                    || i.Path.Contains("D3DSCache", StringComparison.OrdinalIgnoreCase))
                    return i with { Note = CleanupNotes.ShaderCache };
                return i;
            })
            .ToList();

    /// <summary>The largest <see cref="MaxItems"/>, keeping the full count and size.</summary>
    private static CleanupSuggestion Capped(CleanupKind kind, CleanupAction action, List<CleanupItem> all)
    {
        var shown = all.OrderByDescending(i => i.Bytes).Take(MaxItems)
            // Folder or file is only checked for the items that are listed (one stat each).
            .Select(i => i with { IsDirectory = Directory.Exists(i.Path) })
            .ToList();
        return new CleanupSuggestion(kind, action, shown)
        {
            TotalItems = all.Count,
            TotalBytes = all.Sum(i => i.Bytes),
        };
    }

    private readonly record struct Totals(long Bytes, DateTime LastModified);
}
