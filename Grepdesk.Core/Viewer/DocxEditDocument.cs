using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Grepdesk.Core.Viewer;

/// <summary>A caret position: a paragraph of <see cref="DocxEditDocument.Paragraphs"/> and an offset in its text.</summary>
public readonly record struct DocxPosition(int Paragraph, int Offset) : IComparable<DocxPosition>
{
    public int CompareTo(DocxPosition other) =>
        Paragraph != other.Paragraph ? Paragraph.CompareTo(other.Paragraph) : Offset.CompareTo(other.Offset);

    public static bool operator <(DocxPosition a, DocxPosition b) => a.CompareTo(b) < 0;
    public static bool operator >(DocxPosition a, DocxPosition b) => a.CompareTo(b) > 0;
    public static bool operator <=(DocxPosition a, DocxPosition b) => a.CompareTo(b) <= 0;
    public static bool operator >=(DocxPosition a, DocxPosition b) => a.CompareTo(b) >= 0;
}

public enum DocxAtomKind { Image, Label, PageBreak }

/// <summary>
/// Something in a paragraph that isn't text but takes one caret step: a picture,
/// a page break, or an object shown by a short label (footnote number, equation, symbol).
/// </summary>
public sealed record DocxAtom(DocxAtomKind Kind, string Label, byte[]? Image, double Width, double Height);

/// <summary>A stretch of a paragraph with one look, for drawing. Atoms are one U+FFFC character.</summary>
public sealed record DocxEditRun(int Start, string Text, DocxRunStyle Style, DocxAtom? Atom);

/// <param name="Id">The style's id, or null for a built-in style the document doesn't define yet (added when first used).</param>
public sealed record DocxStyleInfo(string? Id, string Name);

/// <summary>A change to the look of text.</summary>
public abstract record DocxRunChange
{
    public sealed record Bold(bool On) : DocxRunChange;
    public sealed record Italic(bool On) : DocxRunChange;
    public sealed record Underline(bool On) : DocxRunChange;
    public sealed record Strike(bool On) : DocxRunChange;
    /// <param name="Points">Font size in points.</param>
    public sealed record Size(double Points) : DocxRunChange;
    public sealed record Font(string Name) : DocxRunChange;
    /// <param name="Hex">"#RRGGBB", or null for automatic.</param>
    public sealed record Color(string? Hex) : DocxRunChange;
    /// <param name="Name">One of Word's highlight names ("yellow", "green"...), or null for none.</param>
    public sealed record Highlight(string? Name) : DocxRunChange;
    /// <summary>Removes the text's own formatting, leaving its paragraph style's.</summary>
    public sealed record Clear : DocxRunChange;
}

/// <summary>What an edit changed, for the view to refresh just that.</summary>
/// <param name="Caret">Where the caret goes.</param>
/// <param name="Relayout">Lists, sections or pages changed: lay the whole document out again.</param>
/// <param name="Retext">Paragraphs whose text or text formatting changed.</param>
/// <param name="Reformat">Paragraphs whose paragraph formatting changed (alignment, style, indent).</param>
/// <param name="Inserted">New paragraphs, each with the paragraph it follows.</param>
/// <param name="Removed">Paragraphs that no longer exist.</param>
public sealed record DocxEditResult(
    DocxPosition Caret,
    bool Relayout = false,
    IReadOnlyList<DocxEditParagraph>? Retext = null,
    IReadOnlyList<DocxEditParagraph>? Reformat = null,
    IReadOnlyList<(DocxEditParagraph Paragraph, DocxEditParagraph After)>? Inserted = null,
    IReadOnlyList<DocxEditParagraph>? Removed = null)
{
    public static DocxEditResult Nothing(DocxPosition caret) => new(caret);
}

/// <summary>The formatting new text gets: what Word applies when you press Ctrl+B with nothing selected and type.</summary>
public sealed class DocxFormat
{
    internal DocxFormat(XElement? rPr) => RPr = rPr;
    internal XElement? RPr { get; }
}

/// <summary>
/// One editable paragraph. Its text has U+FFFC for each atom, U+2028 for a line
/// break and a tab for a tab. The &lt;w:p&gt; element stays the truth: every edit
/// is written straight back into it, keeping what isn't text (bookmarks,
/// fields, comments, pictures) where it was.
/// </summary>
public sealed class DocxEditParagraph
{
    public const char AtomChar = '\uFFFC';
    public const char LineBreakChar = '\u2028';

    internal abstract class Piece
    {
        public Wrapper? Wrapper;
        public XElement? RPr;
        public abstract int Length { get; }
    }

    internal sealed class TextPiece : Piece
    {
        public readonly StringBuilder Text = new();
        public override int Length => Text.Length;
    }

    /// <summary>An XML node kept as it is: an atom (one character) or a marker (none).</summary>
    internal sealed class NodePiece : Piece
    {
        public XElement Node = null!;
        /// <summary>A child of a run (a field character, a drawing), rewritten inside a run of its own properties.</summary>
        public bool InRun;
        public DocxAtom? Atom;
        public override int Length => Atom is null ? 0 : 1;
    }

    /// <summary>An element the text sits in (hyperlink, simple field, tracked insertion), kept by its name and attributes.</summary>
    internal sealed class Wrapper(XElement template, Wrapper? parent)
    {
        public readonly XElement Template = template;
        public readonly Wrapper? Parent = parent;
        public bool IsHyperlink => Template.Name.LocalName == "hyperlink";
    }

    private readonly DocxEditDocument _owner;
    private List<DocxEditRun>? _runs;
    private string? _text;

    internal DocxEditParagraph(DocxEditDocument owner, XElement element)
    {
        _owner = owner;
        Element = element;
        Parse(element, null);
    }

    internal XElement Element { get; }
    internal List<Piece> Pieces { get; } = [];

    /// <summary>Its place in <see cref="DocxEditDocument.Paragraphs"/>.</summary>
    public int Index { get; internal set; }

    /// <summary>Changes on every edit, so a view knows to lay it out again.</summary>
    public int Version { get; private set; }

    public int Length => Pieces.Sum(p => p.Length);

    public string Text => _text ??= string.Concat(Pieces.Select(p => p switch
    {
        TextPiece t => t.Text.ToString(),
        NodePiece { Atom: not null } => AtomChar.ToString(),
        _ => "",
    }));

    /// <summary>The paragraph as runs of one look each, for drawing.</summary>
    public IReadOnlyList<DocxEditRun> Runs => _runs ??= BuildRuns();

    public bool IsList => _owner.ListFormatOf(this) is not null;

    private List<DocxEditRun> BuildRuns()
    {
        var runs = new List<DocxEditRun>();
        var start = 0;
        foreach (var piece in Pieces)
        {
            if (piece.Length == 0) continue;
            var style = _owner.Resolve(Element, piece.RPr);
            if (piece is TextPiece text)
            {
                // Merge what looks the same; Word splits runs for reasons that don't show.
                if (runs.Count > 0 && runs[^1] is { Atom: null } last && last.Style == style)
                    runs[^1] = last with { Text = last.Text + text.Text };
                else
                    runs.Add(new DocxEditRun(start, text.Text.ToString(), style, null));
            }
            else if (piece is NodePiece { Atom: { } atom })
                runs.Add(new DocxEditRun(start, AtomChar.ToString(), style, atom));
            start += piece.Length;
        }
        return runs;
    }

    internal void Changed()
    {
        _runs = null;
        _text = null;
        Version++;
    }

    // =====================================================================
    // Reading the XML
    // =====================================================================

    private void Parse(XElement container, Wrapper? wrapper)
    {
        foreach (var element in container.Elements())
        {
            switch (element.Name.LocalName)
            {
                case "pPr" when container == Element:
                    break;
                case "r":
                    ParseRun(element, wrapper);
                    break;
                case "hyperlink" or "fldSimple" or "smartTag" or "ins" or "moveTo" or "customXml" or "dir" or "bdo":
                    Parse(element, new Wrapper(new XElement(element.Name, element.Attributes()), wrapper));
                    break;
                case "proofErr":
                    break; // spellcheck marks, stale once the text changes
                case "oMath" or "oMathPara" or "sdt":
                    var text = string.Concat(element.Descendants().Where(d => d.Name.LocalName == "t").Select(d => d.Value));
                    Pieces.Add(new NodePiece { Node = element, Wrapper = wrapper, Atom = Label(text.Length > 0 ? text : "…") });
                    break;
                default:
                    // Bookmarks, comment ranges, deleted text...: kept where they are.
                    Pieces.Add(new NodePiece { Node = element, Wrapper = wrapper });
                    break;
            }
        }
    }

