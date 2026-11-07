using System.Globalization;

namespace Grepdesk.Core.Browse;

/// <summary>One file or folder directly inside the folder being browsed.</summary>
public sealed record FolderEntry(string FullPath, string Name, bool IsDirectory, long Size, DateTime Modified, FileAttributes Attributes)
{
    public bool IsHidden => (Attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    /// <summary>The extension without its dot, upper-case ("PDF"); empty for folders and names without one.</summary>
    public string Extension => IsDirectory ? "" : Path.GetExtension(Name).TrimStart('.').ToUpperInvariant();

    public static FolderEntry From(FileSystemInfo info) => info is FileInfo file
        ? new FolderEntry(file.FullName, file.Name, false, file.Length, file.LastWriteTime, file.Attributes)
        : new FolderEntry(info.FullName, info.Name, true, -1, info.LastWriteTime, info.Attributes);
}

public enum FolderListingError { None, NotFound, AccessDenied, Failed }

/// <summary>What a folder holds, or why it couldn't be read.</summary>
public sealed record FolderContents(string Path, IReadOnlyList<FolderEntry> Entries, FolderListingError Error = FolderListingError.None)
{
    public bool IsReadable => Error == FolderListingError.None;
}

// Order matches the column headers of the browse page.
public enum FolderSortColumn { Name, Modified, Type, Size }

public sealed record FolderSort(FolderSortColumn Column = FolderSortColumn.Name, bool Descending = false)
{
    /// <summary>Same column again flips the direction; a new column starts the way Explorer does (dates and sizes largest first).</summary>
    public FolderSort Toggle(FolderSortColumn column) => column == Column
        ? this with { Descending = !Descending }
        : new FolderSort(column, column is FolderSortColumn.Modified or FolderSortColumn.Size);
}

public sealed record FolderSizeResult(long Size, bool Complete);

public static class FolderListing
{
    /// <summary>Automatic folder-size calculation stops once a subfolder exceeds 1 GB or 5,000 entries.</summary>
    public const long DefaultMaxAutoBytes = 1L * 1024 * 1024 * 1024;
    public const int DefaultMaxAutoEntries = 5_000;

    private const FileAttributes CloudOnlyAttributes = (FileAttributes)0x00400000 | FileAttributes.Offline;

    // Natural order, like Explorer: "file2" before "file10".
    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

    private static readonly HashSet<string> KnownHugeFolders = BuildKnownHugeFolders();

    private static HashSet<string> BuildKnownHugeFolders()
    {
        var set = new HashSet<string>(PathUtil.Comparer);
        void Add(Environment.SpecialFolder special, bool includeParent = false)
        {
            var path = Environment.GetFolderPath(special);
            if (string.IsNullOrEmpty(path)) return;
            var normalized = PathUtil.Normalize(path);
            set.Add(normalized);
            if (includeParent && PathUtil.Parent(normalized) is { } parent)
                set.Add(parent);
        }

        Add(Environment.SpecialFolder.UserProfile, includeParent: true); // C:\Users\<me> and C:\Users
        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);
        Add(Environment.SpecialFolder.ApplicationData, includeParent: true); // AppData\Roaming and AppData
        Add(Environment.SpecialFolder.LocalApplicationData);
        return set;
    }

