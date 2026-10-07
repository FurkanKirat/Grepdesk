using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Grepdesk.UI.Jobs;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI;

/// <summary>
/// Side panel showing the selected result: metadata plus a look inside.
/// What "inside" means per file type is up to the <see cref="IPreviewProvider"/>s;
/// this control only lays out what they return.
/// </summary>
public partial class PreviewPane : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    internal static readonly FontFamily Monospace = new("Cascadia Mono, Consolas, Menlo, DejaVu Sans Mono, monospace");
    private static readonly IBrush HighlightBackground = new SolidColorBrush(Color.Parse("#f9e2af"), 0.25);
    private static readonly IBrush HighlightForeground = new SolidColorBrush(Color.Parse("#f9e2af"));
    private const int MaxHighlights = 300;

    private CancellationTokenSource _cts = new();
    private ResultItem? _item;
    private string? _highlight;
    private Bitmap? _bitmap;
    private IReadOnlyCollection<Bitmap> _markdownImages = [];

    /// <summary>Raised with the full path when the user clicks Open / Show in folder.</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public PreviewPane()
    {
        InitializeComponent();
        OpenButton.Click += (_, _) => { if (_item is not null) OpenRequested?.Invoke(_item.FullPath); };
        ShowInFolderButton.Click += (_, _) => { if (_item is not null) ShowInFolderRequested?.Invoke(_item.FullPath); };
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        EmptyText.Text = Loc.Get("PreviewEmpty");
        OpenButton.Content = Loc.Get("ContextMenuOpen");
        ShowInFolderButton.Content = Loc.Get("ContextMenuShowInFolder");
        Show(_item, _highlight);
    }

    /// <param name="highlight">Text to mark in the preview (the content search query), or null.</param>
    public async void Show(ResultItem? item, string? highlight = null)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _item = item;
        _highlight = highlight;

        SetBitmap(null);
        SetMarkdown(null, null);
        TextScroll.IsVisible = false;
        TextPreview.Inlines?.Clear();
        TextPreview.Text = "";

        EmptyText.IsVisible = item is null;
        Details.IsVisible = item is not null;
        if (item is null) return;

        var r = item.Result;
        NameText.Text = item.FileName;
        PathText.Text = item.Directory;

        FolderIcon.IsVisible = item.ShowFolderIcon;
        DocumentIcon.IsVisible = item.ShowDocumentIcon;
        Badge.IsVisible = item.ShowBadge;
        BadgeText.Text = item.Kind.Label;
        BadgeText.Foreground = Badge.BorderBrush = item.Kind.Foreground;
        Badge.Background = item.Kind.Background;

        var meta = new List<(string, string)>
        {
            (Loc.Get("PreviewType"), r.IsDirectory
                ? Loc.Get("PreviewFolder")
                : item.Kind.Label.Length > 0 ? Loc.Get("PreviewFileType", item.Kind.Label) : Loc.Get("PreviewFile")),
            (Loc.Get("PreviewSize"), $"{Format.Size(r.Size)} ({r.Size:N0} B)"),
            (Loc.Get("PreviewModified"), item.ModifiedText),
        };
        SetMeta(meta);

        BodyMessage.IsVisible = true;
        BodyMessage.Text = Loc.Get("PreviewLoading");

        var provider = PreviewProviders.For(r);
        PreviewContent content;
        try
        {
            content = provider is null
                ? new PreviewContent()
                : await Task.Run(() => provider.LoadAsync(r, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            content = PreviewContent.FromMessage(Loc.Get("PreviewFailed"));
        }

        if (token.IsCancellationRequested)
        {
            content.Image?.Dispose();
            foreach (var image in content.Markdown?.Images.Values ?? []) image.Dispose();
            return;
        }

        if (content.Details.Count > 0)
            SetMeta(meta.Concat(content.Details).ToList());

        if (content.Image is not null)
        {
            SetBitmap(content.Image);
            BodyMessage.IsVisible = false;
        }
        else if (content.Markdown is not null)
        {
            SetMarkdown(content.Markdown, highlight);
            MarkdownFooter.Text = content.Footer;
            MarkdownFooter.IsVisible = content.Footer is not null;
            BodyMessage.IsVisible = false;
        }
        else if (content.Text is not null)
        {
            TextPreview.FontFamily = content.Monospace ? Monospace : FontFamily.Default;
            TextPreview.FontSize = content.Monospace ? 11.5 : 12;
            SetText(content.Footer is null ? content.Text : content.Text + "\n\n" + content.Footer, highlight);
            TextScroll.IsVisible = true;
            TextScroll.Offset = default;
            BodyMessage.IsVisible = false;
        }
        else
        {
            BodyMessage.Text = content.Message ?? Loc.Get("PreviewUnavailable");
        }
    }

    private void SetMeta(IReadOnlyList<(string Label, string Value)> rows)
    {
        MetaGrid.Children.Clear();
        MetaGrid.RowDefinitions.Clear();

        for (var i = 0; i < rows.Count; i++)
        {
            MetaGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var label = new TextBlock { Text = rows[i].Label, Foreground = Brush.Parse("#7f849c"), FontSize = 12, Margin = new(0, 2, 14, 2) };
            var value = new TextBlock { Text = rows[i].Value, Foreground = Brush.Parse("#cdd6f4"), FontSize = 12, Margin = new(0, 2), TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(label, i);
            Grid.SetRow(value, i);
            Grid.SetColumn(value, 1);
            MetaGrid.Children.Add(label);
            MetaGrid.Children.Add(value);
        }
    }

    /// <summary>Shows text with every occurrence of <paramref name="highlight"/> marked.</summary>
    private void SetText(string text, string? highlight)
    {
        if (string.IsNullOrEmpty(highlight) || text.IndexOf(highlight, StringComparison.OrdinalIgnoreCase) < 0)
        {
            TextPreview.Text = text;
            return;
        }

        var inlines = new InlineCollection();
        int start = 0, count = 0, index;
        while (count < MaxHighlights && (index = text.IndexOf(highlight, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (index > start) inlines.Add(new Run(text[start..index]));
            inlines.Add(new Run(text.Substring(index, highlight.Length))
            {
                Background = HighlightBackground,
                Foreground = HighlightForeground,
                FontWeight = FontWeight.Bold
            });
            start = index + highlight.Length;
            count++;
        }
        if (start < text.Length) inlines.Add(new Run(text[start..]));
        TextPreview.Inlines = inlines;
    }

    private void SetMarkdown(MarkdownPreview? markdown, string? highlight)
    {
        MarkdownHost.Content = markdown is null
            ? null
            : MarkdownView.Build(markdown, highlight,
                open: target => OpenRequested?.Invoke(target),
                reveal: target => ShowInFolderRequested?.Invoke(target));
        MarkdownScroll.IsVisible = markdown is not null;
        MarkdownScroll.Offset = default;

        foreach (var image in _markdownImages) image.Dispose();
        _markdownImages = markdown?.Images.Values.ToList() ?? [];
    }

    private void SetBitmap(Bitmap? bitmap)
    {
        ImagePreview.Source = bitmap;
        ImagePreview.IsVisible = bitmap is not null;
        _bitmap?.Dispose();
        _bitmap = bitmap;
    }
}
