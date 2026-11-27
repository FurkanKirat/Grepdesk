using Grepdesk.Core.Ocr;
using Grepdesk.Core.Viewer;
using Grepdesk.Windows;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Grepdesk.Tests;

public class PdfOcrTests
{
    [Fact]
    public void CompositeOnWhite_turns_transparent_background_opaque_white()
    {
        // 3 pixels: transparent (0,0,0,0), opaque black (0,0,0,255), 50% black (0,0,0,128)
        byte[] bgra =
        [
            0, 0, 0, 0,
            0, 0, 0, 255,
            0, 0, 0, 128,
        ];

        var img = OcrPdfText.CompositeOnWhite(bgra, 3, 1);

        Assert.Equal(3, img.Width);
        Assert.Equal(1, img.Height);
        // Pixel 0: white
        Assert.Equal((byte)255, img.BgraPixels[0]);
        Assert.Equal((byte)255, img.BgraPixels[1]);
        Assert.Equal((byte)255, img.BgraPixels[2]);
        Assert.Equal((byte)255, img.BgraPixels[3]);
        // Pixel 1: black
        Assert.Equal((byte)0, img.BgraPixels[4]);
        Assert.Equal((byte)0, img.BgraPixels[5]);
        Assert.Equal((byte)0, img.BgraPixels[6]);
        Assert.Equal((byte)255, img.BgraPixels[7]);
        // Pixel 2: ~mid gray
        Assert.InRange(img.BgraPixels[8], (byte)125, (byte)129);
        Assert.Equal((byte)255, img.BgraPixels[11]);
    }

    [Fact]
    public void ToPdfPageText_maps_ocr_words_to_searchable_and_selectable_page_text()
    {
        var ocr = new OcrResult(
        [
            new OcrLine("Hello Grepdesk", [
                new OcrWord("Hello", 100, 100, 100, 40),
                new OcrWord("Grepdesk", 220, 100, 160, 40),
            ]),
            new OcrLine("Second line", [
                new OcrWord("Second", 100, 200, 120, 40),
                new OcrWord("line", 240, 200, 80, 40),
            ]),
        ]);

        // Image is 1000x2000 px, page is 500x1000 pt (scale = 0.5 pt/px)
        var pageText = OcrPdfText.ToPdfPageText(ocr, 1000, 2000, 500, 1000);

        Assert.True(OcrPdfText.HasMeaningfulText(pageText));
        Assert.Equal("Hello Grepdesk\r\nSecond line", pageText.GetText(0, pageText.Length));

        Assert.True(CodeEditorSearch.PreparedSearch.TryCreate("Grepdesk", false, false, false, out var prepared));
        var matches = new List<PdfSearchMatch>();
        DocumentSearch.SearchPdfPage(pageText, 0, prepared!, matches);

        var match = Assert.Single(matches);
        Assert.Equal(110, match.LeftPoints, precision: 2);
        Assert.Equal(50, match.TopPoints, precision: 2);
        Assert.NotEmpty(pageText.SelectionRects(match.Start, match.End));

        var wordRange = pageText.WordAt(match.Start + 2);
        Assert.Equal("Grepdesk", pageText.GetText(wordRange.Start, wordRange.End));
    }

    [Fact]
    public void OcrScaleForPage_caps_dimension_and_pixel_budget()
    {
        var a4Scale = OcrPdfText.OcrScaleForPage(595, 842, 2600);
        Assert.InRange(a4Scale, 2.5, 2.8);

        var hugeScale = OcrPdfText.OcrScaleForPage(4000, 3000, 2600);
        Assert.True(4000 * hugeScale <= 2600.01);
        Assert.True(3000 * hugeScale <= 2600.01);
        Assert.True(4000 * 3000 * hugeScale * hugeScale <= 8_000_000.1);
    }

    [Fact]
    public async Task WindowsOcrEngine_recognizes_rendered_pdf_page_text()
    {
        if (!OperatingSystem.IsWindows()) return;

        var engine = new WindowsOcrEngine();
        if (!engine.IsAvailable) return;

        using var dir = new TempDir();
        var pdfPath = dir.Combine("scan.pdf");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.HelveticaBold);
        builder.AddPage(PageSize.A4).AddText("HELLO GREPDESK 2026", 32, new PdfPoint(60, 700), font);
        File.WriteAllBytes(pdfPath, builder.Build());

        using var renderer = PdfRenderer.Open(pdfPath, CancellationToken.None);
        var (w, h) = renderer.PageSizes[0];
        var scale = OcrPdfText.OcrScaleForPage(w, h, engine.MaxImageDimension);
        var rendered = renderer.Render(0, scale);
        Assert.NotNull(rendered);

        var ocrImage = OcrPdfText.CompositeOnWhite(rendered!.Pixels, rendered.Width, rendered.Height);
        var ocrResult = await engine.RecognizeAsync(ocrImage, CancellationToken.None);
        var pageText = OcrPdfText.ToPdfPageText(ocrResult, ocrImage.Width, ocrImage.Height, w, h);

        Assert.True(OcrPdfText.HasMeaningfulText(pageText));
        var recognized = pageText.GetText(0, pageText.Length);
        Assert.Contains("GREPDESK", recognized, StringComparison.OrdinalIgnoreCase);
    }
}