    private void ParseRun(XElement run, Wrapper? wrapper)
    {
        var rPr = DocxXml.Child(run, "rPr") is { } props ? new XElement(props) : null;
        foreach (var child in run.Elements())
        {
            switch (child.Name.LocalName)
            {
                case "rPr" or "lastRenderedPageBreak":
                    break;
                case "t":
                    AddText(child.Value, rPr, wrapper);
                    break;
                case "tab":
                    AddText("\t", rPr, wrapper);
                    break;
                case "br" when DocxXml.Attr(child, "type") is "page":
                    AddNode(child, rPr, wrapper, new DocxAtom(DocxAtomKind.PageBreak, "", null, 0, 0));
                    break;
                case "br" or "cr":
                    AddText(LineBreakChar.ToString(), rPr, wrapper);
                    break;
                case "drawing" or "pict" or "object":
                    AddNode(child, rPr, wrapper, _owner.ImageAtom(child) ?? Label("▭"));
                    break;
                case "footnoteReference" or "endnoteReference":
                    AddNode(child, rPr, wrapper, Label(_owner.NextNoteNumber().ToString(CultureInfo.InvariantCulture)));
                    break;
                case "sym":
                    AddNode(child, rPr, wrapper, Label("□"));
                    break;
                case "noBreakHyphen":
                    AddNode(child, rPr, wrapper, Label("\u2011"));
                    break;
                case "ptab":
                    AddNode(child, rPr, wrapper, Label("    "));
                    break;
                default:
                    // Field characters and codes, soft hyphens, annotation marks.
                    AddNode(child, rPr, wrapper, null);
                    break;
            }
        }
    }

    private static DocxAtom Label(string text) => new(DocxAtomKind.Label, text, null, 0, 0);

    private void AddText(string text, XElement? rPr, Wrapper? wrapper)
    {
        if (text.Length == 0) return;
        if (Pieces.Count > 0 && Pieces[^1] is TextPiece last && SameFormat(last, rPr, wrapper))
        {
            last.Text.Append(text);
            return;
        }
        var piece = new TextPiece { RPr = rPr, Wrapper = wrapper };
        piece.Text.Append(text);
        Pieces.Add(piece);
    }

    private void AddNode(XElement node, XElement? rPr, Wrapper? wrapper, DocxAtom? atom) =>
        Pieces.Add(new NodePiece { Node = node, RPr = rPr, Wrapper = wrapper, InRun = true, Atom = atom });

    private static bool SameFormat(Piece piece, XElement? rPr, Wrapper? wrapper) =>
        piece.Wrapper == wrapper && DocxXml.SameProperties(piece.RPr, rPr);

    // =====================================================================
    // Editing
    // =====================================================================

    /// <summary>Index of the piece holding the character just before <paramref name="offset"/>, and that offset inside it.</summary>
    private (int Index, int Inner) PieceBefore(int offset)
    {
        var start = 0;
        for (var i = 0; i < Pieces.Count; i++)
        {
            var length = Pieces[i].Length;
            if (length > 0 && offset > start && offset <= start + length) return (i, offset - start);
            start += length;
        }
        return (-1, 0);
    }

    /// <summary>The look text typed at this offset takes: the character before it, else the first one, else the paragraph mark's.</summary>
    internal (XElement? RPr, Wrapper? Wrapper) FormatAt(int offset)
    {
        var (i, inner) = PieceBefore(offset);
        if (offset > 0 && i >= 0)
        {
            var piece = Pieces[i];
            var wrapper = piece.Wrapper;
            var rPr = piece.RPr;
            // Typing right after a link doesn't extend it, as in Word.
            if (wrapper is { IsHyperlink: true } && inner == piece.Length && !NextContentShares(i, wrapper))
            {
                wrapper = wrapper.Parent;
                rPr = DocxXml.WithoutLinkLook(rPr);
            }
            return (rPr, wrapper);
        }
        if (Pieces.FirstOrDefault(p => p.Length > 0) is { } first) return (first.RPr, first.Wrapper);
        return (DocxXml.Child(DocxXml.Child(Element, "pPr"), "rPr") is { } mark ? DocxXml.MarkToRun(mark) : null, null);
    }

    private bool NextContentShares(int index, Wrapper wrapper)
    {
        for (var i = index + 1; i < Pieces.Count; i++)
            if (Pieces[i].Length > 0) return Pieces[i].Wrapper == wrapper;
        return false;
    }

    internal void Insert(int offset, string text, XElement? rPr, Wrapper? wrapper)
    {
        if (text.Length == 0) return;
        var piece = new TextPiece { RPr = rPr, Wrapper = wrapper };
        piece.Text.Append(text);

        var (i, inner) = PieceBefore(offset);
        if (offset > 0 && i >= 0)
        {
            if (Pieces[i] is TextPiece target && SameFormat(target, rPr, wrapper))
                target.Text.Insert(inner, text);
            else
            {
                SplitPiece(i, inner);
                Pieces.Insert(i + 1, piece);
            }
        }
        else
        {
            var first = Pieces.FindIndex(p => p.Length > 0);
            if (first >= 0 && Pieces[first] is TextPiece target && SameFormat(target, rPr, wrapper))
                target.Text.Insert(0, text);
            else
                Pieces.Insert(first >= 0 ? first : Pieces.Count, piece);
        }
        Normalize();
    }

    /// <summary>Cuts a text piece in two at <paramref name="inner"/>; the second half follows it.</summary>
    private void SplitPiece(int index, int inner)
    {
        if (Pieces[index] is not TextPiece text || inner <= 0 || inner >= text.Length) return;
        var rest = new TextPiece { RPr = text.RPr, Wrapper = text.Wrapper };
        rest.Text.Append(text.Text, inner, text.Length - inner);
        text.Text.Length = inner;
        Pieces.Insert(index + 1, rest);
    }

    /// <summary>Makes <paramref name="offset"/> fall between pieces; returns the index of the first piece at or after it.</summary>
    private int SplitAt(int offset)
    {
        var start = 0;
        for (var i = 0; i < Pieces.Count; i++)
        {
            var length = Pieces[i].Length;
            if (offset > start && offset < start + length)
            {
                SplitPiece(i, offset - start);
                return i + 1;
            }
            if (offset <= start && length > 0) return i;
            start += length;
        }
        return Pieces.Count;
    }

    /// <returns>True when a page break went with the text.</returns>
    internal bool Delete(int from, int to)
    {
        if (to <= from) return false;
        var pageBreak = false;
        var start = 0;
        for (var i = 0; i < Pieces.Count; i++)
        {
            var piece = Pieces[i];
            var length = piece.Length;
            var end = start + length;
            if (length > 0 && end > from && start < to)
            {
                if (piece is TextPiece text)
                {
                    var a = Math.Max(from, start) - start;
                    var b = Math.Min(to, end) - start;
                    text.Text.Remove(a, b - a);
                }
                else
                {
                    pageBreak |= piece is NodePiece { Atom.Kind: DocxAtomKind.PageBreak };
                    Pieces.RemoveAt(i--);
                }
            }
            start = end;
        }
        Normalize();
        return pageBreak;
    }

    /// <summary>Everything from <paramref name="offset"/> on, taken out to start a new paragraph.</summary>
    internal List<Piece> TakeFrom(int offset)
    {
        var index = SplitAt(offset);
        var tail = Pieces.GetRange(index, Pieces.Count - index);
        Pieces.RemoveRange(index, Pieces.Count - index);
        Normalize();
        return tail;
    }

    internal void Append(IEnumerable<Piece> pieces)
    {
        Pieces.AddRange(pieces);
        Normalize();
    }

    internal void ApplyRun(int from, int to, Func<XElement?, XElement?> change)
    {
        if (to <= from) return;
        var first = SplitAt(from);
        var last = SplitAt(to);
        for (var i = first; i < last; i++)
            if (Pieces[i].Length > 0) Pieces[i].RPr = change(Pieces[i].RPr);
        Normalize();
    }

    /// <summary>The direct run properties over a range, piece by piece, for checking what's on.</summary>
    internal IEnumerable<XElement?> RunPropertiesIn(int from, int to)
    {
        var start = 0;
        foreach (var piece in Pieces)
        {
            var end = start + piece.Length;
            if (piece.Length > 0 && end > from && start < to) yield return piece.RPr;
            start = end;
        }
    }

    /// <summary>Copies of the pieces in a range, for the clipboard.</summary>
    internal List<Piece> CopyRange(int from, int to)
    {
        var copy = new List<Piece>();
        var start = 0;
        foreach (var piece in Pieces)
        {
            var end = start + piece.Length;
            if (piece.Length > 0 && end > from && start < to)
            {
                if (piece is TextPiece text)
                {
                    var a = Math.Max(from, start) - start;
                    var b = Math.Min(to, end) - start;
                    var part = new TextPiece { RPr = text.RPr, Wrapper = text.Wrapper };
                    part.Text.Append(text.Text, a, b - a);
                    copy.Add(part);
                }
                else if (piece is NodePiece node)
                    copy.Add(new NodePiece { Node = new XElement(node.Node), RPr = node.RPr, Wrapper = node.Wrapper, InRun = node.InRun, Atom = node.Atom });
            }
            start = end;
        }
        return copy;
    }

