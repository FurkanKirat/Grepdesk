using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using Grepdesk.Core.Viewer;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI.Viewer;

/// <summary>A Word document read and its pictures decoded, off the UI thread.</summary>
internal sealed class DocxPreview : IDisposable
{
    public required DocxDocument Document { get; init; }
    public required string BaseDirectory { get; init; }
    public required IReadOnlyDictionary<byte[], Bitmap> Images { get; init; }
    /// <summary>False when the pictures belong to an editor that lays the document out again and again.</summary>
    public bool OwnsImages { get; init; } = true;

    public static DocxPreview Load(string path, CancellationToken ct)
    {
        var images = new Dictionary<byte[], Bitmap>(ReferenceEqualityComparer.Instance);
        try
        {
            return From(DocxDocument.Load(path, ct), Path.GetDirectoryName(path) ?? "", images, ct);
        }
        catch
        {
            foreach (var bitmap in images.Values) bitmap.Dispose();
            throw;
        }
    }

    /// <summary>Decodes the pictures a layout needs into <paramref name="images"/>, keeping any already there.</summary>
    public static DocxPreview From(DocxDocument document, string baseDirectory, Dictionary<byte[], Bitmap> images, CancellationToken ct, bool ownsImages = true)
    {
        foreach (var image in Inlines(document).OfType<DocxImage>())
        {
            ct.ThrowIfCancellationRequested();
            Decode(image.Data, images);
        }
        return new DocxPreview { Document = document, BaseDirectory = baseDirectory, Images = images, OwnsImages = ownsImages };
    }

    public static Bitmap? Decode(byte[] data, Dictionary<byte[], Bitmap> images)
    {
        if (images.TryGetValue(data, out var known)) return known;
        try
        {
            using var stream = new MemoryStream(data);
            return images[data] = new Bitmap(stream);
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null; // a picture Skia can't decode is left out
        }
    }

    private static IEnumerable<DocxInline> Inlines(DocxDocument document) =>
        document.Body.Concat(document.Notes)
            .Concat(document.Sections.SelectMany(s => s.Header.Concat(s.Footer).Concat(s.FirstHeader).Concat(s.FirstFooter)))
            .SelectMany(Inlines);

    private static IEnumerable<DocxInline> Inlines(DocxBlock block) => block switch
    {
        DocxParagraph p => p.Inlines,
        DocxTable t => t.Cells.SelectMany(c => c.Blocks).SelectMany(Inlines),
        _ => [],
    };

    public void Dispose()
    {
        if (OwnsImages) foreach (var bitmap in Images.Values) bitmap.Dispose();
    }
}

/// <summary>
/// A Word document as white pages: styled text, lists, tables, pictures,
/// links, headers and footers with their page numbers, and footnotes at the end. Read-only; text
/// can be selected and copied a paragraph at a time. Zooms like the image
/// view: Ctrl+wheel, + and -, 0 to fit the width.
/// <para>
/// With an editor, the same pages hold editable paragraphs and a formatting
/// toolbar sits on top; headers, footers and notes stay as they are, dimmed.
/// </para>
/// </summary>
internal sealed class DocxView : DockPanel, ISearchableViewerBody
{
    private const double MinZoom = 0.25, MaxZoom = 4, Step = 1.15;
    private const double PageGap = 20;
    internal const double Dip = 96.0 / 72; // points to device-independent pixels

    private static LocalizationService Loc => LocalizationService.Instance;

    internal static readonly IBrush Ink = Brushes.Black;
    private static readonly IBrush PageBrush = Brushes.White;
    private static readonly IBrush TableBorder = Brush.Parse("#a0a0a0");
    internal static readonly IBrush SelectionBrush = new ImmutableSolidColorBrush(Color.Parse("#3390ff"), 0.35);
    internal static readonly IBrush SearchHighlight = new ImmutableSolidColorBrush(Color.Parse("#ffd400"), 0.55);
    internal static readonly IBrush ActiveSearchHighlight = new ImmutableSolidColorBrush(Color.Parse("#ff8c00"), 0.80);
    private static readonly Dictionary<string, IBrush> ColorBrushes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FontFamily> Fonts = new(StringComparer.OrdinalIgnoreCase);

    private sealed record SearchEntry(
        DocxTextBlock Block,
        List<DocxText> Pieces,
        DocxRunStyle ParagraphStyle,
        int IndentChars,
        string FullText);

