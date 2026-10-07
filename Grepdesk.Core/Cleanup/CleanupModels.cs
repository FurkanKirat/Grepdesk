using System;
using System.Collections.Generic;

namespace Grepdesk.Core.Cleanup;

/// <summary>The ways to get space back that the "Free up space" page offers.</summary>
public enum CleanupKind
{
    RecycleBin,
    TempFiles,
    Caches,
    DeveloperCaches,
    Duplicates,
    OldDownloads,
    LargeOldFiles,
    Hibernation,
    SystemTools,
}

public enum CleanupAction
{
    /// <summary>Regenerable data (caches, temp): deleted for good, space is back at once.</summary>
    DeletePermanently,
    /// <summary>The user's own files: to the Recycle Bin, so a mistake can be undone.</summary>
    MoveToTrash,
    EmptyTrash,
    /// <summary>Nothing to delete here; the card explains (and may open a system tool).</summary>
    None,
}

/// <param name="ContentsOnly">Delete what is inside the folder but keep the folder (caches apps expect to exist).</param>
/// <param name="Group">Duplicates: which set of identical files this belongs to; -1 otherwise.</param>
/// <param name="Note">A <see cref="CleanupNotes"/> key with a caveat the UI shows next to the item.</param>
public sealed record CleanupItem(
    string Path,
    long Bytes,
    bool IsDirectory,
    DateTime LastModified,
    bool SelectedByDefault,
    bool ContentsOnly = false,
    int Group = -1,
    string? Note = null);

/// <summary>Caveats attached to items (the UI localizes them as "CleanupNote" + key).</summary>
public static class CleanupNotes
{
    /// <summary>GPU shader caches: games recompile shaders on next start (brief stutter).</summary>
    public const string ShaderCache = "ShaderCache";

    /// <summary>Service Worker storage: may hold a web app's offline data.</summary>
    public const string OfflineWebData = "OfflineWebData";
}

/// <remarks>
/// <see cref="Items"/> is capped (the largest first) to keep the list usable;
/// <see cref="TotalItems"/> and <see cref="TotalBytes"/> describe everything found.
/// </remarks>
public sealed record CleanupSuggestion(CleanupKind Kind, CleanupAction Action, IReadOnlyList<CleanupItem> Items)
{
    public long Bytes { get; init; } = Sum(Items);
    public int TotalItems { get; init; } = Items.Count;
    public long TotalBytes { get; init; } = Sum(Items);

    private static long Sum(IReadOnlyList<CleanupItem> items)
    {
        long total = 0;
        foreach (var item in items) total += item.Bytes;
        return total;
    }
}

/// <summary>Where the per-user folders are; injectable so tests don't depend on the real profile.</summary>
public sealed record CleanupContext(string UserProfile, string TempFolder, string DownloadsFolder, DateTime Now)
{
    public static CleanupContext ForCurrentUser()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new CleanupContext(
            profile,
            System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetTempPath()),
            KnownFolders.Downloads(),
            DateTime.Now);
    }
}
