using System.ComponentModel;
using Avalonia.Controls;
using Grepdesk.Core.Browse;

namespace Grepdesk.UI.Browse;

/// <summary>
/// The widths of the folder list's resizable columns, shared by the header and every row
/// (their grids bind to it, so a drag on the header moves all rows at once) and saved in the settings.
/// </summary>
internal sealed class BrowseColumnsModel : INotifyPropertyChanged
{
    public static BrowseColumnsModel Instance { get; } = new();

    private BrowseColumnWidths _widths = new BrowseColumnWidths(
        AppSettings.Current.BrowseModifiedWidth, AppSettings.Current.BrowseTypeWidth, AppSettings.Current.BrowseSizeWidth).Normalized();

    public event PropertyChangedEventHandler? PropertyChanged;

    public GridLength ModifiedWidth => new(_widths.Modified);
    public GridLength TypeWidth => new(_widths.Type);
    public GridLength SizeWidth => new(_widths.Size);

    public double WidthOf(BrowseColumn column) => _widths[column];

    public void SetWidth(BrowseColumn column, double width) => Set(column, width);

    public void Reset(BrowseColumn column)
    {
        Set(column, BrowseColumnWidths.DefaultFor(column));
        Save();
    }

    public void Save()
    {
        var settings = AppSettings.Current;
        settings.BrowseModifiedWidth = _widths.Modified;
        settings.BrowseTypeWidth = _widths.Type;
        settings.BrowseSizeWidth = _widths.Size;
        settings.Save();
    }

    private void Set(BrowseColumn column, double width)
    {
        var next = _widths.With(column, width);
        if (next == _widths) return;
        _widths = next;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(column switch
        {
            BrowseColumn.Modified => nameof(ModifiedWidth),
            BrowseColumn.Type => nameof(TypeWidth),
            _ => nameof(SizeWidth),
        }));
    }
}
