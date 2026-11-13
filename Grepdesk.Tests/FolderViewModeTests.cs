using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class FolderViewModeTests
{
    [Fact]
    public void Icon_views_fall_back_to_details_for_very_large_folders_only()
    {
        Assert.Equal(FolderViewMode.LargeIcons, FolderViewModes.Effective(FolderViewMode.LargeIcons, FolderViewModes.MaxIconItems));
        Assert.Equal(FolderViewMode.Details, FolderViewModes.Effective(FolderViewMode.LargeIcons, FolderViewModes.MaxIconItems + 1));
        Assert.Equal(FolderViewMode.Details, FolderViewModes.Effective(FolderViewMode.Details, 10));
    }

    [Fact]
    public void An_unknown_saved_mode_becomes_details()
    {
        Assert.Equal(FolderViewMode.Details, FolderViewModes.Normalize((FolderViewMode)42));
        Assert.Equal(FolderViewMode.Details, FolderViewModes.Effective((FolderViewMode)42, 5));
    }

    [Fact]
    public void Larger_views_ask_for_larger_thumbnails()
    {
        Assert.True(FolderViewModes.ThumbnailPixels(FolderViewMode.LargeIcons) > FolderViewModes.ThumbnailPixels(FolderViewMode.MediumIcons));
        Assert.True(FolderViewModes.ThumbnailPixels(FolderViewMode.MediumIcons) > FolderViewModes.ThumbnailPixels(FolderViewMode.Details));
    }
}