    internal void InsertPieces(int offset, IEnumerable<Piece> pieces)
    {
        var index = SplitAt(offset);
        Pieces.InsertRange(index, pieces);
        Normalize();
    }

    private void Normalize()
    {
        for (var i = Pieces.Count - 1; i >= 0; i--)
        {
            if (Pieces[i] is TextPiece { Length: 0 }) Pieces.RemoveAt(i);
            else if (i > 0 && Pieces[i] is TextPiece next && Pieces[i - 1] is TextPiece prev && SameFormat(prev, next.RPr, next.Wrapper))
            {
                prev.Text.Append(next.Text);
                Pieces.RemoveAt(i);
            }
        }
    }

    // =====================================================================
    // Writing the XML back
    // =====================================================================

    /// <summary>Rewrites the paragraph's content from its pieces, its properties untouched.</summary>
    internal void Commit()
    {
        var w = Element.Name.Namespace;
        var pPr = DocxXml.Child(Element, "pPr");
        pPr?.Remove();
        Element.RemoveNodes();
        if (pPr is not null) Element.Add(pPr);

        var open = new List<(Wrapper Wrapper, XElement Element)>();
        XElement? run = null;
        XElement? runProps = null;

        foreach (var piece in Pieces)
        {
            if (piece is TextPiece { Length: 0 }) continue;

            var chain = new List<Wrapper>();
            for (var x = piece.Wrapper; x is not null; x = x.Parent) chain.Insert(0, x);
            var common = 0;
            while (common < open.Count && common < chain.Count && open[common].Wrapper == chain[common]) common++;
            if (common < open.Count)
            {
                open.RemoveRange(common, open.Count - common);
                run = null;
            }
            for (; common < chain.Count; common++)
            {
                var wrapperElement = new XElement(chain[common].Template.Name, chain[common].Template.Attributes());
                (open.Count == 0 ? Element : open[^1].Element).Add(wrapperElement);
                open.Add((chain[common], wrapperElement));
                run = null;
            }
            var container = open.Count == 0 ? Element : open[^1].Element;

            if (piece is NodePiece { InRun: false } block)
            {
                block.Node = Add(container, block.Node);
                run = null;
                continue;
            }

            if (run is null || run.Parent != container || !DocxXml.SameProperties(runProps, piece.RPr))
            {
                run = new XElement(w + "r");
                if (piece.RPr is not null) run.Add(new XElement(piece.RPr));
                container.Add(run);
                runProps = piece.RPr;
            }

            if (piece is TextPiece text) AppendText(run, text.Text.ToString(), w);
            else if (piece is NodePiece node) node.Node = Add(run, node.Node);
        }
        Changed();
    }

    /// <summary>Adds a node, which LINQ to XML copies when it still has a parent; returns what was added.</summary>
    private static XElement Add(XElement parent, XElement node)
    {
        parent.Add(node);
        return (XElement)parent.LastNode!;
    }

    private static void AppendText(XElement run, string text, XNamespace w)
    {
        var start = 0;
        for (var i = 0; i <= text.Length; i++)
        {
            if (i < text.Length && text[i] is not ('\t' or LineBreakChar)) continue;
            if (i > start)
                run.Add(new XElement(w + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), text[start..i]));
            if (i < text.Length) run.Add(new XElement(w + (text[i] == '\t' ? "tab" : "br")));
            start = i + 1;
        }
    }
}

/// <summary>
/// A .docx opened for editing. Holds every part of the package in memory and
/// writes back only what changed, so pictures, headers, comments and parts
/// this editor knows nothing about come out of a save exactly as they went in.
/// </summary>
public sealed class DocxEditDocument
{
    private const string DocumentPart = "word/document.xml";
    private const string DocumentRels = "word/_rels/document.xml.rels";
    private const string ContentTypes = "[Content_Types].xml";
    private const string RelNamespace = "http://schemas.openxmlformats.org/package/2006/relationships";
    private const string OfficeRels = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string TypesNamespace = "http://schemas.openxmlformats.org/package/2006/content-types";
    private const string WordNamespace = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private static readonly HashSet<string> ImageTypes = new([".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<string, byte[]> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _times = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, XDocument?> _xml = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<DocxEditParagraph> _paragraphs = [];
    private readonly Dictionary<XElement, DocxEditParagraph> _byElement = [];
    private readonly Parts _parts;
    private DocxReader _reader;
    private Dictionary<string, string>? _rels;
    private int _version, _savedVersion, _noteNumber;

    private DocxEditDocument()
    {
        _parts = new Parts(this);
        _reader = new DocxReader(_parts, default);
    }

    /// <exception cref="InvalidDataException">Not a Word document.</exception>
    public static DocxEditDocument Open(string path, CancellationToken ct)
    {
        var document = new DocxEditDocument();
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                using var stream = entry.Open();
                using var memory = new MemoryStream();
                stream.CopyTo(memory);
                if (document._raw.TryAdd(entry.FullName, memory.ToArray()))
                {
                    document._order.Add(entry.FullName);
                    document._times[entry.FullName] = entry.LastWriteTime;
                }
            }
        }
        if (document.Xml(DocumentPart)?.Root is null) throw new InvalidDataException("No word/document.xml");
        return document;
    }

    public IReadOnlyList<DocxEditParagraph> Paragraphs => _paragraphs;

    public bool IsDirty => _version != _savedVersion;

    private XElement Body => DocxXml.Child(Xml(DocumentPart)!.Root, "body") ?? throw new InvalidDataException("No document body");

    private XNamespace W => Xml(DocumentPart)!.Root!.Name.Namespace;

    // =====================================================================
    // Layout
    // =====================================================================

    /// <summary>
    /// Lays the document out for editing and refreshes <see cref="Paragraphs"/>
    /// to match it, in the order the layout shows them.
    /// </summary>
    public DocxDocument Layout(CancellationToken ct = default)
    {
        _reader = new DocxReader(_parts, ct, editLayout: true);
        var layout = _reader.Read();

        var old = new Dictionary<XElement, DocxEditParagraph>(_byElement);
        _paragraphs.Clear();
        _byElement.Clear();
        _noteNumber = 0;

        void Visit(IEnumerable<DocxBlock> blocks)
        {
            foreach (var block in blocks)
            {
                switch (block)
                {
                    case DocxParagraph { Source: { } source } when !_byElement.ContainsKey(source):
                        var paragraph = old.TryGetValue(source, out var kept) ? kept : new DocxEditParagraph(this, source);
                        paragraph.Index = _paragraphs.Count;
                        paragraph.Changed(); // styles may have changed under it
                        _paragraphs.Add(paragraph);
                        _byElement[source] = paragraph;
                        break;
                    case DocxTable table:
                        foreach (var cell in table.Cells) Visit(cell.Blocks);
                        break;
                }
            }
        }
        Visit(layout.Body);
        return layout;
    }

    public DocxEditParagraph? Find(XElement source) => _byElement.GetValueOrDefault(source);

    /// <summary>One paragraph's format as the layout gives it, for refreshing it alone.</summary>
    public DocxParagraph LayoutParagraph(DocxEditParagraph paragraph) => _reader.ReadSingle(paragraph.Element);

    internal DocxRunStyle Resolve(XElement p, XElement? rPr) => _reader.ResolveRun(p, rPr);

    internal string? ListFormatOf(DocxEditParagraph p) => _reader.ListFormat(p.Element);

    internal int NextNoteNumber() => ++_noteNumber;

