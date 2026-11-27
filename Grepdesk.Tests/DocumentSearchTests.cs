using System.IO.Compression;
using Grepdesk.Core.Viewer;
using UglyToad.PdfPig.Content;
using UglyToad.PdfPig.Core;
using UglyToad.PdfPig.Fonts.Standard14Fonts;
using UglyToad.PdfPig.Writer;

namespace Grepdesk.Tests;

public class DocumentSearchTests
{
    private static CodeEditorSearch.PreparedSearch Prepare(
        string query,
        bool matchCase = false,
        bool wholeWord = false,
        bool useRegex = false)
    {
        Assert.True(CodeEditorSearch.PreparedSearch.TryCreate(query, matchCase, wholeWord, useRegex, out var search));
        return search!;
    }

    [Fact]
    public void Pdf_page_search_normalizes_breaks_and_soft_hyphens_back_to_raw_boxes()
    {
        // "co-\r\noperate\uFFFE!" where '-' is PDFium's line-break hyphen '\u0002'
        var raw = "co\u0002\r\noperate\uFFFE!";
        var boxes = new PdfCharBox[raw.Length];
        for (var i = 0; i < raw.Length; i++)
        {
            if (raw[i] is '\r' or '\n' or '\uFFFE') continue;
            boxes[i] = new PdfCharBox(10 * i, 20, 10 * i + 9, 32);
        }
        var page = new PdfPageText(raw, boxes);

        var matches = new List<PdfSearchMatch>();
        DocumentSearch.SearchPdfPage(page, pageIndex: 2, Prepare("co-\noperate"), matches);

        var match = Assert.Single(matches);
        Assert.Equal(2, match.PageIndex);
        Assert.Equal("co-\r\noperate", page.GetText(match.Start, match.End));
        Assert.Equal(20, match.TopPoints);
        Assert.Equal(0, match.LeftPoints);
        Assert.NotEmpty(page.SelectionRects(match.Start, match.End));
    }