    /// <summary>
    /// True for drive roots and top-level system/user containers (e.g. <c>C:\</c>, <c>C:\Users</c>,
    /// <c>C:\Users\me</c>, <c>C:\Windows</c>, <c>C:\Program Files</c>, <c>AppData</c>) that should not
    /// be traversed automatically without user confirmation.
    /// </summary>
    public static bool IsKnownHugeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        var normalized = PathUtil.Normalize(folder);
        return PathUtil.Parent(normalized) is null || KnownHugeFolders.Contains(normalized);
    }

    /// <summary>
    /// True when <paramref name="folder"/> is a known huge location or exceeds the automatic traversal budget.
    /// </summary>
    public static bool IsHugeFolder(
        string folder,
        long maxBytes = DefaultMaxAutoBytes,
        int maxEntries = DefaultMaxAutoEntries,
        CancellationToken ct = default) =>
        !TryGetFolderSize(folder, maxBytes, maxEntries, ct).Complete;

    /// <summary>
    /// Total size in bytes of all files under <paramref name="folder"/>.
    /// Inaccessible subfolders, online-only cloud placeholders, and symlink/junction loops are skipped.
    /// </summary>
    public static long GetFolderSize(string folder, CancellationToken ct = default, Action<long>? onProgress = null) =>
        TryGetFolderSize(folder, long.MaxValue, int.MaxValue, ct, onProgress).Size;

    /// <summary>
    /// Calculates the total size of <paramref name="folder"/> up to <paramref name="maxBytes"/> and
    /// <paramref name="maxEntries"/>. Returns <c>Complete = false</c> if the folder is a known huge
    /// folder (when bounded) or exceeds the budget during traversal.
    /// </summary>
    public static FolderSizeResult TryGetFolderSize(
        string folder,
        long maxBytes = DefaultMaxAutoBytes,
        int maxEntries = DefaultMaxAutoEntries,
        CancellationToken ct = default,
        Action<long>? onProgress = null)
    {
        var bounded = maxBytes < long.MaxValue || maxEntries < int.MaxValue;
        if (bounded && IsKnownHugeFolder(folder))
            return new FolderSizeResult(0, Complete: false);

        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            AttributesToSkip = 0
        };

        long total = 0;
        var entriesVisited = 0;
        var filesVisited = 0;
        var lastReport = Environment.TickCount64;
        var pending = new Stack<DirectoryInfo>();
        try
        {
            var root = new DirectoryInfo(folder);
            if (!root.Exists) return new FolderSizeResult(0, Complete: true);
            pending.Push(root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return new FolderSizeResult(0, Complete: true);
        }

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();
            try
            {
                foreach (var info in dir.EnumerateFileSystemInfos("*", options))
                {
                    ct.ThrowIfCancellationRequested();
                    if (bounded && ++entriesVisited > maxEntries)
                        return new FolderSizeResult(total, Complete: false);

                    try
                    {
                        if (info is FileInfo file)
                        {
                            if ((file.Attributes & CloudOnlyAttributes) == 0)
                                total += file.Length;

                            if (bounded && total > maxBytes)
                                return new FolderSizeResult(total, Complete: false);

                            if (onProgress is not null && (++filesVisited & 0xFFF) == 0)
                            {
                                var now = Environment.TickCount64;
                                if (now - lastReport >= 100)
                                {
                                    lastReport = now;
                                    onProgress(total);
                                }
                            }
                        }
                        else if (info is DirectoryInfo sub)
                        {
                            if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint) && sub.LinkTarget is not null)
                                continue;
                            pending.Push(sub);
                        }
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return new FolderSizeResult(total, Complete: true);
    }

    /// <summary>
    /// Reads the folder's direct children. Entries that vanish or can't be read
    /// while listing are skipped; only a folder that can't be opened at all is
    /// reported as an error.
    /// </summary>
    public static FolderContents List(string folder, bool showHidden, CancellationToken ct = default)
    {
        var options = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = false,
            ReturnSpecialDirectories = false,
            // Explorer hides protected OS files even with "show hidden files" on; showing them all is the expected power-user switch here.
            AttributesToSkip = showHidden ? 0 : FileAttributes.Hidden | FileAttributes.System
        };

        try
        {
            var entries = new List<FolderEntry>();
            foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", options))
            {
                ct.ThrowIfCancellationRequested();
                try { entries.Add(FolderEntry.From(info)); }
                catch (IOException) { }                  // gone between enumeration and reading its size
                catch (UnauthorizedAccessException) { }
            }
            return new FolderContents(folder, entries);
        }
        catch (DirectoryNotFoundException) { return new FolderContents(folder, [], FolderListingError.NotFound); }
        catch (UnauthorizedAccessException) { return new FolderContents(folder, [], FolderListingError.AccessDenied); }
        catch (IOException) { return new FolderContents(folder, [], FolderListingError.Failed); }
    }

    /// <summary>Folders before files whatever the column, as in Explorer; ties fall back to the name.</summary>
    public static Comparison<FolderEntry> Comparer(FolderSort sort)
    {
        Comparison<FolderEntry> byColumn = sort.Column switch
        {
            FolderSortColumn.Name => (a, b) => NaturalOrder.Compare(a.Name, b.Name),
            FolderSortColumn.Modified => (a, b) => a.Modified.CompareTo(b.Modified),
            FolderSortColumn.Type => (a, b) => string.Compare(a.Extension, b.Extension, StringComparison.OrdinalIgnoreCase),
            FolderSortColumn.Size => (a, b) => a.Size.CompareTo(b.Size),
            _ => throw new ArgumentOutOfRangeException(nameof(sort))
        };

        return (a, b) =>
        {
            if (a.IsDirectory != b.IsDirectory) return a.IsDirectory ? -1 : 1;
            var result = byColumn(a, b);
            if (sort.Descending) result = -result;
            return result != 0 ? result : NaturalOrder.Compare(a.Name, b.Name);
        };
    }

    /// <summary>Entries whose name contains <paramref name="filter"/> (case-insensitive); all of them for an empty filter.</summary>
    public static IEnumerable<FolderEntry> Filter(IEnumerable<FolderEntry> entries, string? filter) =>
        string.IsNullOrWhiteSpace(filter)
            ? entries
            : entries.Where(e => e.Name.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase));
}
