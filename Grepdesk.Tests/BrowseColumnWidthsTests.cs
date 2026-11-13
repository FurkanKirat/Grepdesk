using Grepdesk.Core.Browse;

namespace Grepdesk.Tests;

public class BrowseColumnWidthsTests
{
    [Fact]
    public void Setting_a_width_keeps_it_within_the_limits_and_leaves_the_others()
    {
        var widths = BrowseColumnWidths.Default.With(BrowseColumn.Type, 9999);
        Assert.Equal(BrowseColumnWidths.MaxWidth, widths.Type);
        Assert.Equal(BrowseColumnWidths.Default.Modified, widths.Modified);

        widths = widths.With(BrowseColumn.Size, 1);
        Assert.Equal(BrowseColumnWidths.MinWidth, widths.Size);
    }

    [Fact]
    public void Missing_or_broken_saved_widths_fall_back_to_the_defaults()
    {
        var loaded = new BrowseColumnWidths(0, double.NaN, 5000).Normalized();

        Assert.Equal(BrowseColumnWidths.Default.Modified, loaded.Modified);
        Assert.Equal(BrowseColumnWidths.Default.Type, loaded.Type);
        Assert.Equal(BrowseColumnWidths.MaxWidth, loaded.Size);
    }

    [Fact]
    public void Indexer_reads_each_column()
    {
        var widths = new BrowseColumnWidths(100, 200, 300);
        Assert.Equal([100, 200, 300], new[] { BrowseColumn.Modified, BrowseColumn.Type, BrowseColumn.Size }.Select(c => widths[c]));
    }
}
