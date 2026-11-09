using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>Draws the blue highlight of the selected text over one PDF page.</summary>
internal sealed class PdfSelectionLayer : Control
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x8E, 0xF5));

    private readonly double _pageWidth, _pageHeight;
    private PdfPageText? _text;
    private int _start, _end;

    /// <param name="pageWidth">Page size in points, which the character boxes use.</param>
    public PdfSelectionLayer(double pageWidth, double pageHeight)
    {
        _pageWidth = pageWidth;
        _pageHeight = pageHeight;
        IsHitTestVisible = false;
    }

    public PdfPageText? Text
    {
        get => _text;
        set
        {
            if (ReferenceEquals(_text, value)) return;
            _text = value;
            if (_start != _end) InvalidateVisual();
        }
    }

    /// <summary>Caret positions on this page; equal when nothing here is selected. Clamped to the text when drawn.</summary>
    public void SetRange(int start, int end)
    {
        if (start == _start && end == _end) return;
        _start = start;
        _end = end;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_text is null || _start == _end || _pageWidth <= 0 || _pageHeight <= 0) return;
        var sx = Bounds.Width / _pageWidth;
        var sy = Bounds.Height / _pageHeight;
        foreach (var r in _text.SelectionRects(_start, _end))
            context.FillRectangle(Highlight, new Rect(r.Left * sx, r.Top * sy, r.Width * sx, r.Height * sy));
    }
}