    private DocxPreview _preview;
    private DocxDocument _doc;
    private readonly DocxEditor? _editor;
    private readonly string? _highlight;
    private readonly Action<string> _open, _reveal;
    private readonly ScrollViewer _scroll;
    private readonly LayoutTransformControl _zoomHost;
    private readonly TextBlock _zoomText;
    private readonly List<SearchEntry> _searchEntries = [];
    private List<DocxSearchMatch> _readMatches = [];
    private Dictionary<int, List<(int GlobalIndex, TextSpan Span)>> _readMatchesByEntry = new();
    private readonly HashSet<int> _highlightedEntries = [];
    private int _activeReadMatch = -1;
    private bool _interactiveSearchActive;
    private bool _fit = true;
    private string _pageLabel = "1", _pageCount = "1"; // what PAGE and NUMPAGES fields show on the page being built
    private double _zoom = 1;

    public string? InitialSearchQuery => _highlight;

    public DocxView(DocxPreview preview, string? highlight, Action<string> open, Action<string> reveal, DocxEditor? editor = null)
    {
        _preview = preview;
        _doc = preview.Document;
        _editor = editor;
        _highlight = string.IsNullOrEmpty(highlight) ? null : highlight;
        _open = open;
        _reveal = reveal;

        _zoomHost = new LayoutTransformControl { Child = BuildPages() };
        _scroll = new ScrollViewer
        {
            Content = _zoomHost,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Background = Brush.Parse("#11111b"),
            Focusable = true,
        };
        _scroll.SizeChanged += (_, _) => { if (_fit) ApplyZoom(); };
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var zoomOut = ViewerBar.Button("−", Loc.Get("ViewerZoomOut"));
        var zoomIn = ViewerBar.Button("+", Loc.Get("ViewerZoomIn"));
        var fit = ViewerBar.Button(Loc.Get("ViewerZoomFitWidth"));
        var actual = ViewerBar.Button("100%", Loc.Get("ViewerZoomActual"));
        _zoomText = ViewerBar.Label("", 52);
        zoomOut.Click += (_, _) => SetZoom(_zoom / Step);
        zoomIn.Click += (_, _) => SetZoom(_zoom * Step);
        fit.Click += (_, _) => { _fit = true; ApplyZoom(); };
        actual.Click += (_, _) => SetZoom(1);

        var zoom = ViewerBar.Row(zoomOut, _zoomText, zoomIn, fit, actual);
        zoom.HorizontalAlignment = HorizontalAlignment.Right;
        Children.Add(ViewerBar.Create(zoom));
        if (editor is not null)
        {
            SetDock(editor.Toolbar, Dock.Top);
            Children.Add(editor.Toolbar);
        }
        Children.Add(_scroll);
        ApplyZoom();
        editor?.Attach(this);
        if (editor is null && _highlight is not null)
            ScrollToInitialHighlight();
    }

    public bool IsEditing => _editor is not null;

    public double Zoom => _zoom;

    public ScrollViewer Scroll => _scroll;

    /// <summary>The panel holding the pages, for finding what's under the pointer.</summary>
    public Control Pages => _zoomHost.Child!;

    private StackPanel BuildPages()
    {
        _searchEntries.Clear();
        _editor?.BeginBuild();
        var pages = new StackPanel { Spacing = PageGap, Margin = new Thickness(PageGap), HorizontalAlignment = HorizontalAlignment.Center };
        var split = SplitPages();
        var labels = PageLabels(split);
        _pageCount = split.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        for (var i = 0; i < split.Count; i++)
        {
            _pageLabel = labels[i];
            pages.Children.Add(BuildPage(split[i]));
        }
        AddNotes((Panel)((Border)pages.Children[^1]).Child!);
        return pages;
    }

    /// <summary>Shows a new layout of the edited document, scrolled where it was.</summary>
    public void Rebuild(DocxPreview preview)
    {
        var offset = _scroll.Offset;
        var old = _preview;
        _preview = preview;
        _doc = preview.Document;
        _zoomHost.Child = BuildPages();
        old.Dispose();
        Avalonia.Threading.Dispatcher.UIThread.Post(() => _scroll.Offset = offset, Avalonia.Threading.DispatcherPriority.Loaded);
    }

    // =====================================================================
    // Pages
    // =====================================================================

    private sealed record Page(List<DocxBlock> Blocks, int Section, bool FirstOfSection);

