using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>Draws search match highlights and text selection over one PDF page.</summary>
internal sealed class PdfSelectionLayer : Control
{
    private static readonly IBrush Highlight = new SolidColorBrush(Color.FromArgb(0x66, 0x33, 0x8E, 0xF5));
    private static readonly IBrush SearchMatchBrush = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xD4, 0x00));
    private static readonly IBrush ActiveSearchMatchBrush = new SolidColorBrush(Color.FromArgb(0xB8, 0xFF, 0x8C, 0x00));
    private static readonly IPen ActiveSearchMatchPen = new Pen(new SolidColorBrush(Color.FromArgb(0xE0, 0xD9, 0x5F, 0x02)), 1.5);

    private readonly double _pageWidth, _pageHeight;
    private PdfPageText? _text;
    private int _start, _end;
    private IReadOnlyList<PdfSearchMatch>? _searchMatches;
    private int _activeSearchLocalIndex = -1;

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
            if (_start != _end || _searchMatches is { Count: > 0 }) InvalidateVisual();
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

    public void SetSearchMatches(IReadOnlyList<PdfSearchMatch>? matches, int activeLocalIndex)
    {
        _searchMatches = matches;
        _activeSearchLocalIndex = activeLocalIndex;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_text is null || _pageWidth <= 0 || _pageHeight <= 0) return;
        var sx = Bounds.Width / _pageWidth;
        var sy = Bounds.Height / _pageHeight;

        if (_searchMatches is { Count: > 0 })
        {
            for (var i = 0; i < _searchMatches.Count; i++)
            {
                var match = _searchMatches[i];
                var isActive = i == _activeSearchLocalIndex;
                foreach (var r in _text.SelectionRects(match.Start, match.End))
                {
                    var rect = new Rect(r.Left * sx, r.Top * sy, r.Width * sx, r.Height * sy);
                    if (isActive) context.DrawRectangle(ActiveSearchMatchBrush, ActiveSearchMatchPen, rect);
                    else context.FillRectangle(SearchMatchBrush, rect);
                }
            }
        }

        if (_start != _end)
        {
            foreach (var r in _text.SelectionRects(_start, _end))
                context.FillRectangle(Highlight, new Rect(r.Left * sx, r.Top * sy, r.Width * sx, r.Height * sy));
        }
    }
}