    internal DocxAtom? ImageAtom(XElement node)
    {
        var blip = node.Descendants().FirstOrDefault(e => e.Name.LocalName is "blip" or "imagedata");
        var id = DocxXml.Attr(blip, "embed") ?? DocxXml.Attr(blip, "id");
        if (id is null || !DocumentRelations().TryGetValue(id, out var target)) return null;
        if (!ImageTypes.Contains(Path.GetExtension(target)) || !_raw.TryGetValue(target, out var data)) return null;

        var extent = node.Descendants().FirstOrDefault(e => e.Name.LocalName == "extent");
        double Emu(string? v) => double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) && d > 0 ? d / 12700 : 0;
        return new DocxAtom(DocxAtomKind.Image, "", data, Emu(DocxXml.Attr(extent, "cx")), Emu(DocxXml.Attr(extent, "cy")));
    }

    private Dictionary<string, string> DocumentRelations()
    {
        if (_rels is not null) return _rels;
        _rels = new Dictionary<string, string>();
        foreach (var rel in Xml(DocumentRels)?.Root?.Elements() ?? [])
            if (DocxXml.Attr(rel, "Id") is { } id && DocxXml.Attr(rel, "Target") is { } target && DocxXml.Attr(rel, "TargetMode") != "External")
                _rels[id] = target.StartsWith('/') ? target[1..] : "word/" + target;
        return _rels;
    }

    // =====================================================================
    // Looks
    // =====================================================================

    /// <summary>The formatting text typed at this position gets.</summary>
    public DocxFormat FormatAt(DocxPosition at) => new(_paragraphs[at.Paragraph].FormatAt(at.Offset).RPr);

    public DocxFormat Change(DocxFormat format, DocxRunChange change) => new(DocxXml.ApplyChange(format.RPr, change));

    public DocxRunStyle StyleOf(DocxPosition at, DocxFormat format) => Resolve(_paragraphs[at.Paragraph].Element, format.RPr);

    /// <summary>Whether every piece of text in the range satisfies <paramref name="test"/>; for an empty range, the text at its start.</summary>
    public bool All(DocxPosition from, DocxPosition to, Func<DocxRunStyle, bool> test)
    {
        if (from >= to) return test(StyleOf(from, FormatAt(from)));
        var any = false;
        for (var i = from.Paragraph; i <= to.Paragraph; i++)
        {
            var p = _paragraphs[i];
            var a = i == from.Paragraph ? from.Offset : 0;
            var b = i == to.Paragraph ? to.Offset : p.Length;
            foreach (var rPr in p.RunPropertiesIn(a, b))
            {
                any = true;
                if (!test(Resolve(p.Element, rPr))) return false;
            }
        }
        return any || test(StyleOf(from, FormatAt(from)));
    }

    public DocxAlignment AlignmentOf(DocxEditParagraph p) => _reader.ReadSingle(p.Element).Format.Alignment;

    /// <summary>"bullet", "decimal" and the like for a list paragraph, else null.</summary>
    public string? ListFormat(DocxEditParagraph p) => ListFormatOf(p);

    /// <summary>The paragraph's style, as one of <see cref="ParagraphStyles"/>.</summary>
    public string? StyleIdOf(DocxEditParagraph p) =>
        DocxXml.Attr(DocxXml.Child(DocxXml.Child(p.Element, "pPr"), "pStyle")) ?? _reader.DefaultParagraphStyle;

    private static readonly (string Name, string Id, string Xml)[] BuiltInStyles =
    [
        ("Title", "Title", """<w:style w:type="paragraph" w:styleId="Title" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:name w:val="Title"/><w:basedOn w:val="{0}"/><w:next w:val="{0}"/><w:uiPriority w:val="10"/><w:qFormat/><w:pPr><w:spacing w:after="80"/><w:contextualSpacing/></w:pPr><w:rPr><w:sz w:val="56"/><w:szCs w:val="56"/></w:rPr></w:style>"""),
        ("heading 1", "Heading1", """<w:style w:type="paragraph" w:styleId="Heading1" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:name w:val="heading 1"/><w:basedOn w:val="{0}"/><w:next w:val="{0}"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="360" w:after="80"/><w:outlineLvl w:val="0"/></w:pPr><w:rPr><w:b/><w:bCs/><w:sz w:val="32"/><w:szCs w:val="32"/></w:rPr></w:style>"""),
        ("heading 2", "Heading2", """<w:style w:type="paragraph" w:styleId="Heading2" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:name w:val="heading 2"/><w:basedOn w:val="{0}"/><w:next w:val="{0}"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="240" w:after="80"/><w:outlineLvl w:val="1"/></w:pPr><w:rPr><w:b/><w:bCs/><w:sz w:val="28"/><w:szCs w:val="28"/></w:rPr></w:style>"""),
        ("heading 3", "Heading3", """<w:style w:type="paragraph" w:styleId="Heading3" xmlns:w="http://schemas.openxmlformats.org/wordprocessingml/2006/main"><w:name w:val="heading 3"/><w:basedOn w:val="{0}"/><w:next w:val="{0}"/><w:uiPriority w:val="9"/><w:qFormat/><w:pPr><w:keepNext/><w:keepLines/><w:spacing w:before="160" w:after="80"/><w:outlineLvl w:val="2"/></w:pPr><w:rPr><w:b/><w:bCs/><w:sz w:val="24"/><w:szCs w:val="24"/></w:rPr></w:style>"""),
    ];

    /// <summary>
    /// Styles to offer: the document's visible paragraph styles, then Title and
    /// Heading 1–3 when it lacks them. Names are Word's internal ones ("heading 1").
    /// </summary>
    public IReadOnlyList<DocxStyleInfo> ParagraphStyles
    {
        get
        {
            var used = new HashSet<string>(_paragraphs.Select(StyleIdOf).OfType<string>());
            var styles = _reader.ParagraphStyles
                .Where(s => (s.Quick && !s.Hidden) || used.Contains(s.Id) || s.Id == _reader.DefaultParagraphStyle)
                .Select(s => new DocxStyleInfo(s.Id, s.Name))
                .ToList();
            foreach (var (name, _, _) in BuiltInStyles)
                if (!_reader.ParagraphStyles.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    styles.Add(new DocxStyleInfo(null, name));
            return styles;
        }
    }

    /// <summary>Fonts to offer: the document's own, then common ones.</summary>
    public IReadOnlyList<string> Fonts
    {
        get
        {
            var fonts = new List<string>();
            if (_reader.MinorFont is { } minor) fonts.Add(minor);
            if (_reader.MajorFont is { } major) fonts.Add(major);
            foreach (var font in Xml("word/fontTable.xml")?.Root?.Elements() ?? [])
                if (DocxXml.Attr(font, "name") is { Length: > 0 } name) fonts.Add(name);
            fonts.AddRange(["Calibri", "Aptos", "Arial", "Times New Roman", "Cambria", "Georgia", "Verdana", "Segoe UI", "Tahoma", "Courier New", "Consolas"]);
            return fonts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }
    }

    // =====================================================================
    // Text edits
    // =====================================================================

    /// <summary>Types text at a position. New lines in it start new paragraphs.</summary>
    public DocxEditResult Insert(DocxPosition at, string text, DocxFormat? format = null)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        var result = new ResultBuilder(at);
        for (var i = 0; i < lines.Length; i++)
        {
            if (i > 0) result.Add(SplitParagraph(result.Caret, keepList: true));
            if (lines[i].Length == 0) continue;
            var p = _paragraphs[result.Caret.Paragraph];
            var (rPr, wrapper) = p.FormatAt(result.Caret.Offset);
            if (format is not null) rPr = format.RPr;
            p.Insert(result.Caret.Offset, lines[i], rPr, wrapper);
            p.Commit();
            result.Retext(p);
            result.Caret = result.Caret with { Offset = result.Caret.Offset + lines[i].Length };
        }
        return Done(result);
    }

    /// <summary>Removes the text between two positions, joining paragraphs where it can.</summary>
    public DocxEditResult Delete(DocxPosition from, DocxPosition to)
    {
        if (to < from) (from, to) = (to, from);
        var result = new ResultBuilder(from);
        if (from == to) return result.Build();

        if (from.Paragraph == to.Paragraph)
        {
            var p = _paragraphs[from.Paragraph];
            result.Relayout |= p.Delete(from.Offset, to.Offset);
            p.Commit();
            result.Retext(p);
            return Done(result);
        }

        var first = _paragraphs[from.Paragraph];
        var last = _paragraphs[to.Paragraph];
        for (var i = from.Paragraph; i <= to.Paragraph; i++)
        {
            var p = _paragraphs[i];
            var a = i == from.Paragraph ? from.Offset : 0;
            var b = i == to.Paragraph ? to.Offset : p.Length;
            result.Relayout |= p.Delete(a, b);
            result.Relayout |= p.IsList || HasPageProperties(p);
            p.Commit();
            result.Retext(p);
        }

        // Paragraphs side by side in one container join; across table cells they just empty.
        if (Siblings(from.Paragraph, to.Paragraph))
        {
            for (var i = to.Paragraph - 1; i > from.Paragraph; i--) RemoveParagraph(_paragraphs[i], result);
            JoinNext(first, result);
        }
        return Done(result);
    }

    /// <summary>Backspace: the character before the caret, or the start of a paragraph.</summary>
    public DocxEditResult Backspace(DocxPosition at)
    {
        var p = _paragraphs[at.Paragraph];
        if (at.Offset > 0)
        {
            return Delete(at with { Offset = PreviousCharacter(p.Text, at.Offset) }, at);
        }

        // At the start, Word first takes away a page break before, then a bullet, then joins.
        var result = new ResultBuilder(at);
        var pPr = DocxXml.Child(p.Element, "pPr");
        if (DocxXml.Child(pPr, "pageBreakBefore") is { } pageBreak)
        {
            pageBreak.Remove();
            result.Relayout = true;
            return Done(result);
        }
        if (p.IsList)
        {
            RemoveNumbering(p);
            result.Relayout = true;
            return Done(result);
        }
        if (at.Paragraph > 0 && CanJoin(_paragraphs[at.Paragraph - 1], p))
        {
            var previous = _paragraphs[at.Paragraph - 1];
            result.Caret = new DocxPosition(previous.Index, previous.Length);
            result.Relayout |= HasPageProperties(previous) || HasPageProperties(p);
            JoinNext(previous, result);
            return Done(result);
        }
        return result.Build();
    }

    /// <summary>
    /// Where the character before <paramref name="offset"/> starts: a whole
    /// character as the eye sees it, so "İ" written as I plus a combining dot,
    /// or an emoji, goes in one step.
    /// </summary>
    public static int PreviousCharacter(string text, int offset)
    {
        if (offset <= 0) return 0;
        var previous = 0;
        foreach (var start in StringInfo.ParseCombiningCharacters(text))
        {
            if (start >= offset) break;
            previous = start;
        }
        return previous;
    }

    /// <summary>Where the character after <paramref name="offset"/> ends.</summary>
    public static int NextCharacter(string text, int offset)
    {
        if (offset >= text.Length) return text.Length;
        foreach (var start in StringInfo.ParseCombiningCharacters(text))
            if (start > offset) return start;
        return text.Length;
    }

    /// <summary>Delete: the character after the caret, or the next paragraph joined on.</summary>
    public DocxEditResult DeleteForward(DocxPosition at)
    {
        var p = _paragraphs[at.Paragraph];
        if (at.Offset < p.Length)
        {
            return Delete(at, at with { Offset = NextCharacter(p.Text, at.Offset) });
        }
        var result = new ResultBuilder(at);
        if (at.Paragraph + 1 < _paragraphs.Count && CanJoin(p, _paragraphs[at.Paragraph + 1]))
        {
            var next = _paragraphs[at.Paragraph + 1];
            result.Relayout |= HasPageProperties(p) || HasPageProperties(next) || next.IsList;
            JoinNext(p, result);
            return Done(result);
        }
        return result.Build();
    }

    /// <summary>Enter. In an empty list item it ends the list instead, as in Word.</summary>
    public DocxEditResult Enter(DocxPosition at)
    {
        var p = _paragraphs[at.Paragraph];
        if (p.Length == 0 && p.IsList)
        {
            RemoveNumbering(p);
            return Done(new ResultBuilder(at) { Relayout = true });
        }
        var result = new ResultBuilder(at);
        result.Add(SplitParagraph(at, keepList: true));
        return Done(result);
    }

    /// <summary>Ctrl+Enter: the rest of the paragraph starts a new page.</summary>
    public DocxEditResult PageBreak(DocxPosition at)
    {
        var result = new ResultBuilder(at);
        result.Add(SplitParagraph(at, keepList: true));
        var p = _paragraphs[result.Caret.Paragraph];
        DocxXml.SetOrdered(PPr(p), W + "pageBreakBefore", DocxXml.ParagraphOrder);
        result.Relayout = true;
        return Done(result);
    }

    private ResultBuilder SplitParagraph(DocxPosition at, bool keepList)
    {
        var result = new ResultBuilder(at);
        var p = _paragraphs[at.Paragraph];
        var atEnd = at.Offset >= p.Length;
        var tail = p.TakeFrom(at.Offset);

        var element = new XElement(W + "p");
        var pPr = DocxXml.Child(p.Element, "pPr");
        if (pPr is not null)
        {
            var copy = new XElement(pPr);
            // The section break and its page settings belong to the paragraph that ends the section: the second one.
            DocxXml.Child(pPr, "sectPr")?.Remove();
            DocxXml.Child(copy, "pageBreakBefore")?.Remove();
            if (!keepList) DocxXml.Child(copy, "numPr")?.Remove();
            element.Add(copy);
        }
        p.Element.AddAfterSelf(element);

        var created = new DocxEditParagraph(this, element);
        created.Append(tail);
        // After a heading comes the style it names as next, usually Normal.
        if (atEnd && StyleIdOf(p) is { } styleId &&
            _reader.ParagraphStyles.FirstOrDefault(s => s.Id == styleId)?.Next is { } next && next != styleId)
            SetStyleElement(created, next);

        _paragraphs.Insert(p.Index + 1, created);
        _byElement[element] = created;
        Renumber(p.Index);
        p.Commit();
        created.Commit();

        result.Retext(p);
        result.Inserted.Add((created, p));
        result.Relayout |= p.IsList || pPr is not null && DocxXml.Child(element.Element(W + "pPr"), "sectPr") is not null;
        result.Caret = new DocxPosition(created.Index, 0);
        return result;
    }

    private bool CanJoin(DocxEditParagraph first, DocxEditParagraph second)
    {
        if (first.Element.Parent != second.Element.Parent) return false;
        // A section break ends a section; deleting it is a step too far for a keystroke.
        if (DocxXml.Child(DocxXml.Child(first.Element, "pPr"), "sectPr") is not null) return false;
        return first.Element.ElementsAfterSelf().TakeWhile(e => e != second.Element)
            .All(e => e.Name.LocalName is not ("p" or "tbl" or "sdt" or "customXml"));
    }

    /// <summary>Whether paragraphs from a to b are siblings with only paragraphs between them.</summary>
    private bool Siblings(int a, int b)
    {
        for (var i = a; i < b; i++)
            if (!CanJoinIgnoringSections(_paragraphs[i], _paragraphs[i + 1])) return false;
        return true;
    }

    private static bool CanJoinIgnoringSections(DocxEditParagraph first, DocxEditParagraph second) =>
        first.Element.Parent == second.Element.Parent &&
        first.Element.ElementsAfterSelf().TakeWhile(e => e != second.Element)
            .All(e => e.Name.LocalName is not ("p" or "tbl" or "sdt" or "customXml"));

    /// <summary>Moves the next paragraph's content onto the end of this one and removes it.</summary>
    private void JoinNext(DocxEditParagraph first, ResultBuilder result)
    {
        var second = _paragraphs[first.Index + 1];
        first.Append(second.Pieces.ToList());
        // A section break at the end of the second paragraph moves to the joined one.
        if (DocxXml.Child(DocxXml.Child(second.Element, "pPr"), "sectPr") is { } sect)
        {
            DocxXml.Child(DocxXml.Child(first.Element, "pPr"), "sectPr")?.Remove();
            DocxXml.SetOrdered(PPr(first), new XElement(sect), DocxXml.ParagraphOrder);
            result.Relayout = true;
        }
        second.Pieces.Clear();
        RemoveParagraph(second, result);
        first.Commit();
        result.Retext(first);
    }

    private void RemoveParagraph(DocxEditParagraph p, ResultBuilder result)
    {
        result.Relayout |= HasPageProperties(p);
        p.Element.Remove();
        _paragraphs.RemoveAt(p.Index);
        _byElement.Remove(p.Element);
        Renumber(p.Index);
        result.Removed.Add(p);
    }

    private void Renumber(int from)
    {
        for (var i = Math.Max(0, from); i < _paragraphs.Count; i++) _paragraphs[i].Index = i;
    }

    private static bool HasPageProperties(DocxEditParagraph p)
    {
        var pPr = DocxXml.Child(p.Element, "pPr");
        return DocxXml.Child(pPr, "sectPr") is not null || DocxXml.Child(pPr, "pageBreakBefore") is not null ||
               p.Pieces.Any(x => x is DocxEditParagraph.NodePiece { Atom.Kind: DocxAtomKind.PageBreak });
    }

    // =====================================================================
    // Clipboard
    // =====================================================================

    /// <summary>A copied range: its plain text, and its pieces for pasting back here with their formatting.</summary>
    public sealed class Clip
    {
        internal Clip(string text, List<List<DocxEditParagraph.Piece>> paragraphs)
        {
            Text = text;
            Paragraphs = paragraphs;
        }

        public string Text { get; }
        internal List<List<DocxEditParagraph.Piece>> Paragraphs { get; }
    }

    public Clip Copy(DocxPosition from, DocxPosition to)
    {
        if (to < from) (from, to) = (to, from);
        var parts = new List<List<DocxEditParagraph.Piece>>();
        var text = new StringBuilder();
        for (var i = from.Paragraph; i <= to.Paragraph; i++)
        {
            var p = _paragraphs[i];
            var a = i == from.Paragraph ? from.Offset : 0;
            var b = i == to.Paragraph ? to.Offset : p.Length;
            parts.Add(p.CopyRange(a, b));
            if (i > from.Paragraph) text.Append(Environment.NewLine);
            text.Append(p.Text[a..b].Replace(DocxEditParagraph.LineBreakChar, '\n').Replace(DocxEditParagraph.AtomChar.ToString(), ""));
        }
        return new Clip(text.ToString(), parts);
    }

    /// <summary>Pastes a range copied from this editor, formatting and all.</summary>
    public DocxEditResult Paste(DocxPosition at, Clip clip)
    {
        var result = new ResultBuilder(at);
        for (var i = 0; i < clip.Paragraphs.Count; i++)
        {
            if (i > 0) result.Add(SplitParagraph(result.Caret, keepList: true));
            var pieces = clip.Paragraphs[i].Select(Clone).ToList();
            if (pieces.Count == 0) continue;
            var p = _paragraphs[result.Caret.Paragraph];
            p.InsertPieces(result.Caret.Offset, pieces);
            p.Commit();
            result.Retext(p);
            result.Relayout |= pieces.Any(x => x is DocxEditParagraph.NodePiece { Atom.Kind: DocxAtomKind.PageBreak });
            result.Caret = result.Caret with { Offset = result.Caret.Offset + pieces.Sum(x => x.Length) };
        }
        return Done(result);
    }

    private static DocxEditParagraph.Piece Clone(DocxEditParagraph.Piece piece)
    {
        // Pasted text keeps its look but not the link or field it sat in, which may not exist here.
        switch (piece)
        {
            case DocxEditParagraph.TextPiece text:
                var copy = new DocxEditParagraph.TextPiece { RPr = text.RPr };
                copy.Text.Append(text.Text);
                return copy;
            case DocxEditParagraph.NodePiece node:
                return new DocxEditParagraph.NodePiece { Node = new XElement(node.Node), RPr = node.RPr, InRun = node.InRun, Atom = node.Atom };
            default:
                throw new InvalidOperationException();
        }
    }

    // =====================================================================
    // Formatting
    // =====================================================================

    public DocxEditResult FormatText(DocxPosition from, DocxPosition to, DocxRunChange change)
    {
        if (to < from) (from, to) = (to, from);
        var result = new ResultBuilder(to);
        for (var i = from.Paragraph; i <= to.Paragraph; i++)
        {
            var p = _paragraphs[i];
            var a = i == from.Paragraph ? from.Offset : 0;
            var b = i == to.Paragraph ? to.Offset : p.Length;
            if (b <= a) continue;
            p.ApplyRun(a, b, rPr => DocxXml.ApplyChange(rPr, change));
            p.Commit();
            result.Retext(p);
        }
        return Done(result);
    }

    public DocxEditResult SetAlignment(int first, int last, DocxAlignment alignment)
    {
        var value = alignment switch
        {
            DocxAlignment.Center => "center",
            DocxAlignment.Right => "right",
            DocxAlignment.Justify => "both",
            _ => "left",
        };
        return ForParagraphs(first, last, (p, result) =>
        {
            DocxXml.SetOrdered(PPr(p), new XElement(W + "jc", new XAttribute(W + "val", value)), DocxXml.ParagraphOrder);
            result.Reformat(p);
        });
    }

    public DocxEditResult SetStyle(int first, int last, DocxStyleInfo style)
    {
        var id = style.Id ?? AddBuiltInStyle(style.Name);
        if (id is null) return DocxEditResult.Nothing(new DocxPosition(last, 0));
        var result = ForParagraphs(first, last, (p, r) =>
        {
            SetStyleElement(p, id);
            r.Reformat(p);
            r.Retext(p);
        });
        // A new style is in styles.xml now; the reader has to see it.
        _reader = new DocxReader(_parts, default, editLayout: true);
        return result with { Relayout = true };
    }

    private void SetStyleElement(DocxEditParagraph p, string id)
    {
        var pPr = PPr(p);
        if (id == _reader.DefaultParagraphStyle) DocxXml.Child(pPr, "pStyle")?.Remove();
        else DocxXml.SetOrdered(pPr, new XElement(W + "pStyle", new XAttribute(W + "val", id)), DocxXml.ParagraphOrder);
        p.Changed();
    }

    /// <summary>Bullets or numbering on, or off when every paragraph already has that kind.</summary>
    public DocxEditResult ToggleList(int first, int last, bool numbered)
    {
        bool Matches(DocxEditParagraph p) => ListFormatOf(p) is { } f && (f == "bullet") != numbered;
        var all = Enumerable.Range(first, last - first + 1).All(i => Matches(_paragraphs[i]));
        int? numId = all ? null : ListFor(first, numbered);

        var result = ForParagraphs(first, last, (p, _) =>
        {
            if (numId is null) RemoveNumbering(p);
            else
            {
                var level = int.TryParse(DocxXml.Attr(DocxXml.Child(DocxXml.Child(DocxXml.Child(p.Element, "pPr"), "numPr"), "ilvl")), out var l) ? l : 0;
                SetNumbering(p, numId.Value, level);
            }
        });
        _reader = new DocxReader(_parts, default, editLayout: true);
        return result with { Relayout = true };
    }

    /// <summary>Tab and Shift+Tab: a list item moves a level; another paragraph its left indent by half an inch.</summary>
    public DocxEditResult Indent(int first, int last, int delta)
    {
        var relayout = false;
        var result = ForParagraphs(first, last, (p, r) =>
        {
            var numPr = DocxXml.Child(DocxXml.Child(p.Element, "pPr"), "numPr");
            if (p.IsList)
            {
                var level = int.TryParse(DocxXml.Attr(DocxXml.Child(numPr, "ilvl")), out var l) ? l : 0;
                var numId = int.TryParse(DocxXml.Attr(DocxXml.Child(numPr, "numId")), out var n) ? n : ListNumIdFromStyle(p);
                if (numId is { } id) SetNumbering(p, id, Math.Clamp(level + delta, 0, 8));
                relayout = true;
                return;
            }
            var ind = DocxXml.Child(PPr(p), "ind") ?? new XElement(W + "ind");
            var name = ind.Attribute(W + "start") is not null ? "start" : "left";
            var current = int.TryParse(DocxXml.Attr(ind, name), out var twips) ? twips : 0;
            ind.SetAttributeValue(W + name, Math.Max(0, current + 720 * delta).ToString(CultureInfo.InvariantCulture));
            DocxXml.SetOrdered(PPr(p), ind, DocxXml.ParagraphOrder);
            r.Reformat(p);
        });
        return result with { Relayout = result.Relayout || relayout };
    }

    private int? ListNumIdFromStyle(DocxEditParagraph p)
    {
        var styleId = StyleIdOf(p);
        var style = styleId is null ? null : Xml(StylesPart)?.Root?.Elements().FirstOrDefault(s => DocxXml.Attr(s, "styleId") == styleId);
        return int.TryParse(DocxXml.Attr(DocxXml.Child(DocxXml.Child(DocxXml.Child(style, "pPr"), "numPr"), "numId")), out var id) ? id : null;
    }

    private DocxEditResult ForParagraphs(int first, int last, Action<DocxEditParagraph, ResultBuilder> action)
    {
        if (last < first) (first, last) = (last, first);
        var result = new ResultBuilder(new DocxPosition(last, 0));
        for (var i = first; i <= last && i < _paragraphs.Count; i++)
        {
            action(_paragraphs[i], result);
            _paragraphs[i].Changed();
        }
        var built = Done(result);
        return built;
    }

    private XElement PPr(DocxEditParagraph p)
    {
        if (DocxXml.Child(p.Element, "pPr") is { } pPr) return pPr;
        pPr = new XElement(W + "pPr");
        p.Element.AddFirst(pPr);
        return pPr;
    }

    private void SetNumbering(DocxEditParagraph p, int numId, int level)
    {
        var numPr = new XElement(W + "numPr",
            new XElement(W + "ilvl", new XAttribute(W + "val", level.ToString(CultureInfo.InvariantCulture))),
            new XElement(W + "numId", new XAttribute(W + "val", numId.ToString(CultureInfo.InvariantCulture))));
        DocxXml.SetOrdered(PPr(p), numPr, DocxXml.ParagraphOrder);
        p.Changed();
    }

    private void RemoveNumbering(DocxEditParagraph p)
    {
        // Numbering that comes from the style is switched off with numId 0; the paragraph's own is just removed.
        if (_reader.StyleNumbers(p.Element)) SetNumbering(p, 0, 0);
        else DocxXml.Child(DocxXml.Child(p.Element, "pPr"), "numPr")?.Remove();
        p.Changed();
    }

    // =====================================================================
    // Parts this editor adds to: numbering, styles, relationships
    // =====================================================================

    private const string NumberingPart = "word/numbering.xml";
    private const string StylesPart = "word/styles.xml";

    /// <summary>A list to put paragraphs in: the one just before them when it's the same kind, else a new one.</summary>
    private int ListFor(int first, bool numbered)
    {
        if (first > 0 && _paragraphs[first - 1] is var previous && ListFormatOf(previous) is { } format && (format == "bullet") != numbered &&
            int.TryParse(DocxXml.Attr(DocxXml.Child(DocxXml.Child(DocxXml.Child(previous.Element, "pPr"), "numPr"), "numId")), out var previousId))
            return previousId;

        var numbering = EnsurePart(NumberingPart, "numbering",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.numbering+xml", OfficeRels + "/numbering");
        var root = numbering.Root!;
        var w = root.Name.Namespace;

        var abstractId = root.Elements(w + "abstractNum").Select(a => int.TryParse(DocxXml.Attr(a, "abstractNumId"), out var x) ? x : 0).DefaultIfEmpty(-1).Max() + 1;
        var numId = root.Elements(w + "num").Select(n => int.TryParse(DocxXml.Attr(n, "numId"), out var x) ? x : 0).DefaultIfEmpty(0).Max() + 1;

        string[] bullets = ["•", "◦", "▪"];
        string[] formats = ["decimal", "lowerLetter", "lowerRoman"];
        var definition = new XElement(w + "abstractNum", new XAttribute(w + "abstractNumId", abstractId),
            new XElement(w + "multiLevelType", new XAttribute(w + "val", "hybridMultilevel")),
            Enumerable.Range(0, 9).Select(level => new XElement(w + "lvl", new XAttribute(w + "ilvl", level),
                new XElement(w + "start", new XAttribute(w + "val", 1)),
                new XElement(w + "numFmt", new XAttribute(w + "val", numbered ? formats[level % 3] : "bullet")),
                new XElement(w + "lvlText", new XAttribute(w + "val", numbered ? $"%{level + 1}." : bullets[level % 3])),
                new XElement(w + "lvlJc", new XAttribute(w + "val", "left")),
                new XElement(w + "pPr", new XElement(w + "ind",
                    new XAttribute(w + "left", 720 * (level + 1)), new XAttribute(w + "hanging", 360))))));

        // Schema order: every abstractNum before every num.
        if (root.Elements(w + "abstractNum").LastOrDefault() is { } lastAbstract) lastAbstract.AddAfterSelf(definition);
        else if (root.Elements(w + "num").FirstOrDefault() is { } firstNum) firstNum.AddBeforeSelf(definition);
        else root.AddFirst(definition);

        var num = new XElement(w + "num", new XAttribute(w + "numId", numId),
            new XElement(w + "abstractNumId", new XAttribute(w + "val", abstractId)));
        if (root.Elements(w + "num").LastOrDefault() is { } lastNum) lastNum.AddAfterSelf(num);
        else if (root.Elements(w + "numIdMacAtCleanup").FirstOrDefault() is { } cleanup) cleanup.AddBeforeSelf(num);
        else root.Add(num);

        _dirtyParts.Add(NumberingPart);
        return numId;
    }

    /// <summary>Adds Title or a heading the document doesn't define, based on its default style.</summary>
    private string? AddBuiltInStyle(string name)
    {
        var builtIn = BuiltInStyles.FirstOrDefault(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (builtIn.Id is null) return null;
        var styles = EnsurePart(StylesPart, "styles",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml", OfficeRels + "/styles");
        var root = styles.Root!;

        var id = builtIn.Id;
        if (root.Elements().Any(s => DocxXml.Attr(s, "styleId") == id)) return id;

        var basedOn = _reader.DefaultParagraphStyle ?? "Normal";
        var element = XElement.Parse(builtIn.Xml.Replace("{0}", basedOn, StringComparison.Ordinal));
        // Retag into the document's own namespace (strict files use another).
        foreach (var e in element.DescendantsAndSelf())
        {
            e.Name = root.Name.Namespace + e.Name.LocalName;
            foreach (var a in e.Attributes().ToList())
                if (a.Name.Namespace == WordNamespace && root.Name.Namespace != WordNamespace)
                {
                    a.Remove();
                    e.SetAttributeValue(root.Name.Namespace + a.Name.LocalName, a.Value);
                }
        }
        foreach (var a in element.Attributes().Where(a => a.IsNamespaceDeclaration).ToList()) a.Remove();
        if (DocxXml.Child(element, "basedOn") is { } b && !root.Elements().Any(s => DocxXml.Attr(s, "styleId") == basedOn))
        {
            b.Remove();
            DocxXml.Child(element, "next")?.Remove();
        }
        root.Add(element);
        _dirtyParts.Add(StylesPart);
        return id;
    }

    private XDocument EnsurePart(string name, string rootName, string contentType, string relType)
    {
        if (Xml(name) is { Root: not null } existing)
        {
            _dirtyParts.Add(name);
            return existing;
        }

        var w = W;
        var document = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(w + rootName, new XAttribute(XNamespace.Xmlns + "w", w.NamespaceName)));
        _xml[name] = document;
        _dirtyParts.Add(name);

        XNamespace t = TypesNamespace;
        var types = Xml(ContentTypes);
        if (types?.Root is null)
        {
            types = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(t + "Types"));
            _xml[ContentTypes] = types;
        }
        types.Root!.Add(new XElement(t + "Override",
            new XAttribute("PartName", "/" + name), new XAttribute("ContentType", contentType)));
        _dirtyParts.Add(ContentTypes);

        var rels = Xml(DocumentRels);
        if (rels?.Root is null)
        {
            rels = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(XName.Get("Relationships", RelNamespace)));
            _xml[DocumentRels] = rels;
        }
        var ids = rels.Root!.Elements().Select(e => DocxXml.Attr(e, "Id")).ToHashSet();
        var next = 1;
        while (ids.Contains("rIdGd" + next)) next++;
        rels.Root.Add(new XElement(XName.Get("Relationship", RelNamespace),
            new XAttribute("Id", "rIdGd" + next), new XAttribute("Type", relType),
            new XAttribute("Target", name["word/".Length..])));
        _dirtyParts.Add(DocumentRels);
        _rels = null;
        return document;
    }

    // =====================================================================
    // Undo, and saving
    // =====================================================================

    /// <summary>The editable parts as they are now, to go back to.</summary>
    public sealed class Snapshot
    {
        internal Snapshot(Dictionary<string, XElement?> roots, HashSet<string> dirty, int version)
        {
            Roots = roots;
            Dirty = dirty;
            Version = version;
        }

        internal Dictionary<string, XElement?> Roots { get; }
        internal HashSet<string> Dirty { get; }
        internal int Version { get; }
    }

    private static readonly string[] EditableParts = [DocumentPart, NumberingPart, StylesPart, DocumentRels, ContentTypes];

    public Snapshot TakeSnapshot() => new(
        EditableParts.ToDictionary(n => n, n => Xml(n)?.Root is { } root ? new XElement(root) : null, StringComparer.OrdinalIgnoreCase),
        new HashSet<string>(_dirtyParts, StringComparer.OrdinalIgnoreCase),
        _version);

    /// <summary>Puts the parts back as they were. <see cref="Paragraphs"/> is stale until the next <see cref="Layout"/>.</summary>
    public void Restore(Snapshot snapshot)
    {
        foreach (var (name, root) in snapshot.Roots)
        {
            if (root is null)
            {
                // Created after the snapshot: gone again.
                if (!_raw.ContainsKey(name)) _xml.Remove(name);
                continue;
            }
            var document = Xml(name);
            if (document?.Root is not null) document.Root.ReplaceWith(new XElement(root));
            else _xml[name] = new XDocument(new XDeclaration("1.0", "UTF-8", "yes"), new XElement(root));
        }
        _dirtyParts.Clear();
        _dirtyParts.UnionWith(snapshot.Dirty);
        _version = snapshot.Version;
        _rels = null;
        _byElement.Clear();
        _paragraphs.Clear();
    }

    /// <summary>Writes the package: changed parts rewritten, the rest copied byte for byte.</summary>
    public void Save(string path)
    {
        var written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _dirtyParts.Append(DocumentPart).Distinct(StringComparer.OrdinalIgnoreCase))
            if (Xml(name) is { } document) written[name] = Serialize(document);

        var temp = path + ".grepdesk-save";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                // [Content_Types].xml first, as Word writes it; the rest in their old order.
                var names = _order.Concat(written.Keys.Where(k => !_raw.ContainsKey(k)))
                    .OrderBy(n => string.Equals(n, ContentTypes, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();
                foreach (var name in names)
                {
                    var bytes = written.TryGetValue(name, out var fresh) ? fresh : _raw[name];
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    if (_times.TryGetValue(name, out var time) && !written.ContainsKey(name)) entry.LastWriteTime = time;
                    using var output = entry.Open();
                    output.Write(bytes);
                }
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }

        foreach (var (name, bytes) in written)
        {
            if (!_raw.ContainsKey(name)) _order.Add(name);
            _raw[name] = bytes;
        }
        _savedVersion = _version;
    }

    private static byte[] Serialize(XDocument document)
    {
        using var memory = new MemoryStream();
        var settings = new XmlWriterSettings { Encoding = new UTF8Encoding(false), Indent = false };
        using (var writer = XmlWriter.Create(memory, settings)) document.Save(writer);
        return memory.ToArray();
    }

    private XDocument? Xml(string name)
    {
        if (_xml.TryGetValue(name, out var cached)) return cached;
        XDocument? document = null;
        if (_raw.TryGetValue(name, out var bytes))
        {
            try
            {
                using var stream = new MemoryStream(bytes);
                document = XDocument.Load(stream, LoadOptions.PreserveWhitespace);
            }
            catch (XmlException)
            {
            }
        }
        _xml[name] = document;
        return document;
    }

    private DocxEditResult Done(ResultBuilder result)
    {
        _version++;
        _dirtyParts.Add(DocumentPart);
        return result.Build();
    }

    private sealed class ResultBuilder(DocxPosition caret)
    {
        public DocxPosition Caret = caret;
        public bool Relayout;
        private readonly List<DocxEditParagraph> _retext = [];
        private readonly List<DocxEditParagraph> _reformat = [];
        public readonly List<(DocxEditParagraph, DocxEditParagraph)> Inserted = [];
        public readonly List<DocxEditParagraph> Removed = [];

        public void Retext(DocxEditParagraph p) { if (!_retext.Contains(p)) _retext.Add(p); }
        public void Reformat(DocxEditParagraph p) { if (!_reformat.Contains(p)) _reformat.Add(p); }

        public void Add(ResultBuilder other)
        {
            Caret = other.Caret;
            Relayout |= other.Relayout;
            foreach (var p in other._retext) Retext(p);
            foreach (var p in other._reformat) Reformat(p);
            Inserted.AddRange(other.Inserted);
            Removed.AddRange(other.Removed);
        }

        public DocxEditResult Build() => new(Caret, Relayout,
            _retext.Where(p => !Removed.Contains(p)).ToList(), _reformat.Where(p => !Removed.Contains(p)).ToList(),
            Inserted.Where(x => !Removed.Contains(x.Item1)).ToList(), Removed.ToList());
    }

    private sealed class Parts(DocxEditDocument owner) : IDocxParts
    {
        public XDocument? Xml(string name) => owner.Xml(name);
        public long Length(string name) => owner._raw.TryGetValue(name, out var b) ? b.Length : -1;
        public byte[]? Bytes(string name) => owner._raw.GetValueOrDefault(name);
    }
}