    private List<Page> SplitPages()
    {
        var pages = new List<Page> { new([], 0, true) };
        foreach (var block in _doc.Body)
        {
            if (block is DocxPageBreak pageBreak)
            {
                var section = Math.Clamp(pageBreak.Section, 0, _doc.Sections.Count - 1);
                pages.Add(new Page([], section, section != pages[^1].Section));
            }
            else pages[^1].Blocks.Add(block);
        }
        return pages;
    }

    /// <summary>Page numbers as each page shows them: per section, restarting and in its format where it says so.</summary>
    private List<string> PageLabels(List<Page> pages)
    {
        var labels = new List<string>();
        var number = 0;
        foreach (var page in pages)
        {
            var section = _doc.Sections[page.Section];
            number = page.FirstOfSection && section.NumberStart is { } start ? start : number + 1;
            labels.Add(DocxDocument.FormatNumber(number, section.NumberFormat));
        }
        return labels;
    }

    /// <summary>A sheet of paper: header and footer in the margins, the body between them.</summary>
    private Control BuildPage(Page page)
    {
        var section = _doc.Sections[page.Section];
        var left = section.MarginLeft * Dip;
        var right = section.MarginRight * Dip;

        var body = new StackPanel { Margin = new Thickness(left, section.MarginTop * Dip, right, section.MarginBottom * Dip) };
        AddBlocks(page.Blocks, body);

        var sheet = new Panel { Width = section.PageWidth * Dip, MinHeight = section.PageHeight * Dip, Background = PageBrush };
        sheet.Children.Add(body);

        var first = page.FirstOfSection && section.TitlePage;
        var headerBlocks = first ? section.FirstHeader : section.Header;
        var footerBlocks = first ? section.FirstFooter : section.Footer;
        if (headerBlocks.Count > 0)
        {
            var header = new StackPanel { Margin = new Thickness(left, section.HeaderDistance * Dip, right, 0), VerticalAlignment = VerticalAlignment.Top, Opacity = IsEditing ? 0.45 : 1 };
            AddBlocks(headerBlocks, header);
            sheet.Children.Add(header);
        }
        if (footerBlocks.Count > 0)
        {
            var footer = new StackPanel { Margin = new Thickness(left, 0, right, section.FooterDistance * Dip), VerticalAlignment = VerticalAlignment.Bottom, Opacity = IsEditing ? 0.45 : 1 };
            AddBlocks(footerBlocks, footer);
            sheet.Children.Add(footer);
        }

        return new Border
        {
            Child = sheet,
            BoxShadow = BoxShadows.Parse("0 2 10 0 #80000000"),
        };
    }

    /// <summary>Footnotes and endnotes after the last page's text, under a short rule.</summary>
    private void AddNotes(Panel lastPage)
    {
        if (_doc.Notes.Count == 0 || lastPage.Children[0] is not StackPanel body) return;
        var notes = new StackPanel { Opacity = IsEditing ? 0.45 : 1 };
        body.Children.Add(notes);
        body = notes;
        body.Children.Add(new Border
        {
            Width = 144,
            Height = 1,
            Background = Ink,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(0, 18, 0, 6),
        });
        AddBlocks(_doc.Notes, body);
    }

    // =====================================================================
    // Blocks
    // =====================================================================

    private void AddBlocks(IEnumerable<DocxBlock> blocks, Panel target)
    {
        foreach (var block in blocks)
        {
            switch (block)
            {
                case DocxParagraph paragraph:
                    target.Children.Add(BuildParagraph(paragraph));
                    break;
                case DocxTable table:
                    target.Children.Add(BuildTable(table));
                    break;
            }
        }
    }

    /// <summary>A paragraph with its margins, shading and list marker: the editor builds these for paragraphs it adds.</summary>
    public Control BuildParagraph(DocxParagraph p)
    {
        if (_editor is not null && p.Source is { } source && _editor.Document.Find(source) is { } model)
        {
            var editable = _editor.CreateControl(model, p);
            var wrapper = WrapParagraph(p, editable);
            _editor.Register(model, editable, wrapper);
            return wrapper;
        }
        return WrapParagraph(p, BuildContent(p));
    }

