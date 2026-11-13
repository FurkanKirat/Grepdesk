using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using Grepdesk.Cli;
using Grepdesk.Core.ContentSearch;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class OfficeDocumentTests
{
    internal static string WriteXlsx(
        TempDir dir,
        string sheetXml,
        string? sharedStringsXml = null,
        string? stylesXml = null,
        Dictionary<string, string>? extraParts = null,
        string fileName = "test.xlsx")
    {
        var path = dir.Combine(fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Add("xl/workbook.xml", """
            <workbook xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main"
                      xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <sheets>
                <sheet name="Summary" sheetId="1" r:id="rId1"/>
              </sheets>
            </workbook>
            """);
        Add("xl/_rels/workbook.xml.rels", """
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              <Relationship Id="rId1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet" Target="worksheets/sheet1.xml"/>
            </Relationships>
            """);
        Add("xl/worksheets/sheet1.xml", $"""
            <worksheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
              {sheetXml}
            </worksheet>
            """);

        if (sharedStringsXml is not null)
        {
            Add("xl/sharedStrings.xml", $"""
                <sst xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  {sharedStringsXml}
                </sst>
                """);
        }

        if (stylesXml is not null)
        {
            Add("xl/styles.xml", $"""
                <styleSheet xmlns="http://schemas.openxmlformats.org/spreadsheetml/2006/main">
                  {stylesXml}
                </styleSheet>
                """);
        }

        foreach (var (name, content) in extraParts ?? [])
            Add(name, content);

        return path;
    }

    internal static string WritePptx(
        TempDir dir,
        IReadOnlyList<string> slideBodies,
        string? notesText = null,
        Dictionary<string, string>? extraParts = null,
        string fileName = "deck.pptx")
    {
        var path = dir.Combine(fileName);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        var sldIds = string.Join("", Enumerable.Range(1, slideBodies.Count)
            .Select(i => $"""<p:sldId id="{255 + i}" r:id="rId{i}"/>"""));
        var rels = string.Join("", Enumerable.Range(1, slideBodies.Count)
            .Select(i => $"""<Relationship Id="rId{i}" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/slide" Target="slides/slide{i}.xml"/>"""));

        Add("ppt/presentation.xml", $"""
            <p:presentation xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                            xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                            xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
              <p:sldIdLst>{sldIds}</p:sldIdLst>
              <p:sldSz cx="12192000" cy="6858000"/>
            </p:presentation>
            """);
        Add("ppt/_rels/presentation.xml.rels", $"""
            <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
              {rels}
            </Relationships>
            """);

        for (var i = 0; i < slideBodies.Count; i++)
        {
            var num = i + 1;
            Add($"ppt/slides/slide{num}.xml", $"""
                <p:sld xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                       xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main"
                       xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships">
                  <p:cSld>
                    <p:spTree>
                      {slideBodies[i]}
                    </p:spTree>
                  </p:cSld>
                </p:sld>
                """);
        }

        if (notesText is not null && slideBodies.Count > 0)
        {
            Add("ppt/slides/_rels/slide1.xml.rels", """
                <Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">
                  <Relationship Id="rIdNotes1" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/notesSlide" Target="../notesSlides/notesSlide1.xml"/>
                </Relationships>
                """);
            Add("ppt/notesSlides/notesSlide1.xml", $"""
                <p:notes xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main"
                         xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main">
                  <p:cSld>
                    <p:spTree>
                      <p:sp>
                        <p:nvSpPr><p:cNvPr id="2" name="Notes Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
                        <p:spPr/>
                        <p:txBody>
                          <a:bodyPr/><a:lstStyle/>
                          <a:p><a:r><a:t>{notesText}</a:t></a:r></a:p>
                        </p:txBody>
                      </p:sp>
                    </p:spTree>
                  </p:cSld>
                </p:notes>
                """);
        }

        foreach (var (name, content) in extraParts ?? [])
            Add(name, content);

        return path;
    }

    [Fact]
    public void Xlsx_reads_shared_strings_inline_strings_numbers_dates_and_formulas()
    {
        using var dir = new TempDir();
        var path = WriteXlsx(dir,
            sheetXml: """
                <cols><col min="1" max="2" width="18"/></cols>
                <sheetData>
                  <row r="1">
                    <c r="A1" t="s" s="1"><v>0</v></c>
                    <c r="B1" t="inlineStr"><is><t>Revenue</t></is></c>
                  </row>
                  <row r="2">
                    <c r="A2" s="2"><v>45292</v></c>
                    <c r="B2"><v>120.5</v></c>
                  </row>
                  <row r="3">
                    <c r="A3" t="b"><v>1</v></c>
                    <c r="B3"><f>SUM(B2:B2)+79.5</f></c>
                  </row>
                </sheetData>
                <mergeCells count="1"><mergeCell ref="A4:B4"/></mergeCells>
                """,
            sharedStringsXml: """<si><r><t>Quarter </t></r><r><t>1</t></r></si>""",
            stylesXml: """
                <fonts count="2">
                  <font><sz val="11"/><name val="Calibri"/></font>
                  <font><b/><sz val="14"/><color rgb="FF1E66F5"/></font>
                </fonts>
                <fills count="1"><fill><patternFill patternType="none"/></fill></fills>
                <borders count="1"><border/></borders>
                <cellXfs count="3">
                  <xf numFmtId="0" fontId="0" fillId="0" borderId="0"/>
                  <xf numFmtId="0" fontId="1" fillId="0" borderId="0"><alignment horizontal="center"/></xf>
                  <xf numFmtId="14" fontId="0" fillId="0" borderId="0"/>
                </cellXfs>
                """);

        var doc = XlsxDocument.Load(path);

        var sheet = Assert.Single(doc.Sheets);
        Assert.Equal("Summary", sheet.Name);

        var a1 = sheet.GetCell(0, 0)!;
        Assert.Equal("Quarter 1", a1.DisplayText);
        Assert.True(a1.Style.Bold);
        Assert.Equal(14, a1.Style.FontSize);
        Assert.Equal("#1E66F5", a1.Style.FontColor);
        Assert.Equal(DocxAlignment.Center, a1.Style.Alignment);

        var b1 = sheet.GetCell(0, 1)!;
        Assert.Equal("Revenue", b1.DisplayText);

        var a2 = sheet.GetCell(1, 0)!;
        Assert.Equal(XlsxCellKind.Date, a2.Kind);
        Assert.Equal("2024-01-01", a2.DisplayText);

        var a3 = sheet.GetCell(2, 0)!;
        Assert.Equal(XlsxCellKind.Boolean, a3.Kind);
        Assert.Equal("TRUE", a3.DisplayText);

        var b3 = sheet.GetCell(2, 1)!;
        Assert.Equal(XlsxCellKind.Formula, b3.Kind);
        Assert.Equal("200", b3.DisplayText);
        Assert.Equal("=SUM(B2:B2)+79.5", b3.FormulaBarText);

        var a4 = sheet.GetCell(3, 0)!;
        Assert.Equal(2, a4.ColumnSpan);
    }

    [Fact]
    public void Xlsx_editor_saves_cell_values_formulas_styles_and_preserves_unknown_parts()
    {
        using var dir = new TempDir();
        var path = WriteXlsx(dir,
            sheetXml: """
                <sheetData>
                  <row r="1">
                    <c r="A1"><v>10</v></c>
                    <c r="B1"><v>25</v></c>
                  </row>
                </sheetData>
                """,
            extraParts: new Dictionary<string, string>
            {
                ["xl/customXml/item1.xml"] = "<custom>untouched</custom>"
            });

        var editDoc = XlsxEditDocument.Open(path);
        var snap = editDoc.TakeSnapshot();

        editDoc.SetCell(0, 0, 2, "=SUM(A1:B1)");
        editDoc.SetCell(0, 1, 0, "Total Label");
        editDoc.FormatCells(0, 1, 0, 1, 0, new XlsxStyleChange.Bold(true));
        Assert.True(editDoc.IsDirty);

        // Verify undo snapshot restore and re-apply
        var restored = editDoc.Restore(snap);
        Assert.Null(restored.Sheets[0].GetCell(0, 2));

        editDoc.SetCell(0, 0, 2, "=SUM(A1:B1)");
        editDoc.SetCell(0, 1, 0, "Total Label");
        editDoc.FormatCells(0, 1, 0, 1, 0, new XlsxStyleChange.Bold(true));
        editDoc.Save(path);
        Assert.False(editDoc.IsDirty);

        var reloaded = XlsxDocument.Load(path);
        var sheet = reloaded.Sheets[0];
        Assert.Equal("35", sheet.GetCell(0, 2)?.DisplayText);
        Assert.Equal("SUM(A1:B1)", sheet.GetCell(0, 2)?.Formula);
        Assert.Equal("Total Label", sheet.GetCell(1, 0)?.DisplayText);
        Assert.True(sheet.GetCell(1, 0)?.Style.Bold);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("xl/customXml/item1.xml")!.Open());
        Assert.Equal("<custom>untouched</custom>", reader.ReadToEnd());
    }

    [Fact]
    public async Task Pptx_reads_slides_tables_notes_and_supports_content_search()
    {
        using var dir = new TempDir();
        var slide1 = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="914400" y="914400"/><a:ext cx="10363200" cy="1828800"/></a:xfrm></p:spPr>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a:r><a:rPr sz="4000" b="1"/><a:t>Q4 Architecture Review</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            <p:sp>
              <p:nvSpPr><p:cNvPr id="3" name="Content Placeholder"/><p:cNvSpPr/><p:nvPr><p:ph type="body" idx="1"/></p:nvPr></p:nvSpPr>
              <p:spPr/>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a:pPr><a:buChar char="•"/></a:pPr><a:r><a:t>Zero-allocation hot paths</a:t></a:r></a:p>
                <a:p><a:pPr><a:buChar char="•"/></a:pPr><a:r><a:t>Multi-core archive engine</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            """;

        var slide2 = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title 2"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:spPr/>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a:r><a:t>Benchmarks</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            <p:graphicFrame>
              <p:xfrm><a:off x="914400" y="1828800"/><a:ext cx="8000000" cy="2000000"/></p:xfrm>
              <a:graphic><a:graphicData>
                <a:tbl>
                  <a:tblGrid><a:gridCol w="4000000"/><a:gridCol w="4000000"/></a:tblGrid>
                  <a:tr h="370840">
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>Metric</a:t></a:r></a:p></a:txBody></a:tc>
                    <a:tc><a:txBody><a:bodyPr/><a:p><a:r><a:t>Throughput</a:t></a:r></a:p></a:txBody></a:tc>
                  </a:tr>
                </a:tbl>
              </a:graphicData></a:graphic>
            </p:graphicFrame>
            """;

        var path = WritePptx(dir, [slide1, slide2], notesText: "Mention Linux container tests");

        var doc = PptxDocument.Load(path);
        Assert.Equal(2, doc.Slides.Count);
        Assert.Equal(960, doc.SlideWidth, 0);
        Assert.Equal(540, doc.SlideHeight, 0);
        Assert.Equal("Q4 Architecture Review", doc.Slides[0].Title);
        Assert.Equal("Mention Linux container tests", doc.Slides[0].Notes);
        Assert.Single(doc.Slides[1].Shapes.OfType<PptxTableShape>());

        Assert.True(ExtractorRegistry.TryGetExtractor(".pptx", out var extractor));
        var extracted = await extractor.ExtractTextAsync(path, default);
        Assert.NotNull(extracted);
        Assert.Contains("Q4 Architecture Review", extracted);
        Assert.Contains("Zero-allocation hot paths", extracted);
        Assert.Contains("Throughput", extracted);
        Assert.Contains("Mention Linux container tests", extracted);
    }

    [Fact]
    public void Pptx_reads_hanging_bullets_and_the_picture_of_an_embedded_object()
    {
        using var dir = new TempDir();
        const string ns = """xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """;
        var slide = $"""
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Content"/><p:cNvSpPr txBox="1"/><p:nvPr/></p:nvSpPr>
              <p:spPr><a:xfrm><a:off x="0" y="0"/><a:ext cx="4000000" cy="2000000"/></a:xfrm></p:spPr>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a:pPr marL="285750" indent="-285750"><a:buClr><a:srgbClr val="FF0000"/></a:buClr><a:buFont typeface="Wingdings"/><a:buChar char="&#xA7;"/></a:pPr><a:r><a:rPr lang="en-US"/><a:t>First point</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            <p:graphicFrame {ns}>
              <p:nvGraphicFramePr><p:cNvPr id="3" name="Object 2"/><p:cNvGraphicFramePr/><p:nvPr/></p:nvGraphicFramePr>
              <p:xfrm><a:off x="0" y="0"/><a:ext cx="100" cy="100"/></p:xfrm>
              <a:graphic><a:graphicData uri="http://schemas.openxmlformats.org/presentationml/2006/ole">
                <p:oleObj name="Chart" r:id="rId9"><p:pic><p:nvPicPr><p:cNvPr id="4" name="Chart"/><p:cNvPicPr/><p:nvPr/></p:nvPicPr>
                  <p:blipFill><a:blip r:embed="rId3"/></p:blipFill>
                  <p:spPr><a:xfrm><a:off x="635000" y="635000"/><a:ext cx="2540000" cy="1270000"/></a:xfrm></p:spPr></p:pic></p:oleObj>
              </a:graphicData></a:graphic>
            </p:graphicFrame>
            """;
        var path = WritePptx(dir, [slide], extraParts: new Dictionary<string, string>
        {
            ["ppt/slides/_rels/slide1.xml.rels"] = """<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships"><Relationship Id="rId3" Type="x/image" Target="../media/chart.emf"/></Relationships>""",
            ["ppt/media/chart.emf"] = "emf-bytes",
        });

        var doc = PptxDocument.Load(path);
        var paragraph = doc.Slides[0].Shapes.OfType<PptxTextShape>().Single().Paragraphs.Single();
        Assert.Equal("▪", paragraph.Bullet);
        Assert.Equal("#FF0000", paragraph.BulletColor);
        Assert.Equal(22.5, paragraph.MarginLeft);
        Assert.Equal(-22.5, paragraph.Indent);

        var chart = Assert.Single(doc.Slides[0].Shapes.OfType<PptxImageShape>());
        Assert.Equal(50, chart.X);
        Assert.Equal(200, chart.Width);
    }

    [Fact]
    public void Pptx_inherits_text_style_from_layout_and_draws_master_lines_behind_the_slide()
    {
        using var dir = new TempDir();
        const string ns = """xmlns:p="http://schemas.openxmlformats.org/presentationml/2006/main" xmlns:a="http://schemas.openxmlformats.org/drawingml/2006/main" xmlns:r="http://schemas.openxmlformats.org/officeDocument/2006/relationships" """;
        const string relsNs = "http://schemas.openxmlformats.org/package/2006/relationships";
        var slide = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
              <p:spPr/>
              <p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:rPr lang="en-US"/><a:t>Computer Architecture</a:t></a:r></a:p></p:txBody>
            </p:sp>
            """;
        var path = WritePptx(dir, [slide], extraParts: new Dictionary<string, string>
        {
            ["ppt/slides/_rels/slide1.xml.rels"] = $"""<Relationships xmlns="{relsNs}"><Relationship Id="rId1" Type="x/slideLayout" Target="../slideLayouts/slideLayout1.xml"/></Relationships>""",
            ["ppt/slideLayouts/_rels/slideLayout1.xml.rels"] = $"""<Relationships xmlns="{relsNs}"><Relationship Id="rId1" Type="x/slideMaster" Target="../slideMasters/slideMaster1.xml"/></Relationships>""",
            ["ppt/slideLayouts/slideLayout1.xml"] = $"""
                <p:sldLayout {ns}><p:cSld><p:spTree>
                  <p:sp>
                    <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="ctrTitle"/></p:nvPr></p:nvSpPr>
                    <p:spPr><a:xfrm><a:off x="0" y="3175000"/><a:ext cx="9144000" cy="1270000"/></a:xfrm></p:spPr>
                    <p:txBody><a:bodyPr lIns="0" anchor="b"/>
                      <a:lstStyle><a:lvl1pPr algn="l"><a:defRPr sz="5400" b="0"><a:solidFill><a:srgbClr val="002244"/></a:solidFill><a:latin typeface="Garamond"/></a:defRPr></a:lvl1pPr></a:lstStyle>
                      <a:p><a:endParaRPr lang="en-US"/></a:p></p:txBody>
                  </p:sp>
                </p:spTree></p:cSld></p:sldLayout>
                """,
            ["ppt/slideMasters/slideMaster1.xml"] = $"""
                <p:sldMaster {ns}><p:cSld><p:spTree>
                  <p:cxnSp>
                    <p:nvCxnSpPr><p:cNvPr id="9" name="Rule"/><p:cNvCxnSpPr/><p:nvPr/></p:nvCxnSpPr>
                    <p:spPr><a:xfrm><a:off x="0" y="4500000"/><a:ext cx="8000000" cy="0"/></a:xfrm>
                      <a:ln w="57150"><a:solidFill><a:srgbClr val="002244"/></a:solidFill></a:ln></p:spPr>
                  </p:cxnSp>
                </p:spTree></p:cSld></p:sldMaster>
                """,
        });

        var doc = PptxDocument.Load(path);
        var title = doc.Slides[0].Shapes.OfType<PptxTextShape>().Single();
        var run = title.Paragraphs[0].Runs[0];
        Assert.Equal(DocxAlignment.Left, title.Paragraphs[0].Alignment);
        Assert.Equal(54.0, run.FontSize);
        Assert.Equal("#002244", run.Color);
        Assert.False(run.Bold);
        Assert.Equal("Garamond", run.FontFamily);
        Assert.Equal(PptxAnchor.Bottom, title.Anchor);
        Assert.Equal(0, title.InsetLeft);

        var rule = Assert.Single(doc.Slides[0].Backdrop.OfType<PptxLineShape>());
        Assert.Equal("#002244", rule.Color);
        Assert.Equal(4.5, rule.Thickness);
        Assert.DoesNotContain("Rule", doc.ToPlainText());
    }

    [Fact]
    public void Pptx_editor_modifies_shapes_and_notes_losslessly()
    {
        using var dir = new TempDir();
        var slide1 = """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title 1"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:spPr/>
              <p:txBody><a:bodyPr/><a:lstStyle/>
                <a:p><a:r><a:rPr sz="3200"/><a:t>Draft Title</a:t></a:r></a:p>
              </p:txBody>
            </p:sp>
            """;
        var path = WritePptx(dir, [slide1], notesText: "Old note", extraParts: new Dictionary<string, string>
        {
            ["ppt/customProps.xml"] = "<props>keep-me</props>"
        });

        var editDoc = PptxEditDocument.Open(path);
        editDoc.SetShapeText(0, 0, "Final Release Title\nSubtitle Line");
        editDoc.FormatShape(0, 0, new DocxRunChange.Bold(true));
        editDoc.SetShapeAlignment(0, 0, DocxAlignment.Center);
        editDoc.SetSlideNotes(0, "Updated speaker notes");
        Assert.True(editDoc.IsDirty);
        editDoc.Save(path);
        Assert.False(editDoc.IsDirty);

        var reloaded = PptxDocument.Load(path);
        var shape = reloaded.Slides[0].Shapes.OfType<PptxTextShape>().Single();
        Assert.Equal("Final Release Title\nSubtitle Line", shape.PlainText);
        Assert.True(shape.Paragraphs[0].Runs[0].Bold);
        Assert.Equal(DocxAlignment.Center, shape.Paragraphs[0].Alignment);
        Assert.Equal("Updated speaker notes", reloaded.Slides[0].Notes);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("ppt/customProps.xml")!.Open());
        Assert.Equal("<props>keep-me</props>", reader.ReadToEnd());
    }

    [Fact]
    public async Task Cli_open_and_grep_work_with_pptx_and_xlsx()
    {
        using var dir = new TempDir();
        var pptxPath = WritePptx(dir,
        [
            """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Slide Alpha</a:t></a:r></a:p></p:txBody>
            </p:sp>
            """,
            """
            <p:sp>
              <p:nvSpPr><p:cNvPr id="2" name="Title"/><p:cNvSpPr/><p:nvPr><p:ph type="title"/></p:nvPr></p:nvSpPr>
              <p:spPr/><p:txBody><a:bodyPr/><a:lstStyle/><a:p><a:r><a:t>Slide Beta</a:t></a:r></a:p></p:txBody>
            </p:sp>
            """
        ]);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliRunner.RunAsync(["open", pptxPath, "--page", "2"], stdout, stderr);
        Assert.Equal(0, code);
        Assert.Contains("Slide Beta", stdout.ToString());
        Assert.DoesNotContain("Slide Alpha", stdout.ToString());

        stdout = new StringWriter();
        code = await CliRunner.RunAsync(["grep", "Beta", "--path", dir.Path], stdout, stderr);
        Assert.Equal(0, code);
        Assert.Contains("deck.pptx", stdout.ToString());
    }

    [Fact]
    public async Task Ppt_reads_slides_styles_pictures_tables_notes_and_supports_search_and_cli()
    {
        using var dir = new TempDir();
        var pngBytes = Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mP8z8BQDwAEhQGAhKmMIQAAAABJRU5ErkJggg==");
        var path = WriteSamplePpt(dir, "legacy.ppt", pngBytes);

        Assert.False(PptxDocument.CanEdit(path));
        Assert.True(PptxDocument.CanEdit(dir.Combine("other.pptx")));

        var doc = PptxDocument.Load(path);
        Assert.Equal(2, doc.Slides.Count);
        Assert.Equal(960, doc.SlideWidth, 0);
        Assert.Equal(540, doc.SlideHeight, 0);

        var s1 = doc.Slides[0];
        Assert.Equal("Legacy Q4 Review", s1.Title);
        Assert.Equal("#F0F4F8", s1.BackgroundColor);
        Assert.Equal("Speaker note for legacy slide 1", s1.Notes);

        var bodyShape = s1.Shapes.OfType<PptxTextShape>().First(s => s.Placeholder == PptxPlaceholderKind.Body);
        Assert.Equal(2, bodyShape.Paragraphs.Count);
        Assert.Equal("Binary CFBF parser", bodyShape.Paragraphs[0].PlainText);
        Assert.Equal("•", bodyShape.Paragraphs[0].Bullet);
        Assert.True(bodyShape.Paragraphs[0].Runs[0].Bold);
        Assert.Equal(22.0, bodyShape.Paragraphs[0].Runs[0].FontSize);
        Assert.Equal("#112233", bodyShape.Paragraphs[0].Runs[0].Color);

        var pic = Assert.Single(s1.Shapes.OfType<PptxImageShape>());
        Assert.Equal(pngBytes, pic.Data);

        var s2 = doc.Slides[1];
        Assert.Equal("Metrics Slide", s2.Title);
        var table = Assert.Single(s2.Shapes.OfType<PptxTableShape>());
        Assert.Equal(2, table.RowCount);
        Assert.Equal(2, table.ColumnCount);
        Assert.Contains(table.Cells, c => c.Row == 1 && c.Column == 1 && c.Text == "420 MB/s");

        Assert.True(ExtractorRegistry.TryGetExtractor(".ppt", out var extractor));
        var extracted = await extractor.ExtractTextAsync(path, default);
        Assert.NotNull(extracted);
        Assert.Contains("Legacy Q4 Review", extracted);
        Assert.Contains("Binary CFBF parser", extracted);
        Assert.Contains("420 MB/s", extracted);
        Assert.Contains("Speaker note for legacy slide 1", extracted);

        var stdout = new StringWriter();
        var stderr = new StringWriter();
        var code = await CliRunner.RunAsync(["open", path, "--page", "2"], stdout, stderr);
        Assert.Equal(0, code);
        Assert.Contains("Metrics Slide", stdout.ToString());
        Assert.DoesNotContain("Legacy Q4 Review", stdout.ToString());

        stdout = new StringWriter();
        code = await CliRunner.RunAsync(["grep", "420 MB/s", "--path", dir.Path], stdout, stderr);
        Assert.Equal(0, code);
        Assert.Contains("legacy.ppt", stdout.ToString());
    }

    [Fact]
    public void Ppt_rejects_encrypted_presentations()
    {
        using var dir = new TempDir();
        var path = WriteEncryptedPpt(dir, "locked.ppt");
        Assert.Throws<InvalidDataException>(() => PptxDocument.Load(path));
    }

    [Fact]
    public void Ppt_reads_ansi_text_master_backdrop_scheme_colors_and_edit_guard()
    {
        using var dir = new TempDir();
        // TextBytesAtom (0x0FA8) uses single-byte Windows-1252 / Latin-1 characters
        var ansiBytes = Encoding.Latin1.GetBytes("Master-Backed Title");
        var textType = new byte[4]; // 0 = Title
        var titleShape = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(202, 0x0A00),
            MakeFopt((0x0181, 0x08000004u), (0x01BF, 0x00100010u)), // scheme index 4 fill + fUsefFilled
            MakeClientAnchor(40, 30, 800, 110),
            PptRecord(0xF, 0, 0xF00D, Concat(
                PptRecord(0, 0, 0x0F9F, textType),
                PptRecord(0, 0, 0x0FA8, ansiBytes))),
            PptRecord(0xF, 0, 0xF011, MakePlaceHolderAtom(1))));

        // MainMaster (persistId = 5, masterId = 0x80000000) with custom ColorSchemeAtom and a backdrop text banner
        var schemePayload = new byte[32];
        // scheme[0] = #FAFAFA, scheme[1] = #101010, ..., scheme[4] = #336699
        schemePayload[0] = 0xFA; schemePayload[1] = 0xFA; schemePayload[2] = 0xFA;
        schemePayload[4] = 0x10; schemePayload[5] = 0x10; schemePayload[6] = 0x10;
        schemePayload[16] = 0x33; schemePayload[17] = 0x66; schemePayload[18] = 0x99;

        var masterBanner = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(202, 0x0200),
            MakeClientAnchor(40, 490, 400, 525),
            MakeInlineTextBox("Confidential Footer")));

        var masterSlide = PptRecord(0xF, 0, 0x03F8, Concat(
            PptRecord(0, 0, 0x07F0, schemePayload),
            PptRecord(0xF, 0, 0x040C, PptRecord(0xF, 0, 0xF002, masterBanner))));

        var slideAtom = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(slideAtom.AsSpan(12, 4), 0x80000000u); // masterIdRef
        BinaryPrimitives.WriteUInt16LittleEndian(slideAtom.AsSpan(20, 2), 7);
        var slide1 = PptRecord(0xF, 0, 0x03EE, Concat(
            PptRecord(2, 0, 0x03EF, slideAtom),
            PptRecord(0xF, 0, 0x040C, PptRecord(0xF, 0, 0xF002, titleShape))));

        var docAtom = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(docAtom.AsSpan(0, 4), 960 * 8);
        BinaryPrimitives.WriteInt32LittleEndian(docAtom.AsSpan(4, 4), 540 * 8);

        byte[] SlidePersist(uint persistId, uint slideId)
        {
            var b = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), persistId);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12, 4), slideId);
            return PptRecord(0, 0, 0x03F3, b);
        }

        var masterList = PptRecord(0xF, 1, 0x0FF0, SlidePersist(3, 0x80000000u));
        var slideList = PptRecord(0xF, 0, 0x0FF0, SlidePersist(2, 256));
        var document = PptRecord(0xF, 0, 0x03E8, Concat(
            PptRecord(1, 0, 0x03E9, docAtom),
            masterList,
            slideList));

        var offsetSlide1 = (uint)document.Length;
        var offsetMaster = offsetSlide1 + (uint)slide1.Length;
        var offsetPersistDir = offsetMaster + (uint)masterSlide.Length;

        var pdaPayload = new byte[4 + 3 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(0, 4), 1u | (3u << 20));
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(8, 4), offsetSlide1);
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(12, 4), offsetMaster);
        var persistDir = PptRecord(0, 0, 0x1772, pdaPayload);

        var offsetUserEdit = offsetPersistDir + (uint)persistDir.Length;
        var uePayload = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(uePayload.AsSpan(12, 4), offsetPersistDir);
        BinaryPrimitives.WriteUInt32LittleEndian(uePayload.AsSpan(16, 4), 1);
        var userEdit = PptRecord(0, 0, 0x0FF5, uePayload);

        var pptStream = Concat(document, slide1, masterSlide, persistDir, userEdit);
        if (pptStream.Length < 4096) Array.Resize(ref pptStream, 4096);

        var cuPayload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(4, 4), 0xE391C05Fu);
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(8, 4), offsetUserEdit);
        var currentUser = PptRecord(0, 0, 0x0FF6, cuPayload);

        // Even if renamed to .pptx, OLE2 magic bytes must prevent CanEdit from returning true and still load via PptReader
        var misnamedPath = dir.Combine("actually_legacy.pptx");
        WriteCompoundFile(misnamedPath, [("Current User", currentUser), ("PowerPoint Document", pptStream)]);

        Assert.False(PptxDocument.CanEdit(misnamedPath));
        var doc = PptxDocument.Load(misnamedPath);
        var slide = Assert.Single(doc.Slides);
        Assert.Equal("Master-Backed Title", slide.Title);
        var title = slide.Shapes.OfType<PptxTextShape>().First(s => s.PlainText == "Master-Backed Title");
        Assert.Equal("#336699", title.FillColor);
        Assert.Contains(slide.Backdrop.OfType<PptxTextShape>(), s => s.PlainText == "Confidential Footer");
    }

    private static byte[] PptRecord(byte ver, ushort inst, ushort type, ReadOnlySpan<byte> payload)
    {
        var buf = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(0, 2), (ushort)((ver & 0xF) | (inst << 4)));
        BinaryPrimitives.WriteUInt16LittleEndian(buf.AsSpan(2, 2), type);
        BinaryPrimitives.WriteUInt32LittleEndian(buf.AsSpan(4, 4), (uint)payload.Length);
        payload.CopyTo(buf.AsSpan(8));
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var total = parts.Sum(p => p.Length);
        var buf = new byte[total];
        var pos = 0;
        foreach (var p in parts)
        {
            Buffer.BlockCopy(p, 0, buf, pos, p.Length);
            pos += p.Length;
        }
        return buf;
    }

    private static byte[] MakeClientAnchor(int leftPts, int topPts, int rightPts, int bottomPts)
    {
        var b = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(0, 4), leftPts * 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(4, 4), topPts * 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(8, 4), rightPts * 8);
        BinaryPrimitives.WriteInt32LittleEndian(b.AsSpan(12, 4), bottomPts * 8);
        return PptRecord(0, 0, 0xF010, b);
    }

    private static byte[] MakeFsp(ushort shapeType, uint flags)
    {
        var b = new byte[8];
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), 1025);
        BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(4, 4), flags);
        return PptRecord(2, shapeType, 0xF00A, b);
    }

    private static byte[] MakeFopt(params (ushort PropId, uint Value)[] props)
    {
        var b = new byte[props.Length * 6];
        for (var i = 0; i < props.Length; i++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(i * 6, 2), props[i].PropId);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(i * 6 + 2, 4), props[i].Value);
        }
        return PptRecord(3, (ushort)props.Length, 0xF00B, b);
    }

    private static byte[] MakePlaceHolderAtom(byte placeholderId)
    {
        var b = new byte[8];
        b[4] = placeholderId;
        return PptRecord(0, 0, 0x0BC3, b);
    }

    private static byte[] MakeOutlineRefBox(int index)
    {
        var payload = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(payload, index);
        return PptRecord(0xF, 0, 0xF00D, PptRecord(0, 0, 0x0F9E, payload));
    }

    private static byte[] MakeInlineTextBox(string text)
    {
        var textType = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(textType, 4);
        var chars = Encoding.Unicode.GetBytes(text);
        return PptRecord(0xF, 0, 0xF00D, Concat(
            PptRecord(0, 0, 0x0F9F, textType),
            PptRecord(0, 0, 0x0FA0, chars)));
    }

    private static byte[] MakeStyleAtom(int charCountWithSentinel, bool bold, ushort fontSize, uint rgbColor, bool bullet)
    {
        using var ms = new MemoryStream();
        Span<byte> buf = stackalloc byte[4];
        // PF run
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)charCountWithSentinel); ms.Write(buf);
        ms.Write([0, 0]); // level 0
        uint pfMask = bullet ? 0x0001u : 0u;
        BinaryPrimitives.WriteUInt32LittleEndian(buf, pfMask); ms.Write(buf);
        if (bullet) ms.Write([1, 0]);
        // CF run
        BinaryPrimitives.WriteUInt32LittleEndian(buf, (uint)charCountWithSentinel); ms.Write(buf);
        uint cfMask = (bold ? 0x0001u : 0u) | 0x00020000u | 0x00040000u;
        BinaryPrimitives.WriteUInt32LittleEndian(buf, cfMask); ms.Write(buf);
        if (bold) ms.Write([1, 0]);
        ms.Write([(byte)(fontSize & 0xFF), (byte)(fontSize >> 8)]);
        BinaryPrimitives.WriteUInt32LittleEndian(buf, rgbColor); ms.Write(buf);
        return PptRecord(0, 0, 0x0FA1, ms.ToArray());
    }

    private static byte[] MakeSlideListBlock(int textType, string text, byte[]? styleAtom = null)
    {
        var tt = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(tt, textType);
        return Concat(
            PptRecord(0, 0, 0x0F9F, tt),
            PptRecord(0, 0, 0x0FA0, Encoding.Unicode.GetBytes(text)),
            styleAtom ?? []);
    }

    private static string WriteSamplePpt(TempDir dir, string fileName, byte[] pngBytes)
    {
        // Pictures stream: 1 PNG OfficeArtBlip at offset 0
        var blipPayload = new byte[16 + 1 + pngBytes.Length];
        blipPayload[16] = 0xFF;
        Buffer.BlockCopy(pngBytes, 0, blipPayload, 17, pngBytes.Length);
        var picturesStream = PptRecord(0, 0x6E0, 0xF01E, blipPayload);

        // Slide 1 (persistId = 2): background #F0F4F8, title ref 0, body ref 1, image pib=1
        var bgShape = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(1, 0x0400),
            MakeFopt((0x0181, 0x00F8F4F0u), (0x01BF, (1u << 20) | (1u << 4)))));
        var titleShape = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(1, 0x0200),
            MakeClientAnchor(60, 40, 900, 120),
            PptRecord(0xF, 0, 0xF011, MakePlaceHolderAtom(13)),
            MakeOutlineRefBox(0)));
        var bodyShape = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(1, 0x0200),
            MakeClientAnchor(60, 140, 500, 420),
            PptRecord(0xF, 0, 0xF011, MakePlaceHolderAtom(14)),
            MakeOutlineRefBox(1)));
        var picShape = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(75, 0x0200),
            MakeFopt((0x0104, 1)),
            MakeClientAnchor(520, 140, 880, 420)));

        var slide1Atom = new byte[24];
        BinaryPrimitives.WriteUInt32LittleEndian(slide1Atom.AsSpan(16, 4), 100); // notesIdRef = 100
        BinaryPrimitives.WriteUInt16LittleEndian(slide1Atom.AsSpan(20, 2), 7);
        var slide1 = PptRecord(0xF, 0, 0x03EE, Concat(
            PptRecord(2, 0, 0x03EF, slide1Atom),
            PptRecord(0xF, 0, 0x040C, PptRecord(0xF, 0, 0xF002, Concat(bgShape, titleShape, bodyShape, picShape)))));

        // Slide 2 (persistId = 3): title ref 0 + 2x2 table group
        var s2Title = PptRecord(0xF, 0, 0xF004, Concat(
            MakeFsp(1, 0x0200),
            MakeClientAnchor(60, 30, 900, 100),
            PptRecord(0xF, 0, 0xF011, MakePlaceHolderAtom(13)),
            MakeOutlineRefBox(0)));
        var fspgr = new byte[16];
        BinaryPrimitives.WriteInt32LittleEndian(fspgr.AsSpan(8, 4), 400);
        BinaryPrimitives.WriteInt32LittleEndian(fspgr.AsSpan(12, 4), 200);
        var tableGroupHeader = PptRecord(0xF, 0, 0xF004, Concat(
            PptRecord(1, 0, 0xF009, fspgr),
            MakeFsp(0, 0x0001),
            MakeClientAnchor(60, 130, 860, 330),
            PptRecord(0, 1, 0xF122, [0xA9, 0x03, 1, 0, 0, 0])));
        byte[] TableCell(int l, int t, int r, int b, string text)
        {
            var ca = new byte[16];
            BinaryPrimitives.WriteInt32LittleEndian(ca.AsSpan(0, 4), l);
            BinaryPrimitives.WriteInt32LittleEndian(ca.AsSpan(4, 4), t);
            BinaryPrimitives.WriteInt32LittleEndian(ca.AsSpan(8, 4), r);
            BinaryPrimitives.WriteInt32LittleEndian(ca.AsSpan(12, 4), b);
            return PptRecord(0xF, 0, 0xF004, Concat(MakeFsp(1, 0x0200), PptRecord(0, 0, 0xF00F, ca), MakeInlineTextBox(text)));
        }
        var tableGroup = PptRecord(0xF, 0, 0xF003, Concat(
            tableGroupHeader,
            TableCell(0, 0, 200, 100, "Metric"),
            TableCell(200, 0, 400, 100, "Speed"),
            TableCell(0, 100, 200, 200, "Read"),
            TableCell(200, 100, 400, 200, "420 MB/s")));
        var slide2Atom = new byte[24];
        BinaryPrimitives.WriteUInt16LittleEndian(slide2Atom.AsSpan(20, 2), 7);
        var slide2 = PptRecord(0xF, 0, 0x03EE, Concat(
            PptRecord(2, 0, 0x03EF, slide2Atom),
            PptRecord(0xF, 0, 0x040C, PptRecord(0xF, 0, 0xF002, Concat(s2Title, tableGroup)))));

        // Notes slide (persistId = 4, slideId = 100)
        var notesSlide = PptRecord(0xF, 0, 0x03F0, PptRecord(0xF, 0, 0x040C,
            PptRecord(0xF, 0, 0xF002, PptRecord(0xF, 0, 0xF004, Concat(
                MakeFsp(1, 0x0200),
                MakeClientAnchor(50, 50, 500, 200),
                MakeInlineTextBox("Speaker note for legacy slide 1"))))));

        // Document (persistId = 1)
        var docAtom = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(docAtom.AsSpan(0, 4), 960 * 8);
        BinaryPrimitives.WriteInt32LittleEndian(docAtom.AsSpan(4, 4), 540 * 8);

        var fbse = new byte[36];
        fbse[0] = 6; // PNG
        BinaryPrimitives.WriteUInt32LittleEndian(fbse.AsSpan(20, 4), (uint)picturesStream.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(fbse.AsSpan(28, 4), 0); // foDelay = 0
        var drawingGroup = PptRecord(0xF, 0, 0x040B,
            PptRecord(0xF, 0, 0xF000, PptRecord(0xF, 1, 0xF001, PptRecord(2, 6, 0xF007, fbse))));

        byte[] SlidePersist(uint persistId, uint slideId)
        {
            var b = new byte[20];
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(0, 4), persistId);
            BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(12, 4), slideId);
            return PptRecord(0, 0, 0x03F3, b);
        }

        const string bodyText = "Binary CFBF parser\rSecond point";
        var bodyStyle = MakeStyleAtom(bodyText.Length + 1, bold: true, fontSize: 22, rgbColor: 0xFE332211u, bullet: true);
        var slideList = PptRecord(0xF, 0, 0x0FF0, Concat(
            SlidePersist(2, 256),
            MakeSlideListBlock(0, "Legacy Q4 Review"),
            MakeSlideListBlock(1, bodyText, bodyStyle),
            SlidePersist(3, 257),
            MakeSlideListBlock(0, "Metrics Slide")));
        var notesList = PptRecord(0xF, 2, 0x0FF0, SlidePersist(4, 100));

        var document = PptRecord(0xF, 0, 0x03E8, Concat(
            PptRecord(1, 0, 0x03E9, docAtom),
            drawingGroup,
            slideList,
            notesList));

        // Assemble PowerPoint Document stream: [Document @ 0][Slide1][Slide2][Notes][PersistDir][UserEdit]
        var offsetSlide1 = (uint)document.Length;
        var offsetSlide2 = offsetSlide1 + (uint)slide1.Length;
        var offsetNotes = offsetSlide2 + (uint)slide2.Length;
        var offsetPersistDir = offsetNotes + (uint)notesSlide.Length;

        var pdaPayload = new byte[4 + 4 * 4];
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(0, 4), 1u | (4u << 20)); // persistId 1..4
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(4, 4), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(8, 4), offsetSlide1);
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(12, 4), offsetSlide2);
        BinaryPrimitives.WriteUInt32LittleEndian(pdaPayload.AsSpan(16, 4), offsetNotes);
        var persistDir = PptRecord(0, 0, 0x1772, pdaPayload);

        var offsetUserEdit = offsetPersistDir + (uint)persistDir.Length;
        var uePayload = new byte[28];
        BinaryPrimitives.WriteUInt32LittleEndian(uePayload.AsSpan(12, 4), offsetPersistDir);
        BinaryPrimitives.WriteUInt32LittleEndian(uePayload.AsSpan(16, 4), 1); // docPersistIdRef
        var userEdit = PptRecord(0, 0, 0x0FF5, uePayload);

        // Pad PowerPoint Document >= 4096 bytes so it exercises regular FAT sectors while Current User uses MiniStream
        var pptStream = Concat(document, slide1, slide2, notesSlide, persistDir, userEdit);
        if (pptStream.Length < 4096)
            Array.Resize(ref pptStream, 4096);

        var cuPayload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(4, 4), 0xE391C05Fu);
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(8, 4), offsetUserEdit);
        var currentUser = PptRecord(0, 0, 0x0FF6, cuPayload);

        var path = dir.Combine(fileName);
        WriteCompoundFile(path,
        [
            ("Current User", currentUser),
            ("PowerPoint Document", pptStream),
            ("Pictures", picturesStream)
        ]);
        return path;
    }

    private static string WriteEncryptedPpt(TempDir dir, string fileName)
    {
        var cuPayload = new byte[20];
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(0, 4), 20);
        BinaryPrimitives.WriteUInt32LittleEndian(cuPayload.AsSpan(4, 4), 0xF3D1C4DFu);
        var currentUser = PptRecord(0, 0, 0x0FF6, cuPayload);

        var path = dir.Combine(fileName);
        WriteCompoundFile(path, [("Current User", currentUser)]);
        return path;
    }

    private static void WriteCompoundFile(string path, IReadOnlyList<(string Name, byte[] Data)> streams)
    {
        const int sectorSize = 512;
        const int miniSectorSize = 64;
        const uint miniCutoff = 4096;

        // Partition streams into mini (< 4096) and regular (>= 4096)
        using var miniStreamBuf = new MemoryStream();
        var miniFat = new List<uint>();
        var regularChunks = new List<byte[]>();
        var fat = new List<uint>();

        uint AllocRegularChain(byte[] data)
        {
            if (data.Length == 0) return 0xFFFFFFFEu;
            var start = (uint)regularChunks.Count;
            var count = (data.Length + sectorSize - 1) / sectorSize;
            for (var i = 0; i < count; i++)
            {
                var sector = new byte[sectorSize];
                var len = Math.Min(sectorSize, data.Length - i * sectorSize);
                Buffer.BlockCopy(data, i * sectorSize, sector, 0, len);
                var sectId = (uint)regularChunks.Count;
                regularChunks.Add(sector);
                fat.Add(i + 1 < count ? sectId + 1 : 0xFFFFFFFEu);
            }
            return start;
        }

        uint AllocMiniChain(byte[] data)
        {
            if (data.Length == 0) return 0xFFFFFFFEu;
            var start = (uint)miniFat.Count;
            var count = (data.Length + miniSectorSize - 1) / miniSectorSize;
            for (var i = 0; i < count; i++)
            {
                var mini = new byte[miniSectorSize];
                var len = Math.Min(miniSectorSize, data.Length - i * miniSectorSize);
                Buffer.BlockCopy(data, i * miniSectorSize, mini, 0, len);
                miniStreamBuf.Write(mini);
                miniFat.Add(i + 1 < count ? (uint)(miniFat.Count + 1) : 0xFFFFFFFEu);
            }
            return start;
        }

        var dirEntries = new List<(string Name, byte Type, uint Left, uint Right, uint Child, uint Start, ulong Size)>();
        var streamInfos = new List<(string Name, uint Start, ulong Size)>();
        foreach (var (name, data) in streams)
        {
            var start = data.Length < miniCutoff ? AllocMiniChain(data) : AllocRegularChain(data);
            streamInfos.Add((name, start, (ulong)data.Length));
        }

        var miniStreamBytes = miniStreamBuf.ToArray();
        var miniStreamStart = AllocRegularChain(miniStreamBytes);
        var miniFatBytes = new byte[Math.Max(1, miniFat.Count) * 4];
        for (var i = 0; i < miniFat.Count; i++)
            BinaryPrimitives.WriteUInt32LittleEndian(miniFatBytes.AsSpan(i * 4, 4), miniFat[i]);
        var sectMiniFatStart = miniFat.Count > 0 ? AllocRegularChain(miniFatBytes) : 0xFFFFFFFEu;
        var csectMiniFat = miniFat.Count > 0 ? (uint)((miniFatBytes.Length + sectorSize - 1) / sectorSize) : 0u;

        // Directory: entry 0 = Root Entry, entries 1..N = flat right-sibling chain
        dirEntries.Add(("Root Entry", 5, 0xFFFFFFFFu, 0xFFFFFFFFu, streamInfos.Count > 0 ? 1u : 0xFFFFFFFFu, miniStreamStart, (ulong)miniStreamBytes.Length));
        for (var i = 0; i < streamInfos.Count; i++)
        {
            var next = i + 1 < streamInfos.Count ? (uint)(i + 2) : 0xFFFFFFFFu;
            dirEntries.Add((streamInfos[i].Name, 2, 0xFFFFFFFFu, next, 0xFFFFFFFFu, streamInfos[i].Start, streamInfos[i].Size));
        }
        while (dirEntries.Count % 4 != 0)
            dirEntries.Add(("", 0, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0xFFFFFFFFu, 0));

        var dirBytes = new byte[dirEntries.Count * 128];
        for (var i = 0; i < dirEntries.Count; i++)
        {
            var e = dirEntries[i];
            var span = dirBytes.AsSpan(i * 128, 128);
            if (e.Name.Length > 0)
            {
                var nameBytes = Encoding.Unicode.GetBytes(e.Name + "\0");
                nameBytes.CopyTo(span);
                BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(64, 2), (ushort)nameBytes.Length);
            }
            span[66] = e.Type;
            span[67] = 1; // black node
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(68, 4), e.Left);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(72, 4), e.Right);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(76, 4), e.Child);
            BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(116, 4), e.Start);
            BinaryPrimitives.WriteUInt64LittleEndian(span.Slice(120, 8), e.Size);
        }
        var sectDirStart = AllocRegularChain(dirBytes);

        // Allocate FAT sector(s)
        var intsPerFatSector = sectorSize / 4;
        var fatSectorCount = Math.Max(1, (fat.Count + intsPerFatSector - 1) / intsPerFatSector);
        if ((fat.Count + fatSectorCount + intsPerFatSector - 1) / intsPerFatSector > fatSectorCount)
            fatSectorCount++;

        var fatSectorIds = new List<uint>(fatSectorCount);
        for (var i = 0; i < fatSectorCount; i++)
        {
            fatSectorIds.Add((uint)regularChunks.Count);
            regularChunks.Add(new byte[sectorSize]);
            fat.Add(0xFFFFFFFDu); // FATSECT
        }
        for (var i = 0; i < fatSectorCount; i++)
        {
            var sec = regularChunks[(int)fatSectorIds[i]];
            for (var j = 0; j < intsPerFatSector; j++)
            {
                var idx = i * intsPerFatSector + j;
                var val = idx < fat.Count ? fat[idx] : 0xFFFFFFFFu;
                BinaryPrimitives.WriteUInt32LittleEndian(sec.AsSpan(j * 4, 4), val);
            }
        }

        // Build 512-byte CFBF header
        var header = new byte[512];
        BinaryPrimitives.WriteUInt64LittleEndian(header.AsSpan(0, 8), 0xE11AB1A1E011CFD0UL);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(24, 2), 0x003E);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(26, 2), 3); // v3
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(28, 2), 0xFFFE);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(30, 2), 9); // 512
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32, 2), 6); // 64
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(44, 4), (uint)fatSectorIds.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(48, 4), sectDirStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(56, 4), miniCutoff);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(60, 4), sectMiniFatStart);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(64, 4), csectMiniFat);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(68, 4), 0xFFFFFFFEu);
        for (var i = 0; i < 109; i++)
        {
            var val = i < fatSectorIds.Count ? fatSectorIds[i] : 0xFFFFFFFFu;
            BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(76 + i * 4, 4), val);
        }

        using var fs = File.Create(path);
        fs.Write(header);
        foreach (var sec in regularChunks)
            fs.Write(sec);
    }
}