/// <summary>Small LINQ to XML helpers for WordprocessingML, by local name so strict files work too.</summary>
internal static class DocxXml
{
    public static XElement? Child(XElement? e, string name) => e?.Elements().FirstOrDefault(x => x.Name.LocalName == name);

    public static string? Attr(XElement? e, string name = "val") =>
        e?.Attributes().FirstOrDefault(a => a.Name.LocalName == name)?.Value;

    public static bool SameProperties(XElement? a, XElement? b) =>
        (a is null || !a.HasElements) && (b is null || !b.HasElements) || a is not null && b is not null && XNode.DeepEquals(a, b);

    /// <summary>Child order the schema requires in a paragraph's properties: Word rejects files that break it.</summary>
    public static readonly string[] ParagraphOrder =
    [
        "pStyle", "keepNext", "keepLines", "pageBreakBefore", "framePr", "widowControl", "numPr", "suppressLineNumbers",
        "pBdr", "shd", "tabs", "suppressAutoHyphens", "kinsoku", "wordWrap", "overflowPunct", "topLinePunct", "autoSpaceDE",
        "autoSpaceDN", "bidi", "adjustRightInd", "snapToGrid", "spacing", "ind", "contextualSpacing", "mirrorIndents",
        "suppressOverlap", "jc", "textDirection", "textAlignment", "textboxTightWrap", "outlineLvl", "divId", "cnfStyle",
        "rPr", "sectPr", "pPrChange",
    ];

