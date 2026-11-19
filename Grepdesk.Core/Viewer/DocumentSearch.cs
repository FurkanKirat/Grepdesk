using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace Grepdesk.Core.Viewer;

public readonly record struct PdfSearchMatch(int PageIndex, int Start, int End, double TopPoints, double LeftPoints);

public readonly record struct DocxSearchMatch(int BlockIndex, TextSpan Span);

public readonly record struct XlsxSearchMatch(int SheetIndex, int Row, int Column, bool MatchedFormula);

public enum PptxSearchTarget
{
    TextShape,
    TableCell,
    Notes
}

public readonly record struct PptxSearchMatch(
    int SlideIndex,
    PptxSearchTarget Target,
    int ShapeIndex,
    int SubIndex,
    TextSpan Span);

/// <summary>
/// Cross-format in-document search helpers for PDF, Word, Excel, and PowerPoint viewers.
/// </summary>
public static class DocumentSearch
{
    public static void SearchPdfPage(
        PdfPageText pageText,
        int pageIndex,
        CodeEditorSearch.PreparedSearch search,
        List<PdfSearchMatch> output,
        int maxMatches = CodeEditorSearch.DefaultMaxMatches,
        CancellationToken ct = default)
    {
        if (search.IsEmpty || pageText.Length == 0 || output.Count >= maxMatches)
            return;

        var raw = pageText.Text;
        var sb = new StringBuilder(raw.Length);
        var rawIndices = new List<int>(raw.Length);

        for (var i = 0; i < raw.Length; i++)
        {
            var c = raw[i];
            if (c == '\r')
            {
                if (i + 1 < raw.Length && raw[i + 1] == '\n') i++;
                sb.Append('\n');
                rawIndices.Add(i);
            }
            else if (c is '\n' or '\t')
            {
                sb.Append(c);
                rawIndices.Add(i);
            }
            else if (c == '\u0002')
            {
                sb.Append('-');
                rawIndices.Add(i);
            }
            else if (c < ' ' || c is '\uFFFE' or '\uFFFF')
            {
                continue;
            }
            else
            {
                sb.Append(c);
                rawIndices.Add(i);
            }
        }

        if (sb.Length == 0) return;

        var spans = new List<TextSpan>();
        search.FindInto(sb.ToString(), spans, maxMatches - output.Count, ct);
        foreach (var span in spans)
        {
            if (span.Length <= 0) continue;
            var rawStart = rawIndices[span.Offset];
            var rawEnd = rawIndices[span.Offset + span.Length - 1] + 1;
            var rects = pageText.SelectionRects(rawStart, rawEnd);
            var top = rects.Count > 0 ? rects[0].Top : 0;
            var left = rects.Count > 0 ? rects[0].Left : 0;
            output.Add(new PdfSearchMatch(pageIndex, rawStart, rawEnd, top, left));
        }
    }

    public static List<PdfSearchMatch> SearchPdf(
        int pageCount,
        Func<int, PdfPageText?> getPageText,
        CodeEditorSearch.PreparedSearch search,
        int maxMatches = CodeEditorSearch.DefaultMaxMatches,
        CancellationToken ct = default)
    {
        var results = new List<PdfSearchMatch>();
        if (search.IsEmpty || pageCount <= 0 || maxMatches <= 0) return results;

        for (var i = 0; i < pageCount && results.Count < maxMatches; i++)
        {
            ct.ThrowIfCancellationRequested();
            var pageText = getPageText(i);
            if (pageText is null || pageText.Length == 0) continue;
            SearchPdfPage(pageText, i, search, results, maxMatches, ct);
        }
        return results;
    }

    public static List<DocxSearchMatch> SearchBlocks(
        IReadOnlyList<string> blocks,
        CodeEditorSearch.PreparedSearch search,
        int maxMatches = CodeEditorSearch.DefaultMaxMatches,
        CancellationToken ct = default)
    {
        var results = new List<DocxSearchMatch>();
        if (search.IsEmpty || blocks.Count == 0 || maxMatches <= 0) return results;

        var spans = new List<TextSpan>();
        for (var i = 0; i < blocks.Count && results.Count < maxMatches; i++)
        {
            ct.ThrowIfCancellationRequested();
            var text = blocks[i];
            if (string.IsNullOrEmpty(text)) continue;

            spans.Clear();
            search.FindInto(text, spans, maxMatches - results.Count, ct);
            foreach (var span in spans)
                results.Add(new DocxSearchMatch(i, span));
        }
        return results;
    }

