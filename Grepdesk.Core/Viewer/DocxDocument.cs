using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Grepdesk.Core.Viewer;

public enum DocxAlignment { Left, Center, Right, Justify }

public enum DocxBaseline { Normal, Superscript, Subscript }

/// <summary>A run's look, resolved through the document's styles. Sizes are in points.</summary>
public sealed record DocxRunStyle(
    bool Bold, bool Italic, bool Underline, bool Strike, double Size,
    string? Color, string? Highlight, string? Font, DocxBaseline Baseline);

/// <summary>Paragraph layout in points. A negative <see cref="FirstLine"/> is a hanging indent.</summary>
public sealed record DocxParagraphFormat(
    DocxAlignment Alignment, double SpaceBefore, double SpaceAfter,
    double IndentLeft, double IndentRight, double FirstLine, string? Shading)
{
    internal string? StyleId { get; init; }
    internal bool Contextual { get; init; }
}

public abstract record DocxBlock;

/// <param name="Marker">The list bullet or number ("•", "2.", "a)"), or null outside lists.</param>
/// <param name="MarkStyle">The paragraph's own text style: for the marker, and the height of an empty line.</param>
public sealed record DocxParagraph(IReadOnlyList<DocxInline> Inlines, DocxParagraphFormat Format, string? Marker, DocxRunStyle MarkStyle) : DocxBlock
{
    /// <summary>In an editing layout: the &lt;w:p&gt; this was read from, when it can be edited (not in a text box).</summary>
    public XElement? Source { get; init; }
}

/// <param name="ColumnWidths">Grid column widths in points; 0 where the file gives none.</param>
public sealed record DocxTable(IReadOnlyList<double> ColumnWidths, int RowCount, IReadOnlyList<DocxTableCell> Cells, bool Borders) : DocxBlock;

public sealed record DocxTableCell(int Row, int Column, int RowSpan, int ColumnSpan, IReadOnlyList<DocxBlock> Blocks, string? Shading);

/// <summary>Where a new page starts: a page break, a section break, or where Word last broke the page.</summary>
/// <param name="Section">The section the new page belongs to, an index into <see cref="DocxDocument.Sections"/>.</param>
public sealed record DocxPageBreak(int Section) : DocxBlock;

public abstract record DocxInline;

public sealed record DocxText(string Text, DocxRunStyle Style, string? Link) : DocxInline;

/// <summary>An embedded picture Avalonia can decode (PNG, JPEG, GIF, BMP, WebP). Size in points.</summary>
public sealed record DocxImage(byte[] Data, double Width, double Height) : DocxInline;

/// <summary>A PAGE field (or NUMPAGES, when <paramref name="Total"/>): filled in per page when shown.</summary>
public sealed record DocxPageNumber(DocxRunStyle Style, bool Total) : DocxInline;

/// <summary>
/// Page setup of one section, in points, with the headers and footers its
/// pages show. "First" ones are for the section's first page when it has a
/// different first page (<see cref="TitlePage"/>), usually empty on a cover.
/// </summary>
public sealed record DocxSection
{
    public double PageWidth { get; init; } = 612;
    public double PageHeight { get; init; } = 792;
    public double MarginLeft { get; init; } = 72;
    public double MarginRight { get; init; } = 72;
    public double MarginTop { get; init; } = 72;
    public double MarginBottom { get; init; } = 72;
    public double HeaderDistance { get; init; } = 36;
    public double FooterDistance { get; init; } = 36;
    public IReadOnlyList<DocxBlock> Header { get; init; } = [];
    public IReadOnlyList<DocxBlock> Footer { get; init; } = [];
    public IReadOnlyList<DocxBlock> FirstHeader { get; init; } = [];
    public IReadOnlyList<DocxBlock> FirstFooter { get; init; } = [];
    public bool TitlePage { get; init; }
    /// <summary>How page numbers are written: "decimal", "lowerRoman", "upperLetter"...</summary>
    public string NumberFormat { get; init; } = "decimal";
    /// <summary>The section's first page number when it restarts numbering, else null to continue.</summary>
    public int? NumberStart { get; init; }
}

/// <summary>
/// A Word document read for display: paragraphs with their styles, lists,
/// tables, pictures, links, headers and footers per section, and footnotes.
/// Text boxes follow the paragraph they're anchored to. Pages break where
/// the file says (explicit breaks, section breaks, and the breaks Word wrote
/// the last time it laid the document out); there is no layout engine here.
/// </summary>
public sealed class DocxDocument
{
    public required IReadOnlyList<DocxBlock> Body { get; init; }
    /// <summary>At least one; the body starts in the first.</summary>
    public required IReadOnlyList<DocxSection> Sections { get; init; }
    /// <summary>Footnotes then endnotes, in the order they're referenced; each starts with its number.</summary>
    public IReadOnlyList<DocxBlock> Notes { get; init; } = [];

    /// <exception cref="InvalidDataException">Not a zip, or no word/document.xml in it.</exception>
    public static DocxDocument Load(string path, CancellationToken ct)
    {
        using var zip = ZipFile.OpenRead(path);
        return new DocxReader(new ZipParts(zip), ct).Read();
    }

    /// <summary>A page or list number in one of Word's formats: 4 as "iv", "d", "04"...</summary>
    public static string FormatNumber(int n, string format) => DocxReader.FormatNumber(n, format);
}