    /// <summary>The same for a run's properties.</summary>
    public static readonly string[] RunOrder =
    [
        "rStyle", "rFonts", "b", "bCs", "i", "iCs", "caps", "smallCaps", "strike", "dstrike", "outline", "shadow", "emboss",
        "imprint", "noProof", "snapToGrid", "vanish", "webHidden", "color", "spacing", "w", "kern", "position", "sz", "szCs",
        "highlight", "u", "effect", "bdr", "shd", "fitText", "vertAlign", "rtl", "cs", "em", "lang", "eastAsianLayout",
        "specVanish", "oMath", "rPrChange",
    ];

    /// <summary>Puts <paramref name="element"/> in <paramref name="parent"/>, replacing one of its name, where the schema wants it.</summary>
    public static void SetOrdered(XElement parent, XElement element, string[] order)
    {
        var name = element.Name.LocalName;
        Child(parent, name)?.Remove();
        var rank = Array.IndexOf(order, name);
        if (rank < 0) rank = order.Length;
        var before = parent.Elements().FirstOrDefault(e =>
        {
            var r = Array.IndexOf(order, e.Name.LocalName);
            return (r < 0 ? order.Length : r) > rank;
        });
        if (before is not null) before.AddBeforeSelf(element);
        else parent.Add(element);
    }

