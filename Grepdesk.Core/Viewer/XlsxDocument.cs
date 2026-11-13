using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Grepdesk.Core.Viewer;

public enum XlsxCellKind
{
    Empty,
    Text,
    Number,
    Boolean,
    Date,
    Formula,
    Error
}

public sealed record XlsxCellStyle(
    bool Bold,
    bool Italic,
    bool Underline,
    double FontSize,
    string? FontColor,
    string? FillColor,
    DocxAlignment Alignment,
    bool WrapText)
{
    public static readonly XlsxCellStyle Default = new(false, false, false, 11, null, null, DocxAlignment.Left, false);
}

public sealed record XlsxCell(
    int Row,
    int Column,
    string DisplayText,
    string RawValue,
    string? Formula,
    XlsxCellKind Kind,
    XlsxCellStyle Style,
    int RowSpan = 1,
    int ColumnSpan = 1)
{
    /// <summary>What the formula bar shows when this cell is selected.</summary>
    public string FormulaBarText => Formula is { Length: > 0 } f ? "=" + f : RawValue;
}

public sealed record XlsxMerge(int StartRow, int StartColumn, int EndRow, int EndColumn)
{
    public int RowSpan => EndRow - StartRow + 1;
    public int ColumnSpan => EndColumn - StartColumn + 1;
}

public sealed class XlsxSheet
{
    public required string Name { get; init; }
    public required string PartPath { get; init; }
    public required int RowCount { get; init; }
    public required int ColumnCount { get; init; }
    /// <summary>Column widths in points (0-based column index).</summary>
    public required IReadOnlyList<double> ColumnWidths { get; init; }
    public required IReadOnlyDictionary<(int Row, int Column), XlsxCell> Cells { get; init; }
    public required IReadOnlyList<XlsxMerge> Merges { get; init; }

    public XlsxCell? GetCell(int row, int col) => Cells.TryGetValue((row, col), out var cell) ? cell : null;
}

/// <summary>A formatting change applied to one or more spreadsheet cells.</summary>
public abstract record XlsxStyleChange
{
    public sealed record Bold(bool On) : XlsxStyleChange;
    public sealed record Italic(bool On) : XlsxStyleChange;
    public sealed record Underline(bool On) : XlsxStyleChange;
    public sealed record Alignment(DocxAlignment Value) : XlsxStyleChange;
}

/// <summary>
/// Reads an Excel workbook (.xlsx) for display and structured text extraction:
/// sheets in workbook order, shared and inline strings, numbers and dates with
/// their number formats, formulas, cell styles (fonts, fills, alignment),
/// column widths, and merged cells.
/// </summary>
public sealed class XlsxDocument
{
    public required IReadOnlyList<XlsxSheet> Sheets { get; init; }

    public static XlsxDocument Load(string path, CancellationToken ct = default)
    {
        using var zip = ZipFile.OpenRead(path);
        return new XlsxReader(new ZipPackageParts(zip), ct).Read();
    }

    /// <summary>Converts 0-based column index to Excel column letters (0 -> "A", 25 -> "Z", 26 -> "AA").</summary>
    public static string ColumnName(int column)
    {
        var sb = new StringBuilder(4);
        var n = column + 1;
        while (n > 0)
        {
            var rem = (n - 1) % 26;
            sb.Insert(0, (char)('A' + rem));
            n = (n - 1) / 26;
        }
        return sb.ToString();
    }

    /// <summary>Converts 0-based (row, column) to an A1-style reference.</summary>
    public static string CellReference(int row, int column) => $"{ColumnName(column)}{row + 1}";

    /// <summary>Parses an A1-style cell reference into 0-based (row, column).</summary>
    public static bool TryParseCellReference(ReadOnlySpan<char> reference, out int row, out int column)
    {
        row = 0;
        column = 0;
        var i = 0;
        while (i < reference.Length && reference[i] == '$') i++;

        var colVal = 0;
        var hasCol = false;
        while (i < reference.Length && char.IsAsciiLetter(reference[i]))
        {
            hasCol = true;
            colVal = colVal * 26 + (char.ToUpperInvariant(reference[i]) - 'A' + 1);
            i++;
        }
        if (!hasCol) return false;

        while (i < reference.Length && reference[i] == '$') i++;
        if (i >= reference.Length || !int.TryParse(reference[i..], NumberStyles.None, CultureInfo.InvariantCulture, out var rowVal) || rowVal < 1)
            return false;

        row = rowVal - 1;
        column = colVal - 1;
        return true;
    }

    /// <summary>Formats all sheets as tab-separated rows for search and CLI output.</summary>
    public string ToPlainText(int maxRowsPerSheet = int.MaxValue, (int Start, int End)? sheetRange = null)
    {
        var sb = new StringBuilder();
        var startSheet = sheetRange.HasValue ? Math.Max(1, sheetRange.Value.Start) : 1;
        var endSheet = sheetRange.HasValue ? Math.Min(Sheets.Count, sheetRange.Value.End) : Sheets.Count;
        var showHeaders = Sheets.Count > 1 || sheetRange.HasValue;

        for (var s = startSheet - 1; s < endSheet && s < Sheets.Count; s++)
        {
            var sheet = Sheets[s];
            if (sb.Length > 0) sb.Append('\n');
            if (showHeaders)
            {
                sb.Append("--- ").Append(sheet.Name).Append(" ---\n");
            }

            var rowsToRead = Math.Min(sheet.RowCount, maxRowsPerSheet);
            var rowCells = new List<XlsxCell>();
            for (var r = 0; r < rowsToRead; r++)
            {
                var lastNonEmptyCol = -1;
                for (var c = sheet.ColumnCount - 1; c >= 0; c--)
                {
                    if (sheet.GetCell(r, c) is { DisplayText.Length: > 0 })
                    {
                        lastNonEmptyCol = c;
                        break;
                    }
                }
                if (lastNonEmptyCol < 0) continue;

                for (var c = 0; c <= lastNonEmptyCol; c++)
                {
                    if (c > 0) sb.Append('\t');
                    var text = sheet.GetCell(r, c)?.DisplayText ?? "";
                    if (text.AsSpan().IndexOfAny('\r', '\n', '\t') >= 0)
                        text = text.Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
                    sb.Append(text);
                }
                sb.Append('\n');
            }
        }

        return sb.ToString().TrimEnd('\r', '\n');
    }
}