    private StackPanel BuildContent(DocxParagraph p)
    {
        var f = p.Format;
        var content = new StackPanel();
        var pieces = new List<DocxText>();

        void Flush()
        {
            if (pieces.Count == 0) return;
            // A first-line indent, which text blocks lack, as em spaces.
            var indent = content.Children.Count == 0 && p.Marker is null && f.FirstLine > 0 ? f.FirstLine : 0;
            content.Children.Add(BuildText(pieces, f.Alignment, p.MarkStyle, indent));
            pieces.Clear();
        }

        foreach (var inline in p.Inlines)
        {
            switch (inline)
            {
                case DocxText text:
                    pieces.Add(text);
                    break;
                case DocxPageNumber number:
                    pieces.Add(new DocxText(number.Total ? _pageCount : _pageLabel, number.Style, null));
                    break;
                case DocxImage image when _preview.Images.TryGetValue(image.Data, out var bitmap):
                    Flush();
                    content.Children.Add(BuildImage(image, bitmap, f.Alignment));
                    break;
            }
        }
        // Text that's only the line breaks around a picture would add blank lines.
        if (content.Children.Count > 0 && pieces.All(t => string.IsNullOrWhiteSpace(t.Text))) pieces.Clear();
        Flush();
        // An empty paragraph is still a line of its font's height.
        if (content.Children.Count == 0) content.Children.Add(BuildText([new DocxText(" ", p.MarkStyle, null)], f.Alignment, p.MarkStyle, 0, registerSearch: false));
        return content;
    }

    private Control WrapParagraph(DocxParagraph p, Control content)
    {
        var f = p.Format;
        var left = f.IndentLeft;
        Control control = content;
        if (p.Marker is { } marker)
        {
            // The marker hangs in the indent: Word's usual "left 0.5in, hanging 0.25in".
            var hanging = f.FirstLine < 0 ? -f.FirstLine : 0;
            left = Math.Max(0, f.IndentLeft - hanging);
            var markerBlock = BuildText([new DocxText(marker, p.MarkStyle, null)], DocxAlignment.Left, p.MarkStyle, 0, registerSearch: false);
            markerBlock.Margin = new Thickness(0, 0, 6, 0);
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            var column = new Border { MinWidth = Math.Max(hanging, 0) * Dip, Child = markerBlock };
            grid.Children.Add(column);
            Grid.SetColumn(content, 1);
            grid.Children.Add(content);
            control = grid;
        }

        return new Border
        {
            Background = BrushOf(f.Shading),
            Margin = new Thickness(Math.Max(0, left) * Dip, f.SpaceBefore * Dip, Math.Max(0, f.IndentRight) * Dip, f.SpaceAfter * Dip),
            Child = control,
        };
    }