    public static void SetOrdered(XElement parent, XName name, string[] order) => SetOrdered(parent, new XElement(name), order);

    /// <summary>A copy of the run properties with one change made; null when nothing is left.</summary>
    public static XElement? ApplyChange(XElement? rPr, DocxRunChange change)
    {
        if (change is DocxRunChange.Clear) return null;

        var w = rPr?.Name.Namespace ?? (XNamespace)"http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var props = rPr is null ? new XElement(w + "rPr") : new XElement(rPr);
        XElement Val(string name, string value) => new(w + name, new XAttribute(w + "val", value));
        XElement Toggle(string name, bool on) => on ? new XElement(w + name) : Val(name, "0");
        void Set(XElement e) => SetOrdered(props, e, RunOrder);
        void Remove(string name) => Child(props, name)?.Remove();

        switch (change)
        {
            case DocxRunChange.Bold b: Set(Toggle("b", b.On)); Set(Toggle("bCs", b.On)); break;
            case DocxRunChange.Italic i: Set(Toggle("i", i.On)); Set(Toggle("iCs", i.On)); break;
            case DocxRunChange.Strike s: Set(Toggle("strike", s.On)); Remove("dstrike"); break;
            case DocxRunChange.Underline u: Set(Val("u", u.On ? "single" : "none")); break;
            case DocxRunChange.Size size:
                var halfPoints = Math.Clamp((int)Math.Round(size.Points * 2), 2, 3276).ToString(CultureInfo.InvariantCulture);
                Set(Val("sz", halfPoints));
                Set(Val("szCs", halfPoints));
                break;
            case DocxRunChange.Font font:
                var fonts = Child(props, "rFonts") ?? new XElement(w + "rFonts");
                foreach (var a in fonts.Attributes().Where(a => a.Name.LocalName.EndsWith("Theme", StringComparison.Ordinal)).ToList()) a.Remove();
                fonts.SetAttributeValue(w + "ascii", font.Name);
                fonts.SetAttributeValue(w + "hAnsi", font.Name);
                fonts.SetAttributeValue(w + "cs", font.Name);
                Set(fonts);
                break;
            case DocxRunChange.Color color:
                Set(Val("color", color.Hex is { Length: 7 } hex ? hex[1..].ToUpperInvariant() : "auto"));
                break;
            case DocxRunChange.Highlight highlight:
                if (highlight.Name is null) { Remove("highlight"); Remove("shd"); }
                else Set(Val("highlight", highlight.Name));
                break;
        }
        return props.HasElements ? props : null;
    }

    /// <summary>A paragraph mark's properties as a run's: the same, less what only a mark can have.</summary>
    public static XElement? MarkToRun(XElement mark)
    {
        var copy = new XElement(mark);
        foreach (var name in new[] { "ins", "del", "moveFrom", "moveTo", "rPrChange" }) Child(copy, name)?.Remove();
        return copy.HasElements ? copy : null;
    }

    /// <summary>Run properties without a hyperlink's look, for text typed after a link.</summary>
    public static XElement? WithoutLinkLook(XElement? rPr)
    {
        if (rPr is null) return null;
        var copy = new XElement(rPr);
        if (Attr(Child(copy, "rStyle")) is { } style && style.Contains("link", StringComparison.OrdinalIgnoreCase))
            Child(copy, "rStyle")!.Remove();
        return copy.HasElements ? copy : null;
    }
}
