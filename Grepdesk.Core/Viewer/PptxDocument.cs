using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Grepdesk.Core.Viewer;

public enum PptxPlaceholderKind
{
    None,
    Title,
    CenterTitle,
    Subtitle,
    Body,
    Footer
}

public sealed record PptxRun(
    string Text,
    bool Bold,
    bool Italic,
    bool Underline,
    double FontSize,
    string? Color,
    string? FontFamily);

public sealed record PptxParagraph(
    IReadOnlyList<PptxRun> Runs,
    DocxAlignment Alignment,
    string? Bullet,
    int Level)
{
    public string PlainText => string.Concat(Runs.Select(r => r.Text));

    /// <summary>Left margin of the paragraph in points (<c>marL</c>), or null when nothing sets one.</summary>
    public double? MarginLeft { get; init; }

    /// <summary>First-line indent in points (<c>indent</c>; negative = hanging, where the bullet sits).</summary>
    public double? Indent { get; init; }

    public string? BulletColor { get; init; }
}

public abstract record PptxShape(double X, double Y, double Width, double Height);

public enum PptxAnchor { Top, Middle, Bottom }

public sealed record PptxLineShape(
    double X,
    double Y,
    double Width,
    double Height,
    string Color,
    double Thickness,
    bool FlipH,
    bool FlipV) : PptxShape(X, Y, Width, Height);

public sealed record PptxTextShape(
    int EditableIndex,
    string ShapeName,
    PptxPlaceholderKind Placeholder,
    double X,
    double Y,
    double Width,
    double Height,
    string? FillColor,
    string? BorderColor,
    IReadOnlyList<PptxParagraph> Paragraphs) : PptxShape(X, Y, Width, Height)
{
    public string PlainText => string.Join('\n', Paragraphs.Select(p => p.PlainText));
    public PptxAnchor Anchor { get; init; } = PptxAnchor.Top;
    public double InsetLeft { get; init; } = 7.2;
    public double InsetTop { get; init; } = 3.6;
    public double InsetRight { get; init; } = 7.2;
    public double InsetBottom { get; init; } = 3.6;
}

public sealed record PptxImageShape(
    byte[] Data,
    double X,
    double Y,
    double Width,
    double Height) : PptxShape(X, Y, Width, Height);

public sealed record PptxTableCell(
    int Row,
    int Column,
    int RowSpan,
    int ColumnSpan,
    string Text,
    bool Bold,
    string? FillColor);

public sealed record PptxTableShape(
    double X,
    double Y,
    double Width,
    double Height,
    IReadOnlyList<double> ColumnWidths,
    int RowCount,
    int ColumnCount,
    IReadOnlyList<PptxTableCell> Cells) : PptxShape(X, Y, Width, Height);

public sealed class PptxSlide
{
    public required int Number { get; init; }
    public required string PartPath { get; init; }
    public string? NotesPartPath { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
    public required string BackgroundColor { get; init; }
    public required IReadOnlyList<PptxShape> Shapes { get; init; }
    /// <summary>Non-placeholder shapes inherited from the layout and master (logos, rules), drawn behind the slide's own shapes. Not part of the text.</summary>
    public IReadOnlyList<PptxShape> Backdrop { get; init; } = [];
    public required string Notes { get; init; }

    public string Title
    {
        get
        {
            foreach (var shape in Shapes.OfType<PptxTextShape>())
            {
                if (shape.Placeholder is PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle)
                {
                    var text = shape.PlainText.Trim();
                    if (text.Length > 0) return FirstLine(text);
                }
            }
            foreach (var shape in Shapes.OfType<PptxTextShape>())
            {
                var text = shape.PlainText.Trim();
                if (text.Length > 0) return FirstLine(text);
            }
            return "";
        }
    }

    private static string FirstLine(string s)
    {
        var idx = s.IndexOfAny(['\r', '\n']);
        return idx >= 0 ? s[..idx].Trim() : s;
    }
}

/// <summary>
/// Reads a PowerPoint presentation (.pptx or .ppt) for slide display and text extraction:
/// slides in presentation order, slide geometry, theme/scheme colors and fonts, layout
/// placeholder inheritance, text boxes, bullet lists, tables, pictures, and speaker notes.
/// </summary>
public sealed class PptxDocument
{
    public required double SlideWidth { get; init; }
    public required double SlideHeight { get; init; }
    public required IReadOnlyList<PptxSlide> Slides { get; init; }

    public static PptxDocument Load(string path, CancellationToken ct = default)
    {
        if (IsBinaryPowerPoint(path))
            return PptReader.Read(path, ct);

        using var zip = ZipFile.OpenRead(path);
        return new PptxReader(new ZipPackageParts(zip), ct).Read();
    }

    /// <summary>Only OpenXML .pptx packages can be edited in place; legacy .ppt files are read-only.</summary>
    public static bool CanEdit(string path) =>
        string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase) && !IsBinaryPowerPoint(path);

    private static bool IsBinaryPowerPoint(string path)
    {
        if (!File.Exists(path))
            return string.Equals(Path.GetExtension(path), ".ppt", StringComparison.OrdinalIgnoreCase);

        Span<byte> header = stackalloc byte[8];
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var read = fs.Read(header);
        if (CompoundFile.HasSignature(header[..read]))
            return true;
        return string.Equals(Path.GetExtension(path), ".ppt", StringComparison.OrdinalIgnoreCase) &&
               !(read >= 2 && header[0] == (byte)'P' && header[1] == (byte)'K');
    }