    private static Control BuildImage(DocxImage image, Bitmap bitmap, DocxAlignment alignment)
    {
        var view = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = alignment switch
            {
                DocxAlignment.Center => HorizontalAlignment.Center,
                DocxAlignment.Right => HorizontalAlignment.Right,
                _ => HorizontalAlignment.Left,
            },
        };
        // The size it's placed at in the document; pictures without one keep their own.
        if (image.Width > 0 && image.Height > 0)
        {
            view.MaxWidth = image.Width * Dip;
            view.MaxHeight = image.Height * Dip;
        }
        else view.StretchDirection = StretchDirection.DownOnly;
        RenderOptions.SetBitmapInterpolationMode(view, BitmapInterpolationMode.HighQuality);
        return view;
    }

    private Control BuildTable(DocxTable table)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 2, 0, 2) };
        foreach (var width in table.ColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(width > 0 ? new GridLength(width * Dip) : GridLength.Auto));
        for (var r = 0; r < table.RowCount; r++) grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        foreach (var cell in table.Cells)
        {
            var content = new StackPanel();
            AddBlocks(cell.Blocks, content);
            var border = new Border
            {
                BorderBrush = table.Borders ? TableBorder : null,
                BorderThickness = table.Borders ? new Thickness(cell.Column == 0 ? 1 : 0, cell.Row == 0 ? 1 : 0, 1, 1) : default,
                Background = BrushOf(cell.Shading),
                Padding = new Thickness(5.4 * Dip, 0),
                Child = content,
            };
            Grid.SetRow(border, cell.Row);
            Grid.SetColumn(border, cell.Column);
            Grid.SetRowSpan(border, Math.Max(1, cell.RowSpan));
            Grid.SetColumnSpan(border, Math.Max(1, cell.ColumnSpan));
            grid.Children.Add(border);
        }
        return grid;
    }

    // =====================================================================
    // Text
    // =====================================================================

    private sealed record LinkSpan(int Start, int End, string Url);

    private DocxTextBlock BuildText(List<DocxText> pieces, DocxAlignment alignment, DocxRunStyle paragraph, double firstLine, bool registerSearch = true)
    {
        var block = new DocxTextBlock
        {
            JustifyLines = alignment == DocxAlignment.Justify,
            TextWrapping = TextWrapping.Wrap,
            Foreground = Ink,
            SelectionBrush = SelectionBrush,
            FontSize = paragraph.Size * Dip,
            FontFamily = Font(paragraph.Font) ?? FontFamily.Default,
            TextAlignment = alignment switch
            {
                DocxAlignment.Center => TextAlignment.Center,
                DocxAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
        };

        var indentChars = firstLine > 0 ? Math.Max(1, (int)Math.Round(firstLine / Math.Max(1, paragraph.Size))) : 0;
        var copiedPieces = pieces.ToList();
        var links = new List<LinkSpan>();
        var offset = indentChars;

        foreach (var piece in copiedPieces)
        {
            if (piece.Text.Length == 0) continue;
            if (piece.Link is not null) links.Add(new LinkSpan(offset, offset + piece.Text.Length, piece.Link));
            offset += piece.Text.Length;
        }

        var entry = new SearchEntry(block, copiedPieces, paragraph, indentChars, string.Concat(copiedPieces.Select(p => p.Text)));
        PopulateInlines(entry, matches: null, activeGlobalIndex: -1, fallbackHighlight: _interactiveSearchActive ? null : _highlight);
        if (registerSearch && _editor is null)
            _searchEntries.Add(entry);

        if (links.Count > 0) AttachLinks(block, links);
        return block;
    }

    private void PopulateInlines(
        SearchEntry entry,
        List<(int GlobalIndex, TextSpan Span)>? matches,
        int activeGlobalIndex,
        string? fallbackHighlight)
    {
        var inlines = new InlineCollection();
        if (entry.IndentChars > 0)
            inlines.Add(new Run(new string(' ', entry.IndentChars)) { FontSize = entry.ParagraphStyle.Size * Dip });

        if (matches is null || matches.Count == 0)
        {
            foreach (var piece in entry.Pieces)
            {
                if (piece.Text.Length == 0) continue;
                foreach (var (part, marked) in SplitHighlight(piece.Text, fallbackHighlight))
                    inlines.Add(MakeRun(part, piece.Style, marked ? 1 : 0));
            }
            entry.Block.Inlines = inlines;
            return;
        }

        var pieceStart = 0;
        foreach (var piece in entry.Pieces)
        {
            if (piece.Text.Length == 0) continue;
            var pieceEnd = pieceStart + piece.Text.Length;
            var pos = 0;

            foreach (var (globalIndex, span) in matches)
            {
                var spanEnd = span.Offset + span.Length;
                if (spanEnd <= pieceStart) continue;
                if (span.Offset >= pieceEnd) break;

                var localStart = Math.Max(pos, span.Offset - pieceStart);
                var localEnd = Math.Min(piece.Text.Length, spanEnd - pieceStart);
                if (localEnd <= localStart) continue;

                if (localStart > pos)
                    inlines.Add(MakeRun(piece.Text[pos..localStart], piece.Style, 0));

                var state = globalIndex == activeGlobalIndex ? 2 : 1;
                inlines.Add(MakeRun(piece.Text[localStart..localEnd], piece.Style, state));
                pos = localEnd;
            }

            if (pos < piece.Text.Length)
                inlines.Add(MakeRun(piece.Text[pos..], piece.Style, 0));

            pieceStart = pieceEnd;
        }

        entry.Block.Inlines = inlines;
    }

    private static Run MakeRun(string text, DocxRunStyle style, int highlightState)
    {
        var run = new Run(text)
        {
            FontSize = style.Size * Dip * (style.Baseline == DocxBaseline.Normal ? 1 : 0.65),
            FontWeight = style.Bold ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = style.Italic ? FontStyle.Italic : FontStyle.Normal,
            Foreground = BrushOf(style.Color) ?? Ink,
            Background = highlightState switch
            {
                2 => ActiveSearchHighlight,
                1 => SearchHighlight,
                _ => BrushOf(style.Highlight),
            },
            BaselineAlignment = style.Baseline switch
            {
                DocxBaseline.Superscript => BaselineAlignment.Superscript,
                DocxBaseline.Subscript => BaselineAlignment.Subscript,
                _ => BaselineAlignment.Baseline,
            },
        };
        if (Font(style.Font) is { } font) run.FontFamily = font;
        if (style.Underline || style.Strike)
        {
            var decorations = new TextDecorationCollection();
            if (style.Underline) decorations.AddRange(TextDecorations.Underline);
            if (style.Strike) decorations.AddRange(TextDecorations.Strikethrough);
            run.TextDecorations = decorations;
        }
        return run;
    }

    private static IEnumerable<(string Text, bool Marked)> SplitHighlight(string text, string? highlight)
    {
        if (string.IsNullOrEmpty(highlight))
        {
            yield return (text, false);
            yield break;
        }

        int start = 0, index;
        while ((index = text.IndexOf(highlight, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (index > start) yield return (text[start..index], false);
            yield return (text.Substring(index, highlight.Length), true);
            start = index + highlight.Length;
        }
        if (start < text.Length) yield return (text[start..], false);
    }

    // Inlines aren't controls, so links are found by hit-testing the text
    // position under the pointer, as in the Markdown view.
    private void AttachLinks(SelectableTextBlock block, List<LinkSpan> links)
    {
        LinkSpan? LinkAt(PointerEventArgs e)
        {
            var point = e.GetPosition(block) - new Point(block.Padding.Left, block.Padding.Top);
            var hit = block.TextLayout.HitTestPoint(point);
            if (!hit.IsInside) return null;
            return links.FirstOrDefault(l => hit.TextPosition >= l.Start && hit.TextPosition < l.End);
        }

        var textCursor = block.Cursor;
        var handCursor = new Cursor(StandardCursorType.Hand);
        block.PointerMoved += (_, e) => block.Cursor = LinkAt(e) is null ? textCursor : handCursor;
        block.AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd) return;
            if (LinkAt(e) is { } link) Follow(link.Url);
        }, handledEventsToo: true);
    }

    private void Follow(string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            _open(uri.AbsoluteUri);
            return;
        }

        var path = MarkdownView.ResolveLocalPath(url, _preview.BaseDirectory);
        if (path is null || !(File.Exists(path) || Directory.Exists(path))) return;
        // A link in a document shouldn't be able to start a program with one click.
        if (MarkdownView.IsRunnable(path)) _reveal(path);
        else _open(path);
    }

    internal static IBrush? BrushOf(string? color)
    {
        if (color is null) return null;
        lock (ColorBrushes)
        {
            if (!ColorBrushes.TryGetValue(color, out var brush))
                ColorBrushes[color] = brush = Color.TryParse(color, out var c) ? new ImmutableSolidColorBrush(c) : Ink;
            return brush;
        }
    }

    internal static FontFamily? Font(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        lock (Fonts)
        {
            if (!Fonts.TryGetValue(name, out var font))
            {
                // A font this machine lacks falls back to the default one.
                try { font = new FontFamily(name); }
                catch (ArgumentException) { font = FontFamily.Default; }
                Fonts[name] = font;
            }
            return font;
        }
    }

    // =====================================================================
    // Zoom and keys
    // =====================================================================

    private void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_fit)
        {
            // The page's width, never past 100% on a wide window.
            var available = _scroll.Bounds.Width - 2 * PageGap - 20;
            var page = _doc.Sections.Max(s => s.PageWidth) * Dip;
            _zoom = available > 0 ? Math.Clamp(available / page, MinZoom, 1) : 1;
        }
        _zoomHost.LayoutTransform = new ScaleTransform(_zoom, _zoom);
        _zoomText.Text = $"{_zoom * 100:0}%";
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * Step : _zoom / Step);
        e.Handled = true;
    }

    public void FocusContent()
    {
        _scroll.Focus();
        _editor?.ShowCaret();
    }

    public bool HandleKey(KeyEventArgs e)
    {
        if (IsEditing) return false; // keys are typing; the editor has them first
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: SetZoom(_zoom * Step); return true;
            case Key.OemMinus or Key.Subtract: SetZoom(_zoom / Step); return true;
            case Key.D0 or Key.NumPad0: _fit = true; ApplyZoom(); return true;
            default: return false;
        }
    }

    // =====================================================================
    // In-document search
    // =====================================================================

    private void ScrollToInitialHighlight()
    {
        if (_highlight is null) return;
        for (var i = 0; i < _searchEntries.Count; i++)
        {
            var idx = _searchEntries[i].FullText.IndexOf(_highlight, StringComparison.OrdinalIgnoreCase);
            if (idx < 0) continue;
            var entryIndex = i;
            Avalonia.Threading.Dispatcher.UIThread.Post(() => ScrollToEntryOffset(entryIndex, idx), Avalonia.Threading.DispatcherPriority.Loaded);
            return;
        }
    }

    public async Task<ViewerSearchResult> SearchAsync(string query, ViewerSearchOptions options, bool jumpToMatch, CancellationToken ct)
    {
        if (_editor is not null)
            return _editor.Search(query, options, jumpToMatch, ct);

        _interactiveSearchActive = true;
        if (!CodeEditorSearch.PreparedSearch.TryCreate(query, options.MatchCase, options.WholeWord, options.UseRegex, out var prepared) || prepared is null)
        {
            ApplyReadSearch([], -1, scrollToActive: false);
            return new ViewerSearchResult(ValidRegex: false, MatchCount: 0, ActiveIndex: -1);
        }

        if (prepared.IsEmpty)
        {
            ApplyReadSearch([], -1, scrollToActive: false);
            return new ViewerSearchResult(ValidRegex: true, MatchCount: 0, ActiveIndex: -1);
        }

        var texts = _searchEntries.Select(e => e.FullText).ToList();
        var matches = texts.Count <= 250
            ? DocumentSearch.SearchBlocks(texts, prepared, CodeEditorSearch.DefaultMaxMatches, ct)
            : await Task.Run(() => DocumentSearch.SearchBlocks(texts, prepared, CodeEditorSearch.DefaultMaxMatches, ct), ct);

        ct.ThrowIfCancellationRequested();

        var activeIndex = matches.Count == 0
            ? -1
            : jumpToMatch
                ? 0
                : (_activeReadMatch >= 0 && _activeReadMatch < matches.Count ? _activeReadMatch : 0);

        ApplyReadSearch(matches, activeIndex, scrollToActive: jumpToMatch && activeIndex >= 0);
        return new ViewerSearchResult(ValidRegex: true, MatchCount: matches.Count, ActiveIndex: activeIndex);
    }

    public int StepMatch(bool backward)
    {
        if (_editor is not null)
            return _editor.StepSearch(backward);

        var prevEntry = _activeReadMatch >= 0 && _activeReadMatch < _readMatches.Count
            ? _readMatches[_activeReadMatch].BlockIndex
            : -1;
        var next = DocumentSearch.StepIndex(_activeReadMatch, _readMatches.Count, backward);
        if (next < 0) return -1;

        _activeReadMatch = next;
        var nextEntry = _readMatches[next].BlockIndex;
        if (prevEntry >= 0 && prevEntry != nextEntry && _readMatchesByEntry.TryGetValue(prevEntry, out var prevList))
            PopulateInlines(_searchEntries[prevEntry], prevList, _activeReadMatch, fallbackHighlight: null);
        if (_readMatchesByEntry.TryGetValue(nextEntry, out var nextList))
            PopulateInlines(_searchEntries[nextEntry], nextList, _activeReadMatch, fallbackHighlight: null);

        ScrollToReadMatch(next);
        return next;
    }

    public void ClearSearch()
    {
        if (_editor is not null)
        {
            _editor.ClearSearch();
            return;
        }

        _interactiveSearchActive = false;
        _readMatches = [];
        _readMatchesByEntry.Clear();
        _activeReadMatch = -1;

        foreach (var entryIdx in _highlightedEntries)
        {
            if (entryIdx >= 0 && entryIdx < _searchEntries.Count)
                PopulateInlines(_searchEntries[entryIdx], matches: null, activeGlobalIndex: -1, fallbackHighlight: _highlight);
        }
        _highlightedEntries.Clear();
    }

    private void ApplyReadSearch(List<DocxSearchMatch> matches, int activeIndex, bool scrollToActive)
    {
        var previous = _highlightedEntries.ToList();
        _highlightedEntries.Clear();

        var byEntry = new Dictionary<int, List<(int GlobalIndex, TextSpan Span)>>();
        for (var i = 0; i < matches.Count; i++)
        {
            var m = matches[i];
            if (!byEntry.TryGetValue(m.BlockIndex, out var list))
            {
                list = [];
                byEntry[m.BlockIndex] = list;
            }
            list.Add((i, m.Span));
        }

        _readMatches = matches;
        _readMatchesByEntry = byEntry;
        _activeReadMatch = activeIndex;

        foreach (var idx in previous)
        {
            if (!byEntry.ContainsKey(idx) && idx >= 0 && idx < _searchEntries.Count)
                PopulateInlines(_searchEntries[idx], matches: null, activeGlobalIndex: -1, fallbackHighlight: null);
        }
        if (_highlight is not null)
        {
            // Clear initial search-result highlights while interactive search is active.
            for (var i = 0; i < _searchEntries.Count; i++)
            {
                if (!byEntry.ContainsKey(i) && _searchEntries[i].FullText.Contains(_highlight, StringComparison.OrdinalIgnoreCase))
                {
                    PopulateInlines(_searchEntries[i], matches: null, activeGlobalIndex: -1, fallbackHighlight: null);
                    _highlightedEntries.Add(i);
                }
            }
        }

        foreach (var (entryIdx, list) in byEntry)
        {
            if (entryIdx >= 0 && entryIdx < _searchEntries.Count)
            {
                PopulateInlines(_searchEntries[entryIdx], list, _activeReadMatch, fallbackHighlight: null);
                _highlightedEntries.Add(entryIdx);
            }
        }

        if (scrollToActive && activeIndex >= 0 && activeIndex < _readMatches.Count)
            ScrollToReadMatch(activeIndex);
    }

    private void ScrollToReadMatch(int index)
    {
        if (index < 0 || index >= _readMatches.Count) return;
        var match = _readMatches[index];
        ScrollToEntryOffset(match.BlockIndex, match.Span.Offset);
    }

    private void ScrollToEntryOffset(int entryIndex, int offsetInText)
    {
        if (entryIndex < 0 || entryIndex >= _searchEntries.Count) return;
        var entry = _searchEntries[entryIndex];
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (entry.Block.GetVisualRoot() is null) return;
            var charRect = entry.Block.TextLayout.HitTestTextPosition(Math.Clamp(offsetInText, 0, entry.FullText.Length) + entry.IndentChars);
            var viewH = _scroll.Viewport.Height;
            var viewW = _scroll.Viewport.Width;
            if (viewH > 0 && entry.Block.TranslatePoint(new Point(charRect.X, charRect.Y), Pages) is { } pos)
            {
                var targetY = pos.Y * _zoom;
                var maxY = Math.Max(0, _scroll.Extent.Height - viewH);
                var nextY = Math.Clamp(targetY - viewH / 3, 0, maxY);

                var targetX = pos.X * _zoom;
                var maxX = Math.Max(0, _scroll.Extent.Width - viewW);
                var nextX = _scroll.Offset.X;
                if (viewW > 0 && (targetX < nextX + 24 || targetX > nextX + viewW - 24))
                    nextX = Math.Clamp(targetX - viewW / 3, 0, maxX);

                _scroll.Offset = new Vector(nextX, nextY);
            }
            else
            {
                entry.Block.BringIntoView(charRect.Inflate(new Thickness(0, 40)));
            }
        }, Avalonia.Threading.DispatcherPriority.Background);
    }

    public void Dispose()
    {
        _zoomHost.Child = null;
        _preview.Dispose();
        _editor?.Dispose();
    }
}

