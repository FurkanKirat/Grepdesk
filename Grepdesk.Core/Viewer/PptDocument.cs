using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Grepdesk.Core.Viewer;

/// <summary>
/// Reads legacy binary PowerPoint presentations (.ppt, MS-CFB + MS-PPT) into
/// the shared <see cref="PptxDocument"/> model so the slide viewer, slide show,
/// preview pane, content search, and CLI open/grep work uniformly with .pptx.
/// </summary>
internal sealed class PptReader
{
    private const double MasterUnitsPerPoint = 8.0;
    private const double EmuPerPoint = 12700.0;
    private const int MaxImageBytes = 30 * 1024 * 1024;
    private const int MaxWalkSteps = 4096;

    // MS-PPT Record Types
    private const ushort RtDocument = 0x03E8;
    private const ushort RtDocumentAtom = 0x03E9;
    private const ushort RtSlide = 0x03EE;
    private const ushort RtSlideAtom = 0x03EF;
    private const ushort RtNotes = 0x03F0;
    private const ushort RtNotesAtom = 0x03F1;
    private const ushort RtEnvironment = 0x03F2;
    private const ushort RtSlidePersistAtom = 0x03F3;
    private const ushort RtMainMaster = 0x03F8;
    private const ushort RtPPDrawingGroup = 0x040B;
    private const ushort RtPPDrawing = 0x040C;
    private const ushort RtFontCollection = 0x07D5;
    private const ushort RtFontEntityAtom = 0x0FB7;
    private const ushort RtColorSchemeAtom = 0x07F0;
    private const ushort RtOEPlaceHolderAtom = 0x0BC3;
    private const ushort RtOutlineTextRefAtom = 0x0F9E;
    private const ushort RtTextHeaderAtom = 0x0F9F;
    private const ushort RtTextCharsAtom = 0x0FA0;
    private const ushort RtStyleTextPropAtom = 0x0FA1;
    private const ushort RtTextMasterStyleAtom = 0x0FA3;
    private const ushort RtTextBytesAtom = 0x0FA8;
    private const ushort RtSlideListWithText = 0x0FF0;
    private const ushort RtUserEditAtom = 0x0FF5;
    private const ushort RtCurrentUserAtom = 0x0FF6;
    private const ushort RtPersistDirectoryAtom = 0x1772;
    private const ushort RtCryptSession10Container = 0x2F14;

    // OfficeArt (Escher) Record Types
    private const ushort OaDggContainer = 0xF000;
    private const ushort OaBStoreContainer = 0xF001;
    private const ushort OaDgContainer = 0xF002;
    private const ushort OaSpgrContainer = 0xF003;
    private const ushort OaSpContainer = 0xF004;
    private const ushort OaFBSE = 0xF007;
    private const ushort OaFSPGR = 0xF009;
    private const ushort OaFSP = 0xF00A;
    private const ushort OaFOPT = 0xF00B;
    private const ushort OaClientTextbox = 0xF00D;
    private const ushort OaChildAnchor = 0xF00F;
    private const ushort OaClientAnchor = 0xF010;
    private const ushort OaClientData = 0xF011;
    private const ushort OaTertiaryFOPT = 0xF122;

    // OfficeArt Blip Record Types
    private const ushort BlipEmf = 0xF01A;
    private const ushort BlipWmf = 0xF01B;
    private const ushort BlipPict = 0xF01C;
    private const ushort BlipJpeg = 0xF01D;
    private const ushort BlipPng = 0xF01E;
    private const ushort BlipDib = 0xF01F;
    private const ushort BlipTiff = 0xF029;
    private const ushort BlipJpegCmyk = 0xF02A;

    private static readonly Encoding LegacyAnsiEncoding;

