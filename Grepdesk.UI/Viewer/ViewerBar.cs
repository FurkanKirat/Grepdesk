using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// A body the viewer window shows for one kind of file. Views own what they
/// load (bitmaps, players) and release it in <see cref="IDisposable.Dispose"/>.
/// </summary>
internal interface IViewerBody : IDisposable
{
    /// <summary>Keys the view handles itself (zoom, seek...), before the window's own shortcuts.</summary>
    bool HandleKey(KeyEventArgs e);

    /// <summary>Gives the keyboard to the part that scrolls, once the view is on screen.</summary>
    void FocusContent();
}

/// <summary>The bottom toolbar of the image, PDF and media views, styled like the window's header.</summary>
internal static class ViewerBar
{
    public static readonly IBrush Background = Brush.Parse("#181825");
    public static readonly IBrush Border = Brush.Parse("#313244");
    public static readonly IBrush Text = Brush.Parse("#cdd6f4");
    public static readonly IBrush Muted = Brush.Parse("#6c7086");

    public static Border Create(Control content) => new()
    {
        Background = Background,
        BorderBrush = Border,
        BorderThickness = new Thickness(0, 1, 0, 0),
        Padding = new Thickness(12, 7),
        Child = content,
        [DockPanel.DockProperty] = Dock.Bottom,
    };

    public static StackPanel Row(params Control[] children)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center };
        row.Children.AddRange(children);
        return row;
    }

    public static Button Button(string text, string? tip = null)
    {
        var button = new Button { Content = text, MinWidth = 32, HorizontalContentAlignment = HorizontalAlignment.Center };
        if (tip is not null) ToolTip.SetTip(button, tip);
        return button;
    }

    public static TextBlock Label(string text = "", double minWidth = 0) => new()
    {
        Text = text,
        Foreground = Muted,
        FontSize = 12,
        MinWidth = minWidth,
        TextAlignment = TextAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
}
