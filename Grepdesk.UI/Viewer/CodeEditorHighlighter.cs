using Avalonia.Media;
using AvaloniaEdit.Document;
using AvaloniaEdit.Rendering;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// Renders in-editor search matches and matching bracket pairs behind text on <see cref="KnownLayer.Selection"/>.
/// </summary>
internal sealed class CodeEditorHighlighter : IBackgroundRenderer
{
    private static readonly IBrush MatchBrush = new SolidColorBrush(Color.Parse("#f9e2af"), 0.25);
    private static readonly IBrush ActiveMatchBrush = new SolidColorBrush(Color.Parse("#f9e2af"), 0.50);
    private static readonly IPen ActiveMatchPen = new Pen(new SolidColorBrush(Color.Parse("#f9e2af"), 0.90), 1);

    private static readonly IBrush BracketBrush = new SolidColorBrush(Color.Parse("#89b4fa"), 0.22);
    private static readonly IPen BracketPen = new Pen(new SolidColorBrush(Color.Parse("#89b4fa"), 0.75), 1);

    private IReadOnlyList<TextSpan> _matches = [];
    private int _activeIndex = -1;
    private (int OpenOffset, int CloseOffset)? _brackets;

    public KnownLayer Layer => KnownLayer.Selection;

    public void SetMatches(TextView textView, IReadOnlyList<TextSpan> matches, int activeIndex)
    {
        _matches = matches;
        _activeIndex = activeIndex;
        textView.InvalidateLayer(KnownLayer.Selection);
    }

    public void SetBrackets(TextView textView, (int OpenOffset, int CloseOffset)? brackets)
    {
        if (_brackets == brackets) return;
        _brackets = brackets;
        textView.InvalidateLayer(KnownLayer.Selection);
    }

    public void Draw(TextView textView, DrawingContext drawingContext)
    {
        if (!textView.VisualLinesValid) return;
        var docLength = textView.Document?.TextLength ?? 0;
        if (docLength == 0) return;

        var visualLines = textView.VisualLines;
        if (visualLines.Count == 0) return;

        var viewStart = visualLines[0].FirstDocumentLine.Offset;
        var viewEnd = visualLines[^1].LastDocumentLine.EndOffset;

        if (_matches.Count > 0)
        {
            var startIdx = LowerBound(_matches, viewStart);
            for (var i = startIdx; i < _matches.Count; i++)
            {
                var span = _matches[i];
                if (span.Offset > viewEnd) break;
                if (span.Offset + span.Length > docLength || span.Length <= 0) continue;

                var builder = new BackgroundGeometryBuilder
                {
                    AlignToWholePixels = true,
                    CornerRadius = 2,
                };
                builder.AddSegment(textView, new TextSegment { StartOffset = span.Offset, Length = span.Length });
                if (builder.CreateGeometry() is { } geometry)
                {
                    if (i == _activeIndex)
                        drawingContext.DrawGeometry(ActiveMatchBrush, ActiveMatchPen, geometry);
                    else
                        drawingContext.DrawGeometry(MatchBrush, null, geometry);
                }
            }
        }

        if (_brackets is var (open, close))
        {
            DrawBracket(textView, drawingContext, open, docLength, viewStart, viewEnd);
            DrawBracket(textView, drawingContext, close, docLength, viewStart, viewEnd);
        }
    }

    private static void DrawBracket(TextView textView, DrawingContext drawingContext, int offset, int docLength, int viewStart, int viewEnd)
    {
        if (offset < viewStart || offset > viewEnd || offset < 0 || offset >= docLength) return;
        var builder = new BackgroundGeometryBuilder
        {
            AlignToWholePixels = true,
            CornerRadius = 2,
        };
        builder.AddSegment(textView, new TextSegment { StartOffset = offset, Length = 1 });
        if (builder.CreateGeometry() is { } geometry)
            drawingContext.DrawGeometry(BracketBrush, BracketPen, geometry);
    }

    private static int LowerBound(IReadOnlyList<TextSpan> spans, int targetOffset)
    {
        int lo = 0, hi = spans.Count;
        while (lo < hi)
        {
            var mid = lo + (hi - lo) / 2;
            if (spans[mid].Offset + spans[mid].Length < targetOffset) lo = mid + 1;
            else hi = mid;
        }
        return lo;
    }
}
