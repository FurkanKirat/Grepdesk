namespace Grepdesk.Core.Browse;

/// <summary>How the folder browser lays out a folder. One choice for every folder, not one per folder.</summary>
public enum FolderViewMode { Details, MediumIcons, LargeIcons }

public static class FolderViewModes
{
    /// <summary>
    /// The icon views realise one control per item (a wrapping grid can't be virtualised the way the
    /// details list is), so a folder with more entries than this is shown as Details instead.
    /// </summary>
    public const int MaxIconItems = 1500;

    /// <summary>The view to draw: the chosen one, unless the folder is too large for an icon view.</summary>
    public static FolderViewMode Effective(FolderViewMode chosen, int itemCount) =>
        Normalize(chosen) != FolderViewMode.Details && itemCount > MaxIconItems ? FolderViewMode.Details : Normalize(chosen);

    /// <summary>A settings file may hold a value this version doesn't know.</summary>
    public static FolderViewMode Normalize(FolderViewMode mode) => Enum.IsDefined(mode) ? mode : FolderViewMode.Details;

    /// <summary>Pixels to ask the OS for, about twice the size the cell is drawn at so it stays sharp on scaled screens.</summary>
    public static int ThumbnailPixels(FolderViewMode mode) => mode switch
    {
        FolderViewMode.MediumIcons => 96,
        FolderViewMode.LargeIcons => 192,
        _ => 32,
    };
}