    [Fact]
    public void Pdf_multi_page_search_supports_options_cap_and_cancellation()
    {
        using var dir = new TempDir();
        var path = dir.Combine("multi.pdf");

        var builder = new PdfDocumentBuilder();
        var font = builder.AddStandard14Font(Standard14Font.Helvetica);
        builder.AddPage(PageSize.A4).AddText("Alpha alpha alphabet", 12, new PdfPoint(50, 720), font);
        builder.AddPage(PageSize.A4).AddText("Second Alpha target", 12, new PdfPoint(50, 500), font);
        File.WriteAllBytes(path, builder.Build());

        using var renderer = PdfRenderer.Open(path, CancellationToken.None);

        var caseWhole = DocumentSearch.SearchPdf(
            renderer.TotalPages,
            i => renderer.GetText(i),
            Prepare("Alpha", matchCase: true, wholeWord: true));
        Assert.Equal(2, caseWhole.Count);
        Assert.Equal(0, caseWhole[0].PageIndex);
        Assert.Equal(1, caseWhole[1].PageIndex);
        Assert.True(caseWhole[1].TopPoints > caseWhole[0].TopPoints);

        var regexMatches = DocumentSearch.SearchPdf(
            renderer.TotalPages,
            i => renderer.GetText(i),
            Prepare(@"alpha\w*", useRegex: true),
            maxMatches: 2);
        Assert.Equal(2, regexMatches.Count);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() =>
            DocumentSearch.SearchPdf(renderer.TotalPages, i => renderer.GetText(i), Prepare("Alpha"), ct: cts.Token));
    }

    [Fact]
    public void Word_block_search_finds_cross_run_text_and_respects_options()
    {
        using var dir = new TempDir();
        var path = DocxDocumentTests.WriteDocx(dir, """
            <w:p>
              <w:r><w:t>bud</w:t></w:r>
              <w:r><w:rPr><w:b/></w:rPr><w:t>get</w:t></w:r>
              <w:r><w:t> summary and budgeting</w:t></w:r>
            </w:p>
            <w:p>
              <w:r><w:t>Final Budget total</w:t></w:r>
            </w:p>
            """);

        var editDoc = DocxEditDocument.Open(path, CancellationToken.None);
        editDoc.Layout();
        var blocks = editDoc.Paragraphs.Select(p => p.Text).ToList();

        var wholeWord = DocumentSearch.SearchBlocks(blocks, Prepare("budget", matchCase: false, wholeWord: true));
        Assert.Equal(2, wholeWord.Count);
        Assert.Equal(new DocxSearchMatch(0, new TextSpan(0, 6)), wholeWord[0]);
        Assert.Equal(new DocxSearchMatch(1, new TextSpan(6, 6)), wholeWord[1]);

        var caseSensitive = DocumentSearch.SearchBlocks(blocks, Prepare("Budget", matchCase: true, wholeWord: true));
        var onlySecond = Assert.Single(caseSensitive);
        Assert.Equal(1, onlySecond.BlockIndex);
    }

    [Fact]
    public void Excel_search_matches_display_and_formula_without_duplicating_cells()
    {
        using var dir = new TempDir();
        var path = dir.Combine("multi.xlsx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            void Add(string name, string content)
            {
                using var writer = new StreamWriter(zip.CreateEntry(name).Open());
                writer.Write(content);
            }

            Add("xl/workbook.xml", """
                <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                          xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <sheets>
                    <sheet name="First" sheetId="1" r:id="rId1"/>
                    <sheet name="Second" sheetId="2" r:id="rId2"/>
                  </sheets>
                </workbook>
                """);
            Add("xl/_rels/workbook.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
                  <Relationship Id="rId2" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet2.xml"/>
                </Relationships>
                """);
            Add("xl/worksheets/sheet1.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1"><v>10</v></c>
                      <c r="B1"><v>20</v></c>
                    </row>
                    <row r="2">
                      <c r="A2"><f>SUM(A1:B1)</f><v>30</v></c>
                    </row>
                  </sheetData>
                </worksheet>
                """);
            Add("xl/worksheets/sheet2.xml", """
                <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  <sheetData>
                    <row r="1">
                      <c r="A1" t="inlineStr"><is><t>SUM(A1:B1) summary</t></is></c>
                      <c r="B1" t="inlineStr"><is><t>Revenue 30</t></is></c>
                    </row>
                  </sheetData>
                </worksheet>
                """);
        }

        var doc = XlsxDocument.Load(path);

        var formulaAndDisplay = DocumentSearch.SearchXlsx(doc, Prepare("SUM"));
        Assert.Equal(2, formulaAndDisplay.Count);
        Assert.Equal(new XlsxSearchMatch(0, 1, 0, MatchedFormula: true), formulaAndDisplay[0]);
        Assert.Equal(new XlsxSearchMatch(1, 0, 0, MatchedFormula: false), formulaAndDisplay[1]);

        var displayOnly = DocumentSearch.SearchXlsx(doc, Prepare("30", wholeWord: true));
        Assert.Equal(2, displayOnly.Count);
        Assert.Equal(new XlsxSearchMatch(0, 1, 0, MatchedFormula: false), displayOnly[0]);
        Assert.Equal(new XlsxSearchMatch(1, 0, 1, MatchedFormula: false), displayOnly[1]);
    }

    [Fact]
    public void Powerpoint_search_finds_cross_run_shapes_tables_and_notes()
    {
        using var dir = new TempDir();
        var slide1 = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:spPr/>
              <p:txBody>
                <a:bodyPr/><a:lstStyle/>
                <a:p>
                  <a:r><a:t>Quar</a:t></a:r>
                  <a:r><a:t>terly Review</a:t></a:r>
                </a:p>
              </p:txBody>
            </p:sp>
            <p:graphicFrame>
              <p:nvGraphicFramePr><p:cNvPr id="3" name="Table"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
              <p:xfrm><a:off x="0" y="1000000"/><a:ext cx="4000000" cy="1000000"/></p:xfrm>
              <a:graphic>
                <a:graphicData uri="http://schemas.openxmlformats.org/drawingml/2006/table">
                  <a:tbl>
                    <a:tblGrid><a:gridCol w="2000000"/></a:tblGrid>
                    <a:tr h="500000">
                      <a:tc><a:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Quarterly Target</a:t></a:r></a:p></a:txBody><a:tcPr/></a:tc>
                    </a:tr>
                  </a:tbl>
                </a:graphicData>
              </a:graphic>
            </p:graphicFrame>
            """;
        var slide2 = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="4" name="Body"/><p:cNvSpPr/><p:nvPr/></p:nvSpPr>
              <p:spPr/>
              <p:txBody>
                <a:bodyPr/><a:lstStyle/>
                <a:p><a:r><a:t>Next quarterly milestone</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            """;

        var path = OfficeDocumentTests.WritePptx(dir, [slide1, slide2], notesText: "Speaker mentions quarterly metrics");
        var doc = PptxDocument.Load(path);

        var matches = DocumentSearch.SearchPptx(doc, Prepare("quarterly", wholeWord: true));
        Assert.Equal(4, matches.Count);
        Assert.Equal(new PptxSearchMatch(0, PptxSearchTarget.TextShape, 0, 0, new TextSpan(0, 9)), matches[0]);
        Assert.Equal(new PptxSearchMatch(0, PptxSearchTarget.TableCell, 1, 0, new TextSpan(0, 9)), matches[1]);
        Assert.Equal(new PptxSearchMatch(0, PptxSearchTarget.Notes, -1, 0, new TextSpan(17, 9)), matches[2]);
        Assert.Equal(new PptxSearchMatch(1, PptxSearchTarget.TextShape, 0, 0, new TextSpan(5, 9)), matches[3]);
    }

    [Fact]
    public void Navigation_helpers_wrap_and_handle_empty_inputs()
    {
        int[] items = [2, 5, 9];
        Assert.Equal(1, DocumentSearch.FindFirstAtOrAfter(items, x => x >= 4));
        Assert.Equal(0, DocumentSearch.FindFirstAtOrAfter(items, x => x >= 20));
        Assert.Equal(-1, DocumentSearch.FindFirstAtOrAfter<int>([], _ => true));

        Assert.Equal(-1, DocumentSearch.StepIndex(0, 0, backward: false));
        Assert.Equal(0, DocumentSearch.StepIndex(-1, 3, backward: false));
        Assert.Equal(2, DocumentSearch.StepIndex(-1, 3, backward: true));
        Assert.Equal(0, DocumentSearch.StepIndex(2, 3, backward: false));
        Assert.Equal(2, DocumentSearch.StepIndex(0, 3, backward: true));
    }

    [Fact]
    public async Task ContentSearcher_reports_scanned_file_counts()
    {
        using var dir = new TempDir();
        var a = dir.WriteFile("a.txt", "hello world");
        var b = dir.WriteFile("b.txt", "nothing here");
        var c = dir.WriteFile("c.txt", "hello again");

        var counts = new System.Collections.Concurrent.ConcurrentBag<int>();
        var matches = new List<Grepdesk.Core.ContentSearch.ContentMatch>();
        await foreach (var m in Grepdesk.Core.ContentSearch.ContentSearcher.SearchAsync([a, b, c], "hello", null, c => counts.Add(c)))
            matches.Add(m);

        Assert.Equal(2, matches.Count);
        Assert.Equal(3, counts.Count);
        Assert.Equal(3, counts.Max());
    }

    [Fact]
    public async Task ContentSearcher_reports_skip_reasons_and_file_paths()
    {
        using var dir = new TempDir();

        var corruptDocx = dir.Combine("corrupt.docx");
        File.WriteAllBytes(corruptDocx, [0x01, 0x02, 0x03, 0x04]);

        var encryptedDocx = dir.Combine("encrypted.docx");
        File.WriteAllBytes(encryptedDocx, [0xD0, 0xCF, 0x11, 0xE0, 0xA1, 0xB1, 0x1A, 0xE1, 0x00, 0x00]);

        var blankPdf = dir.Combine("scanned.pdf");
        var builder = new PdfDocumentBuilder();
        builder.AddPage(PageSize.A4);
        File.WriteAllBytes(blankPdf, builder.Build());

        var lockedTxt = dir.WriteFile("locked.txt", "secret text");
        FileStream? lockStream = OperatingSystem.IsWindows()
            ? new FileStream(lockedTxt, FileMode.Open, FileAccess.ReadWrite, FileShare.None)
            : null;

        try
        {
            var inputs = OperatingSystem.IsWindows()
                ? new[] { corruptDocx, encryptedDocx, blankPdf, lockedTxt }
                : new[] { corruptDocx, encryptedDocx, blankPdf };

            var skipped = new System.Collections.Concurrent.ConcurrentBag<Grepdesk.Core.ContentSearch.SkippedFile>();
            await foreach (var _ in Grepdesk.Core.ContentSearch.ContentSearcher.SearchAsync(inputs, "secret", s => skipped.Add(s)))
            {
            }

            var byPath = skipped.ToDictionary(s => s.FullPath, StringComparer.OrdinalIgnoreCase);
            Assert.Equal(Grepdesk.Core.ContentSearch.SkipReason.CorruptOrUnsupported, byPath[corruptDocx].Reason);
            Assert.Equal(Grepdesk.Core.ContentSearch.SkipReason.PasswordProtected, byPath[encryptedDocx].Reason);
            Assert.Equal(Grepdesk.Core.ContentSearch.SkipReason.NoExtractableText, byPath[blankPdf].Reason);
            if (OperatingSystem.IsWindows())
                Assert.Equal(Grepdesk.Core.ContentSearch.SkipReason.LockedOrInUse, byPath[lockedTxt].Reason);
        }
        finally
        {
            lockStream?.Dispose();
        }
    }
}