    public static List<XlsxSearchMatch> SearchXlsx(
        XlsxDocument doc,
        CodeEditorSearch.PreparedSearch search,
        int maxMatches = CodeEditorSearch.DefaultMaxMatches,
        CancellationToken ct = default)
    {
        var results = new List<XlsxSearchMatch>();
        if (search.IsEmpty || doc.Sheets.Count == 0 || maxMatches <= 0) return results;

        var probe = new List<TextSpan>(1);
        for (var s = 0; s < doc.Sheets.Count && results.Count < maxMatches; s++)
        {
            ct.ThrowIfCancellationRequested();
            var sheet = doc.Sheets[s];
            foreach (var ((row, col), cell) in sheet.Cells.OrderBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Column))
            {
                ct.ThrowIfCancellationRequested();
                if (results.Count >= maxMatches) break;

                if (!string.IsNullOrEmpty(cell.DisplayText))
                {
                    probe.Clear();
                    search.FindInto(cell.DisplayText, probe, 1, ct);
                    if (probe.Count > 0)
                    {
                        results.Add(new XlsxSearchMatch(s, row, col, MatchedFormula: false));
                        continue;
                    }
                }

                if (cell.Formula is { Length: > 0 } formula)
                {
                    probe.Clear();
                    search.FindInto("=" + formula, probe, 1, ct);
                    if (probe.Count > 0)
                        results.Add(new XlsxSearchMatch(s, row, col, MatchedFormula: true));
                }
            }
        }
        return results;
    }

    public static List<PptxSearchMatch> SearchPptx(
        PptxDocument doc,
        CodeEditorSearch.PreparedSearch search,
        int maxMatches = CodeEditorSearch.DefaultMaxMatches,
        CancellationToken ct = default)
    {
        var results = new List<PptxSearchMatch>();
        if (search.IsEmpty || doc.Slides.Count == 0 || maxMatches <= 0) return results;

        var spans = new List<TextSpan>();
        for (var slideIdx = 0; slideIdx < doc.Slides.Count && results.Count < maxMatches; slideIdx++)
        {
            ct.ThrowIfCancellationRequested();
            var slide = doc.Slides[slideIdx];
            for (var shapeIdx = 0; shapeIdx < slide.Shapes.Count && results.Count < maxMatches; shapeIdx++)
            {
                switch (slide.Shapes[shapeIdx])
                {
                    case PptxTextShape textShape:
                        for (var pIdx = 0; pIdx < textShape.Paragraphs.Count && results.Count < maxMatches; pIdx++)
                        {
                            var text = textShape.Paragraphs[pIdx].PlainText;
                            if (string.IsNullOrEmpty(text)) continue;
                            spans.Clear();
                            search.FindInto(text, spans, maxMatches - results.Count, ct);
                            foreach (var span in spans)
                                results.Add(new PptxSearchMatch(slideIdx, PptxSearchTarget.TextShape, shapeIdx, pIdx, span));
                        }
                        break;

                    case PptxTableShape tableShape:
                        for (var cIdx = 0; cIdx < tableShape.Cells.Count && results.Count < maxMatches; cIdx++)
                        {
                            var text = tableShape.Cells[cIdx].Text;
                            if (string.IsNullOrEmpty(text)) continue;
                            spans.Clear();
                            search.FindInto(text, spans, maxMatches - results.Count, ct);
                            foreach (var span in spans)
                                results.Add(new PptxSearchMatch(slideIdx, PptxSearchTarget.TableCell, shapeIdx, cIdx, span));
                        }
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(slide.Notes) && results.Count < maxMatches)
            {
                spans.Clear();
                search.FindInto(slide.Notes, spans, maxMatches - results.Count, ct);
                foreach (var span in spans)
                    results.Add(new PptxSearchMatch(slideIdx, PptxSearchTarget.Notes, -1, 0, span));
            }
        }
        return results;
    }

    public static int FindFirstAtOrAfter<T>(IReadOnlyList<T> items, Func<T, bool> isAtOrAfter)
    {
        if (items.Count == 0) return -1;
        for (var i = 0; i < items.Count; i++)
        {
            if (isAtOrAfter(items[i])) return i;
        }
        return 0;
    }

    public static int StepIndex(int current, int count, bool backward)
    {
        if (count <= 0) return -1;
        if (current < 0 || current >= count) return backward ? count - 1 : 0;
        return (current + (backward ? -1 : 1) + count) % count;
    }
}
