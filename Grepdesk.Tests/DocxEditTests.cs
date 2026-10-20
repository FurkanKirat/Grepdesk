using System.IO.Compression;
using System.Xml.Linq;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Tests;

public class DocxEditTests
{
    private static readonly XNamespace W = DocxDocumentTests.W;

    private static (DocxEditDocument Doc, string Path) Open(TempDir dir, string body, string? styles = null,
        Dictionary<string, string>? parts = null)
    {
        var path = DocxDocumentTests.WriteDocx(dir, body, styles: styles, parts: parts);
        var doc = DocxEditDocument.Open(path, default);
        doc.Layout();
        return (doc, path);
    }

    private static List<DocxParagraph> Reload(string path) =>
        DocxDocument.Load(path, default).Body.OfType<DocxParagraph>().ToList();

    private static string TextOf(DocxParagraph p) => string.Concat(p.Inlines.OfType<DocxText>().Select(t => t.Text));

    private static XDocument Part(string path, string name)
    {
        using var zip = ZipFile.OpenRead(path);
        using var stream = zip.GetEntry(name)!.Open();
        return XDocument.Load(stream);
    }

    [Fact]
    public void Typed_and_bolded_text_saves_and_keeps_bookmarks()
    {
        using var dir = new TempDir();
        var (doc, path) = Open(dir, """<w:p><w:bookmarkStart w:id="0" w:name="here"/><w:r><w:t>Hello world</w:t></w:r><w:bookmarkEnd w:id="0"/></w:p>""");

        doc.Insert(new DocxPosition(0, 5), ", big");
        doc.FormatText(new DocxPosition(0, 7), new DocxPosition(0, 10), new DocxRunChange.Bold(true));
        Assert.True(doc.IsDirty);
        doc.Save(path);
        Assert.False(doc.IsDirty);

        var paragraph = Reload(path).Single();
        Assert.Equal("Hello, big world", TextOf(paragraph));
        var bold = paragraph.Inlines.OfType<DocxText>().Single(t => t.Style.Bold);
        Assert.Equal("big", bold.Text);
        var xml = Part(path, "word/document.xml");
        Assert.Single(xml.Descendants(W + "bookmarkStart"));
        Assert.Single(xml.Descendants(W + "bookmarkEnd"));
    }

    [Fact]
    public void Enter_splits_and_backspace_joins_again()
    {
        using var dir = new TempDir();
        var (doc, _) = Open(dir, """<w:p><w:r><w:t>OneTwo</w:t></w:r></w:p>""");

        var split = doc.Enter(new DocxPosition(0, 3));
        Assert.Equal(new DocxPosition(1, 0), split.Caret);
        Assert.Equal(["One", "Two"], doc.Paragraphs.Select(p => p.Text));

        var joined = doc.Backspace(split.Caret);
        Assert.Equal(new DocxPosition(0, 3), joined.Caret);
        Assert.Equal("OneTwo", doc.Paragraphs.Single().Text);
    }

    [Fact]
    public void Enter_after_a_heading_starts_its_next_style()
    {
        using var dir = new TempDir();
        var (doc, path) = Open(dir, """<w:p><w:pPr><w:pStyle w:val="Heading1"/></w:pPr><w:r><w:t>Title</w:t></w:r></w:p>""",
            styles: """
                <w:style w:type="paragraph" w:default="1" w:styleId="Normal"><w:name w:val="Normal"/></w:style>
                <w:style w:type="paragraph" w:styleId="Heading1"><w:name w:val="heading 1"/><w:next w:val="Normal"/><w:rPr><w:b/></w:rPr></w:style>
                """);

        var result = doc.Enter(new DocxPosition(0, 5));
        doc.Insert(result.Caret, "Body");

        Assert.Equal("Heading1", doc.StyleIdOf(doc.Paragraphs[0]));
        Assert.Equal("Normal", doc.StyleIdOf(doc.Paragraphs[1]));
        doc.Save(path);
        var paragraphs = Reload(path);
        Assert.True(paragraphs[0].Inlines.OfType<DocxText>().Single().Style.Bold);
        Assert.False(paragraphs[1].Inlines.OfType<DocxText>().Single().Style.Bold);
    }

    [Fact]
    public void Bullets_create_numbering_the_file_lacked()
    {
        using var dir = new TempDir();
        var (doc, path) = Open(dir, """<w:p><w:r><w:t>a</w:t></w:r></w:p><w:p><w:r><w:t>b</w:t></w:r></w:p>""");

        var result = doc.ToggleList(0, 1, numbered: false);
        Assert.True(result.Relayout);
        doc.Layout();
        Assert.All(doc.Paragraphs, p => Assert.Equal("bullet", doc.ListFormat(p)));
        doc.Save(path);

        Assert.Equal(["•", "•"], Reload(path).Select(p => p.Marker));
        var rels = Part(path, "word/_rels/document.xml.rels");
        Assert.Contains(rels.Root!.Elements(), r => (string?)r.Attribute("Target") == "numbering.xml");

        // Off again when every paragraph already has bullets.
        doc.ToggleList(0, 1, numbered: false);
        doc.Layout();
        Assert.All(doc.Paragraphs, p => Assert.Null(doc.ListFormat(p)));
    }

