using Grepdesk.Core.Viewer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Grepdesk.Tests;

public class PdfTextSelectionTests
{
    /// <summary>Two lines, "Hello world" above "Second", each letter 10 pt wide and 12 pt high.</summary>
    private static PdfPageText TwoLines()
    {
        const string first = "Hello world", second = "Second";
        var text = first + "\r\n" + second;
        var boxes = new PdfCharBox[text.Length];
        for (var i = 0; i < first.Length; i++) boxes[i] = new PdfCharBox(10 * i, 0, 10 * i + 10, 12);
        boxes[first.Length] = boxes[first.Length + 1] = default; // generated line break: no area
        for (var i = 0; i < second.Length; i++) boxes[first.Length + 2 + i] = new PdfCharBox(10 * i, 20, 10 * i + 10, 32);
        return new PdfPageText(text, boxes);
    }

    [Fact]
    public void Caret_lands_before_or_after_the_character_under_the_pointer()
    {
        var page = TwoLines();
        Assert.Equal(0, page.CaretAt(2, 6));   // left half of "H"
        Assert.Equal(1, page.CaretAt(8, 6));   // right half of "H"
        Assert.Equal(11, page.CaretAt(500, 6)); // past the end of the first line
        Assert.Equal(13, page.CaretAt(-50, 26)); // left of "Second"
    }

    [Fact]
    public void Dragging_across_lines_copies_the_text_with_its_line_break()
    {
        var page = TwoLines();
        var start = page.CaretAt(62, 6);  // before "world"
        var end = page.CaretAt(28, 26);   // after "Sec"
        Assert.Equal("world\r\nSec", page.GetText(start, end));
        Assert.Equal("world\r\nSec", page.GetText(end, start)); // dragging backwards
    }

    [Fact]
    public void Highlight_is_one_rectangle_per_line()
    {
        var page = TwoLines();
        var rects = page.SelectionRects(6, page.Length);
        Assert.Equal(2, rects.Count);
        Assert.Equal(new PdfCharBox(60, 0, 110, 12), rects[0]);
        Assert.Equal(new PdfCharBox(0, 20, 60, 32), rects[1]);
    }

    [Fact]
    public void Double_click_selects_the_word()
    {
        var page = TwoLines();
        Assert.Equal((6, 11), page.WordAt(page.CharAt(75, 6)));
        Assert.Equal((5, 6), page.WordAt(5)); // the space alone
    }

    [Fact]
    public void Control_characters_are_left_out_of_the_copy()
    {
        var page = new PdfPageText("ab\u0002\ufffec\0", new PdfCharBox[6]);
        Assert.Equal("ab-c", page.GetText(0, int.MaxValue));
    }

    [Fact]
    public void Text_and_boxes_come_from_the_pdf()
    {
        var path = Path.Combine(Path.GetTempPath(), $"grepdesk-pdftext-{Guid.NewGuid():N}.pdf");
        try
        {
            var builder = new PdfDocumentBuilder();
            var font = builder.AddStandard14Font(Standard14Font.Helvetica);
            builder.AddPage(PageSize.A4).AddText("Copy me", 12, new PdfPoint(50, 700), font);
            File.WriteAllBytes(path, builder.Build());

            using var renderer = PdfRenderer.Open(path, CancellationToken.None);
            var page = renderer.GetText(0)!;
            Assert.Equal("Copy me", page.GetText(0, page.Length).Trim());

            // Baseline 700 pt from the bottom of an 842 pt page: the letters sit about 142 pt from the top.
            var c = page.Boxes[page.Text.IndexOf('C')];
            Assert.InRange(c.Left, 48, 52);
            Assert.InRange(c.Bottom, 138, 146);
            Assert.Equal(page.Text.IndexOf('C'), page.CharAt(c.CenterX, c.CenterY));
            Assert.True(page.IsOverText(c.CenterX, c.CenterY));
            Assert.False(page.IsOverText(c.CenterX, 400));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
