using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Grepdesk.Core.Viewer;
using Markdig;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Extensions.Yaml;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Grepdesk.UI.Preview;

/// <summary>
/// Turns a parsed Markdown document into Avalonia controls, in the preview
/// panel's colors. Covers what READMEs and notes use: headings, paragraphs,
/// emphasis, code, links, lists (and task lists), quotes, tables, rules and
/// local images. Raw HTML is left out.
/// </summary>
internal static class MarkdownView
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UsePipeTables()
        .UseTaskLists()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseYamlFrontMatter()
        .Build();

    // Immutable brushes only: Parse runs on a worker thread and triggers this
    // class's static initialization, where a mutable brush would throw.
    private static readonly IBrush TextBrush = Brush.Parse("#bac2de");
    private static readonly IBrush HeadingBrush = Brush.Parse("#cdd6f4");
    private static readonly IBrush DimBrush = Brush.Parse("#a6adc8");
    private static readonly IBrush MarkerBrush = Brush.Parse("#7f849c");
    private static readonly IBrush LinkBrush = Brush.Parse("#89b4fa");
    private static readonly IBrush CodeBrush = Brush.Parse("#f5c2e7");
    private static readonly IBrush CodeBackground = Brush.Parse("#313244");
    private static readonly IBrush CodeBlockBackground = Brush.Parse("#1e1e2e");
    private static readonly IBrush RuleBrush = Brush.Parse("#45475a");
    private static readonly IBrush HighlightBackground = new ImmutableSolidColorBrush(Color.Parse("#f9e2af"), 0.25);
    private static readonly IBrush HighlightForeground = Brush.Parse("#f9e2af");
    private static readonly IBrush ActiveHighlightBackground = new ImmutableSolidColorBrush(Color.Parse("#f9e2af"), 0.78);
    private static readonly IBrush ActiveHighlightForeground = Brush.Parse("#11111b");

    private const double BodySize = 12.5;
    private static readonly double[] HeadingSizes = [20, 17, 15, 13.5, 12.5, 12.5];
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    private static readonly HashSet<string> RunnableExtensions = new(
        [".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".scr", ".lnk", ".reg"],
        StringComparer.OrdinalIgnoreCase);

    public static MarkdownDocument Parse(string text) => Markdown.Parse(text, Pipeline);

    /// <summary>A file a click shouldn't open: it would run a program.</summary>
    public static bool IsRunnable(string path) => RunnableExtensions.Contains(Path.GetExtension(path));

    /// <summary>
    /// The file a relative or file: link points to, or null for web links,
    /// in-page anchors and anything that isn't a path.
    /// </summary>
    public static string? ResolveLocalPath(string url, string baseDirectory)
    {
        if (url.StartsWith('#')) return null;
        try
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var absolute) && !(OperatingSystem.IsWindows() && url.Length > 1 && url[1] == ':'))
                return absolute.IsFile ? absolute.LocalPath : null;

            var cut = url.IndexOfAny(['#', '?']);
            var relative = Uri.UnescapeDataString(cut >= 0 ? url[..cut] : url);
            return relative.Length == 0 ? null : Path.GetFullPath(Path.Combine(baseDirectory, relative));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or UriFormatException)
        {
            return null;
        }
    }

    /// <param name="highlight">Text to mark (the content search query), or null.</param>
    /// <param name="open">Called with a web address or a local path when a link is clicked.</param>
    /// <param name="reveal">Called with a local path that would run a program, to show it instead.</param>
    public static Control Build(MarkdownPreview markdown, string? highlight, Action<string> open, Action<string> reveal) =>
        BuildSearchable(markdown, highlight, open, reveal).Root;

    /// <summary>Builds a rendered Markdown control tree with support for interactive in-document search.</summary>
    public static MarkdownDocumentView BuildSearchable(MarkdownPreview markdown, string? highlight, Action<string> open, Action<string> reveal)
    {
        var normalizedHighlight = string.IsNullOrEmpty(highlight) ? null : highlight;
        var root = new StackPanel { Spacing = 10 };
        var context = new Context(markdown, root, normalizedHighlight, open, reveal);
        context.AddBlocks(markdown.Document, root, listDepth: 0);
        return new MarkdownDocumentView(root, context.Entries, normalizedHighlight);
    }

    private sealed record LinkSpan(int Start, int End, string Url);

    internal readonly record struct Style(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

    internal readonly record struct Piece(int Start, int End, string Text, Style Style);

    internal sealed record TextEntry(SelectableTextBlock Block, Piece[] Pieces, string PlainText, int TopBlockIndex);

    private sealed class Context(MarkdownPreview markdown, Panel root, string? highlight, Action<string> open, Action<string> reveal)
    {
        private readonly Cursor _handCursor = new(StandardCursorType.Hand);
        public List<TextEntry> Entries { get; } = [];
        // ── Blocks ──────────────────────────────────────────────────────────

        public void AddBlocks(ContainerBlock container, Panel target, int listDepth)
        {
            foreach (var block in container)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        AddHeading(heading, target);
                        break;
                    case ParagraphBlock paragraph:
                        AddInlines(paragraph.Inline, target.Children, BodySize, TextBrush);
                        break;
                    case ListBlock list:
                        target.Children.Add(BuildList(list, listDepth));
                        break;
                    case QuoteBlock quote:
                        var inner = new StackPanel { Spacing = 8 };
                        AddBlocks(quote, inner, listDepth);
                        target.Children.Add(new Border
                        {
                            BorderBrush = RuleBrush,
                            BorderThickness = new Thickness(3, 0, 0, 0),
                            Padding = new Thickness(10, 2, 0, 2),
                            Child = inner
                        });
                        break;
                    case HtmlBlock or YamlFrontMatterBlock or LinkReferenceDefinitionGroup:
                        break; // before CodeBlock: front matter is one
                    case CodeBlock code: // indented and fenced
                        target.Children.Add(BuildCodeBlock(code));
                        break;
                    case ThematicBreakBlock:
                        target.Children.Add(new Border { Height = 1, Background = RuleBrush, Margin = new Thickness(0, 4) });
                        break;
                    case Table table:
                        target.Children.Add(BuildTable(table));
                        break;
                    case ContainerBlock other:
                        AddBlocks(other, target, listDepth);
                        break;
                    case LeafBlock { Inline: { } inline }:
                        AddInlines(inline, target.Children, BodySize, TextBrush);
                        break;
                }
            }
        }

        private void AddHeading(HeadingBlock heading, Panel target)
        {
            var level = Math.Clamp(heading.Level, 1, 6);
            var controls = new List<Control>(1);
            AddInlines(heading.Inline, controls, HeadingSizes[level - 1], level <= 5 ? HeadingBrush : DimBrush, bold: true);
            if (controls.Count == 0) return;

            Control control;
            if (controls.Count == 1)
            {
                control = controls[0];
            }
            else
            {
                var panel = new StackPanel();
                panel.Children.AddRange(controls);
                control = panel;
            }

            if (level <= 2)
                control = new Border
                {
                    BorderBrush = CodeBackground,
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 4),
                    Child = control
                };
            if (target.Children.Count > 0) control.Margin = new Thickness(0, 6, 0, 0);
            target.Children.Add(control);
        }

        private Control BuildList(ListBlock list, int depth)
        {
            var panel = new StackPanel { Spacing = list.IsLoose ? 8 : 3 };
            var number = list.IsOrdered && int.TryParse(list.OrderedStart, out var start) ? start : 1;

            foreach (var item in list.OfType<ListItemBlock>())
            {
                // "- [x] done": the checkbox replaces the bullet.
                var task = (item.FirstOrDefault() as ParagraphBlock)?.Inline?.FirstChild as TaskList;
                var marker = task is not null ? (task.Checked ? "☑" : "☐")
                    : list.IsOrdered ? $"{number}{list.OrderedDelimiter}"
                    : Bullets[Math.Min(depth, Bullets.Length - 1)];
                number++;

                var content = new StackPanel { Spacing = 6 };
                AddBlocks(item, content, depth + 1);

                var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
                row.Children.Add(new TextBlock
                {
                    Text = marker,
                    Foreground = MarkerBrush,
                    FontSize = BodySize,
                    MinWidth = 16,
                    Margin = new Thickness(0, 0, 6, 0),
                    TextAlignment = TextAlignment.Right
                });
                Grid.SetColumn(content, 1);
                row.Children.Add(content);
                panel.Children.Add(row);
            }
            return panel;
        }

        private Control BuildCodeBlock(CodeBlock code)
        {
            var text = code.Lines.ToString().TrimEnd('\n', '\r');
            var block = new SelectableTextBlock
            {
                FontFamily = PreviewPane.Monospace,
                FontSize = 11.5,
                Foreground = TextBrush,
                TextWrapping = TextWrapping.Wrap,
            };
            SetRuns(block, [(text, default)]);
            return new Border
            {
                Background = CodeBlockBackground,
                BorderBrush = CodeBackground,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(10, 8),
                Child = block
            };
        }

        private Control BuildTable(Table table)
        {
            var rows = table.OfType<TableRow>().ToList();
            var columns = rows.Count == 0 ? 0 : rows.Max(r => r.Count);
            var grid = new Grid();
            for (var c = 0; c < columns; c++) grid.ColumnDefinitions.Add(new ColumnDefinition(GridLength.Auto));

            var cellControls = new List<Control>(2);
            for (var r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var isHeader = rows[r].IsHeader;
                for (var c = 0; c < rows[r].Count; c++)
                {
                    Control? cellContent = null;
                    if (rows[r][c] is TableCell tableCell)
                    {
                        cellControls.Clear();
                        foreach (var paragraph in tableCell.OfType<ParagraphBlock>())
                            AddInlines(paragraph.Inline, cellControls, 12, isHeader ? HeadingBrush : TextBrush, bold: isHeader);

                        if (cellControls.Count == 1)
                        {
                            cellContent = cellControls[0];
                            cellContent.MaxWidth = 280;
                        }
                        else if (cellControls.Count > 1)
                        {
                            var cell = new StackPanel { MaxWidth = 280 };
                            cell.Children.AddRange(cellControls);
                            cellContent = cell;
                        }
                    }

                    var border = new Border
                    {
                        BorderBrush = RuleBrush,
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                        Background = isHeader ? CodeBlockBackground : null,
                        Padding = new Thickness(8, 4),
                        Child = cellContent
                    };
                    Grid.SetRow(border, r);
                    Grid.SetColumn(border, c);
                    grid.Children.Add(border);
                }
            }

            // Wide tables scroll sideways instead of squeezing every column.
            var tableScroll = new ScrollViewer
            {
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
                AllowAutoHide = false,
                Content = grid
            };

            var loc = LocalizationService.Instance;
            var prevBtn = MakeTableNavButton("‹", loc.Get("MarkdownTableScrollLeft"));
            var nextBtn = MakeTableNavButton("›", loc.Get("MarkdownTableScrollRight"));

            var navBar = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 0, 0, 4),
                IsVisible = false,
            };
            navBar.Children.Add(prevBtn);
            navBar.Children.Add(nextBtn);

            void ScrollHorizontal(int direction)
            {
                var maxX = Math.Max(0, tableScroll.Extent.Width - tableScroll.Viewport.Width);
                if (maxX <= 1) return;
                var step = Math.Max(140, tableScroll.Viewport.Width * 0.55);
                var nextX = Math.Clamp(tableScroll.Offset.X + direction * step, 0, maxX);
                tableScroll.Offset = new Vector(nextX, tableScroll.Offset.Y);
            }

            void SyncTableNav()
            {
                var maxX = Math.Max(0, tableScroll.Extent.Width - tableScroll.Viewport.Width);
                var overflows = maxX > 1;
                if (navBar.IsVisible != overflows)
                    navBar.IsVisible = overflows;
                if (!overflows) return;

                prevBtn.IsEnabled = tableScroll.Offset.X > 0.5;
                nextBtn.IsEnabled = tableScroll.Offset.X < maxX - 0.5;
                prevBtn.Opacity = prevBtn.IsEnabled ? 1.0 : 0.4;
                nextBtn.Opacity = nextBtn.IsEnabled ? 1.0 : 0.4;
            }

            prevBtn.Click += (_, _) => ScrollHorizontal(-1);
            nextBtn.Click += (_, _) => ScrollHorizontal(+1);
            tableScroll.ScrollChanged += (_, _) => SyncTableNav();
            tableScroll.SizeChanged += (_, _) => SyncTableNav();
            tableScroll.AddHandler(InputElement.PointerWheelChangedEvent, (_, e) =>
            {
                if (!e.KeyModifiers.HasFlag(KeyModifiers.Shift) || e.Delta.Y == 0) return;
                var maxX = Math.Max(0, tableScroll.Extent.Width - tableScroll.Viewport.Width);
                if (maxX <= 1) return;
                var nextX = Math.Clamp(tableScroll.Offset.X - e.Delta.Y * 72, 0, maxX);
                tableScroll.Offset = new Vector(nextX, tableScroll.Offset.Y);
                e.Handled = true;
            }, RoutingStrategies.Tunnel);

            var container = new StackPanel();
            container.Children.Add(navBar);
            container.Children.Add(tableScroll);
            return container;
        }

        private Button MakeTableNavButton(string label, string tip)
        {
            var button = new Button
            {
                Content = label,
                MinWidth = 26,
                MinHeight = 22,
                Padding = new Thickness(7, 2),
                FontSize = 12,
                Background = CodeBackground,
                Foreground = HeadingBrush,
                BorderBrush = RuleBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                HorizontalContentAlignment = HorizontalAlignment.Center,
                VerticalContentAlignment = VerticalAlignment.Center,
                Focusable = false,
                Cursor = _handCursor,
            };
            ToolTip.SetTip(button, tip);
            return button;
        }

        // ── Inlines ─────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a paragraph's text as one or more text blocks: a local image
        /// splits it, since images are shown as blocks of their own.
        /// </summary>
        private void AddInlines(ContainerInline? inline, IList<Control> target, double size, IBrush foreground, bool bold = false)
        {
            if (inline is null) return;

            var pieces = new List<(string Text, Style Style)>();
            void Flush()
            {
                if (pieces.Count == 0) return;
                if (pieces.All(p => string.IsNullOrWhiteSpace(p.Text))) { pieces.Clear(); return; }

                // Soft breaks and the space after a task checkbox would show as an indent.
                pieces[0] = pieces[0] with { Text = pieces[0].Text.TrimStart() };
                pieces[^1] = pieces[^1] with { Text = pieces[^1].Text.TrimEnd() };

                var block = new SelectableTextBlock
                {
                    FontSize = size,
                    Foreground = foreground,
                    FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal,
                    TextWrapping = TextWrapping.Wrap,
                };
                SetRuns(block, pieces);
                target.Add(block);
                pieces.Clear();
            }

            Collect(inline, new Style(), pieces, image =>
            {
                Flush();
                target.Add(new Image
                {
                    Source = image,
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.DownOnly,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    MaxHeight = 360,
                });
            });
            Flush();
        }

        private void Collect(ContainerInline container, Style style, List<(string, Style)> pieces, Action<Bitmap> addImage)
        {
            foreach (var inline in container)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        pieces.Add((literal.Content.ToString(), style));
                        break;
                    case CodeInline code:
                        pieces.Add((code.Content, style with { Code = true }));
                        break;
                    case LineBreakInline lineBreak:
                        pieces.Add((lineBreak.IsHard ? "\n" : " ", style));
                        break;
                    case HtmlEntityInline entity:
                        pieces.Add((entity.Transcoded.ToString(), style));
                        break;
                    case HtmlInline html:
                        if (html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase)) pieces.Add(("\n", style));
                        break;
                    case AutolinkInline autolink:
                        pieces.Add((autolink.Url, style with { Link = autolink.IsEmail ? "mailto:" + autolink.Url : autolink.Url }));
                        break;
                    case TaskList:
                        break; // drawn as the list marker
                    case LinkInline { IsImage: true } image:
                        if (image.Url is not null && markdown.Images.TryGetValue(image.Url, out var bitmap))
                            addImage(bitmap);
                        else
                        {
                            // Web images (badges, hosted screenshots) aren't fetched: show the alt text.
                            var before = pieces.Count;
                            Collect(image, style with { Italic = true }, pieces, addImage);
                            if (pieces.Count == before) pieces.Add(("[image]", style with { Italic = true }));
                        }
                        break;
                    case LinkInline link:
                        Collect(link, style with { Link = link.Url ?? style.Link }, pieces, addImage);
                        break;
                    case EmphasisInline emphasis:
                        var inner = emphasis.DelimiterChar == '~' ? style with { Strike = true }
                            : emphasis.DelimiterCount >= 2 ? style with { Bold = true }
                            : style with { Italic = true };
                        Collect(emphasis, inner, pieces, addImage);
                        break;
                    case ContainerInline other:
                        Collect(other, style, pieces, addImage);
                        break;
                }
            }
        }

        /// <summary>Fills a text block with styled runs and makes its links clickable.</summary>
        private void SetRuns(SelectableTextBlock block, IEnumerable<(string Text, Style Style)> pieces)
        {
            var inlines = new InlineCollection();
            var links = new List<LinkSpan>();
            var recordedPieces = new List<Piece>();
            var plain = new System.Text.StringBuilder();
            var offset = 0;

            foreach (var (text, style) in pieces)
            {
                if (text.Length == 0) continue;
                recordedPieces.Add(new Piece(offset, offset + text.Length, text, style));
                plain.Append(text);
                foreach (var (part, marked) in SplitHighlight(text, highlight))
                    inlines.Add(MakeRun(part, style, block.FontSize, marked ? 1 : 0));

                if (style.Link is not null) links.Add(new LinkSpan(offset, offset + text.Length, style.Link));
                offset += text.Length;
            }

            block.Inlines = inlines;
            if (recordedPieces.Count > 0)
                Entries.Add(new TextEntry(block, [.. recordedPieces], plain.ToString(), root.Children.Count));
            if (links.Count > 0) AttachLinks(block, links);
        }

        internal static Run MakeRun(string text, Style style, double blockFontSize, int highlightState)
        {
            var run = new Run(text);
            if (style.Bold) run.FontWeight = FontWeight.SemiBold;
            if (style.Italic) run.FontStyle = FontStyle.Italic;
            if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
            if (style.Code)
            {
                run.FontFamily = PreviewPane.Monospace;
                run.FontSize = blockFontSize - 1;
                run.Foreground = CodeBrush;
                run.Background = CodeBackground;
            }
            if (style.Link is not null)
            {
                run.Foreground = LinkBrush;
                run.TextDecorations = TextDecorations.Underline;
            }
            if (highlightState == 1)
            {
                run.Background = HighlightBackground;
                run.Foreground = HighlightForeground;
                run.FontWeight = FontWeight.Bold;
            }
            else if (highlightState == 2)
            {
                run.Background = ActiveHighlightBackground;
                run.Foreground = ActiveHighlightForeground;
                run.FontWeight = FontWeight.Bold;
            }
            return run;
        }

        internal static IEnumerable<(string Text, bool Marked)> SplitHighlight(string text, string? highlight)
        {
            if (highlight is null)
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
        // position under the pointer against the character ranges they cover.
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
            block.PointerMoved += (_, e) => block.Cursor = LinkAt(e) is null ? textCursor : _handCursor;
            block.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton != MouseButton.Left || block.SelectionStart != block.SelectionEnd) return;
                if (LinkAt(e) is { } link) Follow(link.Url);
            }, handledEventsToo: true);
        }

        private void Follow(string url)
        {
            if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
            {
                open(uri.AbsoluteUri);
                return;
            }

            var path = ResolveLocalPath(url, markdown.BaseDirectory);
            if (path is null || !(File.Exists(path) || Directory.Exists(path))) return;

            // A link in a document shouldn't be able to start a program with one click.
            if (RunnableExtensions.Contains(Path.GetExtension(path))) reveal(path);
            else open(path);
        }
    }

    internal sealed class MarkdownDocumentView
    {
        public readonly record struct MatchLocation(SelectableTextBlock Block, int OffsetInBlock, int Length);

        internal sealed record SearchResult(
            bool ValidRegex,
            List<(int EntryIndex, TextSpan Span)> Matches,
            Dictionary<int, List<(int GlobalIndex, TextSpan Span)>> MatchesByEntry,
            List<int> MatchedEntryIndices,
            List<MatchLocation> Locations);

        private const double ViewportBuffer = 320;
        private const int MaxCandidateEntriesBeforeCellCheck = 120;
        private const int MaxCachedRenderedEntries = 240;

        private readonly StackPanel _rootPanel;
        private readonly List<TextEntry> _entries;
        private readonly string? _initialHighlight;
        private readonly HashSet<int> _renderedEntries = [];
        private readonly HashSet<int> _clearedInitialEntries = [];
        private int _renderedActiveEntry = -1;

        private List<(int EntryIndex, TextSpan Span)> _matches = [];
        private Dictionary<int, List<(int GlobalIndex, TextSpan Span)>> _matchesByEntry = new();
        private List<int> _matchedEntryIndices = [];
        private int _activeGlobalIndex = -1;

        private bool _hasViewport;
        private double _viewportTop;
        private double _viewportBottom;
        private int _lastFirstBlock = -1;
        private int _lastLastBlock = -1;
        private bool _lastNeedsFineCheck;
        private double _lastRenderedViewportTop = double.NaN;

        internal MarkdownDocumentView(StackPanel root, List<TextEntry> entries, string? initialHighlight)
        {
            _rootPanel = root;
            _entries = entries;
            _initialHighlight = initialHighlight;
        }

        public Control Root => _rootPanel;
        public int EntryCount => _entries.Count;

        public IReadOnlyList<MatchLocation> Find(
            string query,
            bool matchCase,
            bool wholeWord,
            bool useRegex,
            out bool validRegex)
        {
            if (string.IsNullOrEmpty(query))
            {
                validRegex = true;
                ClearSearch();
                return [];
            }

            var result = Search(query, matchCase, wholeWord, useRegex);
            validRegex = result.ValidRegex;
            if (!result.ValidRegex)
            {
                ClearSearch();
                return [];
            }

            ApplySearchResult(result, activeGlobalIndex: -1);
            return result.Locations;
        }

        /// <summary>
        /// Scans all text entries for matches. Safe to run on a worker thread because it only
        /// reads immutable <see cref="TextEntry.PlainText"/> values.
        /// </summary>
        internal SearchResult Search(
            string query,
            bool matchCase,
            bool wholeWord,
            bool useRegex,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrEmpty(query))
                return new SearchResult(true, [], new(), [], []);

            if (!CodeEditorSearch.PreparedSearch.TryCreate(query, matchCase, wholeWord, useRegex, out var prepared) || prepared is null)
                return new SearchResult(false, [], new(), [], []);

            const int maxMatches = CodeEditorSearch.DefaultMaxMatches;
            var matches = new List<(int EntryIndex, TextSpan Span)>();
            var matchesByEntry = new Dictionary<int, List<(int GlobalIndex, TextSpan Span)>>();
            var matchedEntries = new List<int>();
            var locations = new List<MatchLocation>();
            var entrySpans = new List<TextSpan>();

            for (var i = 0; i < _entries.Count; i++)
            {
                if ((i & 63) == 0) cancellationToken.ThrowIfCancellationRequested();
                var remaining = maxMatches - matches.Count;
                if (remaining <= 0) break;

                var entry = _entries[i];
                entrySpans.Clear();
                prepared.FindInto(entry.PlainText, entrySpans, remaining, cancellationToken);
                if (entrySpans.Count == 0) continue;

                var list = new List<(int GlobalIndex, TextSpan Span)>(entrySpans.Count);
                for (var s = 0; s < entrySpans.Count; s++)
                {
                    var span = entrySpans[s];
                    var globalIndex = matches.Count;
                    matches.Add((i, span));
                    list.Add((globalIndex, span));
                    locations.Add(new MatchLocation(entry.Block, span.Offset, span.Length));
                }
                matchesByEntry[i] = list;
                matchedEntries.Add(i);
            }

            return new SearchResult(true, matches, matchesByEntry, matchedEntries, locations);
        }

        internal void ApplySearchResult(
            SearchResult result,
            int activeGlobalIndex,
            double? viewportTop = null,
            double? viewportBottom = null)
        {
            if (viewportTop.HasValue && viewportBottom.HasValue && viewportBottom.Value > viewportTop.Value)
            {
                _hasViewport = true;
                _viewportTop = viewportTop.Value;
                _viewportBottom = viewportBottom.Value;
            }

            _matches = result.Matches;
            _matchesByEntry = result.MatchesByEntry;
            _matchedEntryIndices = result.MatchedEntryIndices;
            _activeGlobalIndex = activeGlobalIndex;
            _lastFirstBlock = -1;
            _lastLastBlock = -1;
            _lastNeedsFineCheck = false;
            _lastRenderedViewportTop = double.NaN;

            if (_matchedEntryIndices.Count == 0)
            {
                ClearRenderedEntries(restoreInitialHighlight: false);
                return;
            }

            var visibleTargets = CollectVisibleTargetEntries(
                out var activeEntry,
                out _lastFirstBlock,
                out _lastLastBlock,
                out _lastNeedsFineCheck);
            _lastRenderedViewportTop = _viewportTop;

            if (_renderedEntries.Count > 0)
            {
                foreach (var oldIndex in _renderedEntries)
                {
                    if (!visibleTargets.Contains(oldIndex))
                        RestoreEntry(oldIndex, restoreInitialHighlight: false);
                }
                _renderedEntries.Clear();
            }
            _renderedActiveEntry = -1;

            foreach (var entryIndex in visibleTargets)
            {
                if (_matchesByEntry.TryGetValue(entryIndex, out var entryList))
                    HighlightEntry(entryIndex, entryList);
            }
            _renderedActiveEntry = activeEntry >= 0 && _renderedEntries.Contains(activeEntry) ? activeEntry : -1;
        }

        public void UpdateViewport(double viewportTop, double viewportBottom)
        {
            if (viewportBottom <= viewportTop) return;
            _hasViewport = true;
            _viewportTop = viewportTop;
            _viewportBottom = viewportBottom;
            if (_matchedEntryIndices.Count == 0) return;

            RefreshVisibleHighlights();
        }

        public void SetActiveMatch(int globalIndex)
        {
            var prevEntry = _activeGlobalIndex >= 0 && _activeGlobalIndex < _matches.Count
                ? _matches[_activeGlobalIndex].EntryIndex
                : -1;
            var nextEntry = globalIndex >= 0 && globalIndex < _matches.Count
                ? _matches[globalIndex].EntryIndex
                : -1;

            _activeGlobalIndex = globalIndex;

            if (prevEntry >= 0 && prevEntry != nextEntry && _renderedEntries.Contains(prevEntry) && _matchesByEntry.TryGetValue(prevEntry, out var prevList))
                HighlightEntry(prevEntry, prevList);

            if (nextEntry >= 0 && _matchesByEntry.TryGetValue(nextEntry, out var nextList))
                HighlightEntry(nextEntry, nextList);

            _renderedActiveEntry = nextEntry;
        }

        public void ClearSearch() => ClearActiveMatches(restoreInitialHighlight: true);

        private void ClearActiveMatches(bool restoreInitialHighlight)
        {
            ClearRenderedEntries(restoreInitialHighlight);
            _matches = [];
            _matchesByEntry = new();
            _matchedEntryIndices = [];
            _activeGlobalIndex = -1;
            _lastFirstBlock = -1;
            _lastLastBlock = -1;
            _lastNeedsFineCheck = false;
            _lastRenderedViewportTop = double.NaN;
        }

        private void ClearRenderedEntries(bool restoreInitialHighlight)
        {
            var initial = restoreInitialHighlight ? _initialHighlight : null;
            foreach (var entryIndex in _renderedEntries)
                RestoreEntry(entryIndex, restoreInitialHighlight);

            _renderedEntries.Clear();
            _renderedActiveEntry = -1;

            if (restoreInitialHighlight && _clearedInitialEntries.Count > 0)
            {
                foreach (var entryIndex in _clearedInitialEntries)
                    RenderEntry(_entries[entryIndex], null, -1, initial);
                _clearedInitialEntries.Clear();
            }
        }

        private void RefreshVisibleHighlights()
        {
            var minY = _viewportTop - ViewportBuffer;
            var maxY = _viewportBottom + ViewportBuffer;

            if (_hasViewport && _rootPanel.Bounds.Height > 0 && _rootPanel.Children.Count > 0)
            {
                var (firstBlock, lastBlock) = FindVisibleBlockRange(minY, maxY);
                if (firstBlock == _lastFirstBlock && lastBlock == _lastLastBlock)
                {
                    if (!_lastNeedsFineCheck || Math.Abs(_viewportTop - _lastRenderedViewportTop) < 80)
                        return;
                }
            }

            var visibleTargets = CollectVisibleTargetEntries(
                out var activeEntry,
                out _lastFirstBlock,
                out _lastLastBlock,
                out _lastNeedsFineCheck);
            _lastRenderedViewportTop = _viewportTop;

            foreach (var entryIndex in visibleTargets)
            {
                var needsRender = !_renderedEntries.Contains(entryIndex) ||
                    (entryIndex == activeEntry && _renderedActiveEntry != activeEntry) ||
                    (entryIndex != activeEntry && _renderedActiveEntry == entryIndex);
                if (!needsRender) continue;

                if (_matchesByEntry.TryGetValue(entryIndex, out var entryList))
                    HighlightEntry(entryIndex, entryList);
            }
            _renderedActiveEntry = activeEntry >= 0 && _renderedEntries.Contains(activeEntry) ? activeEntry : -1;

            if (_renderedEntries.Count > MaxCachedRenderedEntries)
            {
                List<int>? toRemove = null;
                foreach (var idx in _renderedEntries)
                {
                    if (visibleTargets.Contains(idx)) continue;
                    (toRemove ??= []).Add(idx);
                }
                if (toRemove is not null)
                {
                    foreach (var idx in toRemove)
                    {
                        RestoreEntry(idx, restoreInitialHighlight: false);
                        _renderedEntries.Remove(idx);
                        if (_renderedEntries.Count <= MaxCachedRenderedEntries / 2) break;
                    }
                }
            }
        }

        private void HighlightEntry(int entryIndex, List<(int GlobalIndex, TextSpan Span)> entryList)
        {
            if (_initialHighlight is not null)
                _clearedInitialEntries.Add(entryIndex);
            RenderEntry(_entries[entryIndex], entryList, _activeGlobalIndex, initialHighlight: null);
            _renderedEntries.Add(entryIndex);
        }

        private void RestoreEntry(int entryIndex, bool restoreInitialHighlight)
        {
            if (restoreInitialHighlight)
            {
                RenderEntry(_entries[entryIndex], null, -1, _initialHighlight);
                _clearedInitialEntries.Remove(entryIndex);
            }
            else
            {
                if (_initialHighlight is not null)
                    _clearedInitialEntries.Add(entryIndex);
                RenderEntry(_entries[entryIndex], null, -1, initialHighlight: null);
            }
        }

        private HashSet<int> CollectVisibleTargetEntries(
            out int activeEntry,
            out int firstBlock,
            out int lastBlock,
            out bool usedFineCheck)
        {
            activeEntry = _activeGlobalIndex >= 0 && _activeGlobalIndex < _matches.Count
                ? _matches[_activeGlobalIndex].EntryIndex
                : -1;
            firstBlock = -1;
            lastBlock = -1;
            usedFineCheck = false;

            var targets = new HashSet<int>();
            if (activeEntry >= 0) targets.Add(activeEntry);
            if (_matchedEntryIndices.Count == 0) return targets;

            if (!_hasViewport || _rootPanel.Bounds.Height <= 0 || _rootPanel.Children.Count == 0)
            {
                var limit = Math.Min(_matchedEntryIndices.Count, 200);
                for (var i = 0; i < limit; i++) targets.Add(_matchedEntryIndices[i]);
                return targets;
            }

            var minY = _viewportTop - ViewportBuffer;
            var maxY = _viewportBottom + ViewportBuffer;
            (firstBlock, lastBlock) = FindVisibleBlockRange(minY, maxY);
            if (firstBlock < 0 || lastBlock < firstBlock) return targets;

            var startIdx = LowerBoundMatchedBlock(firstBlock);
            var endIdx = startIdx;
            while (endIdx < _matchedEntryIndices.Count && _entries[_matchedEntryIndices[endIdx]].TopBlockIndex <= lastBlock)
                endIdx++;

            var candidateCount = endIdx - startIdx;
            if (candidateCount <= MaxCandidateEntriesBeforeCellCheck)
            {
                for (var i = startIdx; i < endIdx; i++)
                    targets.Add(_matchedEntryIndices[i]);
                return targets;
            }

            usedFineCheck = true;
            for (var i = startIdx; i < endIdx; i++)
            {
                var entryIdx = _matchedEntryIndices[i];
                var block = _entries[entryIdx].Block;
                if (block.TranslatePoint(new Point(0, 0), _rootPanel) is { } origin)
                {
                    var bottom = origin.Y + block.Bounds.Height;
                    if (bottom >= minY && origin.Y <= maxY)
                        targets.Add(entryIdx);
                }
                else
                {
                    targets.Add(entryIdx);
                }
            }
            return targets;
        }

        private int LowerBoundMatchedBlock(int firstBlock)
        {
            int lo = 0, hi = _matchedEntryIndices.Count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (_entries[_matchedEntryIndices[mid]].TopBlockIndex < firstBlock) lo = mid + 1;
                else hi = mid;
            }
            return lo;
        }

        private (int First, int Last) FindVisibleBlockRange(double minY, double maxY)
        {
            var children = _rootPanel.Children;
            var count = children.Count;
            if (count == 0) return (-1, -1);

            int lo = 0, hi = count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (children[mid].Bounds.Bottom < minY) lo = mid + 1;
                else hi = mid;
            }
            var first = lo;
            if (first >= count) return (-1, -1);

            lo = first;
            hi = count;
            while (lo < hi)
            {
                var mid = lo + ((hi - lo) >> 1);
                if (children[mid].Bounds.Top <= maxY) lo = mid + 1;
                else hi = mid;
            }
            var last = lo - 1;
            return last < first ? (-1, -1) : (first, last);
        }

        private static void RenderEntry(
            TextEntry entry,
            List<(int GlobalIndex, TextSpan Span)>? entryMatches,
            int activeGlobalIndex,
            string? initialHighlight)
        {
            var inlines = new InlineCollection();
            var fontSize = entry.Block.FontSize;

            if (entryMatches is null || entryMatches.Count == 0)
            {
                foreach (var piece in entry.Pieces)
                {
                    foreach (var (part, marked) in Context.SplitHighlight(piece.Text, initialHighlight))
                        inlines.Add(Context.MakeRun(part, piece.Style, fontSize, marked ? 1 : 0));
                }
                entry.Block.Inlines = inlines;
                return;
            }

            foreach (var piece in entry.Pieces)
            {
                var pos = 0;
                foreach (var (globalIndex, span) in entryMatches)
                {
                    var spanEnd = span.Offset + span.Length;
                    if (spanEnd <= piece.Start) continue;
                    if (span.Offset >= piece.End) break;

                    var localStart = Math.Max(0, span.Offset - piece.Start);
                    var localEnd = Math.Min(piece.Text.Length, spanEnd - piece.Start);
                    if (localStart > pos)
                        inlines.Add(Context.MakeRun(piece.Text[pos..localStart], piece.Style, fontSize, 0));

                    var state = globalIndex == activeGlobalIndex ? 2 : 1;
                    inlines.Add(Context.MakeRun(piece.Text[localStart..localEnd], piece.Style, fontSize, state));
                    pos = localEnd;
                }
                if (pos < piece.Text.Length)
                    inlines.Add(Context.MakeRun(piece.Text[pos..], piece.Style, fontSize, 0));
            }

            entry.Block.Inlines = inlines;
        }
    }
}