internal interface IPackageParts
{
    XDocument? Xml(string name);
    IEnumerable<string> Entries { get; }
    byte[]? Bytes(string name);
}

internal sealed class ZipPackageParts(ZipArchive zip) : IPackageParts
{
    public IEnumerable<string> Entries => zip.Entries.Where(e => !e.FullName.EndsWith('/')).Select(e => e.FullName);

    public XDocument? Xml(string name)
    {
        var entry = Find(name);
        if (entry is null) return null;
        try
        {
            using var stream = entry.Open();
            return XDocument.Load(stream);
        }
        catch
        {
            return null;
        }
    }

    public byte[]? Bytes(string name)
    {
        var entry = Find(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    private ZipArchiveEntry? Find(string name)
    {
        var normalized = name.TrimStart('/');
        return zip.GetEntry(normalized)
            ?? zip.Entries.FirstOrDefault(e => string.Equals(e.FullName.TrimStart('/'), normalized, StringComparison.OrdinalIgnoreCase));
    }
}

internal sealed class XlsxReader(IPackageParts parts, CancellationToken ct)
{
    private sealed record FontInfo(bool Bold, bool Italic, bool Underline, double Size, string? Color);
    private sealed record XfInfo(int FontId, int FillId, int NumFmtId, DocxAlignment? Align, bool WrapText);

    private readonly List<string> _sharedStrings = [];
    private readonly List<string?> _themeColors = [];
    private readonly List<FontInfo> _fonts = [];
    private readonly List<string?> _fills = [];
    private readonly Dictionary<int, string> _numFmts = new();
    private readonly List<XfInfo> _cellXfs = [];
    private bool _date1904;

    public XlsxDocument Read()
    {
        ReadTheme();
        ReadSharedStrings();
        ReadStyles();

        var sheetInfos = ReadWorkbookSheets();
        if (sheetInfos.Count == 0)
            throw new InvalidDataException("No worksheets found in .xlsx archive.");

        var sheets = new List<XlsxSheet>(sheetInfos.Count);
        foreach (var (name, partPath) in sheetInfos)
        {
            ct.ThrowIfCancellationRequested();
            if (ReadSheet(name, partPath) is { } sheet)
                sheets.Add(sheet);
        }

        if (sheets.Count == 0)
            throw new InvalidDataException("Unable to read any worksheet in .xlsx archive.");

        return new XlsxDocument { Sheets = sheets };
    }

    private List<(string Name, string PartPath)> ReadWorkbookSheets()
    {
        var result = new List<(string Name, string PartPath)>();
        var workbook = parts.Xml("xl/workbook.xml");
        if (workbook?.Root is not null)
        {
            var wbPr = workbook.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "workbookPr");
            if (Attr(wbPr, "date1904") is "1" or "true")
                _date1904 = true;

            var rels = ReadRels("xl/_rels/workbook.xml.rels", "xl/");
            var sheetsEl = workbook.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheets");
            var allSheetEls = sheetsEl?.Elements().Where(e => e.Name.LocalName == "sheet").ToList() ?? [];

            // Prefer visible sheets; if all are hidden, include them all.
            var visibleEls = allSheetEls.Where(e => Attr(e, "state") is not ("hidden" or "veryHidden")).ToList();
            var targetEls = visibleEls.Count > 0 ? visibleEls : allSheetEls;

            var index = 1;
            foreach (var sheetEl in targetEls)
            {
                var name = Attr(sheetEl, "name") ?? $"Sheet{index}";
                var rId = Attr(sheetEl, "id");
                string? target = null;
                if (rId is not null && rels.TryGetValue(rId, out var relTarget))
                    target = relTarget;
                else
                    target = $"xl/worksheets/sheet{index}.xml";

                if (parts.Xml(target) is not null)
                    result.Add((name, target));
                index++;
            }
        }

        if (result.Count == 0)
        {
            var fallback = parts.Entries
                .Where(e => e.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase) && e.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
                .OrderBy(e => e, StringComparer.OrdinalIgnoreCase)
                .ToList();
            for (var i = 0; i < fallback.Count; i++)
                result.Add(($"Sheet{i + 1}", fallback[i]));
        }

        return result;
    }

    private static Dictionary<string, string> ReadRels(IPackageParts parts, string relsPath, string basePrefix)
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
            else if (!normalized.StartsWith(basePrefix, StringComparison.OrdinalIgnoreCase))
                normalized = NormalizeRelativePath(basePrefix + normalized);
            map[id] = normalized;
        }
        return map;
    }

    private Dictionary<string, string> ReadRels(string relsPath, string basePrefix) => ReadRels(parts, relsPath, basePrefix);

    internal static string NormalizeRelativePath(string path)
    {
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var stack = new List<string>(segments.Length);
        foreach (var seg in segments)
        {
            if (seg == ".") continue;
            if (seg == ".." && stack.Count > 0) stack.RemoveAt(stack.Count - 1);
            else if (seg != "..") stack.Add(seg);
        }
        return string.Join('/', stack);
    }

    private void ReadTheme()
    {
        var theme = parts.Xml("xl/theme/theme1.xml");
        var scheme = theme?.Descendants().FirstOrDefault(e => e.Name.LocalName == "clrScheme");
        if (scheme is null) return;

        // Standard OOXML theme color order: lt1, dk1, lt2, dk2, accent1..6, hlink, folHlink
        string[] order = ["lt1", "dk1", "lt2", "dk2", "accent1", "accent2", "accent3", "accent4", "accent5", "accent6", "hlink", "folHlink"];
        foreach (var name in order)
        {
            var el = scheme.Elements().FirstOrDefault(e => e.Name.LocalName == name);
            var srgb = el?.Elements().FirstOrDefault(e => e.Name.LocalName == "srgbClr");
            var sys = el?.Elements().FirstOrDefault(e => e.Name.LocalName == "sysClr");
            var hex = Attr(srgb, "val") ?? Attr(sys, "lastClr");
            _themeColors.Add(hex is { Length: 6 } ? "#" + hex.ToUpperInvariant() : null);
        }
    }

    private void ReadSharedStrings()
    {
        var doc = parts.Xml("xl/sharedStrings.xml");
        if (doc?.Root is null) return;

        var sb = new StringBuilder();
        foreach (var si in doc.Root.Elements().Where(e => e.Name.LocalName == "si"))
        {
            sb.Clear();
            AppendSiText(si, sb);
            _sharedStrings.Add(sb.ToString());
        }
    }

    private static void AppendSiText(XElement container, StringBuilder sb)
    {
        foreach (var child in container.Elements())
        {
            var local = child.Name.LocalName;
            if (local == "rPh") continue; // skip phonetic guide text
            if (local == "t") sb.Append(child.Value);
            else AppendSiText(child, sb);
        }
    }

    private void ReadStyles()
    {
        var doc = parts.Xml("xl/styles.xml");
        if (doc?.Root is null) return;

        var numFmtsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "numFmts");
        if (numFmtsEl is not null)
        {
            foreach (var nf in numFmtsEl.Elements().Where(e => e.Name.LocalName == "numFmt"))
            {
                if (int.TryParse(Attr(nf, "numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) &&
                    Attr(nf, "formatCode") is { } code)
                {
                    _numFmts[id] = code;
                }
            }
        }

        var fontsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "fonts");
        if (fontsEl is not null)
        {
            foreach (var f in fontsEl.Elements().Where(e => e.Name.LocalName == "font"))
            {
                var bold = HasEnabledFlag(f, "b");
                var italic = HasEnabledFlag(f, "i");
                var underline = HasEnabledFlag(f, "u");
                var szEl = f.Elements().FirstOrDefault(e => e.Name.LocalName == "sz");
                var size = double.TryParse(Attr(szEl, "val"), NumberStyles.Float, CultureInfo.InvariantCulture, out var s) && s > 0 ? s : 11;
                var color = ResolveColor(f.Elements().FirstOrDefault(e => e.Name.LocalName == "color"));
                _fonts.Add(new FontInfo(bold, italic, underline, size, color));
            }
        }

        var fillsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "fills");
        if (fillsEl is not null)
        {
            foreach (var fill in fillsEl.Elements().Where(e => e.Name.LocalName == "fill"))
            {
                var pat = fill.Elements().FirstOrDefault(e => e.Name.LocalName == "patternFill");
                if (Attr(pat, "patternType") == "solid")
                    _fills.Add(ResolveColor(pat?.Elements().FirstOrDefault(e => e.Name.LocalName == "fgColor")));
                else
                    _fills.Add(null);
            }
        }

        var cellXfsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfsEl is not null)
        {
            foreach (var xf in cellXfsEl.Elements().Where(e => e.Name.LocalName == "xf"))
            {
                int.TryParse(Attr(xf, "fontId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fontId);
                int.TryParse(Attr(xf, "fillId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var fillId);
                int.TryParse(Attr(xf, "numFmtId"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var numFmtId);
                var alignEl = xf.Elements().FirstOrDefault(e => e.Name.LocalName == "alignment");
                DocxAlignment? align = Attr(alignEl, "horizontal") switch
                {
                    "center" or "centerContinuous" => DocxAlignment.Center,
                    "right" => DocxAlignment.Right,
                    "justify" or "distributed" => DocxAlignment.Justify,
                    "left" or "general" => DocxAlignment.Left,
                    _ => null
                };
                var wrap = Attr(alignEl, "wrapText") is "1" or "true";
                _cellXfs.Add(new XfInfo(fontId, fillId, numFmtId, align, wrap));
            }
        }
    }

    private string? ResolveColor(XElement? colorEl)
    {
        if (colorEl is null) return null;
        if (Attr(colorEl, "rgb") is { Length: >= 6 } rgb)
        {
            var hex6 = rgb.Length == 8 ? rgb[2..] : rgb[..6];
            return "#" + hex6.ToUpperInvariant();
        }
        if (int.TryParse(Attr(colorEl, "theme"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var themeIdx) &&
            themeIdx >= 0 && themeIdx < _themeColors.Count &&
            _themeColors[themeIdx] is { } baseHex)
        {
            if (double.TryParse(Attr(colorEl, "tint"), NumberStyles.Float, CultureInfo.InvariantCulture, out var tint) && Math.Abs(tint) > 0.001)
                return ApplyTint(baseHex, tint);
            return baseHex;
        }
        return null;
    }

    internal static string ApplyTint(string hex, double tint)
    {
        if (hex.Length != 7 || hex[0] != '#') return hex;
        var r = Convert.ToInt32(hex[1..3], 16);
        var g = Convert.ToInt32(hex[3..5], 16);
        var b = Convert.ToInt32(hex[5..7], 16);
        int Adjust(int c) => Math.Clamp((int)Math.Round(tint < 0 ? c * (1.0 + tint) : c * (1.0 - tint) + 255 * tint), 0, 255);
        return $"#{Adjust(r):X2}{Adjust(g):X2}{Adjust(b):X2}";
    }

    private static bool HasEnabledFlag(XElement parent, string name)
    {
        var el = parent.Elements().FirstOrDefault(e => e.Name.LocalName == name);
        if (el is null) return false;
        return Attr(el, "val") is not ("0" or "false");
    }

    private XlsxSheet? ReadSheet(string name, string partPath)
    {
        var doc = parts.Xml(partPath);
        if (doc?.Root is null) return null;

        var cells = new Dictionary<(int Row, int Column), XlsxCell>();
        var maxRow = -1;
        var maxCol = -1;

        var sheetData = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is not null)
        {
            var nextRow = 0;
            foreach (var rowEl in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
            {
                ct.ThrowIfCancellationRequested();
                var rowIdx = int.TryParse(Attr(rowEl, "r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var rNum) && rNum >= 1
                    ? rNum - 1
                    : nextRow;
                nextRow = rowIdx + 1;

                var nextCol = 0;
                foreach (var cEl in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
                {
                    int colIdx;
                    if (Attr(cEl, "r") is { } cellRef && XlsxDocument.TryParseCellReference(cellRef, out var parsedRow, out var parsedCol))
                    {
                        rowIdx = parsedRow;
                        colIdx = parsedCol;
                    }
                    else
                    {
                        colIdx = nextCol;
                    }
                    nextCol = colIdx + 1;

                    var cell = ReadCell(cEl, rowIdx, colIdx);
                    if (cell.Kind != XlsxCellKind.Empty || cell.Style != XlsxCellStyle.Default || cell.Formula is not null)
                    {
                        cells[(rowIdx, colIdx)] = cell;
                        if (rowIdx > maxRow) maxRow = rowIdx;
                        if (colIdx > maxCol) maxCol = colIdx;
                    }
                }
            }
        }

        var merges = new List<XlsxMerge>();
        var mergeCellsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "mergeCells");
        if (mergeCellsEl is not null)
        {
            foreach (var mc in mergeCellsEl.Elements().Where(e => e.Name.LocalName == "mergeCell"))
            {
                if (Attr(mc, "ref") is not { } refStr) continue;
                var colon = refStr.IndexOf(':');
                if (colon <= 0) continue;
                if (XlsxDocument.TryParseCellReference(refStr.AsSpan(0, colon), out var r1, out var c1) &&
                    XlsxDocument.TryParseCellReference(refStr.AsSpan(colon + 1), out var r2, out var c2))
                {
                    var minR = Math.Min(r1, r2);
                    var maxR = Math.Max(r1, r2);
                    var minC = Math.Min(c1, c2);
                    var maxC = Math.Max(c1, c2);
                    var merge = new XlsxMerge(minR, minC, maxR, maxC);
                    merges.Add(merge);
                    if (maxR > maxRow) maxRow = maxR;
                    if (maxC > maxCol) maxCol = maxC;

                    if (cells.TryGetValue((minR, minC), out var existing))
                        cells[(minR, minC)] = existing with { RowSpan = merge.RowSpan, ColumnSpan = merge.ColumnSpan };
                    else
                        cells[(minR, minC)] = new XlsxCell(minR, minC, "", "", null, XlsxCellKind.Empty, XlsxCellStyle.Default, merge.RowSpan, merge.ColumnSpan);
                }
            }
        }

        // Evaluate formulas whose cached <v> was absent
        foreach (var (pos, cell) in cells.ToList())
        {
            if (cell.Formula is { Length: > 0 } formula && string.IsNullOrEmpty(cell.DisplayText))
            {
                if (TryEvaluateFormula(formula, cells, out var evaluated))
                {
                    var formatted = FormatNumberValue(evaluated, 0, out _);
                    cells[pos] = cell with
                    {
                        DisplayText = formatted,
                        RawValue = evaluated.ToString("G15", CultureInfo.InvariantCulture)
                    };
                }
            }
        }

        var colCount = maxCol + 1;
        var rowCount = maxRow + 1;
        var colWidths = new double[Math.Max(colCount, 1)];
        Array.Fill(colWidths, 80.0);

        var colsEl = doc.Root.Elements().FirstOrDefault(e => e.Name.LocalName == "cols");
        if (colsEl is not null)
        {
            foreach (var colEl in colsEl.Elements().Where(e => e.Name.LocalName == "col"))
            {
                if (!int.TryParse(Attr(colEl, "min"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var min) ||
                    !int.TryParse(Attr(colEl, "max"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var max) ||
                    !double.TryParse(Attr(colEl, "width"), NumberStyles.Float, CultureInfo.InvariantCulture, out var width))
                    continue;

                var ptWidth = Math.Clamp(width * 7.0 + 8.0, 28.0, 420.0);
                var start = Math.Max(0, min - 1);
                var end = Math.Min(colWidths.Length - 1, max - 1);
                for (var c = start; c <= end; c++)
                    colWidths[c] = ptWidth;
            }
        }

        return new XlsxSheet
        {
            Name = name,
            PartPath = partPath,
            RowCount = rowCount,
            ColumnCount = colCount,
            ColumnWidths = colWidths,
            Cells = cells,
            Merges = merges
        };
    }

    private XlsxCell ReadCell(XElement cEl, int row, int col)
    {
        var type = Attr(cEl, "t");
        var styleIdx = int.TryParse(Attr(cEl, "s"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var sIdx) ? sIdx : -1;
        var xf = styleIdx >= 0 && styleIdx < _cellXfs.Count ? _cellXfs[styleIdx] : null;
        var font = xf is not null && xf.FontId >= 0 && xf.FontId < _fonts.Count ? _fonts[xf.FontId] : null;
        var fill = xf is not null && xf.FillId >= 0 && xf.FillId < _fills.Count ? _fills[xf.FillId] : null;

        var fEl = cEl.Elements().FirstOrDefault(e => e.Name.LocalName == "f");
        var formula = fEl?.Value;
        var vEl = cEl.Elements().FirstOrDefault(e => e.Name.LocalName == "v");
        var vText = vEl?.Value ?? "";

        var display = "";
        var raw = "";
        var kind = XlsxCellKind.Empty;
        var defaultAlign = DocxAlignment.Left;

        switch (type)
        {
            case "s":
                if (int.TryParse(vText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ssIdx) &&
                    ssIdx >= 0 && ssIdx < _sharedStrings.Count)
                {
                    display = raw = _sharedStrings[ssIdx];
                    kind = XlsxCellKind.Text;
                }
                break;

            case "inlineStr":
                var isEl = cEl.Elements().FirstOrDefault(e => e.Name.LocalName == "is");
                if (isEl is not null)
                {
                    var sb = new StringBuilder();
                    AppendSiText(isEl, sb);
                    display = raw = sb.ToString();
                    kind = XlsxCellKind.Text;
                }
                break;

            case "str":
                display = raw = vText;
                kind = formula is not null ? XlsxCellKind.Formula : XlsxCellKind.Text;
                break;

            case "b":
                var isTrue = vText is "1" or "true" or "TRUE";
                display = raw = isTrue ? "TRUE" : "FALSE";
                kind = XlsxCellKind.Boolean;
                defaultAlign = DocxAlignment.Center;
                break;

            case "e":
                display = raw = vText;
                kind = XlsxCellKind.Error;
                defaultAlign = DocxAlignment.Center;
                break;

            default:
                if (vText.Length > 0)
                {
                    raw = vText;
                    if (double.TryParse(vText, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
                    {
                        display = FormatNumberValue(num, xf?.NumFmtId ?? 0, out var isDate);
                        kind = formula is not null ? XlsxCellKind.Formula : (isDate ? XlsxCellKind.Date : XlsxCellKind.Number);
                        defaultAlign = DocxAlignment.Right;
                    }
                    else
                    {
                        display = vText;
                        kind = formula is not null ? XlsxCellKind.Formula : XlsxCellKind.Text;
                    }
                }
                else if (formula is not null)
                {
                    kind = XlsxCellKind.Formula;
                    defaultAlign = DocxAlignment.Right;
                }
                break;
        }

        var style = new XlsxCellStyle(
            Bold: font?.Bold ?? false,
            Italic: font?.Italic ?? false,
            Underline: font?.Underline ?? false,
            FontSize: font?.Size ?? 11,
            FontColor: font?.Color,
            FillColor: fill,
            Alignment: xf?.Align ?? defaultAlign,
            WrapText: xf?.WrapText ?? false);

        return new XlsxCell(row, col, display, raw, formula, kind, style);
    }

    private string FormatNumberValue(double value, int numFmtId, out bool isDate)
    {
        isDate = false;
        _numFmts.TryGetValue(numFmtId, out var customCode);

        if (IsDateFormat(numFmtId, customCode))
        {
            if (TryFromExcelSerialDate(value, _date1904, out var dt))
            {
                isDate = true;
                var hasTime = Math.Abs(value - Math.Truncate(value)) > 0.00001;
                if (numFmtId is >= 18 and <= 21 or >= 45 and <= 47)
                    return dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
                return hasTime
                    ? dt.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture)
                    : dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            }
        }

        if (numFmtId == 9 || (customCode is not null && customCode.Contains("0%", StringComparison.Ordinal) && !customCode.Contains("0.0", StringComparison.Ordinal)))
            return (value * 100.0).ToString("0", CultureInfo.InvariantCulture) + "%";
        if (numFmtId == 10 || (customCode is not null && customCode.Contains('%')))
            return (value * 100.0).ToString("0.00", CultureInfo.InvariantCulture) + "%";
        if (numFmtId is 2 or 4)
            return value.ToString(numFmtId == 4 ? "#,##0.00" : "0.00", CultureInfo.InvariantCulture);
        if (numFmtId == 3)
            return value.ToString("#,##0", CultureInfo.InvariantCulture);

        return value.ToString("G15", CultureInfo.InvariantCulture);
    }

    private static bool IsDateFormat(int numFmtId, string? code)
    {
        if (numFmtId is (>= 14 and <= 22) or (>= 27 and <= 36) or (>= 45 and <= 47) or (>= 50 and <= 58))
            return true;
        if (string.IsNullOrWhiteSpace(code))
            return false;

        // Strip quoted literals and color tags like [Red]
        var cleaned = new StringBuilder(code.Length);
        var inQuotes = false;
        var inBrackets = false;
        foreach (var ch in code)
        {
            if (ch == '"') { inQuotes = !inQuotes; continue; }
            if (inQuotes) continue;
            if (ch == '[') { inBrackets = true; continue; }
            if (ch == ']') { inBrackets = false; continue; }
            if (!inBrackets) cleaned.Append(char.ToLowerInvariant(ch));
        }

        var s = cleaned.ToString();
        if (s.IndexOfAny(['0', '#']) >= 0) return false;
        return s.Contains('y') || s.Contains('d') || s.Contains('h') || s.Contains('s');
    }

    internal static bool TryFromExcelSerialDate(double serial, bool date1904, out DateTime result)
    {
        result = default;
        if (serial is < 0 or > 2958465) return false;
        try
        {
            if (date1904)
            {
                result = new DateTime(1904, 1, 1).AddDays(serial);
                return true;
            }
            // Excel 1900 leap year bug adjustment: serial 60 is Feb 29, 1900
            var adjusted = serial >= 60 ? serial - 1 : serial;
            result = new DateTime(1899, 12, 31).AddDays(adjusted);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    internal static bool TryEvaluateFormula(
        string formula,
        IReadOnlyDictionary<(int Row, int Column), XlsxCell> cells,
        out double result) => TryEvalExpr(formula.AsSpan().Trim(), cells, out result);

    private static bool TryEvalExpr(
        ReadOnlySpan<char> span,
        IReadOnlyDictionary<(int Row, int Column), XlsxCell> cells,
        out double result)
    {
        result = 0;
        span = span.Trim();
        if (span.IsEmpty) return false;

        // Binary + / - outside parentheses
        var depth = 0;
        for (var i = span.Length - 1; i >= 0; i--)
        {
            var ch = span[i];
            if (ch == ')') depth++;
            else if (ch == '(') depth--;
            else if (depth == 0 && i > 0 && ch is '+' or '-')
            {
                if (TryEvalExpr(span[..i], cells, out var left) && TryEvalExpr(span[(i + 1)..], cells, out var right))
                {
                    result = ch == '+' ? left + right : left - right;
                    return true;
                }
            }
        }

        // Binary * / / outside parentheses
        depth = 0;
        for (var i = span.Length - 1; i >= 0; i--)
        {
            var ch = span[i];
            if (ch == ')') depth++;
            else if (ch == '(') depth--;
            else if (depth == 0 && i > 0 && ch is '*' or '/')
            {
                if (TryEvalExpr(span[..i], cells, out var left) && TryEvalExpr(span[(i + 1)..], cells, out var right))
                {
                    if (ch == '/' && Math.Abs(right) < 1e-12) return false;
                    result = ch == '*' ? left * right : left / right;
                    return true;
                }
            }
        }

        // Function call like SUM(A1:A5), AVERAGE(B1:B10), MIN(...), MAX(...), COUNT(...)
        var openParen = span.IndexOf('(');
        if (openParen > 0 && span[^1] == ')')
        {
            var fn = span[..openParen].Trim();
            var args = span[(openParen + 1)..^1];
            var values = CollectRangeNumbers(args, cells);
            if (fn.Equals("SUM", StringComparison.OrdinalIgnoreCase))
            {
                result = values.Sum();
                return true;
            }
            if (fn.Equals("AVERAGE", StringComparison.OrdinalIgnoreCase))
            {
                if (values.Count == 0) return false;
                result = values.Average();
                return true;
            }
            if (fn.Equals("MIN", StringComparison.OrdinalIgnoreCase))
            {
                if (values.Count == 0) return false;
                result = values.Min();
                return true;
            }
            if (fn.Equals("MAX", StringComparison.OrdinalIgnoreCase))
            {
                if (values.Count == 0) return false;
                result = values.Max();
                return true;
            }
            if (fn.Equals("COUNT", StringComparison.OrdinalIgnoreCase))
            {
                result = values.Count;
                return true;
            }
        }

        return TryEvalTerm(span, cells, out result);
    }

    private static bool TryEvalTerm(
        ReadOnlySpan<char> token,
        IReadOnlyDictionary<(int Row, int Column), XlsxCell> cells,
        out double value)
    {
        value = 0;
        token = token.Trim();
        if (double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
            return true;
        if (XlsxDocument.TryParseCellReference(token, out var r, out var c))
        {
            if (cells.TryGetValue((r, c), out var cell) &&
                double.TryParse(cell.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out value))
                return true;
            value = 0;
            return true;
        }
        return false;
    }

    private static List<double> CollectRangeNumbers(
        ReadOnlySpan<char> args,
        IReadOnlyDictionary<(int Row, int Column), XlsxCell> cells)
    {
        var list = new List<double>();
        var parts = args.ToString().Split([',', ';'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            var p = part.AsSpan().Trim();
            var colon = p.IndexOf(':');
            if (colon > 0 &&
                XlsxDocument.TryParseCellReference(p[..colon], out var r1, out var c1) &&
                XlsxDocument.TryParseCellReference(p[(colon + 1)..], out var r2, out var c2))
            {
                for (var r = Math.Min(r1, r2); r <= Math.Max(r1, r2); r++)
                {
                    for (var c = Math.Min(c1, c2); c <= Math.Max(c1, c2); c++)
                    {
                        if (cells.TryGetValue((r, c), out var cell) &&
                            double.TryParse(cell.RawValue, NumberStyles.Float, CultureInfo.InvariantCulture, out var v))
                        {
                            list.Add(v);
                        }
                    }
                }
            }
            else if (TryEvalTerm(p, cells, out var single))
            {
                list.Add(single);
            }
        }
        return list;
    }

    private static string? Attr(XElement? el, string localName) =>
        el?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;
}

/// <summary>
/// An .xlsx workbook opened for lossless editing. Holds all package parts in
/// memory and rewrites only modified worksheets / styles on save, preserving
/// charts, pivots, macros, drawings, shared strings, and unedited XML nodes.
/// </summary>
public sealed class XlsxEditDocument
{
    private const string ContentTypes = "[Content_Types].xml";
    private const string StylesPart = "xl/styles.xml";
    private const string CalcChainPart = "xl/calcChain.xml";
    private const string SpreadsheetNamespace = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

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

    private XlsxEditDocument() => _parts = new MemoryParts(this);

    public bool IsDirty => _version != _savedVersion;

    public static XlsxEditDocument Open(string path, CancellationToken ct = default)
    {
        var doc = new XlsxEditDocument();
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
        if (layout.Sheets.Count == 0)
            throw new InvalidDataException("No worksheets in .xlsx package.");
        return doc;
    }

    public XlsxDocument Layout(CancellationToken ct = default) =>
        new XlsxReader(_parts, ct).Read();

    /// <summary>Updates a cell's value or formula in the given sheet (0-based sheet, row, col).</summary>
    public XlsxDocument SetCell(int sheetIndex, int row, int column, string input, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (sheetIndex < 0 || sheetIndex >= current.Sheets.Count)
            throw new ArgumentOutOfRangeException(nameof(sheetIndex));
        if (row < 0 || column < 0)
            throw new ArgumentOutOfRangeException(nameof(row));

        var sheet = current.Sheets[sheetIndex];
        var sheetDoc = Xml(sheet.PartPath) ?? throw new InvalidDataException($"Missing worksheet part {sheet.PartPath}");
        var root = sheetDoc.Root!;
        var ns = root.Name.Namespace;

        var sheetData = root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is null)
        {
            sheetData = new XElement(ns + "sheetData");
            root.Add(sheetData);
        }

        var rowEl = FindOrCreateRow(sheetData, ns, row + 1);
        var cellRef = XlsxDocument.CellReference(row, column);
        var cellEl = FindOrCreateCell(rowEl, ns, cellRef, column);

        var trimmed = input.Trim();
        var hadFormula = cellEl.Elements().Any(e => e.Name.LocalName == "f");

        // Clear existing value/formula children while keeping extLst or other unknown children
        foreach (var child in cellEl.Elements().Where(e => e.Name.LocalName is "v" or "is" or "f").ToList())
            child.Remove();

        if (string.IsNullOrEmpty(input))
        {
            cellEl.Attribute("t")?.Remove();
        }
        else if (trimmed.StartsWith('=') && trimmed.Length > 1)
        {
            var formulaText = trimmed[1..].Trim();
            cellEl.SetAttributeValue("t", null);
            cellEl.AddFirst(new XElement(ns + "f", formulaText));
            if (XlsxReader.TryEvaluateFormula(formulaText, sheet.Cells, out var evaluated))
                cellEl.Add(new XElement(ns + "v", evaluated.ToString("G15", CultureInfo.InvariantCulture)));
        }
        else if (double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var num))
        {
            cellEl.Attribute("t")?.Remove();
            cellEl.Add(new XElement(ns + "v", num.ToString("G15", CultureInfo.InvariantCulture)));
        }
        else if (string.Equals(trimmed, "true", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(trimmed, "false", StringComparison.OrdinalIgnoreCase))
        {
            cellEl.SetAttributeValue("t", "b");
            cellEl.Add(new XElement(ns + "v", trimmed.Equals("true", StringComparison.OrdinalIgnoreCase) ? "1" : "0"));
        }
        else
        {
            cellEl.SetAttributeValue("t", "inlineStr");
            cellEl.Add(new XElement(ns + "is",
                new XElement(ns + "t", new XAttribute(XNamespace.Xml + "space", "preserve"), input)));
        }

        if (hadFormula && !cellEl.Elements().Any(e => e.Name.LocalName == "f"))
            RemoveFromCalcChain(cellRef);

        _dirtyParts.Add(sheet.PartPath);
        _version++;
        return Layout(ct);
    }

    /// <summary>Applies a formatting change (bold, italic, underline, alignment) to a rectangular range of cells.</summary>
    public XlsxDocument FormatCells(int sheetIndex, int minRow, int minCol, int maxRow, int maxCol, XlsxStyleChange change, CancellationToken ct = default)
    {
        var current = Layout(ct);
        if (sheetIndex < 0 || sheetIndex >= current.Sheets.Count)
            throw new ArgumentOutOfRangeException(nameof(sheetIndex));

        var sheet = current.Sheets[sheetIndex];
        var sheetDoc = Xml(sheet.PartPath) ?? throw new InvalidDataException($"Missing worksheet part {sheet.PartPath}");
        var root = sheetDoc.Root!;
        var ns = root.Name.Namespace;
        var sheetData = root.Elements().FirstOrDefault(e => e.Name.LocalName == "sheetData");
        if (sheetData is null)
        {
            sheetData = new XElement(ns + "sheetData");
            root.Add(sheetData);
        }

        var stylesDoc = EnsureStylesDocument();
        var mappedStyles = new Dictionary<int, int>();

        for (var r = Math.Max(0, minRow); r <= maxRow; r++)
        {
            var rowEl = FindOrCreateRow(sheetData, ns, r + 1);
            for (var c = Math.Max(0, minCol); c <= maxCol; c++)
            {
                var cellRef = XlsxDocument.CellReference(r, c);
                var cellEl = FindOrCreateCell(rowEl, ns, cellRef, c);
                var oldStyleIdx = int.TryParse(cellEl.Attribute("s")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
                if (!mappedStyles.TryGetValue(oldStyleIdx, out var newStyleIdx))
                {
                    newStyleIdx = DeriveCellStyle(stylesDoc, oldStyleIdx, change);
                    mappedStyles[oldStyleIdx] = newStyleIdx;
                }
                cellEl.SetAttributeValue("s", newStyleIdx.ToString(CultureInfo.InvariantCulture));
            }
        }

        _dirtyParts.Add(StylesPart);
        _dirtyParts.Add(sheet.PartPath);
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

    public XlsxDocument Restore(Snapshot snapshot, CancellationToken ct = default)
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
                written[name] = SerializeXml(document);
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

    private void RemoveFromCalcChain(string cellRef)
    {
        var calcDoc = Xml(CalcChainPart);
        if (calcDoc?.Root is null) return;
        var matching = calcDoc.Root.Elements()
            .Where(e => e.Name.LocalName == "c" && string.Equals(e.Attribute("r")?.Value, cellRef, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (matching.Count == 0) return;
        foreach (var el in matching) el.Remove();
        _dirtyParts.Add(CalcChainPart);
    }

    private XDocument EnsureStylesDocument()
    {
        if (Xml(StylesPart) is { Root: not null } existing)
            return existing;

        XNamespace ns = SpreadsheetNamespace;
        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", "yes"),
            new XElement(ns + "styleSheet",
                new XElement(ns + "fonts", new XAttribute("count", "1"),
                    new XElement(ns + "font",
                        new XElement(ns + "sz", new XAttribute("val", "11")),
                        new XElement(ns + "name", new XAttribute("val", "Calibri")))),
                new XElement(ns + "fills", new XAttribute("count", "2"),
                    new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "none"))),
                    new XElement(ns + "fill", new XElement(ns + "patternFill", new XAttribute("patternType", "gray125")))),
                new XElement(ns + "borders", new XAttribute("count", "1"),
                    new XElement(ns + "border", new XElement(ns + "left"), new XElement(ns + "right"), new XElement(ns + "top"), new XElement(ns + "bottom"), new XElement(ns + "diagonal"))),
                new XElement(ns + "cellXfs", new XAttribute("count", "1"),
                    new XElement(ns + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0")))));
        _xml[StylesPart] = doc;
        return doc;
    }

    private static int DeriveCellStyle(XDocument stylesDoc, int baseStyleIdx, XlsxStyleChange change)
    {
        var root = stylesDoc.Root!;
        var ns = root.Name.Namespace;

        var fontsEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "fonts");
        if (fontsEl is null)
        {
            fontsEl = new XElement(ns + "fonts", new XElement(ns + "font", new XElement(ns + "sz", new XAttribute("val", "11"))));
            root.AddFirst(fontsEl);
        }
        var fonts = fontsEl.Elements().Where(e => e.Name.LocalName == "font").ToList();

        var cellXfsEl = root.Elements().FirstOrDefault(e => e.Name.LocalName == "cellXfs");
        if (cellXfsEl is null)
        {
            cellXfsEl = new XElement(ns + "cellXfs", new XElement(ns + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0")));
            root.Add(cellXfsEl);
        }
        var xfs = cellXfsEl.Elements().Where(e => e.Name.LocalName == "xf").ToList();

        var baseXf = baseStyleIdx >= 0 && baseStyleIdx < xfs.Count
            ? new XElement(xfs[baseStyleIdx])
            : new XElement(ns + "xf", new XAttribute("numFmtId", "0"), new XAttribute("fontId", "0"), new XAttribute("fillId", "0"), new XAttribute("borderId", "0"));

        if (change is XlsxStyleChange.Bold or XlsxStyleChange.Italic or XlsxStyleChange.Underline)
        {
            var oldFontId = int.TryParse(baseXf.Attribute("fontId")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var fId) && fId >= 0 && fId < fonts.Count ? fId : 0;
            var newFont = fonts.Count > 0 ? new XElement(fonts[oldFontId]) : new XElement(ns + "font", new XElement(ns + "sz", new XAttribute("val", "11")));

            void SetFontFlag(string tagName, bool on)
            {
                newFont.Elements().Where(e => e.Name.LocalName == tagName).Remove();
                if (on) newFont.AddFirst(new XElement(ns + tagName));
            }

            switch (change)
            {
                case XlsxStyleChange.Bold b: SetFontFlag("b", b.On); break;
                case XlsxStyleChange.Italic i: SetFontFlag("i", i.On); break;
                case XlsxStyleChange.Underline u: SetFontFlag("u", u.On); break;
            }

            fontsEl.Add(newFont);
            var newFontId = fonts.Count;
            fontsEl.SetAttributeValue("count", (newFontId + 1).ToString(CultureInfo.InvariantCulture));
            baseXf.SetAttributeValue("fontId", newFontId.ToString(CultureInfo.InvariantCulture));
            baseXf.SetAttributeValue("applyFont", "1");
        }
        else if (change is XlsxStyleChange.Alignment alignChange)
        {
            var alignEl = baseXf.Elements().FirstOrDefault(e => e.Name.LocalName == "alignment");
            if (alignEl is null)
            {
                alignEl = new XElement(ns + "alignment");
                baseXf.Add(alignEl);
            }
            var hVal = alignChange.Value switch
            {
                DocxAlignment.Center => "center",
                DocxAlignment.Right => "right",
                DocxAlignment.Justify => "justify",
                _ => "left"
            };
            alignEl.SetAttributeValue("horizontal", hVal);
            baseXf.SetAttributeValue("applyAlignment", "1");
        }

        cellXfsEl.Add(baseXf);
        var newXfIdx = xfs.Count;
        cellXfsEl.SetAttributeValue("count", (newXfIdx + 1).ToString(CultureInfo.InvariantCulture));
        return newXfIdx;
    }

    private static XElement FindOrCreateRow(XElement sheetData, XNamespace ns, int rowNumber)
    {
        XElement? insertBefore = null;
        foreach (var r in sheetData.Elements().Where(e => e.Name.LocalName == "row"))
        {
            if (int.TryParse(r.Attribute("r")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var existingNum))
            {
                if (existingNum == rowNumber) return r;
                if (existingNum > rowNumber) { insertBefore = r; break; }
            }
        }

        var created = new XElement(ns + "row", new XAttribute("r", rowNumber.ToString(CultureInfo.InvariantCulture)));
        if (insertBefore is not null) insertBefore.AddBeforeSelf(created);
        else sheetData.Add(created);
        return created;
    }

    private static XElement FindOrCreateCell(XElement rowEl, XNamespace ns, string cellRef, int colIndex)
    {
        XElement? insertBefore = null;
        foreach (var c in rowEl.Elements().Where(e => e.Name.LocalName == "c"))
        {
            var rAttr = c.Attribute("r")?.Value;
            if (string.Equals(rAttr, cellRef, StringComparison.OrdinalIgnoreCase))
                return c;
            if (rAttr is not null && XlsxDocument.TryParseCellReference(rAttr, out _, out var existingCol) && existingCol > colIndex)
            {
                insertBefore = c;
                break;
            }
        }

        var created = new XElement(ns + "c", new XAttribute("r", cellRef));
        if (insertBefore is not null) insertBefore.AddBeforeSelf(created);
        else rowEl.Add(created);
        return created;
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

    internal static byte[] SerializeXml(XDocument document)
    {
        using var memory = new MemoryStream();
        using (var writer = XmlWriter.Create(memory, new XmlWriterSettings
        {
            Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            Indent = false
        }))
        {
            document.Save(writer);
        }
        return memory.ToArray();
    }

    private sealed class MemoryParts(XlsxEditDocument owner) : IPackageParts
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