    [Fact]
    public void Enter_in_an_empty_list_item_ends_the_list()
    {
        using var dir = new TempDir();
        var (doc, _) = Open(dir, """<w:p><w:r><w:t>item</w:t></w:r></w:p>""");
        doc.ToggleList(0, 0, numbered: true);
        doc.Layout();

        var next = doc.Enter(new DocxPosition(0, 4));
        doc.Layout();
        Assert.True(doc.Paragraphs[1].IsList);
        doc.Enter(next.Caret);
        doc.Layout();

        Assert.Equal(2, doc.Paragraphs.Count);
        Assert.False(doc.Paragraphs[1].IsList);
    }

    [Fact]
    public void Alignment_goes_where_the_schema_wants_it()
    {
        using var dir = new TempDir();
        var (doc, path) = Open(dir, """<w:p><w:pPr><w:pStyle w:val="X"/><w:spacing w:after="0"/><w:rPr><w:b/></w:rPr></w:pPr><w:r><w:t>x</w:t></w:r></w:p>""");

        doc.SetAlignment(0, 0, DocxAlignment.Center);
        doc.Save(path);

        var pPr = Part(path, "word/document.xml").Descendants(W + "pPr").Single();
        Assert.Equal(["pStyle", "spacing", "jc", "rPr"], pPr.Elements().Select(e => e.Name.LocalName));
        Assert.Equal(DocxAlignment.Center, Reload(path).Single().Format.Alignment);
    }

    [Fact]
    public void Undo_goes_back_to_the_snapshot()
    {
        using var dir = new TempDir();
        var (doc, _) = Open(dir, """<w:p><w:r><w:t>keep</w:t></w:r></w:p>""");

        var snapshot = doc.TakeSnapshot();
        doc.Insert(new DocxPosition(0, 4), " this?");
        doc.Enter(new DocxPosition(0, 0));
        Assert.True(doc.IsDirty);

        doc.Restore(snapshot);
        doc.Layout();
        Assert.Equal("keep", doc.Paragraphs.Single().Text);
        Assert.False(doc.IsDirty);
    }

    [Fact]
    public void Parts_the_editor_does_not_touch_are_copied_unchanged()
    {
        using var dir = new TempDir();
        const string custom = "<root><item>untouched</item></root>";
        var (doc, path) = Open(dir, """<w:p><w:r><w:t>x</w:t></w:r></w:p>""",
            parts: new() { ["customXml/item1.xml"] = custom });

        doc.Insert(new DocxPosition(0, 1), "y");
        doc.Save(path);

        using var zip = ZipFile.OpenRead(path);
        using var reader = new StreamReader(zip.GetEntry("customXml/item1.xml")!.Open());
        Assert.Equal(custom, reader.ReadToEnd());
    }

    [Fact]
    public void Deleting_across_paragraphs_joins_them()
    {
        using var dir = new TempDir();
        var (doc, _) = Open(dir, """<w:p><w:r><w:t>first</w:t></w:r></w:p><w:p><w:r><w:t>middle</w:t></w:r></w:p><w:p><w:r><w:t>last</w:t></w:r></w:p>""");

        var result = doc.Delete(new DocxPosition(0, 2), new DocxPosition(2, 2));

        Assert.Equal(new DocxPosition(0, 2), result.Caret);
        Assert.Equal("fist", doc.Paragraphs.Single().Text);
        Assert.Equal(2, result.Removed!.Count);
    }

    [Fact]
    public void Backspace_takes_a_whole_character_with_its_combining_mark()
    {
        using var dir = new TempDir();
        // "İ" as I + combining dot above, as some Turkish documents store it.
        var (doc, _) = Open(dir, "<w:p><w:r><w:t>SI\u0307</w:t></w:r></w:p>");

        doc.Backspace(new DocxPosition(0, 3));

        Assert.Equal("S", doc.Paragraphs.Single().Text);
    }

    [Fact]
    public void Text_typed_after_a_link_is_not_part_of_it()
    {
        using var dir = new TempDir();
        var path = DocxDocumentTests.WriteDocx(dir,
            """<w:p><w:hyperlink r:id="rId9"><w:r><w:t>site</w:t></w:r></w:hyperlink></w:p>""",
            rels: """<Relationship Id="rId9" Type="http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink" Target="https://a.example/" TargetMode="External"/>""");
        var doc = DocxEditDocument.Open(path, default);
        doc.Layout();

        doc.Insert(new DocxPosition(0, 4), " more");
        doc.Insert(new DocxPosition(0, 2), "-");
        doc.Save(path);

        var links = Reload(path).Single().Inlines.OfType<DocxText>().Select(t => (t.Text, t.Link)).ToList();
        Assert.Equal([("si-te", "https://a.example/"), (" more", null)], links);
    }
}