    /// <summary>Formats all slides (or a 1-based slide range) as structured plain text.</summary>
    public string ToPlainText((int Start, int End)? slideRange = null)
    {
        var sb = new StringBuilder();
        var startSlide = slideRange.HasValue ? Math.Max(1, slideRange.Value.Start) : 1;
        var endSlide = slideRange.HasValue ? Math.Min(Slides.Count, slideRange.Value.End) : Slides.Count;
        var showHeaders = Slides.Count > 1 || slideRange.HasValue;

        for (var i = startSlide - 1; i < endSlide && i < Slides.Count; i++)
        {
            var slide = Slides[i];
            if (sb.Length > 0) sb.Append('\n');
            if (showHeaders)
            {
                var title = slide.Title;
                if (title.Length > 0)
                    sb.Append($"--- Slide {slide.Number}: {title} ---\n");
                else
                    sb.Append($"--- Slide {slide.Number} ---\n");
            }

            foreach (var shape in slide.Shapes)
            {
                switch (shape)
                {
                    case PptxTextShape textShape:
                        foreach (var para in textShape.Paragraphs)
                        {
                            var line = para.PlainText.Trim();
                            if (line.Length == 0) continue;
                            if (para.Bullet is { Length: > 0 } bullet)
                                sb.Append(new string(' ', para.Level * 2)).Append(bullet).Append(' ').Append(line).Append('\n');
                            else
                                sb.Append(line).Append('\n');
                        }
                        break;

                    case PptxTableShape tableShape:
                        for (var r = 0; r < tableShape.RowCount; r++)
                        {
                            var rowCells = tableShape.Cells.Where(c => c.Row == r).OrderBy(c => c.Column).Select(c => c.Text.Replace('\n', ' ').Trim()).ToList();
                            if (rowCells.Any(c => c.Length > 0))
                                sb.Append(string.Join('\t', rowCells)).Append('\n');
                        }
                        break;
                }
            }

            if (!string.IsNullOrWhiteSpace(slide.Notes))
            {
                sb.Append("[Notes] ").Append(slide.Notes.Trim()).Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}

internal sealed class PptxReader(IPackageParts parts, CancellationToken ct)
{
    private const double EmuPerPoint = 12700.0;
    private const int MaxImageBytes = 30 * 1024 * 1024;
    private static readonly HashSet<string> ImageExtensions = new([".png", ".jpg", ".jpeg", ".jpe", ".gif", ".bmp", ".webp", ".emf", ".wmf"], StringComparer.OrdinalIgnoreCase);

    private sealed record PlaceholderGeom(double X, double Y, double Width, double Height);

    private readonly Dictionary<string, string> _themeColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, byte[]?> _imageCache = new(StringComparer.OrdinalIgnoreCase);
    private string _majorFont = "Calibri Light";
    private string _minorFont = "Calibri";
    private double _slideWidth = 960.0;
    private double _slideHeight = 540.0;

    public PptxDocument Read()
    {
        ReadTheme();
        var slidePaths = ReadPresentationAndSlides();
        if (slidePaths.Count == 0)
            throw new InvalidDataException("No slides found in .pptx archive.");

        var slides = new List<PptxSlide>(slidePaths.Count);
        for (var i = 0; i < slidePaths.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadSlide(i + 1, slidePaths[i]) is { } slide)
                slides.Add(slide);
        }

        return new PptxDocument
        {
            SlideWidth = _slideWidth,
            SlideHeight = _slideHeight,
            Slides = slides
        };
    }

    private void ReadTheme()
    {
        var themeDoc = parts.Xml("ppt/theme/theme1.xml")
            ?? (parts.Entries.FirstOrDefault(e => e.StartsWith("ppt/theme/", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)) is { } firstTheme
                ? parts.Xml(firstTheme)
                : null);
        if (themeDoc?.Root is null) return;

        var clrScheme = themeDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
        if (clrScheme is not null)
        {
            foreach (var el in clrScheme.Elements())
            {
                var srgb = el.Elements().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
                var sys = el.Elements().FirstOrDefault(e => e.Name.LocalName == "sysClr");
                var hex = Attr(srgb, "val") ?? Attr(sys, "lastClr");
                if (hex is { Length: 6 })
                    _themeColors[el.Name.LocalName] = "#" + hex.ToUpperInvariant();
            }
            if (_themeColors.TryGetValue("dk1", out var dk1)) _themeColors["tx1"] = dk1;
            if (_themeColors.TryGetValue("lt1", out var lt1)) _themeColors["bg1"] = lt1;
            if (_themeColors.TryGetValue("dk2", out var dk2)) _themeColors["tx2"] = dk2;
            if (_themeColors.TryGetValue("lt2", out var lt2)) _themeColors["bg2"] = lt2;
        }

        var fontScheme = themeDoc.Descendants().FirstOrDefault(e => e.Name.LocalName == "fontScheme");
        if (fontScheme is not null)
        {
            var majorLatin = fontScheme.Elements().FirstOrDefault(e => e.Name.LocalName == "majorFont")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "latin");
            var minorLatin = fontScheme.Elements().FirstOrDefault(e => e.Name.LocalName == "minorFont")
                ?.Elements().FirstOrDefault(e => e.Name.LocalName == "latin");
            if (Attr(majorLatin, "typeface") is { Length: > 0 } maj) _majorFont = maj;
            if (Attr(minorLatin, "typeface") is { Length: > 0 } min) _minorFont = min;
        }
    }

    private List<string> ReadPresentationAndSlides()
    {
        var result = new List<string>();
        var pres = parts.Xml("ppt/presentation.xml");
        if (pres?.Root is not null)
        {
            var sldSz = pres.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sldSz");
            if (sldSz is not null)
            {
                var cx = Emu(Attr(sldSz, "cx"));
                var cy = Emu(Attr(sldSz, "cy"));
                if (cx > 100 && cy > 100)
                {
                    _slideWidth = cx;
                    _slideHeight = cy;
                }
            }

            var rels = ReadRels("ppt/_rels/presentation.xml.rels", "ppt/");
            var sldIdLst = pres.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sldIdLst");
            if (sldIdLst is not null)
            {
                foreach (var sldId in sldIdLst.Elements().Where(e => e.Name.LocalName == "sldId"))
                {
                    var rId = Attr(sldId, "id");
                    if (rId is not null && rels.TryGetValue(rId, out var target) && parts.Xml(target) is not null)
                        result.Add(target);
                }
            }
        }

        if (result.Count == 0)
        {
            result = parts.Entries
                .Where(e => e.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(SlideNumberKey)
                .ToList();
        }

        return result;
    }

    private static int SlideNumberKey(string path)
    {
        var match = Regex.Match(Path.GetFileNameWithoutExtension(path), @"(\d+)$");
        return match.Success && int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : int.MaxValue;
    }

    private PptxSlide? ReadSlide(int number, string slidePath)
    {
        var doc = parts.Xml(slidePath);
        if (doc?.Root is null) return null;

        var relsPath = RelsPathFor(slidePath);
        var baseDir = BaseDirFor(slidePath);
        var rels = ReadRels(relsPath, baseDir);

        // Locate layout and master for inherited placeholders and backgrounds
        XDocument? layoutDoc = null;
        XDocument? masterDoc = null;
        string? layoutPath = null, masterPath = null;
        foreach (var (_, target) in rels)
        {
            if (target.Contains("slideLayouts/", StringComparison.OrdinalIgnoreCase))
            {
                layoutDoc = parts.Xml(target);
                layoutPath = target;
                var layoutRels = ReadRels(RelsPathFor(target), BaseDirFor(target));
                foreach (var (_, mTarget) in layoutRels)
                {
                    if (mTarget.Contains("slideMasters/", StringComparison.OrdinalIgnoreCase))
                    {
                        masterDoc = parts.Xml(mTarget);
                        masterPath = mTarget;
                        break;
                    }
                }
                break;
            }
        }

        var bgColor = ReadBackgroundColor(doc.Root)
            ?? ReadBackgroundColor(layoutDoc?.Root)
            ?? ReadBackgroundColor(masterDoc?.Root)
            ?? "#FFFFFF";

        var spTree = doc.Root.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
        var shapes = new List<PptxShape>();
        var editableIndex = 0;
        var bodyOrder = 0;

        if (spTree is not null)
        {
            ReadShapeTree(spTree, rels, layoutDoc?.Root, masterDoc?.Root, shapes, ref editableIndex, ref bodyOrder);
        }

        var backdrop = new List<PptxShape>();
        var slideHides = Attr(doc.Root, "showMasterSp") is "0" or "false";
        var layoutHidesMaster = Attr(layoutDoc?.Root, "showMasterSp") is "0" or "false";
        if (!slideHides && !layoutHidesMaster && masterDoc is not null && masterPath is not null)
            ReadBackdrop(masterDoc, masterPath, backdrop);
        if (!slideHides && layoutDoc is not null && layoutPath is not null)
            ReadBackdrop(layoutDoc, layoutPath, backdrop);

        string? notesPartPath = null;
        var notesText = "";
        foreach (var (_, target) in rels)
        {
            if (target.Contains("notesSlides/", StringComparison.OrdinalIgnoreCase))
            {
                notesPartPath = target;
                notesText = ReadNotesSlide(target);
                break;
            }
        }

        return new PptxSlide
        {
            Number = number,
            PartPath = slidePath,
            NotesPartPath = notesPartPath,
            Width = _slideWidth,
            Height = _slideHeight,
            BackgroundColor = bgColor,
            Shapes = shapes,
            Backdrop = backdrop,
            Notes = notesText
        };
    }

    private void ReadBackdrop(XDocument part, string partPath, List<PptxShape> into)
    {
        var tree = part.Root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
        if (tree is null) return;
        var rels = ReadRels(RelsPathFor(partPath), BaseDirFor(partPath));
        int edit = 0, body = 0;
        ReadShapeTree(tree, rels, null, null, into, ref edit, ref body, decor: true);
    }

    private static bool IsPlaceholder(XElement el) =>
        el.Descendants().Any(e => e.Name.LocalName == "ph");

    private static bool IsLineGeometry(XElement sp)
    {
        var prst = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == "prstGeom");
        return Attr(prst, "prst") is "line" or "straightConnector1" or "bentConnector2" or "bentConnector3";
    }

    private void ReadShapeTree(
        XElement spTree,
        Dictionary<string, string> rels,
        XElement? layoutRoot,
        XElement? masterRoot,
        List<PptxShape> shapes,
        ref int editableIndex,
        ref int bodyOrder,
        bool decor = false)
    {
        foreach (var el in spTree.Elements())
        {
            ct.ThrowIfCancellationRequested();
            switch (el.Name.LocalName)
            {
                case "cxnSp":
                    if (ReadLineShape(el) is { } cxn) shapes.Add(cxn);
                    break;

                case "sp" when IsLineGeometry(el) && !HasTxBody(el):
                    if (ReadLineShape(el) is { } ln) shapes.Add(ln);
                    break;

                case "sp":
                    if (decor && IsPlaceholder(el)) break;
                    var myEditIdx = !decor && HasTxBody(el) ? editableIndex++ : -1;
                    if (ReadTextShape(el, myEditIdx, layoutRoot, masterRoot, ref bodyOrder) is { } textShape)
                        shapes.Add(textShape);
                    break;

                case "pic":
                    if (decor && IsPlaceholder(el)) break;
                    if (ReadPictureShape(el, rels) is { } picShape)
                        shapes.Add(picShape);
                    break;

                case "graphicFrame":
                    if (ReadTableShape(el) is { } tableShape)
                        shapes.Add(tableShape);
                    else if (!decor && el.Descendants().FirstOrDefault(e => e.Name.LocalName == "pic") is { } fallbackPic
                             && ReadPictureShape(fallbackPic, rels) is { } oleShape)
                        shapes.Add(oleShape); // embedded chart / OLE object: its preview picture
                    break;

                case "grpSp":
                    ReadShapeTree(el, rels, layoutRoot, masterRoot, shapes, ref editableIndex, ref bodyOrder, decor);
                    break;
            }
        }
    }

    internal static bool HasTxBody(XElement sp) =>
        sp.Elements().Any(e => e.Name.LocalName == "txBody");

    private PptxTextShape? ReadTextShape(
        XElement sp,
        int editableIndex,
        XElement? layoutRoot,
        XElement? masterRoot,
        ref int bodyOrder)
    {
        var nvSpPr = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "nvSpPr");
        var cNvPr = nvSpPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "cNvPr");
        var shapeName = Attr(cNvPr, "name") ?? $"Shape {editableIndex + 1}";

