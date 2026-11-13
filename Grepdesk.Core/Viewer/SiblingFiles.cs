using Grepdesk.Core.Browse;

namespace Grepdesk.Core.Viewer;

/// <summary>The files next to a file, in the browser's default (natural name) order, for the viewer's previous / next.</summary>
public static class SiblingFiles
{
    /// <summary>
    /// The file <paramref name="step"/> places after (positive) or before (negative) <paramref name="path"/>
    /// in its folder, or null at either end, when the file is not listed (gone, or hidden) or the folder
    /// can't be read. Folders are skipped; hidden and system files are not offered.
    /// </summary>
    public static string? Neighbor(string path, int step)
    {
        var folder = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(folder) || step == 0) return null;

        var contents = FolderListing.List(folder, showHidden: false);
        if (!contents.IsReadable) return null;

        var files = contents.Entries.Where(e => !e.IsDirectory).ToList();
        files.Sort(FolderListing.Comparer(new FolderSort(FolderSortColumn.Name)));

        var index = files.FindIndex(e => PathUtil.Comparer.Equals(e.FullPath, path));
        if (index < 0) return null;

        var target = index + step;
        return target >= 0 && target < files.Count ? files[target].FullPath : null;
    }
}
