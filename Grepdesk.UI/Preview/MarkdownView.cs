using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
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

    private const double BodySize = 12.5;
    private static readonly double[] HeadingSizes = [20, 17, 15, 13.5, 12.5, 12.5];
    private static readonly string[] Bullets = ["•", "◦", "▪"];

    private static readonly HashSet<string> RunnableExtensions = new(
        [".exe", ".com", ".bat", ".cmd", ".ps1", ".vbs", ".js", ".jse", ".wsf", ".msi", ".scr", ".lnk", ".reg"],
        StringComparer.OrdinalIgnoreCase);

    public static MarkdownDocument Parse(string text) => Markdown.Parse(text, Pipeline);

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
    public static Control Build(MarkdownPreview markdown, string? highlight, Action<string> open, Action<string> reveal)
    {
        var context = new Context(markdown, string.IsNullOrEmpty(highlight) ? null : highlight, open, reveal);
        var root = new StackPanel { Spacing = 10 };
        context.AddBlocks(markdown.Document, root, listDepth: 0);
        return root;
    }

    private sealed record LinkSpan(int Start, int End, string Url);

    private readonly record struct Style(bool Bold = false, bool Italic = false, bool Strike = false, bool Code = false, string? Link = null);

    private sealed class Context(MarkdownPreview markdown, string? highlight, Action<string> open, Action<string> reveal)
    {
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
                        AddInlines(paragraph.Inline, target, BodySize, TextBrush);
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
                        AddInlines(inline, target, BodySize, TextBrush);
                        break;
                }
            }
        }

        private void AddHeading(HeadingBlock heading, Panel target)
        {
            var level = Math.Clamp(heading.Level, 1, 6);
            var panel = new StackPanel();
            AddInlines(heading.Inline, panel, HeadingSizes[level - 1], level <= 5 ? HeadingBrush : DimBrush, bold: true);
            if (panel.Children.Count == 0) return;

            Control control = panel;
            if (level <= 2)
                control = new Border
                {
                    BorderBrush = Brush.Parse("#313244"),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Padding = new Thickness(0, 0, 0, 4),
                    Child = panel
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
                BorderBrush = Brush.Parse("#313244"),
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

            for (var r = 0; r < rows.Count; r++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                for (var c = 0; c < rows[r].Count; c++)
                {
                    var cell = new StackPanel { MaxWidth = 280 };
                    if (rows[r][c] is TableCell tableCell)
                        foreach (var paragraph in tableCell.OfType<ParagraphBlock>())
                            AddInlines(paragraph.Inline, cell, 12, rows[r].IsHeader ? HeadingBrush : TextBrush, bold: rows[r].IsHeader);

                    var border = new Border
                    {
                        BorderBrush = RuleBrush,
                        BorderThickness = new Thickness(c == 0 ? 1 : 0, r == 0 ? 1 : 0, 1, 1),
                        Background = rows[r].IsHeader ? CodeBlockBackground : null,
                        Padding = new Thickness(8, 4),
                        Child = cell
                    };
                    Grid.SetRow(border, r);
                    Grid.SetColumn(border, c);
                    grid.Children.Add(border);
                }
            }

            // Wide tables scroll sideways instead of squeezing every column.
            return new ScrollViewer
            {
                HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
                VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
                Content = grid
            };
        }

        // ── Inlines ─────────────────────────────────────────────────────────

        /// <summary>
        /// Adds a paragraph's text as one or more text blocks: a local image
        /// splits it, since images are shown as blocks of their own.
        /// </summary>
        private void AddInlines(ContainerInline? inline, Panel target, double size, IBrush foreground, bool bold = false)
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
                target.Children.Add(block);
                pieces.Clear();
            }

            Collect(inline, new Style(), pieces, image =>
            {
                Flush();
                target.Children.Add(new Image
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
            var offset = 0;

            foreach (var (text, style) in pieces)
            {
                if (text.Length == 0) continue;
                foreach (var (part, marked) in SplitHighlight(text))
                {
                    var run = new Run(part);
                    if (style.Bold) run.FontWeight = FontWeight.SemiBold;
                    if (style.Italic) run.FontStyle = FontStyle.Italic;
                    if (style.Strike) run.TextDecorations = TextDecorations.Strikethrough;
                    if (style.Code)
                    {
                        run.FontFamily = PreviewPane.Monospace;
                        run.FontSize = block.FontSize - 1;
                        run.Foreground = CodeBrush;
                        run.Background = CodeBackground;
                    }
                    if (style.Link is not null)
                    {
                        run.Foreground = LinkBrush;
                        run.TextDecorations = TextDecorations.Underline;
                    }
                    if (marked)
                    {
                        run.Background = HighlightBackground;
                        run.Foreground = HighlightForeground;
                        run.FontWeight = FontWeight.Bold;
                    }
                    inlines.Add(run);
                }

                if (style.Link is not null) links.Add(new LinkSpan(offset, offset + text.Length, style.Link));
                offset += text.Length;
            }

            block.Inlines = inlines;
            if (links.Count > 0) AttachLinks(block, links);
        }

        private IEnumerable<(string Text, bool Marked)> SplitHighlight(string text)
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
            var handCursor = new Cursor(StandardCursorType.Hand);
            block.PointerMoved += (_, e) => block.Cursor = LinkAt(e) is null ? textCursor : handCursor;
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
}
