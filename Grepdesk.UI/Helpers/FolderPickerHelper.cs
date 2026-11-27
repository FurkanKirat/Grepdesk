using Avalonia.Platform.Storage;
using Grepdesk.Core.Browse;

namespace Grepdesk.UI.Helpers;

internal static class FolderPickerHelper
{
    public static string? ResolveStartFolder(string? preferred, string? currentBrowseFolder)
    {
        string?[] candidates =
        [
            preferred,
            AppSettings.Current.LastPickedFolder,
            currentBrowseFolder,
            AppSettings.Current.LastBrowseFolder,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        ];

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || ZipBrowsePath.IsZipPath(candidate))
                continue;
            if (Directory.Exists(candidate))
                return candidate;
        }
        return null;
    }

    public static async Task<List<string>> PickFoldersAsync(
        IStorageProvider provider,
        string title,
        bool allowMultiple,
        string? preferredStart,
        string? currentBrowseFolder)
    {
        var startPath = ResolveStartFolder(preferredStart, currentBrowseFolder);
        IStorageFolder? startFolder = null;
        if (startPath is not null)
        {
            try { startFolder = await provider.TryGetFolderFromPathAsync(startPath); }
            catch { /* fall back to OS default */ }
        }

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = allowMultiple,
            SuggestedStartLocation = startFolder,
        });

        var paths = folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p!)
            .ToList();

        if (paths.Count > 0)
        {
            AppSettings.Current.LastPickedFolder = paths[0];
            AppSettings.Current.Save();
        }

        return paths;
    }
}