        var nvPr = nvSpPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "nvPr");
        var phEl = nvPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "ph");
        var phType = Attr(phEl, "type");
        var phIdx = Attr(phEl, "idx");

        // Skip slide number / date / header placeholders unless they contain custom non-field text
        if (phType is "sldNum" or "dt" or "hdr")
            return null;

        var phKind = ClassifyPlaceholder(phType, phIdx, shapeName);
        var spPr = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
        var (layoutSp, masterSp) = FindPlaceholderChain(layoutRoot, masterRoot, phType, phIdx);
        var geom = ReadTransform(spPr)
            ?? ReadTransform(layoutSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr"))
            ?? ReadTransform(masterSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr"))
            ?? DefaultPlaceholderGeometry(phKind, ref bodyOrder);

        var fillColor = ResolveSolidFill(spPr);
        var lnEl = spPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "ln");
        var borderColor = ResolveSolidFill(lnEl);
        if (phEl is null)
        {
            if (fillColor is null && !HasChild(spPr, "noFill") && !HasChild(spPr, "gradFill"))
                fillColor = ResolveStyleColor(sp, "fillRef");
            if (borderColor is null && !HasChild(lnEl, "noFill"))
                borderColor = ResolveStyleColor(sp, "lnRef");
        }

        var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
        if (txBody is null)
        {
            if (fillColor is null && borderColor is null) return null;
            return new PptxTextShape(editableIndex, shapeName, phKind, geom.X, geom.Y, geom.Width, geom.Height, fillColor, borderColor, []);
        }

        var defaultSize = phKind switch
        {
            PptxPlaceholderKind.CenterTitle => 40.0,
            PptxPlaceholderKind.Title => 32.0,
            PptxPlaceholderKind.Subtitle => 20.0,
            PptxPlaceholderKind.Footer => 11.0,
            _ => 18.0
        };
        var styleFontColor = ResolveStyleColor(sp, "fontRef");
        var slideBodyPr = txBody.Elements().FirstOrDefault(e => e.Name.LocalName == "bodyPr");
        var bodyPrs = new[]
        {
            slideBodyPr,
            layoutSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")?.Elements().FirstOrDefault(e => e.Name.LocalName == "bodyPr"),
            masterSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")?.Elements().FirstOrDefault(e => e.Name.LocalName == "bodyPr"),
        };
        string? BodyAttr(string name) => bodyPrs.Select(b => Attr(b, name)).FirstOrDefault(v => v is not null);
        var anchor = BodyAttr("anchor") switch { "ctr" => PptxAnchor.Middle, "b" => PptxAnchor.Bottom, _ => PptxAnchor.Top };
        double Inset(string name, double dflt) =>
            BodyAttr(name) is { } v && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var emu) ? emu / EmuPerPoint : dflt;
        var fontScale = double.TryParse(Attr(slideBodyPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "normAutofit"), "fontScale"),
            NumberStyles.Float, CultureInfo.InvariantCulture, out var fsRaw) && fsRaw > 0 ? fsRaw / 100000.0 : 1.0;

        var defaultAlign = phKind is PptxPlaceholderKind.CenterTitle or PptxPlaceholderKind.Subtitle
            ? DocxAlignment.Center
            : DocxAlignment.Left;
        var defaultFont = phKind is PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle
            ? _majorFont
            : _minorFont;

        var paragraphs = new List<PptxParagraph>();
        var autoNumCounter = 0;

        foreach (var pEl in txBody.Elements().Where(e => e.Name.LocalName == "p"))
        {
            var pPr = pEl.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            var level = int.TryParse(Attr(pPr, "lvl"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var lvl) ? Math.Clamp(lvl, 0, 8) : 0;
            var chain = LevelStyles(layoutSp, masterSp, masterRoot, phKind, level);
            var defs = new List<XElement?> { pPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "defRPr") };
            defs.AddRange(chain.Select(c => c.Elements().FirstOrDefault(e => e.Name.LocalName == "defRPr")));
            var algnValue = Attr(pPr, "algn") ?? chain.Select(c => Attr(c, "algn")).FirstOrDefault(v => v is not null);
            var align = algnValue switch
            {
                "ctr" => DocxAlignment.Center,
                "r" => DocxAlignment.Right,
                "just" or "dist" => DocxAlignment.Justify,
                "l" => DocxAlignment.Left,
                _ => defaultAlign
            };

            var runs = new List<PptxRun>();

            foreach (var child in pEl.Elements())
            {
                var local = child.Name.LocalName;
                if (local is "r" or "fld")
                {
                    if (local == "fld" && Attr(child, "type")?.StartsWith("slidenum", StringComparison.OrdinalIgnoreCase) == true)
                        continue;

                    var rPr = child.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
                    var tEl = child.Elements().FirstOrDefault(e => e.Name.LocalName == "t");
                    if (tEl is null || tEl.Value.Length == 0) continue;

                    var run = BuildRun(tEl.Value, rPr, defs, defaultSize - level * 2, defaultFont, phKind, fontScale, styleFontColor, chain.Count > 0);
                    if (runs.Count > 0 && SameRunStyle(runs[^1], run))
                        runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
                    else
                        runs.Add(run);
                }
                else if (local == "br")
                {
                    var rPr = child.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
                    runs.Add(BuildRun("\n", rPr, defs, defaultSize - level * 2, defaultFont, phKind, fontScale, styleFontColor, chain.Count > 0));
                }
            }

            var hasText = runs.Any(r => !string.IsNullOrWhiteSpace(r.Text));
            string? bullet = null;
            string? bulletColor = null;
            double? marL = null, indent = null;
            if (hasText)
            {
                var buSource = new[] { pPr }.Concat(chain).FirstOrDefault(e => e is not null &&
                    e.Elements().Any(c => c.Name.LocalName is "buNone" or "buChar" or "buAutoNum"));
                if (buSource?.Elements().Any(e => e.Name.LocalName == "buNone") == true)
                {
                    bullet = null;
                    autoNumCounter = 0;
                }
                else if (buSource?.Elements().FirstOrDefault(e => e.Name.LocalName == "buAutoNum") is not null)
                {
                    autoNumCounter++;
                    bullet = $"{autoNumCounter}.";
                }
                else if (buSource?.Elements().FirstOrDefault(e => e.Name.LocalName == "buChar") is { } buChar)
                {
                    var ch = Attr(buChar, "char");
                    var buFont = new[] { pPr }.Concat(chain).Select(e => e?.Elements().FirstOrDefault(c => c.Name.LocalName == "buFont")).FirstOrDefault(f => f is not null);
                    bullet = string.IsNullOrEmpty(ch) ? "•" : NormalizeBulletChar(ch, Attr(buFont, "typeface"));
                }
                else if (phKind == PptxPlaceholderKind.Body && pPr?.Attribute("algn") is null)
                {
                    bullet = "•";
                }
            }

            if (hasText)
            {
                var levelProps = new[] { pPr }.Concat(chain).ToList();
                string? P(string n) => levelProps.Select(e => Attr(e, n)).FirstOrDefault(v => v is not null);
                if (double.TryParse(P("marL"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ml)) marL = ml / EmuPerPoint;
                if (double.TryParse(P("indent"), NumberStyles.Float, CultureInfo.InvariantCulture, out var ind)) indent = ind / EmuPerPoint;
                if (bullet is not null)
                {
                    var clr = levelProps.Select(e => e?.Elements().FirstOrDefault(c => c.Name.LocalName == "buClr")).FirstOrDefault(c => c is not null);
                    bulletColor = ResolveColorChild(clr);
                }
            }

            paragraphs.Add(new PptxParagraph(runs, align, bullet, level) { MarginLeft = marL, Indent = indent, BulletColor = bulletColor });
        }

        if (paragraphs.All(p => string.IsNullOrWhiteSpace(p.PlainText)) && fillColor is null && borderColor is null)
            return null;

        return new PptxTextShape(editableIndex, shapeName, phKind, geom.X, geom.Y, geom.Width, geom.Height, fillColor, borderColor, paragraphs)
        {
            Anchor = anchor,
            InsetLeft = Inset("lIns", 7.2),
            InsetTop = Inset("tIns", 3.6),
            InsetRight = Inset("rIns", 7.2),
            InsetBottom = Inset("bIns", 3.6),
        };
    }

    private static bool HasChild(XElement? el, string name) =>
        el is not null && el.Elements().Any(e => e.Name.LocalName == name);

    /// <summary>The colour a shape's style reference (fillRef / lnRef / fontRef) names, unless its idx is 0 (= none).</summary>
    private string? ResolveStyleColor(XElement shape, string refName)
    {
        var r = shape.Elements().FirstOrDefault(e => e.Name.LocalName == "style")
            ?.Elements().FirstOrDefault(e => e.Name.LocalName == refName);
        if (r is null || Attr(r, "idx") == "0") return null;
        return ResolveColorChild(r);
    }

    private PptxLineShape? ReadLineShape(XElement el)
    {
        var spPr = el.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
        var xfrm = spPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
        var off = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "off");
        var ext = xfrm?.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
        if (off is null || ext is null) return null;

        var ln = spPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "ln");
        if (HasChild(ln, "noFill")) return null;
        var color = ResolveSolidFill(ln) ?? ResolveStyleColor(el, "lnRef")
            ?? (_themeColors.TryGetValue("tx1", out var tx1) ? tx1 : "#000000");
        var thickness = double.TryParse(Attr(ln, "w"), NumberStyles.Float, CultureInfo.InvariantCulture, out var wEmu) && wEmu > 0
            ? wEmu / EmuPerPoint
            : 1.0;
        return new PptxLineShape(Emu(Attr(off, "x")), Emu(Attr(off, "y")), Emu(Attr(ext, "cx")), Emu(Attr(ext, "cy")), color, thickness,
            Attr(xfrm, "flipH") is "1" or "true", Attr(xfrm, "flipV") is "1" or "true");
    }

    private static string MasterType(string? t) => t switch
    {
        "ctrTitle" or "title" => "title",
        "subTitle" or "body" or "obj" or null => "body",
        _ => t
    };

    private static XElement? FindPlaceholderShape(XElement? root, string? type, string? idx)
    {
        if (root is null || (type is null && idx is null)) return null;
        XElement? byType = null;
        foreach (var sp in root.Descendants().Where(e => e.Name.LocalName == "sp"))
        {
            var ph = sp.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
            if (ph is null) continue;
            var t = Attr(ph, "type");
            if (idx is not null && Attr(ph, "idx") == idx) return sp;
            if (byType is null && type is not null && (t == type || (type is "ctrTitle" or "title" && t is "title" or "ctrTitle")))
                byType = sp;
        }
        return byType;
    }

    private static (XElement? LayoutSp, XElement? MasterSp) FindPlaceholderChain(XElement? layoutRoot, XElement? masterRoot, string? type, string? idx)
    {
        var layoutSp = FindPlaceholderShape(layoutRoot, type, idx);
        var layoutType = Attr(layoutSp?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph"), "type") ?? type;
        var masterSp = masterRoot is null ? null : FindPlaceholderShape(masterRoot, MasterType(layoutType), null);
        return (layoutSp, masterSp);
    }

    /// <summary>The lvlNpPr elements that style a paragraph at this level, most specific first: layout, master placeholder, master text styles.</summary>
    private static List<XElement> LevelStyles(XElement? layoutSp, XElement? masterSp, XElement? masterRoot, PptxPlaceholderKind kind, int level)
    {
        var name = $"lvl{Math.Clamp(level, 0, 8) + 1}pPr";
        var list = new List<XElement>();
        void Add(XElement? holder)
        {
            if (holder?.Elements().FirstOrDefault(e => e.Name.LocalName == name) is { } l) list.Add(l);
        }
        Add(layoutSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")?.Elements().FirstOrDefault(e => e.Name.LocalName == "lstStyle"));
        Add(masterSp?.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")?.Elements().FirstOrDefault(e => e.Name.LocalName == "lstStyle"));
        var styleName = kind switch
        {
            PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle => "titleStyle",
            PptxPlaceholderKind.Body or PptxPlaceholderKind.Subtitle => "bodyStyle",
            _ => "otherStyle"
        };
        var txStyles = masterRoot?.Descendants().FirstOrDefault(e => e.Name.LocalName == "txStyles");
        Add(txStyles?.Elements().FirstOrDefault(e => e.Name.LocalName == styleName));
        return list;
    }

    private PptxRun BuildRun(string text, XElement? rPr, IReadOnlyList<XElement?> defs, double fallbackSize, string fallbackFont,
        PptxPlaceholderKind phKind, double fontScale, string? styleColor, bool hasInheritedStyle)
    {
        string? A(string n) => Attr(rPr, n) ?? defs.Select(d => Attr(d, n)).FirstOrDefault(v => v is not null);
        static bool? B(string? v) => v switch { "1" or "true" => true, "0" or "false" => false, _ => null };

        var bold = B(A("b")) ?? (!hasInheritedStyle && phKind is PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle);
        var italic = B(A("i")) ?? false;
        var underline = A("u") is { } u && u is not ("none" or "0");

        var fontSize = double.TryParse(A("sz"), NumberStyles.Float, CultureInfo.InvariantCulture, out var hundredths) && hundredths > 100
            ? hundredths / 100.0
            : Math.Max(10.0, fallbackSize);
        fontSize = Math.Max(6.0, fontSize * fontScale);

        var color = ResolveSolidFill(rPr) ?? defs.Select(ResolveSolidFill).FirstOrDefault(c => c is not null) ?? styleColor;
        var latin = rPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "latin")
            ?? defs.Select(d => d?.Elements().FirstOrDefault(e => e.Name.LocalName == "latin")).FirstOrDefault(e => e is not null);
        var font = ResolveFontName(Attr(latin, "typeface")) ?? fallbackFont;

        return new PptxRun(text, bold, italic, underline, fontSize, color, font);
    }

    private static bool SameRunStyle(PptxRun a, PptxRun b) =>
        a.Bold == b.Bold && a.Italic == b.Italic && a.Underline == b.Underline &&
        Math.Abs(a.FontSize - b.FontSize) < 0.05 && a.Color == b.Color && a.FontFamily == b.FontFamily;

    private string? ResolveFontName(string? typeface)
    {
        if (string.IsNullOrWhiteSpace(typeface)) return null;
        if (typeface.StartsWith("+mj", StringComparison.OrdinalIgnoreCase)) return _majorFont;
        if (typeface.StartsWith("+mn", StringComparison.OrdinalIgnoreCase)) return _minorFont;
        return typeface;
    }

    private static string NormalizeBulletChar(string ch, string? font = null)
    {
        if (font is not null && font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase))
        {
            switch (ch[0])
            {
                case '§': return "▪";
                case 'Ø': return "➢";
                case 'ü': return "✓";
                case 'l': return "●";
                case 'n': return "■";
                case 'q': return "❑";
                case 'v': return "❖";
                case 'u': return "◆";
                case 'p': return "▪";
            }
        }
        return NormalizeBulletChar(ch);
    }

    private static string NormalizeBulletChar(string ch) => ch[0] switch
    {
        '\uF0B7' or '\uF06C' => "•",
        '\uF0A7' or '\uF0FE' => "▪",
        '\uF076' or '\uF0D8' => "▸",
        '\uF0FC' => "✓",
        _ => ch
    };

    private PptxImageShape? ReadPictureShape(XElement pic, Dictionary<string, string> rels)
    {
        var blip = pic.Descendants().FirstOrDefault(e => e.Name.LocalName == "blip");
        var embedId = Attr(blip, "embed");
        if (embedId is null || !rels.TryGetValue(embedId, out var target)) return null;
        if (!ImageExtensions.Contains(Path.GetExtension(target))) return null;

        if (!_imageCache.TryGetValue(target, out var bytes))
        {
            bytes = parts.Bytes(target);
            if (bytes is { Length: > MaxImageBytes }) bytes = null;
            _imageCache[target] = bytes;
        }
        if (bytes is null || bytes.Length == 0) return null;

        var spPr = pic.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
        var geom = ReadTransform(spPr) ?? new PlaceholderGeom(_slideWidth * 0.15, _slideHeight * 0.2, _slideWidth * 0.7, _slideHeight * 0.6);
        return new PptxImageShape(bytes, geom.X, geom.Y, geom.Width, geom.Height);
    }

    private PptxTableShape? ReadTableShape(XElement graphicFrame)
    {
        var tbl = graphicFrame.Descendants().FirstOrDefault(e => e.Name.LocalName == "tbl");
        if (tbl is null) return null;

        var xfrm = graphicFrame.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm");
        var geom = ReadTransformElement(xfrm) ?? new PlaceholderGeom(60, 120, _slideWidth - 120, _slideHeight - 180);

        var tblGrid = tbl.Elements().FirstOrDefault(e => e.Name.LocalName == "tblGrid");
        var colWidths = tblGrid?.Elements().Where(e => e.Name.LocalName == "gridCol").Select(c => Math.Max(30.0, Emu(Attr(c, "w")))).ToList() ?? [];

        var trs = tbl.Elements().Where(e => e.Name.LocalName == "tr").ToList();
        var cells = new List<PptxTableCell>();
        var maxCol = colWidths.Count;

        for (var r = 0; r < trs.Count; r++)
        {
            var tcs = trs[r].Elements().Where(e => e.Name.LocalName == "tc").ToList();
            if (tcs.Count > maxCol) maxCol = tcs.Count;
            for (var c = 0; c < tcs.Count; c++)
            {
                var tc = tcs[c];
                if (Attr(tc, "hMerge") is "1" or "true" || Attr(tc, "vMerge") is "1" or "true")
                    continue;

                var rowSpan = int.TryParse(Attr(tc, "rowSpan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rs) && rs > 1 ? rs : 1;
                var colSpan = int.TryParse(Attr(tc, "gridSpan"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cs) && cs > 1 ? cs : 1;
                var txBody = tc.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
                var text = txBody is null ? "" : ExtractPlainTextFromTxBody(txBody);
                var bold = r == 0 || tc.Descendants().Any(e => e.Name.LocalName == "rPr" && ParseBoolAttr(e, "b") == true);
                var tcPr = tc.Elements().FirstOrDefault(e => e.Name.LocalName == "tcPr");
                var fill = ResolveSolidFill(tcPr);

                cells.Add(new PptxTableCell(r, c, rowSpan, colSpan, text, bold, fill));
            }
        }

        if (colWidths.Count < maxCol)
        {
            var each = maxCol > 0 ? geom.Width / maxCol : 100.0;
            while (colWidths.Count < maxCol) colWidths.Add(each);
        }

        return new PptxTableShape(geom.X, geom.Y, geom.Width, geom.Height, colWidths, trs.Count, maxCol, cells);
    }

    private string ReadNotesSlide(string notesPartPath)
    {
        var doc = parts.Xml(notesPartPath);
        if (doc?.Root is null) return "";

        var sb = new StringBuilder();
        foreach (var sp in doc.Root.Descendants().Where(e => e.Name.LocalName == "sp"))
        {
            var ph = sp.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
            var phType = Attr(ph, "type");
            if (phType is "sldImg" or "sldNum" or "dt" or "hdr" or "ftr")
                continue;

            var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
            if (txBody is null) continue;
            var text = ExtractPlainTextFromTxBody(txBody).Trim();
            if (text.Length > 0)
            {
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(text);
            }
        }
        return sb.ToString();
    }

    private static string ExtractPlainTextFromTxBody(XElement txBody)
    {
        var sb = new StringBuilder();
        foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
        {
            if (sb.Length > 0 && sb[^1] != '\n') sb.Append('\n');
            foreach (var child in p.Elements())
            {
                var local = child.Name.LocalName;
                if (local is "r" or "fld")
                {
                    if (local == "fld" && Attr(child, "type")?.StartsWith("slidenum", StringComparison.OrdinalIgnoreCase) == true)
                        continue;
                    var t = child.Elements().FirstOrDefault(e => e.Name.LocalName == "t");
                    if (t is not null) sb.Append(t.Value);
                }
                else if (local == "br")
                {
                    sb.Append('\n');
                }
            }
        }
        return sb.ToString().Trim();
    }

    private string? ReadBackgroundColor(XElement? root)
    {
        var bg = root?.Descendants().FirstOrDefault(e => e.Name.LocalName == "bg");
        return ResolveSolidFill(bg);
    }

    private string? ResolveSolidFill(XElement? container)
    {
        if (container is null) return null;
        var solid = container.Elements().FirstOrDefault(e => e.Name.LocalName == "solidFill")
            ?? container.Elements().FirstOrDefault(e => e.Name.LocalName == "bgPr")?.Elements().FirstOrDefault(e => e.Name.LocalName == "solidFill");
        if (solid is not null) return ResolveColorChild(solid);

        var grad = container.Elements().FirstOrDefault(e => e.Name.LocalName == "gradFill")
            ?? container.Elements().FirstOrDefault(e => e.Name.LocalName == "bgPr")?.Elements().FirstOrDefault(e => e.Name.LocalName == "gradFill");
        return ResolveColorChild(grad?.Descendants().FirstOrDefault(e => e.Name.LocalName == "gs"));
    }

    private string? ResolveColorChild(XElement? parent)
    {
        if (parent is null) return null;
        var srgb = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
        if (srgb is not null && Attr(srgb, "val") is { Length: 6 } hex)
            return ApplyColorTransforms("#" + hex.ToUpperInvariant(), srgb);

        var scheme = parent.Elements().FirstOrDefault(e => e.Name.LocalName == "schemeClr");
        if (scheme is not null && Attr(scheme, "val") is { } schemeName && _themeColors.TryGetValue(schemeName, out var themeHex))
            return ApplyColorTransforms(themeHex, scheme);

        return null;
    }

    private static string ApplyColorTransforms(string hex, XElement colorEl)
    {
        var lumModEl = colorEl.Elements().FirstOrDefault(e => e.Name.LocalName == "lumMod");
        var lumOffEl = colorEl.Elements().FirstOrDefault(e => e.Name.LocalName == "lumOff");
        if (lumModEl is not null && double.TryParse(Attr(lumModEl, "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var mod))
        {
            var off = lumOffEl is not null && double.TryParse(Attr(lumOffEl, "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var o) ? o / 100000.0 : 0.0;
            var factor = mod / 100000.0;
            if (hex.Length == 7 && hex[0] == '#')
            {
                var r = Math.Clamp((int)Math.Round(Convert.ToInt32(hex[1..3], 16) * factor + 255 * off), 0, 255);
                var g = Math.Clamp((int)Math.Round(Convert.ToInt32(hex[3..5], 16) * factor + 255 * off), 0, 255);
                var b = Math.Clamp((int)Math.Round(Convert.ToInt32(hex[5..7], 16) * factor + 255 * off), 0, 255);
                return $"#{r:X2}{g:X2}{b:X2}";
            }
        }
        return hex;
    }

    private static PptxPlaceholderKind ClassifyPlaceholder(string? type, string? idx, string shapeName)
    {
        return type switch
        {
            "ctrTitle" => PptxPlaceholderKind.CenterTitle,
            "title" => PptxPlaceholderKind.Title,
            "subTitle" => PptxPlaceholderKind.Subtitle,
            "body" or "obj" => PptxPlaceholderKind.Body,
            "ftr" => PptxPlaceholderKind.Footer,
            null when idx is "0" || shapeName.Contains("Title", StringComparison.OrdinalIgnoreCase) => PptxPlaceholderKind.Title,
            null when idx is not null => PptxPlaceholderKind.Body,
            _ => PptxPlaceholderKind.None
        };
    }

    private static PlaceholderGeom? FindPlaceholderTransform(XElement? root, string? phType, string? phIdx)
    {
        if (root is null || (phType is null && phIdx is null)) return null;
        foreach (var sp in root.Descendants().Where(e => e.Name.LocalName == "sp"))
        {
            var ph = sp.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
            if (ph is null) continue;
            var t = Attr(ph, "type");
            var idx = Attr(ph, "idx");
            if ((phIdx is not null && idx == phIdx) || (phType is not null && t == phType) ||
                (phType is "ctrTitle" or "title" && t is "title" or "ctrTitle"))
            {
                var spPr = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "spPr");
                if (ReadTransform(spPr) is { } geom)
                    return geom;
            }
        }
        return null;
    }

    private PlaceholderGeom DefaultPlaceholderGeometry(PptxPlaceholderKind kind, ref int bodyOrder)
    {
        var w = _slideWidth;
        var h = _slideHeight;
        switch (kind)
        {
            case PptxPlaceholderKind.CenterTitle:
                return new PlaceholderGeom(w * 0.08, h * 0.25, w * 0.84, h * 0.25);
            case PptxPlaceholderKind.Title:
                return new PlaceholderGeom(w * 0.06, h * 0.07, w * 0.88, h * 0.16);
            case PptxPlaceholderKind.Subtitle:
                return new PlaceholderGeom(w * 0.10, h * 0.52, w * 0.80, h * 0.20);
            case PptxPlaceholderKind.Footer:
                return new PlaceholderGeom(w * 0.06, h * 0.90, w * 0.88, h * 0.06);
            default:
                var yOffset = h * 0.25 + (bodyOrder++ * 40.0);
                return new PlaceholderGeom(w * 0.06, Math.Min(yOffset, h * 0.75), w * 0.88, Math.Max(h * 0.62, 100.0));
        }
    }

    private static PlaceholderGeom? ReadTransform(XElement? spPr) =>
        ReadTransformElement(spPr?.Elements().FirstOrDefault(e => e.Name.LocalName == "xfrm"));

    private static PlaceholderGeom? ReadTransformElement(XElement? xfrm)
    {
        if (xfrm is null) return null;
        var off = xfrm.Elements().FirstOrDefault(e => e.Name.LocalName == "off");
        var ext = xfrm.Elements().FirstOrDefault(e => e.Name.LocalName == "ext");
        if (off is null || ext is null) return null;

        var x = Emu(Attr(off, "x"));
        var y = Emu(Attr(off, "y"));
        var w = Emu(Attr(ext, "cx"));
        var h = Emu(Attr(ext, "cy"));
        if (w <= 0 || h <= 0) return null;
        return new PlaceholderGeom(x, y, w, h);
    }

    private Dictionary<string, string> ReadRels(string relsPath, string baseDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var doc = parts.Xml(relsPath);
        if (doc?.Root is null) return map;

        foreach (var rel in doc.Root.Elements().Where(e => e.Name.LocalName == "Relationship"))
        {
            if (Attr(rel, "Id") is not { } id || Attr(rel, "Target") is not { } target) continue;
            if (Attr(rel, "TargetMode") == "External") continue;
            var normalized = target.Replace('\\', '/');
            if (normalized.StartsWith('/'))
                normalized = normalized.TrimStart('/');
            else
                normalized = XlsxReader.NormalizeRelativePath(baseDir + normalized);
            map[id] = normalized;
        }
        return map;
    }

    private static string RelsPathFor(string partPath)
    {
        var slash = partPath.LastIndexOf('/');
        return slash >= 0
            ? $"{partPath[..slash]}/_rels/{partPath[(slash + 1)..]}.rels"
            : $"_rels/{partPath}.rels";
    }

    private static string BaseDirFor(string partPath)
    {
        var slash = partPath.LastIndexOf('/');
        return slash >= 0 ? partPath[..(slash + 1)] : "";
    }

    private static double Emu(string? v) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d / EmuPerPoint : 0;

    private static bool? ParseBoolAttr(XElement? el, string name) => Attr(el, name) switch
    {
        "1" or "true" => true,
        "0" or "false" => false,
        _ => null
    };

    private static string? Attr(XElement? el, string localName) =>
        el?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
}

/// <summary>
/// A .pptx presentation opened for lossless editing. Holds all package parts in
/// memory and rewrites only modified slide / notes XML parts on save, keeping
/// animations, masters, layouts, embedded media, and SmartArt intact.
/// </summary>
public sealed class PptxEditDocument
{
    private const string ContentTypes = "[Content_Types].xml";
    private const string DrawingNamespace = "http://schemas.openxmlformats.org/drawingml/2006/main";

    private readonly Dictionary<string, byte[]> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _times = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];
    private readonly Dictionary<string, XDocument?> _xml = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _dirtyParts = new(StringComparer.OrdinalIgnoreCase);
    private readonly MemoryParts _parts;
    private int _version, _savedVersion;

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

    private PptxEditDocument() => _parts = new MemoryParts(this);

    public bool IsDirty => _version != _savedVersion;

    public static PptxEditDocument Open(string path, CancellationToken ct = default)
    {
        var doc = new PptxEditDocument();
        using (var zip = ZipFile.OpenRead(path))
        {
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.FullName.EndsWith('/')) continue;
                using var stream = entry.Open();
                using var ms = new MemoryStream();
                stream.CopyTo(ms);
                if (doc._raw.TryAdd(entry.FullName, ms.ToArray()))
                {
                    doc._order.Add(entry.FullName);
                    doc._times[entry.FullName] = entry.LastWriteTime;
                }
            }
        }

        var layout = doc.Layout(ct);
        if (layout.Slides.Count == 0)
            throw new InvalidDataException("No slides in .pptx package.");
        return doc;
    }

    public PptxDocument Layout(CancellationToken ct = default) =>
        new PptxReader(_parts, ct).Read();

    /// <summary>Updates the text of an editable shape on a slide (0-based slideIndex and editableIndex).</summary>
    public PptxDocument SetShapeText(int slideIndex, int editableIndex, string text, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (slideIndex < 0 || slideIndex >= current.Slides.Count)
            throw new ArgumentOutOfRangeException(nameof(slideIndex));

        var slide = current.Slides[slideIndex];
        var slideDoc = Xml(slide.PartPath) ?? throw new InvalidDataException($"Missing slide part {slide.PartPath}");
        var sp = FindEditableShape(slideDoc.Root!, editableIndex)
            ?? throw new InvalidDataException($"Editable shape {editableIndex} not found on slide {slideIndex + 1}");

        var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")!;
        RewriteTxBodyText(txBody, text);

        _dirtyParts.Add(slide.PartPath);
        _version++;
        return Layout(ct);
    }

    /// <summary>Applies a run formatting change (bold, italic, underline, size, color) to all text in an editable shape.</summary>
    public PptxDocument FormatShape(int slideIndex, int editableIndex, DocxRunChange change, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (slideIndex < 0 || slideIndex >= current.Slides.Count)
            throw new ArgumentOutOfRangeException(nameof(slideIndex));

        var slide = current.Slides[slideIndex];
        var slideDoc = Xml(slide.PartPath) ?? throw new InvalidDataException($"Missing slide part {slide.PartPath}");
        var sp = FindEditableShape(slideDoc.Root!, editableIndex)
            ?? throw new InvalidDataException($"Editable shape {editableIndex} not found on slide {slideIndex + 1}");

        var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")!;
        XNamespace a = DrawingNamespace;

        foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
        {
            foreach (var r in p.Elements().Where(e => e.Name.LocalName == "r"))
            {
                var rPr = r.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr");
                if (rPr is null)
                {
                    rPr = new XElement(a + "rPr", new XAttribute("dirty", "0"));
                    r.AddFirst(rPr);
                }
                ApplyDrawingRunChange(rPr, a, change);
            }
            var endRPr = p.Elements().FirstOrDefault(e => e.Name.LocalName == "endParaRPr");
            if (endRPr is not null)
                ApplyDrawingRunChange(endRPr, a, change);
        }

        _dirtyParts.Add(slide.PartPath);
        _version++;
        return Layout(ct);
    }

    /// <summary>Updates paragraph alignment on an editable shape.</summary>
    public PptxDocument SetShapeAlignment(int slideIndex, int editableIndex, DocxAlignment alignment, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (slideIndex < 0 || slideIndex >= current.Slides.Count)
            throw new ArgumentOutOfRangeException(nameof(slideIndex));

        var slide = current.Slides[slideIndex];
        var slideDoc = Xml(slide.PartPath) ?? throw new InvalidDataException($"Missing slide part {slide.PartPath}");
        var sp = FindEditableShape(slideDoc.Root!, editableIndex)
            ?? throw new InvalidDataException($"Editable shape {editableIndex} not found on slide {slideIndex + 1}");

        var txBody = sp.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody")!;
        XNamespace a = DrawingNamespace;
        var algnVal = alignment switch
        {
            DocxAlignment.Center => "ctr",
            DocxAlignment.Right => "r",
            DocxAlignment.Justify => "just",
            _ => "l"
        };

        foreach (var p in txBody.Elements().Where(e => e.Name.LocalName == "p"))
        {
            var pPr = p.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr");
            if (pPr is null)
            {
                pPr = new XElement(a + "pPr");
                p.AddFirst(pPr);
            }
            pPr.SetAttributeValue("algn", algnVal);
        }

        _dirtyParts.Add(slide.PartPath);
        _version++;
        return Layout(ct);
    }

    /// <summary>Updates speaker notes on a slide if the slide has a notesSlide part.</summary>
    public PptxDocument SetSlideNotes(int slideIndex, string notesText, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (slideIndex < 0 || slideIndex >= current.Slides.Count)
            throw new ArgumentOutOfRangeException(nameof(slideIndex));

        var slide = current.Slides[slideIndex];
        if (slide.NotesPartPath is null) return current;

        var notesDoc = Xml(slide.NotesPartPath);
        if (notesDoc?.Root is null) return current;

        var bodySp = notesDoc.Root.Descendants().Where(e => e.Name.LocalName == "sp")
            .FirstOrDefault(sp =>
            {
                var ph = sp.Descendants().FirstOrDefault(e => e.Name.LocalName == "ph");
                var phType = ph?.Attributes().FirstOrDefault(a => a.Name.LocalName == "type")?.Value;
                return phType is "body" or null && sp.Elements().Any(e => e.Name.LocalName == "txBody");
            });

        var txBody = bodySp?.Elements().FirstOrDefault(e => e.Name.LocalName == "txBody");
        if (txBody is null) return current;

        RewriteTxBodyText(txBody, notesText);
        _dirtyParts.Add(slide.NotesPartPath);
        _version++;
        return Layout(ct);
    }

    public Snapshot TakeSnapshot()
    {
        var roots = new Dictionary<string, XElement?>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _order.Where(n => n.EndsWith(".xml", StringComparison.OrdinalIgnoreCase) || n.EndsWith(".rels", StringComparison.OrdinalIgnoreCase)))
        {
            if (_xml.TryGetValue(name, out var loaded) && loaded?.Root is not null)
                roots[name] = new XElement(loaded.Root);
        }
        return new Snapshot(roots, new HashSet<string>(_dirtyParts, StringComparer.OrdinalIgnoreCase), _version);
    }

    public PptxDocument Restore(Snapshot snapshot, CancellationToken ct = default)
    {
        foreach (var key in _xml.Keys.ToList())
        {
            if (snapshot.Roots.TryGetValue(key, out var savedRoot) && savedRoot is not null)
                _xml[key]!.Root!.ReplaceWith(new XElement(savedRoot));
            else
                _xml.Remove(key);
        }
        _dirtyParts.Clear();
        _dirtyParts.UnionWith(snapshot.Dirty);
        _version = snapshot.Version;
        return Layout(ct);
    }

    public void Save(string path)
    {
        var written = new Dictionary<string, byte[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in _dirtyParts)
        {
            if (Xml(name) is { } document)
                written[name] = XlsxEditDocument.SerializeXml(document);
        }

        var temp = path + ".grepdesk-save";
        try
        {
            using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                var names = _order.Concat(written.Keys.Where(k => !_raw.ContainsKey(k)))
                    .OrderBy(n => string.Equals(n, ContentTypes, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                    .ToList();
                foreach (var name in names)
                {
                    var bytes = written.TryGetValue(name, out var fresh) ? fresh : _raw[name];
                    var entry = zip.CreateEntry(name, CompressionLevel.Optimal);
                    if (_times.TryGetValue(name, out var time) && !written.ContainsKey(name))
                        entry.LastWriteTime = time;
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
        _dirtyParts.Clear();
        _savedVersion = _version;
    }

    private static void RewriteTxBodyText(XElement txBody, string text)
    {
        XNamespace a = DrawingNamespace;
        var existingParas = txBody.Elements().Where(e => e.Name.LocalName == "p").ToList();
        var templatePPr = existingParas.Select(p => p.Elements().FirstOrDefault(e => e.Name.LocalName == "pPr")).FirstOrDefault(x => x is not null);
        var templateRPr = existingParas.SelectMany(p => p.Elements().Where(e => e.Name.LocalName == "r"))
            .Select(r => r.Elements().FirstOrDefault(e => e.Name.LocalName == "rPr"))
            .FirstOrDefault(x => x is not null)
            ?? existingParas.Select(p => p.Elements().FirstOrDefault(e => e.Name.LocalName == "endParaRPr")).FirstOrDefault(x => x is not null);

        foreach (var p in existingParas)
            p.Remove();

        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        if (lines.Length == 0) lines = [""];

        foreach (var line in lines)
        {
            var pEl = new XElement(a + "p");
            if (templatePPr is not null)
                pEl.Add(new XElement(templatePPr));

            if (line.Length > 0)
            {
                var rEl = new XElement(a + "r");
                if (templateRPr is not null)
                {
                    var clonedRPr = new XElement(a + "rPr", templateRPr.Attributes(), templateRPr.Elements());
                    rEl.Add(clonedRPr);
                }
                else
                {
                    rEl.Add(new XElement(a + "rPr", new XAttribute("dirty", "0")));
                }
                rEl.Add(new XElement(a + "t", line));
                pEl.Add(rEl);
            }
            else
            {
                pEl.Add(new XElement(a + "endParaRPr", new XAttribute("dirty", "0")));
            }

            txBody.Add(pEl);
        }
    }

    private static void ApplyDrawingRunChange(XElement rPr, XNamespace a, DocxRunChange change)
    {
        switch (change)
        {
            case DocxRunChange.Bold b:
                rPr.SetAttributeValue("b", b.On ? "1" : "0");
                break;
            case DocxRunChange.Italic i:
                rPr.SetAttributeValue("i", i.On ? "1" : "0");
                break;
            case DocxRunChange.Underline u:
                rPr.SetAttributeValue("u", u.On ? "sng" : "none");
                break;
            case DocxRunChange.Size sz:
                var hundredths = Math.Clamp((int)Math.Round(sz.Points * 100), 600, 14400);
                rPr.SetAttributeValue("sz", hundredths.ToString(CultureInfo.InvariantCulture));
                break;
            case DocxRunChange.Color color:
                rPr.Elements().Where(e => e.Name.LocalName == "solidFill").Remove();
                if (color.Hex is { Length: 7 } hex)
                {
                    rPr.AddFirst(new XElement(a + "solidFill",
                        new XElement(a + "srgbClr", new XAttribute("val", hex[1..].ToUpperInvariant()))));
                }
                break;
        }
    }

    private static XElement? FindEditableShape(XElement root, int targetEditableIndex)
    {
        var spTree = root.Descendants().FirstOrDefault(e => e.Name.LocalName == "spTree");
        if (spTree is null) return null;
        var current = 0;
        return FindInTree(spTree, targetEditableIndex, ref current);
    }

    private static XElement? FindInTree(XElement tree, int target, ref int current)
    {
        foreach (var el in tree.Elements())
        {
            if (el.Name.LocalName == "sp" && PptxReader.HasTxBody(el))
            {
                if (current == target) return el;
                current++;
            }
            else if (el.Name.LocalName == "grpSp")
            {
                if (FindInTree(el, target, ref current) is { } found)
                    return found;
            }
        }
        return null;
    }

    private XDocument? Xml(string name)
    {
        var normalized = name.TrimStart('/');
        if (_xml.TryGetValue(normalized, out var cached)) return cached;
        var key = _raw.Keys.FirstOrDefault(k => string.Equals(k.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
        if (key is null || !_raw.TryGetValue(key, out var bytes)) return null;
        try
        {
            using var ms = new MemoryStream(bytes);
            var doc = XDocument.Load(ms);
            _xml[key] = doc;
            if (!string.Equals(key, normalized, StringComparison.Ordinal))
                _xml[normalized] = doc;
            return doc;
        }
        catch
        {
            return null;
        }
    }

    private sealed class MemoryParts(PptxEditDocument owner) : IPackageParts
    {
        public IEnumerable<string> Entries => owner._order;
        public XDocument? Xml(string name) => owner.Xml(name);
        public byte[]? Bytes(string name)
        {
            var normalized = name.TrimStart('/');
            var key = owner._raw.Keys.FirstOrDefault(k => string.Equals(k.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
            return key is not null ? owner._raw[key] : null;
        }
    }
}