internal sealed class DocxReader
{
    private const long MaxImageBytes = 30 * 1024 * 1024;
    private const long MaxTotalImageBytes = 150 * 1024 * 1024;
    private static readonly HashSet<string> ImageTypes = new([".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    private sealed record Rel(string Target, bool External);
    private sealed record Part(string Name, Dictionary<string, Rel> Rels);

    private sealed record RunProps(
        bool? Bold = null, bool? Italic = null, bool? Underline = null, bool? Strike = null, double? Size = null,
        string? Color = null, string? Highlight = null, string? Font = null, DocxBaseline? Baseline = null,
        bool? Caps = null, bool? Hidden = null)
    {
        public static readonly RunProps Empty = new();

        public RunProps Over(RunProps? top) => top is null ? this : new(
            top.Bold ?? Bold, top.Italic ?? Italic, top.Underline ?? Underline, top.Strike ?? Strike, top.Size ?? Size,
            top.Color ?? Color, top.Highlight ?? Highlight, top.Font ?? Font, top.Baseline ?? Baseline,
            top.Caps ?? Caps, top.Hidden ?? Hidden);
    }

    private sealed record ParaProps(
        DocxAlignment? Align = null, double? Before = null, double? After = null, double? Left = null, double? Right = null,
        double? FirstLine = null, int? NumId = null, int? NumLevel = null, string? Shading = null, bool? Contextual = null,
        bool? BreakBefore = null)
    {
        public static readonly ParaProps Empty = new();

        public ParaProps Over(ParaProps? top) => top is null ? this : new(
            top.Align ?? Align, top.Before ?? Before, top.After ?? After, top.Left ?? Left, top.Right ?? Right,
            top.FirstLine ?? FirstLine, top.NumId ?? NumId, top.NumLevel ?? NumLevel, top.Shading ?? Shading,
            top.Contextual ?? Contextual, top.BreakBefore ?? BreakBefore);
    }

    private sealed record Style(string? BasedOn, ParaProps P, RunProps R, bool? Borders);

    private sealed record Level(string Format, string Text, int Start, ParaProps P, RunProps R);

    private sealed class Field
    {
        public readonly StringBuilder Instruction = new();
        public bool InResult;
        public string? Link;
        /// <summary>PAGE or NUMPAGES: its result is the page number, filled in per page.</summary>
        public bool PageNumber;
    }

    private sealed class Cell(int row, int column, int columnSpan, List<DocxBlock> blocks, string? shading)
    {
        public readonly int Row = row, Column = column, ColumnSpan = columnSpan;
        public int RowSpan = 1;
        public DocxTableCell ToRecord() => new(Row, Column, RowSpan, ColumnSpan, blocks, shading);
    }

    private readonly IDocxParts _parts;
    private readonly CancellationToken _ct;
    /// <summary>
    /// Laid out for editing: one block per &lt;w:p&gt; with its <see cref="DocxParagraph.Source"/>, and
    /// pages that break only between paragraphs (Word's last-rendered breaks are left out, as edits make them stale).
    /// </summary>
    private readonly bool _editLayout;
    private int _textBoxDepth;
    private bool _prepared;
    private readonly Dictionary<string, Style> _styles = new();
    private readonly Dictionary<string, Style> _resolved = new();
    private readonly Dictionary<int, Dictionary<int, Level>> _numbering = new();
    private readonly Dictionary<int, int?[]> _counters = new();
    private readonly Dictionary<string, byte[]?> _images = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Field> _fields = [];
    private readonly List<(bool Endnote, string Id, int Number)> _notes = [];
    private RunProps _defaultRun = RunProps.Empty;
    private ParaProps _defaultPara = ParaProps.Empty;
    private string? _defaultParagraphStyle;
    private string? _majorFont, _minorFont;
    private long _imageBytes;
    private int _currentNote;
    /// <summary>Each section's sectPr, in order; the body's last one closes the final section.</summary>
    private readonly List<XElement?> _sectionEnds = [];

    public DocxReader(IDocxParts parts, CancellationToken ct, bool editLayout = false)
    {
        _parts = parts;
        _ct = ct;
        _editLayout = editLayout;
    }

    /// <summary>Reads the theme, styles and numbering; <see cref="Read"/> does it too.</summary>
    public void Prepare()
    {
        if (_prepared) return;
        _prepared = true;
        var part = new Part("word/document.xml", ReadRels("word/document.xml"));
        ReadTheme(part);
        ReadStyles(part);
        ReadNumbering(part);
    }

    public DocxDocument Read()
    {
        var main = OpenPart("word/document.xml") ?? throw new InvalidDataException("No word/document.xml");
        var body = Child(main.Root, "body") ?? throw new InvalidDataException("No document body");
        var part = new Part("word/document.xml", ReadRels("word/document.xml"));
        Prepare();

        var blocks = ReadBlocks(body, part, null, topLevel: true);
        // Two breaks in a row make one page (the later knows the section); one at either end, none.
        for (var i = blocks.Count - 1; i >= 0; i--)
        {
            if (blocks[i] is not DocxPageBreak) continue;
            if (i == blocks.Count - 1) blocks.RemoveAt(i);
            else if (i > 0 && blocks[i - 1] is DocxPageBreak) blocks.RemoveAt(i - 1);
        }
        if (blocks is [DocxPageBreak, ..]) blocks.RemoveAt(0);

        _sectionEnds.Add(Children(body, "sectPr").LastOrDefault());
        return new DocxDocument
        {
            Body = blocks,
            Sections = ReadSections(part),
            Notes = ReadNotes(),
        };
    }

    private List<DocxSection> ReadSections(Part part)
    {
        var sections = new List<DocxSection>();
        var previous = new DocxSection();
        var parts = new Dictionary<string, IReadOnlyList<DocxBlock>>();

        // A section without its own header or footer of a kind keeps the previous section's.
        IReadOnlyList<DocxBlock> Pick(XElement? sect, string reference, string type, IReadOnlyList<DocxBlock> inherited)
        {
            var found = Children(sect, reference).FirstOrDefault(r => (Attr(r, "type") ?? "default") == type);
            if (Attr(found, "id") is not { } id) return inherited;
            if (!part.Rels.TryGetValue(id, out var rel) || rel.External) return [];
            if (!parts.TryGetValue(rel.Target, out var blocks))
                parts[rel.Target] = blocks = ReadHeaderFooter(rel.Target);
            return blocks;
        }

        foreach (var sect in _sectionEnds)
        {
            var size = Child(sect, "pgSz");
            var margin = Child(sect, "pgMar");
            double Get(XElement? e, string name, double fallback) => Twips(Attr(e, name)) is { } v and > 0 ? v : fallback;
            var numbers = Child(sect, "pgNumType");

            var section = new DocxSection
            {
                PageWidth = Get(size, "w", previous.PageWidth),
                PageHeight = Get(size, "h", previous.PageHeight),
                MarginLeft = Get(margin, "left", previous.MarginLeft),
                MarginRight = Get(margin, "right", previous.MarginRight),
                MarginTop = Twips(Attr(margin, "top")) is { } top ? Math.Abs(top) : previous.MarginTop,
                MarginBottom = Twips(Attr(margin, "bottom")) is { } bottom ? Math.Abs(bottom) : previous.MarginBottom,
                HeaderDistance = Get(margin, "header", previous.HeaderDistance),
                FooterDistance = Get(margin, "footer", previous.FooterDistance),
                Header = Pick(sect, "headerReference", "default", previous.Header),
                Footer = Pick(sect, "footerReference", "default", previous.Footer),
                FirstHeader = Pick(sect, "headerReference", "first", previous.FirstHeader),
                FirstFooter = Pick(sect, "footerReference", "first", previous.FirstFooter),
                TitlePage = Toggle(Child(sect, "titlePg")) == true,
                NumberFormat = Attr(numbers, "fmt") ?? "decimal",
                NumberStart = int.TryParse(Attr(numbers, "start"), out var start) ? start : null,
            };
            sections.Add(section);
            previous = section;
        }
        return sections;
    }

    // =====================================================================
    // Blocks
    // =====================================================================

    /// <param name="table">The enclosing table's style, whose paragraph and text settings apply to its cells.</param>
    /// <param name="topLevel">The main body: only there do page breaks start a new page.</param>
    private List<DocxBlock> ReadBlocks(XElement? container, Part part, Style? table, bool topLevel)
    {
        var blocks = new List<DocxBlock>();
        if (container is null) return blocks;

        foreach (var child in container.Elements())
        {
            _ct.ThrowIfCancellationRequested();
            switch (child.Name.LocalName)
            {
                case "p":
                    ReadParagraph(child, part, table, topLevel, blocks);
                    break;
                case "tbl":
                    blocks.Add(ReadTable(child, part));
                    break;
                case "sdt":
                    blocks.AddRange(ReadBlocks(Child(child, "sdtContent"), part, table, topLevel));
                    break;
                case "customXml" or "ins" or "moveTo":
                    blocks.AddRange(ReadBlocks(child, part, table, topLevel));
                    break;
            }
        }

        // "Don't add space between paragraphs of the same style" (lists, mostly).
        for (var i = 1; i < blocks.Count; i++)
        {
            if (blocks[i - 1] is not DocxParagraph prev || blocks[i] is not DocxParagraph cur) continue;
            if (!prev.Format.Contextual || !cur.Format.Contextual || prev.Format.StyleId != cur.Format.StyleId) continue;
            blocks[i - 1] = prev with { Format = prev.Format with { SpaceAfter = 0 } };
            blocks[i] = cur with { Format = cur.Format with { SpaceBefore = 0 } };
        }
        return blocks;
    }

    private void ReadParagraph(XElement p, Part part, Style? table, bool topLevel, List<DocxBlock> output)
    {
        var pPr = Child(p, "pPr");
        var styleId = Attr(Child(pPr, "pStyle")) ?? _defaultParagraphStyle;
        var style = ResolveStyle(styleId);
        var direct = ParseParagraphProps(pPr);

        // Word lets a table style override the Normal style, but not a style picked on purpose.
        var props = _defaultPara.Over(table?.P).Over(style?.P);
        if (table is not null && styleId == _defaultParagraphStyle) props = props.Over(table.P);

        var numId = direct.NumId ?? props.NumId;
        var numLevel = Math.Clamp(direct.NumLevel ?? props.NumLevel ?? 0, 0, 8);
        Level? level = numId is { } id && _numbering.TryGetValue(id, out var levels) && levels.TryGetValue(numLevel, out var l) ? l : null;
        if (level is not null) props = props.Over(level.P);
        props = props.Over(direct);

        var runBase = _defaultRun.Over(table?.R).Over(style?.R);
        var markStyle = Finish(runBase.Over(level?.R).Over(ParseRunProps(Child(pPr, "rPr"))));
        var marker = level is not null ? NextMarker(numId!.Value, numLevel, level) : null;

        var format = new DocxParagraphFormat(
            props.Align ?? DocxAlignment.Left, props.Before ?? 0, props.After ?? 0,
            props.Left ?? 0, props.Right ?? 0, props.FirstLine ?? 0, props.Shading)
        {
            StyleId = styleId,
            Contextual = props.Contextual == true,
        };

        if (topLevel && props.BreakBefore == true) output.Add(new DocxPageBreak(_sectionEnds.Count));

        var inlines = new List<DocxInline>();
        var after = new List<DocxBlock>(); // text boxes
        var parts = 0;

        void Emit(bool last)
        {
            var f = format;
            if (parts > 0) f = f with { SpaceBefore = 0, FirstLine = 0 }; // continues a paragraph from the last page
            if (!last) f = f with { SpaceAfter = 0 };
            output.Add(new DocxParagraph(inlines.ToList(), f, parts == 0 ? marker : null, markStyle)
            {
                Source = _editLayout && _textBoxDepth == 0 ? p : null,
            });
            inlines.Clear();
            parts++;
        }

        var breakAfter = false;
        void Break(bool explicitBreak)
        {
            if (!topLevel)
            {
                if (explicitBreak) inlines.Add(new DocxText("\n", markStyle, null));
                return;
            }
            if (_editLayout)
            {
                // A paragraph stays whole while it's edited: a forced break ends its page after it.
                breakAfter |= explicitBreak;
                return;
            }
            // Where Word last broke the page, right after a forced break, is the same page.
            if (!explicitBreak && inlines.Count == 0 && JustBroke(output)) return;
            if (inlines.Count > 0) Emit(last: false);
            output.Add(new DocxPageBreak(_sectionEnds.Count));
        }

        ReadInlines(p, part, runBase, null, inlines, after, Break);
        if (inlines.Count > 0 || parts == 0) Emit(last: true);
        output.AddRange(after);
        if (breakAfter) output.Add(new DocxPageBreak(_sectionEnds.Count));

        // A section break that starts its next section on a new page.
        if (topLevel && Child(pPr, "sectPr") is { } sect)
        {
            _sectionEnds.Add(sect);
            if (Attr(Child(sect, "type")) is not "continuous") output.Add(new DocxPageBreak(_sectionEnds.Count));
        }
    }

    /// <summary>Nothing but empty paragraphs since the last page break.</summary>
    private static bool JustBroke(List<DocxBlock> output)
    {
        for (var i = output.Count - 1; i >= 0; i--)
        {
            if (output[i] is DocxPageBreak) return true;
            if (output[i] is not DocxParagraph { Inlines.Count: 0 }) return false;
        }
        return false;
    }

    private DocxTable ReadTable(XElement tbl, Part part)
    {
        var tblPr = Child(tbl, "tblPr");
        var style = ResolveStyle(Attr(Child(tblPr, "tblStyle")));
        var borders = HasBorders(Child(tblPr, "tblBorders")) ?? style?.Borders ?? false;
        var widths = Children(Child(tbl, "tblGrid"), "gridCol").Select(g => Twips(Attr(g, "w")) ?? 0).ToList();

        var cells = new List<Cell>();
        var lastInColumn = new Dictionary<int, Cell>();
        var row = 0;
        foreach (var tr in Flatten(tbl, "tr"))
        {
            var trPr = Child(tr, "trPr");
            var column = int.TryParse(Attr(Child(trPr, "gridBefore")), out var before) ? Math.Max(0, before) : 0;
            foreach (var tc in Flatten(tr, "tc"))
            {
                var tcPr = Child(tc, "tcPr");
                var span = int.TryParse(Attr(Child(tcPr, "gridSpan")), out var s) && s > 0 ? s : 1;

                // Continuing a vertical merge: the cell above grows instead.
                if (Child(tcPr, "vMerge") is { } merge && Attr(merge) is null or "continue" &&
                    lastInColumn.TryGetValue(column, out var above) && above.Row + above.RowSpan == row)
                {
                    above.RowSpan++;
                    column += span;
                    continue;
                }

                var cell = new Cell(row, column, span, ReadBlocks(tc, part, style, topLevel: false), Color(Attr(Child(tcPr, "shd"), "fill")));
                cells.Add(cell);
                lastInColumn[column] = cell;
                column += span;
            }
            row++;
        }

        var columns = cells.Count == 0 ? widths.Count : Math.Max(widths.Count, cells.Max(c => c.Column + c.ColumnSpan));
        while (widths.Count < columns) widths.Add(0);
        return new DocxTable(widths, row, cells.Select(c => c.ToRecord()).ToList(), borders);
    }

    /// <summary>Elements of one name, looking through content controls and custom XML that may wrap them.</summary>
    private static IEnumerable<XElement> Flatten(XElement parent, string name)
    {
        foreach (var child in parent.Elements())
        {
            if (child.Name.LocalName == name) yield return child;
            else if (child.Name.LocalName is "sdt")
                foreach (var inner in Flatten(Child(child, "sdtContent") ?? child, name)) yield return inner;
            else if (child.Name.LocalName is "customXml")
                foreach (var inner in Flatten(child, name)) yield return inner;
        }
    }

    // =====================================================================
    // Runs
    // =====================================================================

    private void ReadInlines(XElement container, Part part, RunProps runBase, string? link,
        List<DocxInline> inlines, List<DocxBlock> after, Action<bool> pageBreak)
    {
        foreach (var child in container.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "r":
                    ReadRun(child, part, runBase, link, inlines, after, pageBreak);
                    break;
                case "hyperlink":
                    var target = Attr(child, "id") is { } id && part.Rels.TryGetValue(id, out var rel) && rel.External ? rel.Target : null;
                    ReadInlines(child, part, runBase, target ?? link, inlines, after, pageBreak);
                    break;
                case "fldSimple":
                    var instruction = Attr(child, "instr");
                    if (PageFieldOf(instruction) is { } total)
                    {
                        var first = Child(child, "r");
                        var fieldStyle = runBase.Over(ResolveStyle(Attr(Child(Child(first, "rPr"), "rStyle")))?.R).Over(ParseRunProps(Child(first, "rPr")));
                        inlines.Add(new DocxPageNumber(Finish(fieldStyle), total));
                    }
                    else ReadInlines(child, part, runBase, HyperlinkOf(instruction) ?? link, inlines, after, pageBreak);
                    break;
                case "ins" or "moveTo" or "smartTag" or "customXml" or "bdo" or "dir":
                    ReadInlines(child, part, runBase, link, inlines, after, pageBreak);
                    break;
                case "sdt":
                    if (Child(child, "sdtContent") is { } content) ReadInlines(content, part, runBase, link, inlines, after, pageBreak);
                    break;
                case "oMath" or "oMathPara":
                    // Equations: just their text, in italics as Word sets math.
                    var math = string.Concat(child.Descendants().Where(e => e.Name.LocalName == "t").Select(e => e.Value));
                    if (math.Length > 0) AddText(inlines, math, Finish(runBase.Over(new RunProps(Italic: true))), link);
                    break;
            }
        }
    }

    private void ReadRun(XElement r, Part part, RunProps runBase, string? link,
        List<DocxInline> inlines, List<DocxBlock> after, Action<bool> pageBreak)
    {
        var rPr = Child(r, "rPr");
        var props = runBase.Over(ResolveStyle(Attr(Child(rPr, "rStyle")))?.R).Over(ParseRunProps(rPr));
        var style = Finish(props);
        var hidden = props.Hidden == true;
        var caps = props.Caps == true;

        foreach (var node in r.Elements())
            ReadRunContent(node, part, style, hidden, caps, link, inlines, after, pageBreak);
    }

    private void ReadRunContent(XElement node, Part part, DocxRunStyle style, bool hidden, bool caps, string? link,
        List<DocxInline> inlines, List<DocxBlock> after, Action<bool> pageBreak)
    {
        switch (node.Name.LocalName)
        {
            case "fldChar":
                switch (Attr(node, "fldCharType"))
                {
                    case "begin": _fields.Add(new Field()); break;
                    case "separate" when _fields.Count > 0:
                        var field = _fields[^1];
                        field.InResult = true;
                        field.Link = HyperlinkOf(field.Instruction.ToString());
                        if (PageFieldOf(field.Instruction.ToString()) is { } total && _fields.All(f => f.InResult))
                        {
                            field.PageNumber = true;
                            inlines.Add(new DocxPageNumber(style, total));
                        }
                        break;
                    case "end" when _fields.Count > 0: _fields.RemoveAt(_fields.Count - 1); break;
                }
                return;
            case "instrText":
                if (_fields.Count > 0 && !_fields[^1].InResult) _fields[^1].Instruction.Append(node.Value);
                return;
        }

        // Between a field's begin and separate is its code, not its text.
        if (_fields.Any(f => !f.InResult || f.PageNumber)) return;
        link ??= _fields.LastOrDefault(f => f.Link is not null)?.Link;

        switch (node.Name.LocalName)
        {
            case "t" when !hidden:
                AddText(inlines, caps ? node.Value.ToUpper(CultureInfo.CurrentCulture) : node.Value, style, link);
                break;
            case "tab" or "ptab" when !hidden:
                AddText(inlines, "\t", style, link);
                break;
            case "br" when !hidden:
                if (Attr(node, "type") == "page") pageBreak(true);
                else AddText(inlines, "\n", style, link);
                break;
            case "cr" when !hidden:
                AddText(inlines, "\n", style, link);
                break;
            case "noBreakHyphen" when !hidden:
                AddText(inlines, "\u2011", style, link);
                break;
            case "lastRenderedPageBreak":
                pageBreak(false);
                break;
            case "footnoteReference" or "endnoteReference":
                if (Attr(node, "id") is { } id)
                {
                    var number = _notes.Count + 1;
                    _notes.Add((node.Name.LocalName == "endnoteReference", id, number));
                    AddText(inlines, number.ToString(CultureInfo.InvariantCulture), style with { Baseline = DocxBaseline.Superscript }, link);
                }
                break;
            case "footnoteRef" or "endnoteRef":
                AddText(inlines, _currentNote.ToString(CultureInfo.InvariantCulture), style with { Baseline = DocxBaseline.Superscript }, link);
                break;
            case "drawing" when !hidden:
                ReadDrawing(node, part, inlines, after);
                break;
            case "pict" or "object" when !hidden:
                ReadVml(node, part, inlines, after);
                break;
            case "AlternateContent":
                // The newer form Word wrote; the fallback repeats it for old readers.
                var choice = Child(node, "Choice") ?? Child(node, "Fallback");
                foreach (var inner in choice?.Elements() ?? [])
                    ReadRunContent(inner, part, style, hidden, caps, link, inlines, after, pageBreak);
                break;
        }
    }

    private void ReadDrawing(XElement drawing, Part part, List<DocxInline> inlines, List<DocxBlock> after)
    {
        var extent = drawing.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent");
        var width = Emu(Attr(extent, "cx"));
        var height = Emu(Attr(extent, "cy"));
        foreach (var blip in drawing.Descendants().Where(e => e.Name.LocalName == "blip").Take(1))
            if (Attr(blip, "embed") is { } id) AddImage(inlines, part, id, width, height);
        ReadTextBoxes(drawing, part, after);
    }

    private void ReadVml(XElement pict, Part part, List<DocxInline> inlines, List<DocxBlock> after)
    {
        foreach (var image in pict.Descendants().Where(e => e.Name.LocalName == "imagedata").Take(1))
        {
            if (Attr(image, "id") is not { } id) continue;
            var shapeStyle = Attr(image.Parent, "style") ?? "";
            AddImage(inlines, part, id, CssPoints(shapeStyle, "width"), CssPoints(shapeStyle, "height"));
        }
        ReadTextBoxes(pict, part, after);
    }

    private void ReadTextBoxes(XElement shape, Part part, List<DocxBlock> after)
    {
        // Nested text boxes are listed too: take only the outermost ones.
        foreach (var box in shape.Descendants().Where(e => e.Name.LocalName == "txbxContent"))
            if (!box.Ancestors().TakeWhile(a => a != shape).Any(a => a.Name.LocalName == "txbxContent"))
            {
                _textBoxDepth++;
                try { after.AddRange(ReadBlocks(box, part, null, topLevel: false)); }
                finally { _textBoxDepth--; }
            }
    }

    private void AddImage(List<DocxInline> inlines, Part part, string relId, double width, double height)
    {
        if (!part.Rels.TryGetValue(relId, out var rel) || rel.External) return;
        if (!_images.TryGetValue(rel.Target, out var data))
        {
            data = null;
            var length = _parts.Length(rel.Target);
            if (ImageTypes.Contains(Path.GetExtension(rel.Target)) && length >= 0 &&
                length <= MaxImageBytes && _imageBytes + length <= MaxTotalImageBytes && _parts.Bytes(rel.Target) is { } bytes)
            {
                data = bytes;
                _imageBytes += data.Length;
            }
            _images[rel.Target] = data;
        }
        if (data is not null) inlines.Add(new DocxImage(data, width, height));
    }

    private static void AddText(List<DocxInline> inlines, string text, DocxRunStyle style, string? link)
    {
        if (text.Length == 0) return;
        // Word splits text into runs for spellcheck and revisions; same-looking runs join up again.
        if (inlines.Count > 0 && inlines[^1] is DocxText last && last.Style == style && last.Link == link)
            inlines[^1] = last with { Text = last.Text + text };
        else
            inlines.Add(new DocxText(text, style, link));
    }

    private static readonly Regex HyperlinkField = new(@"^\s*HYPERLINK\s+(?<anchor>\\l\s+)?""?(?<url>[^""\s]+)", RegexOptions.IgnoreCase);

    private static readonly Regex PageField = new(@"^\s*(?<name>PAGE|NUMPAGES)\b",RegexOptions.IgnoreCase);

    /// <summary>False for a PAGE field, true for NUMPAGES, null for any other.</summary>
    private static bool? PageFieldOf(string? instruction) =>
        instruction is not null && PageField.Match(instruction) is { Success: true } match
            ? match.Groups["name"].Value.Equals("NUMPAGES", StringComparison.OrdinalIgnoreCase)
            : null;

    private static string? HyperlinkOf(string? instruction)
    {
        if (instruction is null || HyperlinkField.Match(instruction) is not { Success: true } match) return null;
        return match.Groups["anchor"].Success ? null : match.Groups["url"].Value;
    }

    // =====================================================================
    // Headers, footers and notes
    // =====================================================================

    private IReadOnlyList<DocxBlock> ReadHeaderFooter(string name)
    {
        _fields.Clear();
        if (OpenPart(name)?.Root is not { } root) return [];
        return ReadBlocks(root, new Part(name, ReadRels(name)), null, topLevel: false);
    }

    private IReadOnlyList<DocxBlock> ReadNotes()
    {
        if (_notes.Count == 0) return [];
        _fields.Clear();
        var blocks = new List<DocxBlock>();
        var sources = new Dictionary<bool, (Part Part, Dictionary<string, XElement> Notes)?>();

        foreach (var (endnote, id, number) in _notes.ToList())
        {
            if (!sources.TryGetValue(endnote, out var source))
            {
                var name = endnote ? "word/endnotes.xml" : "word/footnotes.xml";
                source = OpenPart(name)?.Root is { } root
                    ? (new Part(name, ReadRels(name)), root.Elements()
                        .Where(e => e.Name.LocalName is "footnote" or "endnote" && Attr(e, "id") is not null)
                        .GroupBy(e => Attr(e, "id")!).ToDictionary(g => g.Key, g => g.First()))
                    : null;
                sources[endnote] = source;
            }
            if (source is not { } s || !s.Notes.TryGetValue(id, out var note)) continue;
            _currentNote = number;
            blocks.AddRange(ReadBlocks(note, s.Part, null, topLevel: false));
        }
        return blocks;
    }

    // =====================================================================
    // Styles, numbering and theme
    // =====================================================================

    private void ReadStyles(Part document)
    {
        var name = document.Rels.Values.FirstOrDefault(r => !r.External && r.Target.EndsWith("/styles.xml", StringComparison.OrdinalIgnoreCase))?.Target ?? "word/styles.xml";
        if (OpenPart(name)?.Root is not { } root) return;

        var defaults = Child(root, "docDefaults");
        _defaultRun = ParseRunProps(Child(Child(defaults, "rPrDefault"), "rPr"));
        _defaultPara = ParseParagraphProps(Child(Child(defaults, "pPrDefault"), "pPr"));

        foreach (var style in Children(root, "style"))
        {
            if (Attr(style, "styleId") is not { } id) continue;
            var type = Attr(style, "type");
            if (type == "paragraph" && Attr(style, "default") is "1" or "true") _defaultParagraphStyle = id;
            _styles[id] = new Style(
                Attr(Child(style, "basedOn")),
                ParseParagraphProps(Child(style, "pPr")),
                ParseRunProps(Child(style, "rPr")),
                HasBorders(Child(Child(style, "tblPr"), "tblBorders")));
            if (type == "paragraph")
                _paragraphStyles.Add(new StyleInfo(id, Attr(Child(style, "name")) ?? id, Attr(Child(style, "next")),
                    Toggle(Child(style, "qFormat")) == true,
                    Toggle(Child(style, "semiHidden")) == true || Toggle(Child(style, "hidden")) == true));
        }
    }

    // =====================================================================
    // For the editor: styles of single paragraphs and runs
    // =====================================================================

    internal sealed record StyleInfo(string Id, string Name, string? Next, bool Quick, bool Hidden);

    /// <summary>The document's paragraph styles, in styles.xml order.</summary>
    private readonly List<StyleInfo> _paragraphStyles = [];

    internal List<StyleInfo> ParagraphStyles { get { Prepare(); return _paragraphStyles; } }

    internal string? DefaultParagraphStyle { get { Prepare(); return _defaultParagraphStyle; } }

    internal string? MajorFont { get { Prepare(); return _majorFont; } }

    internal string? MinorFont { get { Prepare(); return _minorFont; } }

    private (Style? Style, Style? Table, string? StyleId) StylesOf(XElement p)
    {
        Prepare();
        var styleId = Attr(Child(Child(p, "pPr"), "pStyle")) ?? _defaultParagraphStyle;
        var tbl = p.Ancestors().FirstOrDefault(a => a.Name.LocalName == "tbl");
        var table = tbl is null ? null : ResolveStyle(Attr(Child(Child(tbl, "tblPr"), "tblStyle")));
        return (ResolveStyle(styleId), table, styleId);
    }

    /// <summary>How a run with these direct properties looks in this paragraph.</summary>
    internal DocxRunStyle ResolveRun(XElement p, XElement? rPr)
    {
        var (style, table, _) = StylesOf(p);
        var runBase = _defaultRun.Over(table?.R).Over(style?.R);
        return Finish(runBase.Over(ResolveStyle(Attr(Child(rPr, "rStyle")))?.R).Over(ParseRunProps(rPr)));
    }

    /// <summary>The list format of the paragraph's level ("bullet", "decimal"...), or null outside lists.</summary>
    internal string? ListFormat(XElement p)
    {
        var (style, table, _) = StylesOf(p);
        var props = _defaultPara.Over(table?.P).Over(style?.P).Over(ParseParagraphProps(Child(p, "pPr")));
        if (props.NumId is not { } numId || numId <= 0 || !_numbering.TryGetValue(numId, out var levels)) return null;
        return levels.TryGetValue(Math.Clamp(props.NumLevel ?? 0, 0, 8), out var level) ? level.Format : null;
    }

    /// <summary>Numbering that comes from the paragraph's style rather than its own properties.</summary>
    internal bool StyleNumbers(XElement p) => StylesOf(p).Style?.P.NumId is > 0;

    /// <summary>The paragraph's format alone, as the layout would give it, with no list marker counted.</summary>
    internal DocxParagraph ReadSingle(XElement p)
    {
        var (_, table, _) = StylesOf(p);
        var output = new List<DocxBlock>();
        var part = new Part("word/document.xml", ReadRels("word/document.xml"));
        ReadParagraph(p, part, table, topLevel: false, output);
        return output.OfType<DocxParagraph>().First() with { Source = p };
    }

    /// <summary>A numbering level's format, for choosing lists to reuse.</summary>
    internal string? LevelFormat(int numId, int level) =>
        _numbering.TryGetValue(numId, out var levels) && levels.TryGetValue(level, out var l) ? l.Format : null;

    /// <summary>A style with everything it inherits through basedOn folded in.</summary>
    private Style? ResolveStyle(string? id, int depth = 0)
    {
        if (id is null || !_styles.TryGetValue(id, out var style)) return null;
        if (_resolved.TryGetValue(id, out var done)) return done;
        if (depth > 20) return style;

        var parent = ResolveStyle(style.BasedOn, depth + 1);
        var resolved = parent is null ? style : new Style(null, parent.P.Over(style.P), parent.R.Over(style.R), style.Borders ?? parent.Borders);
        _resolved[id] = resolved;
        return resolved;
    }

    private void ReadNumbering(Part document)
    {
        var name = document.Rels.Values.FirstOrDefault(r => !r.External && r.Target.EndsWith("/numbering.xml", StringComparison.OrdinalIgnoreCase))?.Target ?? "word/numbering.xml";
        if (OpenPart(name)?.Root is not { } root) return;

        var abstracts = new Dictionary<string, Dictionary<int, Level>>();
        foreach (var abs in Children(root, "abstractNum"))
            if (Attr(abs, "abstractNumId") is { } id)
                abstracts[id] = Children(abs, "lvl").Select(ParseLevel).Where(l => l.Index >= 0)
                    .GroupBy(l => l.Index).ToDictionary(g => g.Key, g => g.First().Level);

        foreach (var num in Children(root, "num"))
        {
            if (!int.TryParse(Attr(num, "numId"), out var numId) || Attr(Child(num, "abstractNumId")) is not { } absId) continue;
            var levels = abstracts.TryGetValue(absId, out var a) ? new Dictionary<int, Level>(a) : new Dictionary<int, Level>();
            foreach (var over in Children(num, "lvlOverride"))
            {
                if (!int.TryParse(Attr(over, "ilvl"), out var ilvl)) continue;
                if (Child(over, "lvl") is { } lvl) levels[ilvl] = ParseLevel(lvl).Level;
                if (int.TryParse(Attr(Child(over, "startOverride")), out var start) && levels.TryGetValue(ilvl, out var level))
                    levels[ilvl] = level with { Start = start };
            }
            _numbering[numId] = levels;
        }
    }

    private (int Index, Level Level) ParseLevel(XElement lvl) => (
        int.TryParse(Attr(lvl, "ilvl"), out var i) ? i : -1,
        new Level(
            Attr(Child(lvl, "numFmt")) ?? "decimal",
            Attr(Child(lvl, "lvlText")) ?? "",
            int.TryParse(Attr(Child(lvl, "start")), out var start) ? start : 1,
            ParseParagraphProps(Child(lvl, "pPr")),
            ParseRunProps(Child(lvl, "rPr"))));

    /// <summary>Counts the item and formats its marker: "%1.%2." becomes "3.1.".</summary>
    private string? NextMarker(int numId, int level, Level format)
    {
        if (!_counters.TryGetValue(numId, out var counts)) _counters[numId] = counts = new int?[9];
        counts[level] = counts[level] is { } c ? c + 1 : format.Start;
        for (var i = level + 1; i < counts.Length; i++) counts[i] = null;

        switch (format.Format)
        {
            case "none": return null;
            case "bullet": return Bullet(format.Text);
        }

        var levels = _numbering[numId];
        return Regex.Replace(format.Text, "%([1-9])", m =>
        {
            var l = m.Groups[1].Value[0] - '1';
            var known = levels.TryGetValue(l, out var lf);
            return FormatNumber(counts[l] ?? (known ? lf!.Start : 1), known ? lf!.Format : "decimal");
        });
    }

    /// <summary>Bullets are often characters of the Symbol or Wingdings fonts, which only those fonts draw.</summary>
    private static string Bullet(string text)
    {
        if (text.Length == 0) return "•";
        return text[0] switch
        {
            '' or '' => "▪",
            '' => "✓",
            '' => "➢",
            '' => "❖",
            '' => "□",
            'o' => "◦",
            >= '' and <= '' => "•",
            _ => text,
        };
    }

    internal static string FormatNumber(int n, string format)
    {
        switch (format)
        {
            case "lowerLetter" or "upperLetter" when n > 0:
                // a..z, then aa..zz, as Word counts.
                var letter = (char)('a' + (n - 1) % 26);
                var text = new string(letter, (n - 1) / 26 + 1);
                return format == "upperLetter" ? text.ToUpperInvariant() : text;
            case "lowerRoman" or "upperRoman" when n is > 0 and < 4000:
                var roman = Roman(n);
                return format == "lowerRoman" ? roman.ToLowerInvariant() : roman;
            case "decimalZero":
                return n.ToString("00", CultureInfo.InvariantCulture);
            default:
                return n.ToString(CultureInfo.InvariantCulture);
        }
    }

    private static string Roman(int n)
    {
        (int Value, string Digits)[] table = [(1000, "M"), (900, "CM"), (500, "D"), (400, "CD"), (100, "C"), (90, "XC"),
            (50, "L"), (40, "XL"), (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")];
        var sb = new StringBuilder();
        foreach (var (value, digits) in table)
            while (n >= value) { sb.Append(digits); n -= value; }
        return sb.ToString();
    }

    private void ReadTheme(Part document)
    {
        var name = document.Rels.Values.FirstOrDefault(r => !r.External && r.Target.Contains("/theme/", StringComparison.OrdinalIgnoreCase))?.Target ?? "word/theme/theme1.xml";
        if (OpenPart(name)?.Root is not { } root) return;
        string? Latin(string scheme) => Attr(root.Descendants().FirstOrDefault(e => e.Name.LocalName == scheme)?
            .Elements().FirstOrDefault(e => e.Name.LocalName == "latin"), "typeface") is { Length: > 0 } f ? f : null;
        _majorFont = Latin("majorFont");
        _minorFont = Latin("minorFont");
    }

    // =====================================================================
    // Properties
    // =====================================================================

    private RunProps ParseRunProps(XElement? rPr)
    {
        if (rPr is null) return RunProps.Empty;

        var fonts = Child(rPr, "rFonts");
        var font = Attr(fonts, "ascii") ?? Attr(fonts, "hAnsi");
        if (font is null && (Attr(fonts, "asciiTheme") ?? Attr(fonts, "hAnsiTheme")) is { } theme)
            font = theme.StartsWith("major", StringComparison.Ordinal) ? _majorFont : _minorFont;

        var underline = Child(rPr, "u");
        return new RunProps(
            Bold: Toggle(Child(rPr, "b")),
            Italic: Toggle(Child(rPr, "i")),
            Underline: underline is null ? null : Attr(underline) is not ("none" or "0" or "false"),
            Strike: Toggle(Child(rPr, "strike")) ?? Toggle(Child(rPr, "dstrike")),
            Size: Number(Attr(Child(rPr, "sz"))) is { } halfPoints and > 0 ? halfPoints / 2 : null,
            Color: Color(Attr(Child(rPr, "color"))),
            Highlight: HighlightColor(Attr(Child(rPr, "highlight"))) ?? Color(Attr(Child(rPr, "shd"), "fill")),
            Font: font,
            Baseline: Attr(Child(rPr, "vertAlign")) switch
            {
                "superscript" => DocxBaseline.Superscript,
                "subscript" => DocxBaseline.Subscript,
                "baseline" => DocxBaseline.Normal,
                _ => null,
            },
            Caps: Toggle(Child(rPr, "caps")),
            Hidden: Toggle(Child(rPr, "vanish")));
    }

    private static ParaProps ParseParagraphProps(XElement? pPr)
    {
        if (pPr is null) return ParaProps.Empty;

        var spacing = Child(pPr, "spacing");
        var ind = Child(pPr, "ind");
        var numPr = Child(pPr, "numPr");
        double? firstLine = Twips(Attr(ind, "hanging")) is { } hanging ? -hanging : Twips(Attr(ind, "firstLine"));

        return new ParaProps(
            Align: Attr(Child(pPr, "jc")) switch
            {
                "center" => DocxAlignment.Center,
                "right" or "end" => DocxAlignment.Right,
                "both" or "distribute" or "justify" or "lowKashida" or "mediumKashida" or "highKashida" => DocxAlignment.Justify,
                "left" or "start" => DocxAlignment.Left,
                _ => null,
            },
            Before: Toggle(spacing, "beforeAutospacing") == true ? 14 : Twips(Attr(spacing, "before")),
            After: Toggle(spacing, "afterAutospacing") == true ? 14 : Twips(Attr(spacing, "after")),
            Left: Twips(Attr(ind, "left") ?? Attr(ind, "start")),
            Right: Twips(Attr(ind, "right") ?? Attr(ind, "end")),
            FirstLine: firstLine,
            NumId: int.TryParse(Attr(Child(numPr, "numId")), out var numId) ? numId : null,
            NumLevel: int.TryParse(Attr(Child(numPr, "ilvl")), out var ilvl) ? ilvl : null,
            Shading: Color(Attr(Child(pPr, "shd"), "fill")),
            Contextual: Toggle(Child(pPr, "contextualSpacing")),
            BreakBefore: Toggle(Child(pPr, "pageBreakBefore")));
    }

    private static DocxRunStyle Finish(RunProps p) => new(
        p.Bold == true, p.Italic == true, p.Underline == true, p.Strike == true, p.Size ?? 10,
        string.IsNullOrEmpty(p.Color) ? null : p.Color,
        string.IsNullOrEmpty(p.Highlight) ? null : p.Highlight,
        p.Font, p.Baseline ?? DocxBaseline.Normal);

    private static bool? HasBorders(XElement? borders) =>
        borders is null ? null : borders.Elements().Any(b => Attr(b) is not (null or "nil" or "none"));

    /// <summary>"#RRGGBB"; "" for "auto" (back to the default color, over an inherited one); null when unset.</summary>
    private static string? Color(string? value) => value switch
    {
        null => null,
        "auto" => "",
        { Length: 6 } when value.All(Uri.IsHexDigit) => "#" + value,
        _ => null,
    };

    private static string? HighlightColor(string? name) => name switch
    {
        null => null,
        "none" => "",
        "yellow" => "#FFFF00", "green" => "#00FF00", "cyan" => "#00FFFF", "magenta" => "#FF00FF",
        "blue" => "#0000FF", "red" => "#FF0000", "darkBlue" => "#000080", "darkCyan" => "#008080",
        "darkGreen" => "#008000", "darkMagenta" => "#800080", "darkRed" => "#800000", "darkYellow" => "#808000",
        "darkGray" => "#808080", "lightGray" => "#C0C0C0", "black" => "#000000", "white" => "#FFFFFF",
        _ => null,
    };

    // =====================================================================
    // XML and package helpers
    // =====================================================================

    private static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    private static IEnumerable<XElement> Children(XElement? e, string name) =>
        e?.Elements().Where(x => x.Name.LocalName == name) ?? [];

    /// <summary>An attribute by local name: w:val, r:id and r:embed alike, in either OOXML namespace flavour.</summary>
    private static string? Attr(XElement? e, string name = "val") =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    /// <summary>&lt;w:b/&gt; is on; w:val="0", "false" or "off" turns it off.</summary>
    private static bool? Toggle(XElement? e, string? attribute = null)
    {
        if (e is null) return null;
        var value = Attr(e, attribute ?? "val");
        if (attribute is not null && value is null) return null;
        return value is not ("0" or "false" or "off");
    }

    private static double? Number(string? value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    private static double? Twips(string? value) => Number(value) / 20;

    private static double Emu(string? value) => Number(value) is { } emu and > 0 ? emu / 12700 : 0;

    private static double CssPoints(string style, string property)
    {
        var match = Regex.Match(style, $@"(?:^|;)\s*{property}\s*:\s*([\d.]+)\s*(pt|px|in|cm|mm)?", RegexOptions.IgnoreCase);
        if (!match.Success || Number(match.Groups[1].Value) is not { } v) return 0;
        return match.Groups[2].Value.ToLowerInvariant() switch
        {
            "px" => v * 0.75, "in" => v * 72, "cm" => v * 72 / 2.54, "mm" => v * 72 / 25.4, _ => v,
        };
    }

    private XDocument? OpenPart(string name) => _parts.Xml(name);

    private Dictionary<string, Rel> ReadRels(string partName)
    {
        var slash = partName.LastIndexOf('/');
        var folder = slash >= 0 ? partName[..slash] : "";
        var relsName = (folder.Length > 0 ? folder + "/" : "") + "_rels/" + partName[(slash + 1)..] + ".rels";

        var rels = new Dictionary<string, Rel>();
        foreach (var rel in OpenPart(relsName)?.Root?.Elements() ?? [])
        {
            if (Attr(rel, "Id") is not { } id || Attr(rel, "Target") is not { } target) continue;
            var external = Attr(rel, "TargetMode") == "External";
            rels[id] = new Rel(external ? target : Resolve(folder, target), external);
        }
        return rels;
    }

    /// <summary>A target relative to the part's folder, as a path inside the zip.</summary>
    private static string Resolve(string folder, string target)
    {
        target = Uri.UnescapeDataString(target);
        var segments = target.StartsWith('/') ? new List<string>() : folder.Split('/', StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var segment in target.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == "..") { if (segments.Count > 0) segments.RemoveAt(segments.Count - 1); }
            else if (segment != ".") segments.Add(segment);
        }
        return string.Join('/', segments);
    }
}

/// <summary>Where the reader gets a package's parts: straight from the zip, or from a document being edited.</summary>
internal interface IDocxParts
{
    /// <summary>The part parsed (whitespace kept), or null when missing or not XML.</summary>
    XDocument? Xml(string name);
    /// <summary>The part's size in bytes, or -1 when missing.</summary>
    long Length(string name);
    byte[]? Bytes(string name);
}

internal sealed class ZipParts(ZipArchive zip) : IDocxParts
{
    private ZipArchiveEntry? Entry(string name) =>
        zip.GetEntry(name) ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName, name, StringComparison.OrdinalIgnoreCase));

    public XDocument? Xml(string name)
    {
        if (Entry(name) is not { } entry) return null;
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream, LoadOptions.PreserveWhitespace);
        }
        catch (Exception e) when (e is System.Xml.XmlException or InvalidDataException)
        {
            return null;
        }
    }

    public long Length(string name) => Entry(name)?.Length ?? -1;

    public byte[]? Bytes(string name)
    {
        if (Entry(name) is not { } entry) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }
}
