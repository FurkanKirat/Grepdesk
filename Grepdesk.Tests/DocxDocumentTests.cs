using System.IO.Compression;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class DocxDocumentTests
{
    internal const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string R = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    internal static string WriteDocx(TempDir dir, string body, string? styles = null, string? numbering = null, string? rels = null,
        Dictionary<string, string>? parts = null, string? lastSection = null)
    {
        var path = dir.Combine("test.docx");
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        void Add(string name, string content)
        {
            using var writer = new StreamWriter(zip.CreateEntry(name).Open());
            writer.Write(content);
        }

        Add("word/document.xml", $"""<w:document xmlns:w="{W}" xmlns:r="{R}"><w:body>{body}{lastSection ?? DefaultSection}</w:body></w:document>""");
        if (styles is not null) Add("word/styles.xml", $"""<w:styles xmlns:w="{W}">{styles}</w:styles>""");
        if (numbering is not null) Add("word/numbering.xml", $"""<w:numbering xmlns:w="{W}">{numbering}</w:numbering>""");
        if (rels is not null) Add("word/_rels/document.xml.rels", $"""<Relationships xmlns="http://schemas.openxmlformats.org/package/2006/relationships">{rels}</Relationships>""");
        foreach (var (name, content) in parts ?? []) Add(name, content);
        return path;
    }

    private const string DefaultSection = """<w:sectPr><w:pgSz w:w="11906" w:h="16838"/><w:pgMar w:top="1440" w:right="1134" w:bottom="1440" w:left="1134" w:header="708" w:footer="708"/></w:sectPr>""";

    private static List<DocxParagraph> Paragraphs(DocxDocument doc) => doc.Body.OfType<DocxParagraph>().ToList();

    private static string TextOf(DocxParagraph p) => string.Concat(p.Inlines.OfType<DocxText>().Select(t => t.Text));

    [Fact]
    public void Runs_split_by_Word_join_and_keep_their_formatting()
    {
        using var dir = new TempDir();
        var path = WriteDocx(dir, """
            <w:p><w:r><w:t>Merha</w:t></w:r><w:r><w:t xml:space="preserve">ba </w:t></w:r><w:r><w:rPr><w:b/><w:sz w:val="28"/><w:color w:val="FF0000"/></w:rPr><w:t>dünya</w:t></w:r></w:p>
            """);

        var doc = DocxDocument.Load(path, default);

        var runs = Paragraphs(doc).Single().Inlines.OfType<DocxText>().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Equal("Merhaba ", runs[0].Text);
        Assert.False(runs[0].Style.Bold);
        Assert.Equal("dünya", runs[1].Text);
        Assert.True(runs[1].Style.Bold);
        Assert.Equal(14, runs[1].Style.Size);
        Assert.Equal("#FF0000", runs[1].Style.Color);
        Assert.Equal(595.3, doc.Sections.Single().PageWidth, 1);
        Assert.Equal(56.7, doc.Sections.Single().MarginLeft, 1);
    }

    [Fact]
    public void Heading_styles_bring_their_size_and_inherited_font()
    {
        using var dir = new TempDir();
        var path = WriteDocx(dir,
            """<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p><w:p><w:r><w:t>Body</w:t></w:r></w:p>""",
            styles: """
                <w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii="Calibri"/><w:sz w:val="22"/></w:rPr></w:rPrDefault><w:pPrDefault><w:pPr><w:spacing w:after="160"/></w:pPr></w:pPrDefault></w:docDefaults>
                <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
                <w:style w:type="paragraph" w:styleId="Heading1"><w:basedOn w:val="Normal"/><w:pPr><w:spacing w:before="240" w:after="0"/></w:pPr><w:rPr><w:b/><w:sz w:val="32"/></w:rPr></w:style>
                """);

        var paragraphs = Paragraphs(DocxDocument.Load(path, default));

        var title = (DocxText)paragraphs[0].Inlines.Single();
        Assert.True(title.Style.Bold);
        Assert.Equal(16, title.Style.Size);
        Assert.Equal("Calibri", title.Style.Font);
        Assert.Equal(12, paragraphs[0].Format.SpaceBefore);
        Assert.Equal(0, paragraphs[0].Format.SpaceAfter);

        var body = (DocxText)paragraphs[1].Inlines.Single();
        Assert.False(body.Style.Bold);
        Assert.Equal(11, body.Style.Size);
        Assert.Equal(8, paragraphs[1].Format.SpaceAfter);
    }

    [Fact]
    public void Lists_are_numbered_per_level_and_bullets_become_real_characters()
    {
        using var dir = new TempDir();
        static string Item(int num, int level, string text) =>
            $"""<w:p><w:pPr><w:numPr><w:ilvl w:val="{level}"/><w:numId w:val="{num}"/></w:numPr></w:pPr><w:r><w:t>{text}</w:t></w:r></w:p>""";
        var path = WriteDocx(dir,
            Item(1, 0, "a") + Item(1, 1, "a1") + Item(1, 1, "a2") + Item(1, 0, "b") + Item(1, 1, "b1") + Item(2, 0, "dot"),
            numbering: """
                <w:abstractNum w:abstractNumId="0">
                  <w:lvl w:ilvl="0"><w:start w:val="1"/><w:numFmt w:val="decimal"/><w:lvlText w:val="%1."/><w:pPr><w:ind w:left="720" w:hanging="360"/></w:pPr></w:lvl>
                  <w:lvl w:ilvl="1"><w:start w:val="1"/><w:numFmt w:val="lowerLetter"/><w:lvlText w:val="%1.%2)"/></w:lvl>
                </w:abstractNum>
                <w:abstractNum w:abstractNumId="1"><w:lvl w:ilvl="0"><w:numFmt w:val="bullet"/><w:lvlText w:val="&#xF0B7;"/></w:lvl></w:abstractNum>
                <w:num w:numId="1"><w:abstractNumId w:val="0"/></w:num>
                <w:num w:numId="2"><w:abstractNumId w:val="1"/></w:num>
                """);

        var paragraphs = Paragraphs(DocxDocument.Load(path, default));

        Assert.Equal(["1.", "1.a)", "1.b)", "2.", "2.a)", "•"], paragraphs.Select(p => p.Marker));
        Assert.Equal(36, paragraphs[0].Format.IndentLeft);
        Assert.Equal(-18, paragraphs[0].Format.FirstLine);
    }

    [Fact]
    public void Tables_keep_merged_cells_and_column_widths()
    {
        using var dir = new TempDir();
        static string Cell(string text, string props = "") => $"""<w:tc><w:tcPr>{props}</w:tcPr><w:p><w:r><w:t>{text}</w:t></w:r></w:p></w:tc>""";
        var path = WriteDocx(dir, $"""
            <w:tbl><w:tblPr><w:tblBorders><w:top w:val="single"/></w:tblBorders></w:tblPr>
              <w:tblGrid><w:gridCol w:w="2000"/><w:gridCol w:w="3000"/><w:gridCol w:w="1000"/></w:tblGrid>
              <w:tr>{Cell("wide", """<w:gridSpan w:val="2"/>""")}{Cell("tall", """<w:vMerge w:val="restart"/>""")}</w:tr>
              <w:tr>{Cell("x")}{Cell("y")}{Cell("", "<w:vMerge/>")}</w:tr>
            </w:tbl>
            """);

        var table = Assert.IsType<DocxTable>(DocxDocument.Load(path, default).Body.Single());

        Assert.True(table.Borders);
        Assert.Equal([100, 150, 50], table.ColumnWidths);
        Assert.Equal(2, table.RowCount);
        Assert.Equal(4, table.Cells.Count);
        var wide = table.Cells.Single(c => c.Row == 0 && c.Column == 0);
        Assert.Equal(2, wide.ColumnSpan);
        var tall = table.Cells.Single(c => c.Column == 2);
        Assert.Equal(2, tall.RowSpan);
    }

    [Fact]
    public void Hyperlinks_and_page_breaks_are_kept()
    {
        using var dir = new TempDir();
        var path = WriteDocx(dir, """
            <w:p><w:hyperlink r:id="rId9"><w:r><w:t>site</w:t></w:r></w:hyperlink><w:r><w:t xml:space="preserve"> and </w:t></w:r>
              <w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText xml:space="preserve"> HYPERLINK "https://b.example/" </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>field</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p>
            <w:p><w:r><w:br w:type="page"/></w:r></w:p>
            <w:p><w:r><w:lastRenderedPageBreak/><w:t>Page two</w:t></w:r></w:p>
            """,
            rels: """<Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://a.example/" TargetMode="External"/>""");

        var body = DocxDocument.Load(path, default).Body;

        var first = (DocxParagraph)body[0];
        Assert.Equal("site and field", TextOf(first));
        var links = first.Inlines.OfType<DocxText>().Select(t => t.Link).ToList();
        Assert.Equal(["https://a.example/", null, "https://b.example/"], links);
        Assert.IsType<DocxPageBreak>(body[1]);
        // The forced break and Word's own break before "Page two" make one page break, not two.
        Assert.Single(body.OfType<DocxPageBreak>());
        Assert.Equal("Page two", TextOf((DocxParagraph)body[^1]));
    }

    [Fact]
    public void Sections_keep_their_footers_and_page_numbering()
    {
        using var dir = new TempDir();
        const string footer = $"""<w:ftr xmlns:w="{W}"><w:p><w:r><w:fldChar w:fldCharType="begin"/></w:r><w:r><w:instrText> PAGE   \* MERGEFORMAT </w:instrText></w:r><w:r><w:fldChar w:fldCharType="separate"/></w:r><w:r><w:t>7</w:t></w:r><w:r><w:fldChar w:fldCharType="end"/></w:r></w:p></w:ftr>""";
        var path = WriteDocx(dir, $"""
            <w:p><w:r><w:t>Cover</w:t></w:r></w:p>
            <w:p><w:pPr><w:sectPr><w:footerReference w:type="default" r:id="rF"/><w:pgNumType w:fmt="lowerRoman" w:start="1"/><w:titlePg/></w:sectPr></w:pPr><w:r><w:t>Preface</w:t></w:r></w:p>
            <w:p><w:r><w:t>Chapter</w:t></w:r></w:p>
            """,
            rels: """<Relationship Id="rF" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/footer" Target="footer1.xml"/>""",
            parts: new() { ["word/footer1.xml"] = footer },
            lastSection: """<w:sectPr><w:pgNumType w:start="1"/></w:sectPr>""");

        var doc = DocxDocument.Load(path, default);

        Assert.Equal(2, doc.Sections.Count);
        Assert.True(doc.Sections[0].TitlePage);
        Assert.Equal("lowerRoman", doc.Sections[0].NumberFormat);
        Assert.Empty(doc.Sections[0].FirstFooter);
        // The second section names no footer of its own, so it keeps the first one's.
        Assert.Same(doc.Sections[0].Footer, doc.Sections[1].Footer);
        var number = Assert.IsType<DocxPageNumber>(((DocxParagraph)doc.Sections[1].Footer.Single()).Inlines.Single());
        Assert.False(number.Total);
        Assert.Equal(new DocxPageBreak(1), doc.Body[2]);
        Assert.Equal("iv", DocxDocument.FormatNumber(4, "lowerRoman"));
    }

    [Fact]
    public void Not_a_word_file_throws()
    {
        using var dir = new TempDir();
        var path = dir.WriteFile("fake.docx", "plain text");

        Assert.ThrowsAny<InvalidDataException>(() => DocxDocument.Load(path, default));
    }
}