    static PptReader()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        LegacyAnsiEncoding = Encoding.GetEncoding(1252);
    }

    private readonly record struct RecordHeader(byte Version, ushort Instance, ushort Type, int Length, int DataStart, int DataEnd)
    {
        public bool IsContainer => Version == 0xF;
    }

    private readonly record struct RectPts(double X, double Y, double Width, double Height);

    private sealed class TextBlock
    {
        public int TextType { get; init; } = 4; // Other
        public string RawText { get; set; } = "";
        public List<PfRun> PfRuns { get; set; } = [];
        public List<CfRun> CfRuns { get; set; } = [];
    }

    private sealed record PfRun(
        int CharCount,
        int Level,
        bool? HasBullet,
        char? BulletChar,
        int? BulletFontRef,
        ColorRef? BulletColor,
        DocxAlignment? Alignment,
        double? LeftMarginPts,
        double? IndentPts);

    private sealed record CfRun(
        int CharCount,
        bool? Bold,
        bool? Italic,
        bool? Underline,
        int? FontRef,
        double? FontSize,
        ColorRef? Color);

    private readonly record struct ColorRef(byte R, byte G, byte B, byte Index)
    {
        public string? Resolve(string[] scheme)
        {
            if (Index == 0xFF) return null;
            if (Index == 0xFE) return $"#{R:X2}{G:X2}{B:X2}";
            return Index < scheme.Length ? scheme[Index] : $"#{R:X2}{G:X2}{B:X2}";
        }
    }

    private sealed record StyleLevel(
        bool? HasBullet,
        char? BulletChar,
        int? BulletFontRef,
        ColorRef? BulletColor,
        DocxAlignment? Alignment,
        double? LeftMarginPts,
        double? IndentPts,
        bool? Bold,
        bool? Italic,
        bool? Underline,
        int? FontRef,
        double? FontSize,
        ColorRef? Color);

    private sealed record SlidePersistEntry(uint PersistIdRef, uint SlideId, List<TextBlock> Texts);

    private sealed class FoptProps
    {
        public uint? Pib { get; set; }
        public uint? FillType { get; set; }
        public uint? FillColorRaw { get; set; }
        public uint? FillBackColorRaw { get; set; }
        public bool? Filled { get; set; }
        public uint? LineColorRaw { get; set; }
        public uint? LineWidthEmu { get; set; }
        public bool? HasLine { get; set; }
        public double? TextLeftPts { get; set; }
        public double? TextTopPts { get; set; }
        public double? TextRightPts { get; set; }
        public double? TextBottomPts { get; set; }
        public PptxAnchor? Anchor { get; set; }
        public bool IsTable { get; set; }
    }

    private static readonly string[] DefaultScheme =
    [
        "#FFFFFF", "#000000", "#808080", "#000000",
        "#BBE0E3", "#333399", "#009999", "#99CC00"
    ];

    private readonly byte[] _ppt;
    private readonly byte[]? _picturesStream;
    private readonly CancellationToken _ct;
    private readonly Dictionary<uint, int> _persistOffsets = [];
    private readonly List<string> _fonts = [];
    private readonly Dictionary<int, List<StyleLevel>> _envStyles = [];
    private readonly List<byte[]?> _pictures = [];

    private double _slideWidth = 720.0;
    private double _slideHeight = 540.0;

    private PptReader(byte[] ppt, byte[]? picturesStream, CancellationToken ct)
    {
        _ppt = ppt;
        _picturesStream = picturesStream;
        _ct = ct;
    }

    public static PptxDocument Read(string path, CancellationToken ct = default)
    {
        using var cfb = CompoundFile.Open(path);
        if (cfb.TryReadStream("EncryptedSummary", out _))
            throw new InvalidDataException("Encrypted .ppt presentation is not supported.");

        uint? offsetToCurrentEdit = null;
        if (cfb.TryReadStream("Current User", out var currentUser))
            offsetToCurrentEdit = ParseCurrentUser(currentUser);

        if (!cfb.TryReadStream("PowerPoint Document", out var ppt) || ppt.Length < 8)
            throw new InvalidDataException("Missing PowerPoint Document stream in .ppt file.");

        cfb.TryReadStream("Pictures", out var pictures);
        return new PptReader(ppt, pictures.Length > 0 ? pictures : null, ct).Parse(offsetToCurrentEdit);
    }

    private PptxDocument Parse(uint? offsetToCurrentEdit)
    {
        var docOffset = BuildPersistDirectory(offsetToCurrentEdit);
        if (docOffset < 0 || !TryReadHeader(_ppt, docOffset, _ppt.Length, out var docRec) || docRec.Type != RtDocument)
            throw new InvalidDataException("Document container not found in .ppt stream.");

        var slideEntries = new List<SlidePersistEntry>();
        var masterEntries = new Dictionary<uint, SlidePersistEntry>();
        var notesEntries = new Dictionary<uint, SlidePersistEntry>();

        foreach (var child in ChildrenOf(_ppt, docRec.DataStart, docRec.DataEnd))
        {
            _ct.ThrowIfCancellationRequested();
            switch (child.Type)
            {
                case RtCryptSession10Container:
                    throw new InvalidDataException("Encrypted .ppt presentation is not supported.");

                case RtDocumentAtom:
                    if (child.Length >= 8)
                    {
                        var w = ReadInt32(_ppt, child.DataStart) / MasterUnitsPerPoint;
                        var h = ReadInt32(_ppt, child.DataStart + 4) / MasterUnitsPerPoint;
                        if (w is > 100 and < 10000 && h is > 100 and < 10000)
                        {
                            _slideWidth = w;
                            _slideHeight = h;
                        }
                    }
                    break;

                case RtEnvironment:
                    ParseEnvironment(child.DataStart, child.DataEnd);
                    break;

                case RtPPDrawingGroup:
                    ParseDrawingGroup(child.DataStart, child.DataEnd);
                    break;

                case RtSlideListWithText:
                    ParseSlideListWithText(child, slideEntries, masterEntries, notesEntries);
                    break;
            }
        }

        if (slideEntries.Count == 0)
            throw new InvalidDataException("No slides found in .ppt presentation.");

        var masterCache = new Dictionary<uint, (string[] Scheme, string? Background, List<PptxShape> Backdrop, Dictionary<int, List<StyleLevel>> Styles)>();
        var slides = new List<PptxSlide>(slideEntries.Count);

        for (var i = 0; i < slideEntries.Count; i++)
        {
            _ct.ThrowIfCancellationRequested();
            var slide = ReadSlide(i + 1, slideEntries[i], masterEntries, masterCache, notesEntries);
            if (slide is not null)
                slides.Add(slide);
        }

        if (slides.Count == 0)
            throw new InvalidDataException("No readable slides found in .ppt presentation.");

        return new PptxDocument
        {
            SlideWidth = _slideWidth,
            SlideHeight = _slideHeight,
            Slides = slides
        };
    }

    private static uint? ParseCurrentUser(byte[] data)
    {
        if (!TryReadHeader(data, 0, data.Length, out var rec) || rec.Type != RtCurrentUserAtom || rec.Length < 12)
            return null;

        var headerToken = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec.DataStart + 4, 4));
        if (headerToken == 0xF3D1C4DF)
            throw new InvalidDataException("Encrypted .ppt presentation is not supported.");

        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(rec.DataStart + 8, 4));
    }

    private int BuildPersistDirectory(uint? offsetToCurrentEdit)
    {
        var userEdits = new List<(uint OffsetPersistDir, uint DocPersistIdRef)>();
        var visited = new HashSet<uint>();
        var editOffset = offsetToCurrentEdit;

        if (editOffset is null || editOffset.Value >= (uint)_ppt.Length)
        {
            // Fallback: scan top-level records for the latest UserEditAtom
            foreach (var rec in ChildrenOf(_ppt, 0, _ppt.Length))
            {
                if (rec.Type == RtUserEditAtom)
                    editOffset = (uint)(rec.DataStart - 8);
            }
        }

        while (editOffset.HasValue && editOffset.Value > 0 && editOffset.Value < (uint)_ppt.Length &&
               visited.Add(editOffset.Value) && visited.Count <= MaxWalkSteps)
        {
            if (!TryReadHeader(_ppt, (int)editOffset.Value, _ppt.Length, out var rec) ||
                rec.Type != RtUserEditAtom || rec.Length < 20)
                break;

            var offsetLastEdit = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(rec.DataStart + 8, 4));
            var offsetPersistDir = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(rec.DataStart + 12, 4));
            var docPersistIdRef = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(rec.DataStart + 16, 4));

            userEdits.Add((offsetPersistDir, docPersistIdRef));
            editOffset = offsetLastEdit == 0 ? null : offsetLastEdit;
        }

        // Apply oldest-to-newest so newer persist entries win
        for (var i = userEdits.Count - 1; i >= 0; i--)
        {
            var dirOffset = userEdits[i].OffsetPersistDir;
            if (dirOffset >= (uint)_ppt.Length) continue;
            if (!TryReadHeader(_ppt, (int)dirOffset, _ppt.Length, out var pda) || pda.Type != RtPersistDirectoryAtom)
                continue;

            var pos = pda.DataStart;
            while (pos + 4 <= pda.DataEnd)
            {
                var entry = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(pos, 4));
                pos += 4;
                var persistId = entry & 0x000FFFFFU;
                var count = (int)((entry >> 20) & 0x00000FFFU);
                for (var j = 0; j < count && pos + 4 <= pda.DataEnd; j++)
                {
                    var offset = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(pos, 4));
                    pos += 4;
                    if (offset < (uint)_ppt.Length)
                        _persistOffsets[persistId + (uint)j] = (int)offset;
                }
            }
        }

        if (userEdits.Count > 0 && _persistOffsets.TryGetValue(userEdits[0].DocPersistIdRef, out var docOffset))
            return docOffset;

        // Fallback: find Document container directly in top-level stream
        foreach (var rec in ChildrenOf(_ppt, 0, _ppt.Length))
        {
            if (rec.Type == RtDocument)
                return rec.DataStart - 8;
        }

        return -1;
    }

    private void ParseEnvironment(int start, int end)
    {
        foreach (var child in ChildrenOf(_ppt, start, end))
        {
            switch (child.Type)
            {
                case RtFontCollection:
                    foreach (var fontRec in ChildrenOf(_ppt, child.DataStart, child.DataEnd))
                    {
                        if (fontRec.Type == RtFontEntityAtom && fontRec.Length >= 2)
                        {
                            var nameBytes = Math.Min(64, fontRec.Length);
                            var raw = Encoding.Unicode.GetString(_ppt, fontRec.DataStart, nameBytes);
                            var nul = raw.IndexOf('\0');
                            _fonts.Add((nul >= 0 ? raw[..nul] : raw).Trim());
                        }
                    }
                    break;

                case RtTextMasterStyleAtom:
                    _envStyles[child.Instance] = ParseMasterStyleAtom(child);
                    break;
            }
        }
    }

    private void ParseDrawingGroup(int start, int end)
    {
        foreach (var child in ChildrenOf(_ppt, start, end))
        {
            if (child.Type != OaDggContainer) continue;
            foreach (var dggChild in ChildrenOf(_ppt, child.DataStart, child.DataEnd))
            {
                if (dggChild.Type != OaBStoreContainer) continue;
                foreach (var fbse in ChildrenOf(_ppt, dggChild.DataStart, dggChild.DataEnd))
                {
                    if (fbse.Type == OaFBSE)
                        _pictures.Add(ReadPictureFromFbse(fbse));
                }
            }
        }
    }

    private byte[]? ReadPictureFromFbse(RecordHeader fbse)
    {
        if (fbse.Length < 36) return null;
        var foDelay = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(fbse.DataStart + 28, 4));
        var cbName = _ppt[fbse.DataStart + 33];
        var inlineOffset = fbse.DataStart + 36 + cbName;

        if (inlineOffset + 8 <= fbse.DataEnd && TryReadHeader(_ppt, inlineOffset, fbse.DataEnd, out var inlineBlip))
            return DecodeBlip(_ppt, inlineBlip);

        if (_picturesStream is not null && foDelay < (uint)_picturesStream.Length &&
            TryReadHeader(_picturesStream, (int)foDelay, _picturesStream.Length, out var streamBlip))
            return DecodeBlip(_picturesStream, streamBlip);

        return null;
    }

    private static byte[]? DecodeBlip(byte[] buffer, RecordHeader blip)
    {
        var uidLen = (blip.Instance & 1) == 1 ? 32 : 16;

        switch (blip.Type)
        {
            case BlipJpeg or BlipPng or BlipTiff or BlipJpegCmyk:
            {
                var payloadStart = blip.DataStart + uidLen + 1;
                if (payloadStart >= blip.DataEnd) return null;
                var len = blip.DataEnd - payloadStart;
                if (len <= 0 || len > MaxImageBytes) return null;
                var copy = new byte[len];
                Buffer.BlockCopy(buffer, payloadStart, copy, 0, len);
                return copy;
            }

            case BlipDib:
            {
                var dibStart = blip.DataStart + uidLen + 1;
                if (dibStart + 40 > blip.DataEnd) return null;
                var dibLen = blip.DataEnd - dibStart;
                if (dibLen <= 40 || dibLen > MaxImageBytes) return null;
                return WrapDibAsBmp(buffer.AsSpan(dibStart, dibLen));
            }

            case BlipEmf or BlipWmf or BlipPict:
            {
                // 34-byte OfficeArtMetafileHeader follows UID(s)
                var metaStart = blip.DataStart + uidLen;
                if (metaStart + 34 >= blip.DataEnd) return null;
                var compression = buffer[metaStart + 32];
                var payloadStart = metaStart + 34;
                var payloadLen = blip.DataEnd - payloadStart;
                if (payloadLen <= 0 || payloadLen > MaxImageBytes) return null;

                if (compression == 0) // deflate (zlib)
                {
                    try
                    {
                        using var input = new MemoryStream(buffer, payloadStart, payloadLen);
                        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
                        using var output = new MemoryStream();
                        zlib.CopyTo(output);
                        return output.Length is > 0 and <= MaxImageBytes ? output.ToArray() : null;
                    }
                    catch
                    {
                        return null;
                    }
                }

                var raw = new byte[payloadLen];
                Buffer.BlockCopy(buffer, payloadStart, raw, 0, payloadLen);
                return raw;
            }

            default:
                return null;
        }
    }

    private static byte[] WrapDibAsBmp(ReadOnlySpan<byte> dib)
    {
        var biSize = BinaryPrimitives.ReadUInt32LittleEndian(dib[..4]);
        var bitCount = BinaryPrimitives.ReadUInt16LittleEndian(dib.Slice(14, 2));
        var clrUsed = biSize >= 36 ? BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(32, 4)) : 0u;

        var paletteEntries = clrUsed > 0
            ? clrUsed
            : bitCount <= 8 ? 1u << bitCount : 0u;
        if (biSize == 40 && bitCount is 16 or 32 && BinaryPrimitives.ReadUInt32LittleEndian(dib.Slice(16, 4)) == 3)
            paletteEntries += 3; // BI_BITFIELDS masks

        var pixelOffset = 14 + biSize + paletteEntries * 4;
        var totalSize = 14 + dib.Length;
        var bmp = new byte[totalSize];
        bmp[0] = (byte)'B';
        bmp[1] = (byte)'M';
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(2, 4), (uint)totalSize);
        BinaryPrimitives.WriteUInt32LittleEndian(bmp.AsSpan(10, 4), pixelOffset);
        dib.CopyTo(bmp.AsSpan(14));
        return bmp;
    }

    private void ParseSlideListWithText(
        RecordHeader listRec,
        List<SlidePersistEntry> slideEntries,
        Dictionary<uint, SlidePersistEntry> masterEntries,
        Dictionary<uint, SlidePersistEntry> notesEntries)
    {
        SlidePersistEntry? current = null;
        TextBlock? currentText = null;

        foreach (var rec in ChildrenOf(_ppt, listRec.DataStart, listRec.DataEnd))
        {
            switch (rec.Type)
            {
                case RtSlidePersistAtom:
                    if (rec.Length < 16) break;
                    var persistIdRef = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(rec.DataStart, 4));
                    var slideId = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(rec.DataStart + 12, 4));
                    current = new SlidePersistEntry(persistIdRef, slideId, []);
                    currentText = null;

                    if (listRec.Instance == 0)
                        slideEntries.Add(current);
                    else if (listRec.Instance == 1)
                        masterEntries[slideId] = current;
                    else if (listRec.Instance == 2)
                        notesEntries[slideId] = current;
                    break;

                case RtTextHeaderAtom:
                    if (current is null) break;
                    var textType = rec.Length >= 4 ? ReadInt32(_ppt, rec.DataStart) : 4;
                    currentText = new TextBlock { TextType = textType };
                    current.Texts.Add(currentText);
                    break;

                case RtTextCharsAtom:
                    if (currentText is not null)
                        currentText.RawText = DecodeUtf16Text(_ppt.AsSpan(rec.DataStart, rec.Length));
                    break;

                case RtTextBytesAtom:
                    if (currentText is not null)
                        currentText.RawText = DecodeAnsiText(_ppt.AsSpan(rec.DataStart, rec.Length));
                    break;

                case RtStyleTextPropAtom:
                    if (currentText is not null)
                        ParseStyleTextPropAtom(rec, currentText);
                    break;
            }
        }
    }

    private PptxSlide? ReadSlide(
        int number,
        SlidePersistEntry entry,
        Dictionary<uint, SlidePersistEntry> masterEntries,
        Dictionary<uint, (string[] Scheme, string? Background, List<PptxShape> Backdrop, Dictionary<int, List<StyleLevel>> Styles)> masterCache,
        Dictionary<uint, SlidePersistEntry> notesEntries)
    {
        if (!_persistOffsets.TryGetValue(entry.PersistIdRef, out var slideOffset))
            return null;
        if (!TryReadHeader(_ppt, slideOffset, _ppt.Length, out var slideRec) || slideRec.Type != RtSlide)
            return null;

        uint masterIdRef = 0;
        uint notesIdRef = 0;
        bool useMasterObjects = true;
        bool useMasterScheme = true;
        bool useMasterBackground = true;
        string[]? slideScheme = null;
        RecordHeader? drawingRec = null;

        foreach (var child in ChildrenOf(_ppt, slideRec.DataStart, slideRec.DataEnd))
        {
            switch (child.Type)
            {
                case RtSlideAtom when child.Length >= 22:
                    masterIdRef = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(child.DataStart + 12, 4));
                    notesIdRef = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(child.DataStart + 16, 4));
                    var flags = BinaryPrimitives.ReadUInt16LittleEndian(_ppt.AsSpan(child.DataStart + 20, 2));
                    useMasterObjects = (flags & 0x0001) != 0;
                    useMasterScheme = (flags & 0x0002) != 0;
                    useMasterBackground = (flags & 0x0004) != 0;
                    break;

                case RtColorSchemeAtom:
                    slideScheme = ParseColorScheme(child);
                    break;

                case RtPPDrawing:
                    drawingRec = child;
                    break;
            }
        }

        var masterInfo = ResolveMaster(masterIdRef, masterEntries, masterCache);
        var effectiveScheme = useMasterScheme || slideScheme is null ? masterInfo.Scheme : slideScheme;

        var shapes = new List<PptxShape>();
        string? slideBg = null;
        var editableIndex = 0;
        var bodyOrder = 0;

        if (drawingRec is { } dr)
            ReadDrawing(dr, entry.Texts, effectiveScheme, masterInfo.Styles, shapes, out slideBg, ref editableIndex, ref bodyOrder, decorOnly: false);

        // Fallback if slide shapes had no text but SlideListWithText carried text blocks (very old PPT generators)
        if (shapes.OfType<PptxTextShape>().All(s => string.IsNullOrWhiteSpace(s.PlainText)) && entry.Texts.Count > 0)
        {
            foreach (var tb in entry.Texts)
            {
                if (string.IsNullOrWhiteSpace(tb.RawText)) continue;
                var kind = ClassifyTextType(tb.TextType);
                var geom = DefaultPlaceholderGeometry(kind, ref bodyOrder);
                var paras = BuildParagraphs(tb, kind, effectiveScheme, masterInfo.Styles);
                if (paras.Count > 0)
                    shapes.Add(new PptxTextShape(editableIndex++, $"Shape {editableIndex}", kind, geom.X, geom.Y, geom.Width, geom.Height, null, null, paras));
            }
        }

        var notesText = notesIdRef != 0 ? ReadNotes(notesIdRef, notesEntries) : "";
        var bgColor = slideBg ?? (useMasterBackground ? masterInfo.Background : null) ?? "#FFFFFF";

        return new PptxSlide
        {
            Number = number,
            PartPath = $"ppt/slides/slide{number}",
            NotesPartPath = null,
            Width = _slideWidth,
            Height = _slideHeight,
            BackgroundColor = bgColor,
            Shapes = shapes,
            Backdrop = useMasterObjects ? masterInfo.Backdrop : [],
            Notes = notesText
        };
    }

    private (string[] Scheme, string? Background, List<PptxShape> Backdrop, Dictionary<int, List<StyleLevel>> Styles) ResolveMaster(
        uint masterIdRef,
        Dictionary<uint, SlidePersistEntry> masterEntries,
        Dictionary<uint, (string[] Scheme, string? Background, List<PptxShape> Backdrop, Dictionary<int, List<StyleLevel>> Styles)> cache)
    {
        if (masterIdRef == 0)
            return (DefaultScheme, null, [], _envStyles);

        if (cache.TryGetValue(masterIdRef, out var cached))
            return cached;

        var scheme = DefaultScheme;
        string? bg = null;
        var backdrop = new List<PptxShape>();
        var styles = new Dictionary<int, List<StyleLevel>>(_envStyles);

        if (masterEntries.TryGetValue(masterIdRef, out var masterEntry) &&
            _persistOffsets.TryGetValue(masterEntry.PersistIdRef, out var masterOffset) &&
            TryReadHeader(_ppt, masterOffset, _ppt.Length, out var masterRec) &&
            (masterRec.Type == RtMainMaster || masterRec.Type == RtSlide))
        {
            RecordHeader? drawing = null;
            foreach (var child in ChildrenOf(_ppt, masterRec.DataStart, masterRec.DataEnd))
            {
                switch (child.Type)
                {
                    case RtColorSchemeAtom:
                        scheme = ParseColorScheme(child);
                        break;
                    case RtTextMasterStyleAtom:
                        styles[child.Instance] = ParseMasterStyleAtom(child);
                        break;
                    case RtPPDrawing:
                        drawing = child;
                        break;
                }
            }

            if (drawing is { } dr)
            {
                var dummyEdit = 0;
                var dummyBody = 0;
                ReadDrawing(dr, masterEntry.Texts, scheme, styles, backdrop, out bg, ref dummyEdit, ref dummyBody, decorOnly: true);
            }
        }

        var result = (scheme, bg, backdrop, styles);
        cache[masterIdRef] = result;
        return result;
    }

    private string ReadNotes(uint notesIdRef, Dictionary<uint, SlidePersistEntry> notesEntries)
    {
        if (!notesEntries.TryGetValue(notesIdRef, out var notesEntry))
            return "";

        var sb = new StringBuilder();
        if (_persistOffsets.TryGetValue(notesEntry.PersistIdRef, out var offset) &&
            TryReadHeader(_ppt, offset, _ppt.Length, out var notesRec) &&
            notesRec.Type == RtNotes)
        {
            foreach (var child in ChildrenOf(_ppt, notesRec.DataStart, notesRec.DataEnd))
            {
                if (child.Type != RtPPDrawing) continue;
                var shapes = new List<PptxShape>();
                var dummyEdit = 0;
                var dummyBody = 0;
                ReadDrawing(child, notesEntry.Texts, DefaultScheme, _envStyles, shapes, out _, ref dummyEdit, ref dummyBody, decorOnly: false);
                foreach (var ts in shapes.OfType<PptxTextShape>())
                {
                    var text = ts.PlainText.Trim();
                    if (text.Length == 0) continue;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(text);
                }
            }
        }

        if (sb.Length == 0)
        {
            foreach (var tb in notesEntry.Texts)
            {
                var clean = CleanPlainText(tb.RawText);
                if (clean.Length == 0) continue;
                if (sb.Length > 0) sb.Append('\n');
                sb.Append(clean);
            }
        }

        return sb.ToString();
    }

    private void ReadDrawing(
        RecordHeader drawingRec,
        List<TextBlock> slideTexts,
        string[] scheme,
        Dictionary<int, List<StyleLevel>> masterStyles,
        List<PptxShape> shapes,
        out string? backgroundColor,
        ref int editableIndex,
        ref int bodyOrder,
        bool decorOnly)
    {
        backgroundColor = null;
        foreach (var dg in ChildrenOf(_ppt, drawingRec.DataStart, drawingRec.DataEnd))
        {
            if (dg.Type != OaDgContainer) continue;
            foreach (var item in ChildrenOf(_ppt, dg.DataStart, dg.DataEnd))
            {
                switch (item.Type)
                {
                    case OaSpgrContainer:
                        ReadSpgrContainer(item, parentRect: null, parentGroupBounds: null, slideTexts, scheme, masterStyles, shapes, ref backgroundColor, ref editableIndex, ref bodyOrder, decorOnly);
                        break;

                    case OaSpContainer:
                        ReadSpContainer(item, parentRect: null, parentGroupBounds: null, slideTexts, scheme, masterStyles, shapes, ref backgroundColor, ref editableIndex, ref bodyOrder, decorOnly);
                        break;
                }
            }
        }
    }

    private void ReadSpgrContainer(
        RecordHeader spgr,
        RectPts? parentRect,
        RectPts? parentGroupBounds,
        List<TextBlock> slideTexts,
        string[] scheme,
        Dictionary<int, List<StyleLevel>> masterStyles,
        List<PptxShape> shapes,
        ref string? backgroundColor,
        ref int editableIndex,
        ref int bodyOrder,
        bool decorOnly)
    {
        var children = ChildrenOf(_ppt, spgr.DataStart, spgr.DataEnd).ToList();
        if (children.Count == 0) return;

        // First child SpContainer defines the group coordinate space and bounds
        RectPts? groupBounds = null;
        RectPts? groupRect = null;
        bool isTable = false;

        if (children[0].Type == OaSpContainer)
        {
            var fopt = new FoptProps();
            foreach (var c in ChildrenOf(_ppt, children[0].DataStart, children[0].DataEnd))
            {
                switch (c.Type)
                {
                    case OaFSPGR when c.Length >= 16:
                        var l = ReadInt32(_ppt, c.DataStart);
                        var t = ReadInt32(_ppt, c.DataStart + 4);
                        var r = ReadInt32(_ppt, c.DataStart + 8);
                        var b = ReadInt32(_ppt, c.DataStart + 12);
                        groupBounds = new RectPts(l, t, r - l, b - t);
                        break;
                    case OaClientAnchor:
                        groupRect = ParseClientAnchor(c);
                        break;
                    case OaChildAnchor when parentRect.HasValue && parentGroupBounds.HasValue:
                        groupRect = ParseChildAnchor(c, parentRect.Value, parentGroupBounds.Value);
                        break;
                    case OaFOPT or OaTertiaryFOPT:
                        ParseFopt(c, fopt);
                        break;
                }
            }
            isTable = fopt.IsTable;
        }

        if (isTable && groupRect.HasValue && groupBounds.HasValue)
        {
            var tableShapes = new List<PptxShape>();
            var dummyEdit = 0;
            var dummyBody = 0;
            for (var i = 1; i < children.Count; i++)
            {
                if (children[i].Type == OaSpContainer)
                    ReadSpContainer(children[i], groupRect, groupBounds, slideTexts, scheme, masterStyles, tableShapes, ref backgroundColor, ref dummyEdit, ref dummyBody, decorOnly: false);
                else if (children[i].Type == OaSpgrContainer)
                    ReadSpgrContainer(children[i], groupRect, groupBounds, slideTexts, scheme, masterStyles, tableShapes, ref backgroundColor, ref dummyEdit, ref dummyBody, decorOnly: false);
            }

            if (BuildTableFromGroup(groupRect.Value, tableShapes) is { } table)
            {
                shapes.Add(table);
                return;
            }
        }

        for (var i = 1; i < children.Count; i++)
        {
            var child = children[i];
            if (child.Type == OaSpgrContainer)
                ReadSpgrContainer(child, groupRect ?? parentRect, groupBounds ?? parentGroupBounds, slideTexts, scheme, masterStyles, shapes, ref backgroundColor, ref editableIndex, ref bodyOrder, decorOnly);
            else if (child.Type == OaSpContainer)
                ReadSpContainer(child, groupRect ?? parentRect, groupBounds ?? parentGroupBounds, slideTexts, scheme, masterStyles, shapes, ref backgroundColor, ref editableIndex, ref bodyOrder, decorOnly);
        }
    }

    private void ReadSpContainer(
        RecordHeader sp,
        RectPts? parentRect,
        RectPts? parentGroupBounds,
        List<TextBlock> slideTexts,
        string[] scheme,
        Dictionary<int, List<StyleLevel>> masterStyles,
        List<PptxShape> shapes,
        ref string? backgroundColor,
        ref int editableIndex,
        ref int bodyOrder,
        bool decorOnly)
    {
        ushort shapeType = 0;
        uint fspFlags = 0;
        var fopt = new FoptProps();
        RectPts? rect = null;
        bool flipH = false, flipV = false;
        byte? placeholderId = null;
        TextBlock? inlineText = null;
        int? outlineTextRef = null;

        foreach (var child in ChildrenOf(_ppt, sp.DataStart, sp.DataEnd))
        {
            switch (child.Type)
            {
                case OaFSP when child.Length >= 8:
                    shapeType = child.Instance;
                    fspFlags = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(child.DataStart + 4, 4));
                    flipH = (fspFlags & 0x0040) != 0;
                    flipV = (fspFlags & 0x0080) != 0;
                    break;

                case OaFOPT or OaTertiaryFOPT:
                    ParseFopt(child, fopt);
                    break;

                case OaClientAnchor:
                    rect = ParseClientAnchor(child);
                    break;

                case OaChildAnchor when parentRect.HasValue && parentGroupBounds.HasValue:
                    rect = ParseChildAnchor(child, parentRect.Value, parentGroupBounds.Value);
                    break;

                case OaClientData:
                    foreach (var cdChild in ChildrenOf(_ppt, child.DataStart, child.DataEnd))
                    {
                        if (cdChild.Type == RtOEPlaceHolderAtom && cdChild.Length >= 5)
                            placeholderId = _ppt[cdChild.DataStart + 4];
                    }
                    break;

                case OaClientTextbox:
                    ParseClientTextbox(child, out inlineText, out outlineTextRef);
                    break;
            }
        }

        // Deleted shape
        if ((fspFlags & 0x0004) != 0)
            return;

        // Background shape
        if ((fspFlags & 0x0400) != 0)
        {
            if (fopt.Filled != false)
                backgroundColor = ResolveEscherColor(fopt.FillColorRaw, scheme) ?? ResolveEscherColor(fopt.FillBackColorRaw, scheme);
            return;
        }

        if (decorOnly && placeholderId.HasValue)
            return;

        // Skip slide number / date / header placeholders
        if (placeholderId is 6 or 7 or 9)
            return;

        // Picture shape (msosptPictureFrame = 75, or any shape with pib)
        if (fopt.Pib is { } pib && pib >= 1 && pib <= _pictures.Count)
        {
            var imgBytes = _pictures[(int)pib - 1];
            if (imgBytes is { Length: > 0 })
            {
                var g = rect ?? new RectPts(_slideWidth * 0.15, _slideHeight * 0.2, _slideWidth * 0.7, _slideHeight * 0.6);
                shapes.Add(new PptxImageShape(imgBytes, g.X, g.Y, g.Width, g.Height));
                return;
            }
        }

        // Line / straight connector (msosptLine = 20, msosptStraightConnector1 = 32)
        if (shapeType is 20 or 32 && inlineText is null && outlineTextRef is null)
        {
            if (rect is { } lr && fopt.HasLine != false)
            {
                var lineColor = ResolveEscherColor(fopt.LineColorRaw, scheme) ?? scheme[1];
                var thickness = fopt.LineWidthEmu is > 0 ? fopt.LineWidthEmu.Value / EmuPerPoint : 1.0;
                shapes.Add(new PptxLineShape(lr.X, lr.Y, lr.Width, lr.Height, lineColor, thickness, flipH, flipV));
            }
            return;
        }

        TextBlock? textBlock = null;
        if (outlineTextRef is { } idx && idx >= 0 && idx < slideTexts.Count)
            textBlock = slideTexts[idx];
        else if (inlineText is not null)
            textBlock = inlineText;

        var phKind = ClassifyPlaceholder(placeholderId, textBlock?.TextType);
        var fill = fopt.Filled == true ? ResolveEscherColor(fopt.FillColorRaw, scheme) : null;
        var border = fopt.HasLine == true ? ResolveEscherColor(fopt.LineColorRaw, scheme) : null;

        if (textBlock is null || string.IsNullOrWhiteSpace(textBlock.RawText))
        {
            if (fill is null && border is null)
                return;
            var emptyGeom = rect ?? DefaultPlaceholderGeometry(phKind, ref bodyOrder);
            shapes.Add(new PptxTextShape(decorOnly ? -1 : editableIndex++, $"Shape {editableIndex}", phKind,
                emptyGeom.X, emptyGeom.Y, emptyGeom.Width, emptyGeom.Height, fill, border, []));
            return;
        }

        var geom = rect ?? DefaultPlaceholderGeometry(phKind, ref bodyOrder);
        var paragraphs = BuildParagraphs(textBlock, phKind, scheme, masterStyles);
        if (paragraphs.All(p => string.IsNullOrWhiteSpace(p.PlainText)) && fill is null && border is null)
            return;

        shapes.Add(new PptxTextShape(
            decorOnly ? -1 : editableIndex++,
            $"Shape {editableIndex}",
            phKind,
            geom.X,
            geom.Y,
            geom.Width,
            geom.Height,
            fill,
            border,
            paragraphs)
        {
            InsetLeft = fopt.TextLeftPts ?? 7.2,
            InsetTop = fopt.TextTopPts ?? 3.6,
            InsetRight = fopt.TextRightPts ?? 7.2,
            InsetBottom = fopt.TextBottomPts ?? 3.6,
            Anchor = fopt.Anchor ?? PptxAnchor.Top
        });
    }

    private static PptxTableShape? BuildTableFromGroup(RectPts groupRect, List<PptxShape> childShapes)
    {
        var cellsRaw = childShapes.OfType<PptxTextShape>().ToList();
        if (cellsRaw.Count == 0) return null;

        var ys = ClusterCoords(cellsRaw.Select(c => c.Y));
        var xs = ClusterCoords(cellsRaw.Select(c => c.X));
        if (ys.Count == 0 || xs.Count == 0) return null;

        var colWidths = new List<double>(xs.Count);
        for (var c = 0; c < xs.Count; c++)
        {
            var nextX = c + 1 < xs.Count ? xs[c + 1] : groupRect.X + groupRect.Width;
            colWidths.Add(Math.Max(20.0, nextX - xs[c]));
        }

        var cells = new List<PptxTableCell>(cellsRaw.Count);
        foreach (var cell in cellsRaw)
        {
            var r = NearestIndex(ys, cell.Y);
            var c = NearestIndex(xs, cell.X);

            var colSpan = 1;
            var rightEdge = cell.X + cell.Width;
            for (var k = c + 1; k < xs.Count && xs[k] < rightEdge - 4.0; k++)
                colSpan++;

            var rowSpan = 1;
            var bottomEdge = cell.Y + cell.Height;
            for (var k = r + 1; k < ys.Count && ys[k] < bottomEdge - 4.0; k++)
                rowSpan++;

            var bold = r == 0 || cell.Paragraphs.SelectMany(p => p.Runs).Any(run => run.Bold);
            cells.Add(new PptxTableCell(r, c, rowSpan, colSpan, cell.PlainText.Trim(), bold, cell.FillColor));
        }

        return new PptxTableShape(groupRect.X, groupRect.Y, groupRect.Width, groupRect.Height, colWidths, ys.Count, xs.Count, cells);
    }

    private static List<double> ClusterCoords(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        var clusters = new List<double>();
        foreach (var v in sorted)
        {
            if (clusters.Count == 0 || Math.Abs(v - clusters[^1]) > 3.0)
                clusters.Add(v);
        }
        return clusters;
    }

    private static int NearestIndex(List<double> coords, double value)
    {
        var best = 0;
        var bestDist = Math.Abs(coords[0] - value);
        for (var i = 1; i < coords.Count; i++)
        {
            var d = Math.Abs(coords[i] - value);
            if (d < bestDist)
            {
                bestDist = d;
                best = i;
            }
        }
        return best;
    }

    private void ParseClientTextbox(RecordHeader box, out TextBlock? inlineText, out int? outlineTextRef)
    {
        inlineText = null;
        outlineTextRef = null;

        foreach (var child in ChildrenOf(_ppt, box.DataStart, box.DataEnd))
        {
            switch (child.Type)
            {
                case RtOutlineTextRefAtom when child.Length >= 4:
                    outlineTextRef = ReadInt32(_ppt, child.DataStart);
                    break;

                case RtTextHeaderAtom:
                    inlineText ??= new TextBlock();
                    inlineText = new TextBlock
                    {
                        TextType = child.Length >= 4 ? ReadInt32(_ppt, child.DataStart) : 4,
                        RawText = inlineText.RawText,
                        PfRuns = inlineText.PfRuns,
                        CfRuns = inlineText.CfRuns
                    };
                    break;

                case RtTextCharsAtom:
                    inlineText ??= new TextBlock();
                    inlineText.RawText = DecodeUtf16Text(_ppt.AsSpan(child.DataStart, child.Length));
                    break;

                case RtTextBytesAtom:
                    inlineText ??= new TextBlock();
                    inlineText.RawText = DecodeAnsiText(_ppt.AsSpan(child.DataStart, child.Length));
                    break;

                case RtStyleTextPropAtom:
                    if (inlineText is not null)
                        ParseStyleTextPropAtom(child, inlineText);
                    break;
            }
        }
    }

    private List<PptxParagraph> BuildParagraphs(
        TextBlock block,
        PptxPlaceholderKind phKind,
        string[] scheme,
        Dictionary<int, List<StyleLevel>> masterStyles)
    {
        var raw = block.RawText;
        var paragraphs = new List<PptxParagraph>();
        var pfCursor = new RunCursor<PfRun>(block.PfRuns, r => r.CharCount);
        var cfCursor = new RunCursor<CfRun>(block.CfRuns, r => r.CharCount);

        var defaultSize = phKind switch
        {
            PptxPlaceholderKind.CenterTitle => 40.0,
            PptxPlaceholderKind.Title => 32.0,
            PptxPlaceholderKind.Subtitle => 20.0,
            PptxPlaceholderKind.Footer => 11.0,
            _ => 18.0
        };
        var defaultAlign = phKind is PptxPlaceholderKind.CenterTitle or PptxPlaceholderKind.Subtitle
            ? DocxAlignment.Center
            : DocxAlignment.Left;
        var defaultBold = phKind is PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle;

        var pos = 0;
        while (pos <= raw.Length)
        {
            var cr = raw.IndexOf('\r', pos);
            var paraEnd = cr >= 0 ? cr : raw.Length;
            var paraLenWithSep = (cr >= 0 ? cr + 1 : raw.Length) - pos;

            var pf = pfCursor.CurrentAt(pos);
            var level = Math.Clamp(pf?.Level ?? 0, 0, 4);
            var masterLvl = ResolveMasterLevel(masterStyles, block.TextType, level);

            var align = pf?.Alignment ?? masterLvl?.Alignment ?? defaultAlign;
            var hasBullet = pf?.HasBullet ?? masterLvl?.HasBullet ?? (phKind == PptxPlaceholderKind.Body && align == DocxAlignment.Left);
            var bulletChar = pf?.BulletChar ?? masterLvl?.BulletChar ?? '•';
            var bulletFontRef = pf?.BulletFontRef ?? masterLvl?.BulletFontRef;
            var bulletFont = bulletFontRef is { } bfi && bfi >= 0 && bfi < _fonts.Count ? _fonts[bfi] : null;
            var bulletColor = (pf?.BulletColor ?? masterLvl?.BulletColor)?.Resolve(scheme);
            var marL = pf?.LeftMarginPts ?? masterLvl?.LeftMarginPts;
            var indent = pf?.IndentPts ?? masterLvl?.IndentPts;

            var runs = new List<PptxRun>();
            var segPos = pos;
            while (segPos < paraEnd)
            {
                var cf = cfCursor.CurrentAt(segPos);
                var cfRem = cfCursor.RemainingAt(segPos);
                var segEnd = cfRem > 0 ? Math.Min(paraEnd, segPos + cfRem) : paraEnd;
                if (segEnd <= segPos) segEnd = paraEnd;

                var text = CleanRunText(raw.AsSpan(segPos, segEnd - segPos));
                if (text.Length > 0)
                {
                    var bold = cf?.Bold ?? masterLvl?.Bold ?? defaultBold;
                    var italic = cf?.Italic ?? masterLvl?.Italic ?? false;
                    var underline = cf?.Underline ?? masterLvl?.Underline ?? false;
                    var size = cf?.FontSize ?? masterLvl?.FontSize ?? Math.Max(10.0, defaultSize - level * 2);
                    var color = (cf?.Color ?? masterLvl?.Color)?.Resolve(scheme);
                    var fontRef = cf?.FontRef ?? masterLvl?.FontRef;
                    var fontName = fontRef is { } fi && fi >= 0 && fi < _fonts.Count ? _fonts[fi] : "Calibri";

                    var run = new PptxRun(text, bold, italic, underline, size, color, fontName);
                    if (runs.Count > 0 && SameRunStyle(runs[^1], run))
                        runs[^1] = runs[^1] with { Text = runs[^1].Text + run.Text };
                    else
                        runs.Add(run);
                }

                segPos = segEnd;
            }

            var hasText = runs.Any(r => !string.IsNullOrWhiteSpace(r.Text));
            var bullet = hasText && hasBullet
                ? NormalizeBulletChar(bulletChar, bulletFont)
                : null;

            paragraphs.Add(new PptxParagraph(runs, align, bullet, level)
            {
                MarginLeft = hasText ? marL : null,
                Indent = hasText ? indent : null,
                BulletColor = hasText && bullet is not null ? bulletColor : null
            });

            if (cr < 0) break;
            pos = cr + 1;
            if (pos == raw.Length && paraLenWithSep > 0) break;
        }

        return paragraphs;
    }

    private static StyleLevel? ResolveMasterLevel(Dictionary<int, List<StyleLevel>> styles, int textType, int level)
    {
        if (!styles.TryGetValue(textType, out var list) && !styles.TryGetValue(1, out list) && !styles.TryGetValue(4, out list))
            return null;
        if (list.Count == 0) return null;
        return list[Math.Clamp(level, 0, list.Count - 1)];
    }

    private void ParseStyleTextPropAtom(RecordHeader rec, TextBlock block)
    {
        var pos = rec.DataStart;
        var end = rec.DataEnd;
        var textTarget = block.RawText.Length + 1;

        var coveredPf = 0;
        while (pos + 10 <= end && coveredPf < textTarget)
        {
            var count = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(pos, 4)));
            pos += 4;
            var level = BinaryPrimitives.ReadUInt16LittleEndian(_ppt.AsSpan(pos, 2));
            pos += 2;
            var pf = ReadTextPFException(_ppt, ref pos, end);
            block.PfRuns.Add(pf with { CharCount = count, Level = level });
            if (count <= 0) break;
            coveredPf += count;
        }

        var coveredCf = 0;
        while (pos + 8 <= end && coveredCf < textTarget)
        {
            var count = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(pos, 4)));
            pos += 4;
            var cf = ReadTextCFException(_ppt, ref pos, end);
            block.CfRuns.Add(cf with { CharCount = count });
            if (count <= 0) break;
            coveredCf += count;
        }
    }

    private List<StyleLevel> ParseMasterStyleAtom(RecordHeader rec)
    {
        var list = new List<StyleLevel>();
        if (rec.Length < 2) return list;

        var pos = rec.DataStart;
        var end = rec.DataEnd;
        var cLevels = Math.Min((int)BinaryPrimitives.ReadUInt16LittleEndian(_ppt.AsSpan(pos, 2)), 5);
        pos += 2;

        for (var i = 0; i < cLevels && pos + 8 <= end; i++)
        {
            if (rec.Instance >= 5 && pos + 2 <= end)
                pos += 2; // optional 2-byte level field when textType >= 5

            var pf = ReadTextPFException(_ppt, ref pos, end);
            var cf = pos + 4 <= end ? ReadTextCFException(_ppt, ref pos, end) : new CfRun(0, null, null, null, null, null, null);
            list.Add(new StyleLevel(
                pf.HasBullet, pf.BulletChar, pf.BulletFontRef, pf.BulletColor, pf.Alignment, pf.LeftMarginPts, pf.IndentPts,
                cf.Bold, cf.Italic, cf.Underline, cf.FontRef, cf.FontSize, cf.Color));
        }

        return list;
    }

    private static PfRun ReadTextPFException(byte[] data, ref int pos, int end)
    {
        if (pos + 4 > end) { pos = end; return new PfRun(0, 0, null, null, null, null, null, null, null); }
        var masks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        bool? hasBullet = null;
        char? bulletChar = null;
        int? bulletFontRef = null;
        ColorRef? bulletColor = null;
        DocxAlignment? alignment = null;
        double? leftMarginPts = null;
        double? indentPts = null;

        if ((masks & 0x000F) != 0 && pos + 2 <= end)
        {
            var bulletFlags = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
            if ((masks & 0x0001) != 0)
                hasBullet = (bulletFlags & 0x0001) != 0;
        }

        if ((masks & 0x0080) != 0 && pos + 2 <= end)
        {
            bulletChar = (char)BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
        }

        if ((masks & 0x0010) != 0 && pos + 2 <= end)
        {
            bulletFontRef = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
        }

        if ((masks & 0x0040) != 0 && pos + 2 <= end)
            pos += 2; // bulletSize

        if ((masks & 0x0020) != 0 && pos + 4 <= end)
        {
            bulletColor = new ColorRef(data[pos], data[pos + 1], data[pos + 2], data[pos + 3]);
            pos += 4;
        }

        if ((masks & 0x0800) != 0 && pos + 2 <= end)
        {
            alignment = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2)) switch
            {
                1 => DocxAlignment.Center,
                2 => DocxAlignment.Right,
                3 or 4 => DocxAlignment.Justify,
                _ => DocxAlignment.Left
            };
            pos += 2;
        }

        if ((masks & 0x1000) != 0 && pos + 2 <= end) pos += 2; // lineSpacing
        if ((masks & 0x2000) != 0 && pos + 2 <= end) pos += 2; // spaceBefore
        if ((masks & 0x4000) != 0 && pos + 2 <= end) pos += 2; // spaceAfter

        if ((masks & 0x0100) != 0 && pos + 2 <= end)
        {
            leftMarginPts = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos, 2)) / MasterUnitsPerPoint;
            pos += 2;
        }

        if ((masks & 0x0400) != 0 && pos + 2 <= end)
        {
            indentPts = BinaryPrimitives.ReadInt16LittleEndian(data.AsSpan(pos, 2)) / MasterUnitsPerPoint;
            pos += 2;
        }

        if ((masks & 0x8000) != 0 && pos + 2 <= end) pos += 2; // defaultTabSize

        if ((masks & 0x00100000) != 0 && pos + 2 <= end)
        {
            var tabCount = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos = Math.Min(end, pos + 2 + tabCount * 4);
        }

        if ((masks & 0x00010000) != 0 && pos + 2 <= end) pos += 2; // fontAlign
        if ((masks & 0x000E0000) != 0 && pos + 2 <= end) pos += 2; // wrapFlags
        if ((masks & 0x00200000) != 0 && pos + 2 <= end) pos += 2; // textDirection

        return new PfRun(0, 0, hasBullet, bulletChar, bulletFontRef, bulletColor, alignment, leftMarginPts, indentPts);
    }

    private static CfRun ReadTextCFException(byte[] data, ref int pos, int end)
    {
        if (pos + 4 > end) { pos = end; return new CfRun(0, null, null, null, null, null, null); }
        var masks = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos, 4));
        pos += 4;

        bool? bold = null;
        bool? italic = null;
        bool? underline = null;
        int? fontRef = null;
        double? fontSize = null;
        ColorRef? color = null;

        if ((masks & 0x0000FFFF) != 0 && pos + 2 <= end)
        {
            var style = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
            if ((masks & 0x0001) != 0) bold = (style & 0x0001) != 0;
            if ((masks & 0x0002) != 0) italic = (style & 0x0002) != 0;
            if ((masks & 0x0004) != 0) underline = (style & 0x0004) != 0;
        }

        if ((masks & 0x00010000) != 0 && pos + 2 <= end)
        {
            fontRef = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
        }

        if ((masks & 0x00200000) != 0 && pos + 2 <= end) pos += 2; // oldEATypeface
        if ((masks & 0x00400000) != 0 && pos + 2 <= end)
        {
            fontRef ??= BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            pos += 2;
        }
        if ((masks & 0x00800000) != 0 && pos + 2 <= end) pos += 2; // symbolTypeface

        if ((masks & 0x00020000) != 0 && pos + 2 <= end)
        {
            var sz = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos, 2));
            if (sz is >= 4 and <= 400) fontSize = sz;
            pos += 2;
        }

        if ((masks & 0x00040000) != 0 && pos + 4 <= end)
        {
            color = new ColorRef(data[pos], data[pos + 1], data[pos + 2], data[pos + 3]);
            pos += 4;
        }

        if ((masks & 0x00080000) != 0 && pos + 2 <= end) pos += 2; // position

        return new CfRun(0, bold, italic, underline, fontRef, fontSize, color);
    }

    private void ParseFopt(RecordHeader foptRec, FoptProps props)
    {
        var count = (int)foptRec.Instance;
        var tableEnd = foptRec.DataStart + count * 6;
        if (tableEnd > foptRec.DataEnd) return;

        for (var i = 0; i < count; i++)
        {
            var entryPos = foptRec.DataStart + i * 6;
            var rawId = BinaryPrimitives.ReadUInt16LittleEndian(_ppt.AsSpan(entryPos, 2));
            var propId = (ushort)(rawId & 0x3FFF);
            var op = BinaryPrimitives.ReadUInt32LittleEndian(_ppt.AsSpan(entryPos + 2, 4));

            switch (propId)
            {
                case 0x0081: props.TextLeftPts = (int)op / EmuPerPoint; break;
                case 0x0082: props.TextTopPts = (int)op / EmuPerPoint; break;
                case 0x0083: props.TextRightPts = (int)op / EmuPerPoint; break;
                case 0x0084: props.TextBottomPts = (int)op / EmuPerPoint; break;
                case 0x0087:
                    props.Anchor = op switch
                    {
                        1 or 4 => PptxAnchor.Middle,
                        2 or 5 => PptxAnchor.Bottom,
                        _ => PptxAnchor.Top
                    };
                    break;
                case 0x0104: props.Pib = op; break;
                case 0x0180: props.FillType = op; break;
                case 0x0181: props.FillColorRaw = op; break;
                case 0x0183: props.FillBackColorRaw = op; break;
                case 0x01BF:
                    if ((op & (1u << 20)) != 0) // fUsefFilled
                        props.Filled = (op & (1u << 4)) != 0;
                    break;
                case 0x01C0: props.LineColorRaw = op; break;
                case 0x01CB: props.LineWidthEmu = op; break;
                case 0x01FF:
                    if ((op & (1u << 19)) != 0) // fUsefLine
                        props.HasLine = (op & (1u << 3)) != 0;
                    break;
                case 0x03A9:
                    if ((op & 0x0001) != 0)
                        props.IsTable = true;
                    break;
            }
        }
    }

    private string[] ParseColorScheme(RecordHeader rec)
    {
        var count = Math.Min(8, rec.Length / 4);
        var scheme = (string[])DefaultScheme.Clone();
        for (var i = 0; i < count; i++)
        {
            var p = rec.DataStart + i * 4;
            scheme[i] = $"#{_ppt[p]:X2}{_ppt[p + 1]:X2}{_ppt[p + 2]:X2}";
        }
        return scheme;
    }

    private static string? ResolveEscherColor(uint? raw, string[] scheme)
    {
        if (raw is not { } v) return null;
        var r = (byte)(v & 0xFF);
        var g = (byte)((v >> 8) & 0xFF);
        var b = (byte)((v >> 16) & 0xFF);
        var flags = (byte)((v >> 24) & 0xFF);

        if ((flags & 0x08) != 0) // fSchemeIndex
            return r < scheme.Length ? scheme[r] : $"#{r:X2}{g:X2}{b:X2}";
        if ((flags & 0x10) != 0) // fSysIndex (system color reference without resolved RGB)
            return null;
        return $"#{r:X2}{g:X2}{b:X2}";
    }

    private RectPts? ParseClientAnchor(RecordHeader rec)
    {
        if (rec.Length == 8)
        {
            // SmallRectStruct: top, left, right, bottom (int16 in master units)
            var top = BinaryPrimitives.ReadInt16LittleEndian(_ppt.AsSpan(rec.DataStart, 2)) / MasterUnitsPerPoint;
            var left = BinaryPrimitives.ReadInt16LittleEndian(_ppt.AsSpan(rec.DataStart + 2, 2)) / MasterUnitsPerPoint;
            var right = BinaryPrimitives.ReadInt16LittleEndian(_ppt.AsSpan(rec.DataStart + 4, 2)) / MasterUnitsPerPoint;
            var bottom = BinaryPrimitives.ReadInt16LittleEndian(_ppt.AsSpan(rec.DataStart + 6, 2)) / MasterUnitsPerPoint;
            return NormalizeRect(left, top, right, bottom);
        }

        if (rec.Length >= 16)
        {
            // RectStruct: left, top, right, bottom (int32 in master units)
            var left = ReadInt32(_ppt, rec.DataStart) / MasterUnitsPerPoint;
            var top = ReadInt32(_ppt, rec.DataStart + 4) / MasterUnitsPerPoint;
            var right = ReadInt32(_ppt, rec.DataStart + 8) / MasterUnitsPerPoint;
            var bottom = ReadInt32(_ppt, rec.DataStart + 12) / MasterUnitsPerPoint;
            return NormalizeRect(left, top, right, bottom);
        }

        return null;
    }

    private RectPts? ParseChildAnchor(RecordHeader rec, RectPts parentRect, RectPts groupBounds)
    {
        if (rec.Length < 16) return null;
        var cLeft = ReadInt32(_ppt, rec.DataStart);
        var cTop = ReadInt32(_ppt, rec.DataStart + 4);
        var cRight = ReadInt32(_ppt, rec.DataStart + 8);
        var cBottom = ReadInt32(_ppt, rec.DataStart + 12);

        if (Math.Abs(groupBounds.Width) < 1e-6 || Math.Abs(groupBounds.Height) < 1e-6)
            return NormalizeRect(cLeft / MasterUnitsPerPoint, cTop / MasterUnitsPerPoint, cRight / MasterUnitsPerPoint, cBottom / MasterUnitsPerPoint);

        var x1 = parentRect.X + (cLeft - groupBounds.X) * parentRect.Width / groupBounds.Width;
        var y1 = parentRect.Y + (cTop - groupBounds.Y) * parentRect.Height / groupBounds.Height;
        var x2 = parentRect.X + (cRight - groupBounds.X) * parentRect.Width / groupBounds.Width;
        var y2 = parentRect.Y + (cBottom - groupBounds.Y) * parentRect.Height / groupBounds.Height;
        return NormalizeRect(x1, y1, x2, y2);
    }

    private static RectPts NormalizeRect(double left, double top, double right, double bottom)
    {
        var x = Math.Min(left, right);
        var y = Math.Min(top, bottom);
        var w = Math.Abs(right - left);
        var h = Math.Abs(bottom - top);
        return new RectPts(x, y, w, h);
    }

    private static PptxPlaceholderKind ClassifyPlaceholder(byte? phId, int? textType)
    {
        if (phId is { } id)
        {
            return id switch
            {
                1 or 13 => PptxPlaceholderKind.Title,
                3 or 15 => PptxPlaceholderKind.CenterTitle,
                16 => PptxPlaceholderKind.Subtitle,
                8 => PptxPlaceholderKind.Footer,
                2 or 14 or 17 or 18 or 19 or 20 or 21 or 22 or 23 or 24 => PptxPlaceholderKind.Body,
                _ => PptxPlaceholderKind.None
            };
        }

        if (textType is { } tt)
            return ClassifyTextType(tt);

        return PptxPlaceholderKind.None;
    }

    private static PptxPlaceholderKind ClassifyTextType(int textType) => textType switch
    {
        0 => PptxPlaceholderKind.Title,
        6 => PptxPlaceholderKind.CenterTitle,
        1 or 5 or 7 or 8 => PptxPlaceholderKind.Body,
        _ => PptxPlaceholderKind.None
    };

    private RectPts DefaultPlaceholderGeometry(PptxPlaceholderKind kind, ref int bodyOrder)
    {
        var w = _slideWidth;
        var h = _slideHeight;
        return kind switch
        {
            PptxPlaceholderKind.CenterTitle => new RectPts(w * 0.08, h * 0.25, w * 0.84, h * 0.25),
            PptxPlaceholderKind.Title => new RectPts(w * 0.06, h * 0.07, w * 0.88, h * 0.16),
            PptxPlaceholderKind.Subtitle => new RectPts(w * 0.10, h * 0.52, w * 0.80, h * 0.20),
            PptxPlaceholderKind.Footer => new RectPts(w * 0.06, h * 0.90, w * 0.88, h * 0.06),
            _ => new RectPts(w * 0.06, Math.Min(h * 0.25 + (bodyOrder++ * 40.0), h * 0.75), w * 0.88, Math.Max(h * 0.62, 100.0))
        };
    }

    private static string NormalizeBulletChar(char ch, string? font)
    {
        if (font is not null && font.Contains("Wingdings", StringComparison.OrdinalIgnoreCase))
        {
            switch (ch)
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

        return ch switch
        {
            '\uF0B7' or '\uF06C' => "•",
            '\uF0A7' or '\uF0FE' => "▪",
            '\uF076' or '\uF0D8' => "▸",
            '\uF0FC' => "✓",
            '\0' => "•",
            _ => ch.ToString()
        };
    }

    private static bool SameRunStyle(PptxRun a, PptxRun b) =>
        a.Bold == b.Bold && a.Italic == b.Italic && a.Underline == b.Underline &&
        Math.Abs(a.FontSize - b.FontSize) < 0.05 && a.Color == b.Color && a.FontFamily == b.FontFamily;

    private static string DecodeUtf16Text(ReadOnlySpan<byte> bytes) =>
        Encoding.Unicode.GetString(bytes[..(bytes.Length & ~1)]);

    private static string DecodeAnsiText(ReadOnlySpan<byte> bytes) =>
        LegacyAnsiEncoding.GetString(bytes);

    private static string CleanRunText(ReadOnlySpan<char> span)
    {
        var sb = new StringBuilder(span.Length);
        foreach (var ch in span)
        {
            if (ch == '\v') sb.Append('\n');
            else if (ch >= ' ' || ch == '\t' || ch == '\n') sb.Append(ch);
        }
        return sb.ToString();
    }

    private static string CleanPlainText(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (var ch in raw)
        {
            if (ch is '\r' or '\v') sb.Append('\n');
            else if (ch >= ' ' || ch == '\t' || ch == '\n') sb.Append(ch);
        }
        return sb.ToString().Trim();
    }

    private static bool TryReadHeader(byte[] buffer, int offset, int limit, out RecordHeader header)
    {
        if (offset < 0 || offset + 8 > limit || offset + 8 > buffer.Length)
        {
            header = default;
            return false;
        }

        var verInst = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset, 2));
        var type = BinaryPrimitives.ReadUInt16LittleEndian(buffer.AsSpan(offset + 2, 2));
        var rawLen = BinaryPrimitives.ReadUInt32LittleEndian(buffer.AsSpan(offset + 4, 4));
        var dataStart = offset + 8;
        var maxLen = Math.Max(0, limit - dataStart);
        var len = (int)Math.Min(rawLen, (uint)maxLen);

        header = new RecordHeader((byte)(verInst & 0x0F), (ushort)(verInst >> 4), type, len, dataStart, dataStart + len);
        return true;
    }

    private static IEnumerable<RecordHeader> ChildrenOf(byte[] buffer, int start, int end)
    {
        var pos = start;
        var limit = Math.Min(end, buffer.Length);
        while (pos + 8 <= limit)
        {
            if (!TryReadHeader(buffer, pos, limit, out var rec))
                yield break;
            yield return rec;
            if (rec.DataEnd <= pos)
                yield break;
            pos = rec.DataEnd;
        }
    }

    private static int ReadInt32(byte[] buffer, int offset) =>
        BinaryPrimitives.ReadInt32LittleEndian(buffer.AsSpan(offset, 4));

    private sealed class RunCursor<T> where T : class
    {
        private readonly List<T> _runs;
        private readonly Func<T, int> _lengthOf;
        private int _index;
        private int _runStart;

        public RunCursor(List<T> runs, Func<T, int> lengthOf)
        {
            _runs = runs;
            _lengthOf = lengthOf;
        }

        public T? CurrentAt(int charPos)
        {
            AdvanceTo(charPos);
            return _index < _runs.Count ? _runs[_index] : null;
        }

        public int RemainingAt(int charPos)
        {
            AdvanceTo(charPos);
            if (_index >= _runs.Count) return 0;
            return Math.Max(1, _runStart + _lengthOf(_runs[_index]) - charPos);
        }

        private void AdvanceTo(int charPos)
        {
            while (_index < _runs.Count)
            {
                var len = _lengthOf(_runs[_index]);
                if (len <= 0 || charPos < _runStart + len)
                    break;
                _runStart += len;
                _index++;
            }
        }
    }
}