/// <summary>
/// Selectable text that justifies as Word does. Avalonia's own Justify
/// stretches every line, the last line of the paragraph and lines ending in
/// a line break among them; this stretches only the lines that wrapped.
/// </summary>
internal sealed class DocxTextBlock : SelectableTextBlock
{
    public bool JustifyLines { get; init; }

    protected override Type StyleKeyOverride => typeof(SelectableTextBlock);

    protected override TextLayout CreateTextLayout(string? text)
    {
        var layout = base.CreateTextLayout(text);
        if (JustifyLines) WordJustification.Apply(layout);
        return layout;
    }
}

/// <summary>Justifies the lines that wrapped, leaving a paragraph's last line and lines ending in a break as they are.</summary>
internal static class WordJustification
{
    // Avalonia's inter-word justification, internal to it: the same spacing its Justify uses.
    private static readonly Type? Justification =
        typeof(TextLayout).Assembly.GetType("Avalonia.Media.TextFormatting.InterWordJustification");

    public static void Apply(TextLayout layout)
    {
        var lines = layout.TextLines;
        if (Justification is null || lines.Count < 2) return;

        var width = double.IsFinite(layout.MaxWidth) ? layout.MaxWidth : layout.WidthIncludingTrailingWhitespace;
        if (width <= 0) return;
        try
        {
            if (Activator.CreateInstance(Justification, width) is not JustificationProperties justification) return;
            for (var i = 0; i < lines.Count - 1; i++)
                if (lines[i].NewLineLength == 0) lines[i].Justify(justification);
        }
        catch (Exception e) when (e is MissingMethodException or TargetInvocationException or MemberAccessException)
        {
            // Another Avalonia version: left aligned is still readable.
        }
    }
}
