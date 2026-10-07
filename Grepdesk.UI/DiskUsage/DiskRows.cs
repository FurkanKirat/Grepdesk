using Avalonia.Media;
using Avalonia.Media.Immutable;
using Grepdesk.Core;
using Grepdesk.Core.DiskUsage;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.DiskUsage;

/// <summary>
/// Category colors for the disk chart. These are the validated dark-mode
/// categorical slots, in the fixed order of <see cref="DiskCategory"/>, so a
/// category keeps its color no matter how large it is or what else is shown.
/// "Other" is a neutral gray, not a ninth hue.
/// </summary>
public static class DiskColors
{
    private static readonly IBrush[] Slots =
    [
        Brush("#3987e5"), // Videos
        Brush("#d95926"), // Games
        Brush("#199e70"), // Images
        Brush("#c98500"), // Documents
        Brush("#d55181"), // Apps
        Brush("#008300"), // Developer
        Brush("#9085e9"), // System
        Brush("#e66767"), // Audio
        Brush("#6c7086"), // Other
    ];

    public static readonly IBrush Unreadable = Brush("#45475a");
    public static readonly IBrush Free = Brush("#26263a");
    public static readonly IBrush Bar = Brush("#585b70");

    public static IBrush For(DiskCategory category) => Slots[(int)category];

    private static IBrush Brush(string hex) => new ImmutableSolidColorBrush(Color.Parse(hex));
}

/// <summary>A drive card at the top of the page.</summary>
public sealed class DriveRow
{
    public required string Root { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public required double UsedFraction { get; init; }
    public required IBrush MeterBrush { get; init; }

    // Meter is 160 px wide; the used part is its share of that.
    public double UsedWidth => Math.Clamp(UsedFraction, 0, 1) * 160;
}

/// <summary>One row of the category table (legend + numbers).</summary>
public sealed class CategoryRow
{
    public DiskCategory? Category { get; init; }   // null = "all categories"
    public required string Name { get; init; }
    public required IBrush Swatch { get; init; }
    public required string SizeText { get; init; }
    public required string PercentText { get; init; }
    public required string FilesText { get; init; }
    public bool HasSwatch { get; init; } = true;
}

/// <summary>
/// A file or folder row with a size bar. It is a <see cref="ResultItem"/>, so
/// the main window's context menu, double-click and shortcuts work on it unchanged.
/// </summary>
public sealed class DiskRow(SearchResult result, double fraction, DiskCategory? category = null) : ResultItem(result)
{
    // Bars share one scale per list: the largest item fills the 90 px track.
    public double BarWidth { get; } = Math.Clamp(fraction, 0, 1) * 90;

    public IBrush BarBrush { get; } = category is { } c ? DiskColors.For(c) : DiskColors.Bar;

    public string BigSizeText => Format.Size(Result.Size);
}
