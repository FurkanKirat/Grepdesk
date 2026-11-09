using System;
using System.Collections.Generic;
using System.Text;

namespace Grepdesk.Core.Viewer;

/// <summary>A character's box on its page, in points from the top-left corner.</summary>
public readonly record struct PdfCharBox(double Left, double Top, double Right, double Bottom)
{
    public double Width => Right - Left;
    public double Height => Bottom - Top;

    /// <summary>PDFium's generated characters (line breaks, inferred spaces) have no area.</summary>
    public bool IsEmpty => Width <= 0 && Height <= 0;

    public double CenterX => (Left + Right) / 2;
    public double CenterY => (Top + Bottom) / 2;

    public bool Contains(double x, double y) => x >= Left && x <= Right && y >= Top && y <= Bottom;

    public PdfCharBox Union(PdfCharBox other) => new(
        Math.Min(Left, other.Left), Math.Min(Top, other.Top),
        Math.Max(Right, other.Right), Math.Max(Bottom, other.Bottom));
}

/// <summary>
/// The text of one PDF page in reading order, each character with its box,
/// for mouse selection. Selection ends are caret positions: 0 is before the
/// first character, <see cref="Length"/> after the last.
/// </summary>
public sealed class PdfPageText
{
    public static readonly PdfPageText Empty = new("", []);

    /// <summary>Raw characters as PDFium reports them, generated "\r\n" included.</summary>
    public string Text { get; }

    /// <summary>One box per character of <see cref="Text"/>.</summary>
    public IReadOnlyList<PdfCharBox> Boxes => _boxes;

    public int Length => Text.Length;

    private readonly PdfCharBox[] _boxes;

    public PdfPageText(string text, PdfCharBox[] boxes)
    {
        if (text.Length != boxes.Length) throw new ArgumentException("One box per character is needed.", nameof(boxes));
        Text = text;
        _boxes = boxes;
    }

    /// <summary>Whether the point lies on a character, for the text cursor.</summary>
    public bool IsOverText(double x, double y)
    {
        foreach (var box in _boxes)
            if (!box.IsEmpty && box.Contains(x, y)) return true;
        return false;
    }

    /// <summary>The character nearest to the point, preferring ones on the same line; -1 when the page has no text.</summary>
    public int CharAt(double x, double y)
    {
        int best = -1;
        var bestScore = double.MaxValue;
        for (var i = 0; i < _boxes.Length; i++)
        {
            var box = _boxes[i];
            if (box.IsEmpty || char.IsWhiteSpace(Text[i]) && box.Width <= 0) continue;
            var dx = x < box.Left ? box.Left - x : x > box.Right ? x - box.Right : 0;
            var dy = y < box.Top ? box.Top - y : y > box.Bottom ? y - box.Bottom : 0;
            // Lines are far apart compared with letters: staying on the pointer's line matters most.
            var score = dy * 8 + dx;
            if (score < bestScore)
            {
                bestScore = score;
                best = i;
                if (score == 0) break;
            }
        }
        return best;
    }

    /// <summary>The caret position nearest to the point: before or after the nearest character.</summary>
    public int CaretAt(double x, double y)
    {
        var i = CharAt(x, y);
        if (i < 0) return 0;
        return x > _boxes[i].CenterX ? i + 1 : i;
    }

    /// <summary>The word around a character, for double-click; a lone character when it isn't part of a word.</summary>
    public (int Start, int End) WordAt(int index)
    {
        if (index < 0 || index >= Length) return (0, 0);
        if (!IsWordChar(Text[index])) return (index, index + 1);
        var start = index;
        while (start > 0 && IsWordChar(Text[start - 1])) start--;
        var end = index + 1;
        while (end < Length && IsWordChar(Text[end])) end++;
        return (start, end);
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_' || char.GetUnicodeCategory(c) is
        System.Globalization.UnicodeCategory.NonSpacingMark or System.Globalization.UnicodeCategory.SpacingCombiningMark;

    /// <summary>The selected characters, without PDFium's placeholder and control characters.</summary>
    public string GetText(int start, int end)
    {
        (start, end) = Clamp(start, end);
        var sb = new StringBuilder(end - start);
        AppendText(sb, start, end);
        return sb.ToString();
    }

    public void AppendText(StringBuilder sb, int start, int end)
    {
        (start, end) = Clamp(start, end);
        for (var i = start; i < end; i++)
        {
            var c = Text[i];
            if (c is '\r' or '\n' or '\t') sb.Append(c);
            else if (c == '\u0002') sb.Append('-'); // PDFium's hyphen at a line break
            else if (c < ' ' || c is '\uFFFE' or '\uFFFF') continue;
            else sb.Append(c);
        }
    }

    /// <summary>The highlight for a selection: one rectangle per line it covers.</summary>
    public List<PdfCharBox> SelectionRects(int start, int end)
    {
        (start, end) = Clamp(start, end);
        var rects = new List<PdfCharBox>();
        PdfCharBox? line = null;
        for (var i = start; i < end; i++)
        {
            var box = _boxes[i];
            if (box.IsEmpty) continue;
            if (line is { } current && SameLine(current, box))
            {
                line = current.Union(box);
                continue;
            }
            if (line is { } done) rects.Add(done);
            line = box;
        }
        if (line is { } last) rects.Add(last);
        return rects;
    }

    private static bool SameLine(PdfCharBox line, PdfCharBox box)
    {
        // Text going back left means a new line (or a new column) began.
        if (box.Left < line.Left - line.Height) return false;
        return (box.CenterY >= line.Top && box.CenterY <= line.Bottom) ||
               (line.CenterY >= box.Top && line.CenterY <= box.Bottom);
    }

    private (int, int) Clamp(int start, int end)
    {
        start = Math.Clamp(start, 0, Length);
        end = Math.Clamp(end, 0, Length);
        return start <= end ? (start, end) : (end, start);
    }
}
