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
        : new FolderEntry(info.FullName, info.Name, true, 0, info.LastWriteTime, info.Attributes);
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

public static class FolderListing
{
    // Natural order, like Explorer: "file2" before "file10".
    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(CultureInfo.CurrentCulture, CompareOptions.IgnoreCase | CompareOptions.NumericOrdering);

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
