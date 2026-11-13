namespace Grepdesk.Core.Browse;

/// <summary>The columns of the folder list whose width the user can change; the name column takes what is left.</summary>
public enum BrowseColumn { Modified, Type, Size }

/// <summary>Widths of the resizable folder-list columns, kept within sane limits.</summary>
public readonly record struct BrowseColumnWidths(double Modified, double Type, double Size)
{
    public const double MinWidth = 50;
    public const double MaxWidth = 600;

    public static BrowseColumnWidths Default => new(140, 120, 90);

    public double this[BrowseColumn column] => column switch
    {
        BrowseColumn.Modified => Modified,
        BrowseColumn.Type => Type,
        _ => Size,
    };

    public static double DefaultFor(BrowseColumn column) => Default[column];

    /// <summary>These widths with one column set, clamped to the limits.</summary>
    public BrowseColumnWidths With(BrowseColumn column, double width)
    {
        var clamped = Clamp(width, DefaultFor(column));
        return column switch
        {
            BrowseColumn.Modified => this with { Modified = clamped },
            BrowseColumn.Type => this with { Type = clamped },
            _ => this with { Size = clamped },
        };
    }

    /// <summary>Settings from a file may be missing (0) or hand-edited: anything unusable falls back to the default.</summary>
    public BrowseColumnWidths Normalized() => new(
        Clamp(Modified, Default.Modified),
        Clamp(Type, Default.Type),
        Clamp(Size, Default.Size));

    private static double Clamp(double width, double fallback) =>
        double.IsNaN(width) || double.IsInfinity(width) || width <= 0 ? fallback : Math.Clamp(width, MinWidth, MaxWidth);
}
