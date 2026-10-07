using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using Grepdesk.UI.Jobs;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// A read-only window for text, code and Markdown: the whole file, with line
/// numbers and Ctrl+F, instead of the preview panel's first few kilobytes.
/// Opened with Space on a result; Esc or Space closes it. There is one
/// window, reused for whichever file is viewed next.
/// </summary>
public partial class FileViewerWindow : Window
{
    /// <summary>Bigger Markdown files open as source: rendering builds a control per block.</summary>
    private const int MaxRenderedMarkdownChars = 1_000_000;

    private static LocalizationService Loc => LocalizationService.Instance;

    private CancellationTokenSource _cts = new();
    private string? _highlight;
    private MarkdownPreview? _markdown;
    private bool _preferRendered = true;
    private bool _loading;

    /// <summary>Raised with the full path when the user clicks Open / Show in folder (or a link in Markdown).</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public string? FilePath { get; private set; }

    public FileViewerWindow()
    {
        InitializeComponent();

        Editor.FontFamily = PreviewPane.Monospace;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#89b4fa"), 0.3);
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<LineNumberMargin>())
            margin.Margin = new Thickness(0, 0, 14, 0);

        WrapToggle.IsCheckedChanged += (_, _) => Editor.WordWrap = WrapToggle.IsChecked == true;
        RenderedToggle.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _preferRendered = RenderedToggle.IsChecked == true;
            ShowBody();
        };
        OpenButton.Click += (_, _) => { if (FilePath is not null) OpenRequested?.Invoke(FilePath); };
        ShowInFolderButton.Click += (_, _) => { if (FilePath is not null) ShowInFolderRequested?.Invoke(FilePath); };

        // Bubble, not tunnel: Esc in the Ctrl+F box closes the search box first.
        KeyDown += OnKeyDown;
        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        RenderedToggle.Content = Loc.Get("ViewerRendered");
        WrapToggle.Content = Loc.Get("ViewerWrap");
        OpenButton.Content = Loc.Get("ContextMenuOpen");
        ShowInFolderButton.Content = Loc.Get("ContextMenuShowInFolder");
    }

    /// <param name="highlight">Text to find and select (the content search query), or null.</param>
    public async void Load(string path, string? highlight)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        FilePath = path;
        _highlight = string.IsNullOrEmpty(highlight) ? null : highlight;

        var name = Path.GetFileName(path);
        var directory = Path.GetDirectoryName(path) ?? "";
        Title = $"{name} — Grepdesk";
        NameText.Text = name;
        InfoText.Text = directory;
        SetMarkdown(null);
        Editor.Document = new TextDocument();
        Editor.IsVisible = MarkdownScroll.IsVisible = RenderedToggle.IsVisible = WrapToggle.IsVisible = false;
        ShowMessage(Loc.Get("PreviewLoading"));

        ViewerFile file;
        MarkdownPreview? markdown;
        long size;
        try
        {
            (file, markdown, size) = await Task.Run(() =>
            {
                var loaded = ViewerFileLoader.Load(path, token);
                var md = loaded.Problem == ViewerProblem.None && ViewerFileLoader.IsMarkdown(path) && loaded.Text.Length <= MaxRenderedMarkdownChars
                    ? MarkdownPreviewProvider.Prepare(loaded.Text, directory, token)
                    : null;
                return (loaded, md, new FileInfo(path).Length);
            }, token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) ShowMessage(Loc.Get("PreviewFailed"));
            return;
        }

        if (token.IsCancellationRequested)
        {
            foreach (var image in markdown?.Images.Values ?? []) image.Dispose();
            return;
        }

        if (file.Problem != ViewerProblem.None)
        {
            ShowMessage(file.Problem switch
            {
                ViewerProblem.TooLarge => Loc.Get("ViewerTooLarge", Format.Size(ViewerFileLoader.MaxBytes)),
                ViewerProblem.Binary => Loc.Get("ViewerBinary"),
                ViewerProblem.LongLines => Loc.Get("ViewerLongLines"),
                _ => Loc.Get("PreviewFailed"),
            });
            return;
        }

        InfoText.Text = $"{directory}  ·  {Loc.Get("ViewerLines", file.LineCount)}  ·  {Format.Size(size)}  ·  {file.EncodingName}";
        Editor.Document = new TextDocument(file.Text);
        SetMarkdown(markdown);

        _loading = true;
        RenderedToggle.IsVisible = markdown is not null;
        RenderedToggle.IsChecked = _preferRendered;
        _loading = false;

        ShowBody();
        // After layout: the editor was hidden until now, so it can't scroll yet.
        if (!MarkdownScroll.IsVisible) Dispatcher.UIThread.Post(() => SelectHighlight(file.Text), DispatcherPriority.Loaded);
    }

    private void ShowBody()
    {
        var rendered = _markdown is not null && RenderedToggle.IsChecked == true;
        MarkdownHost.Content = rendered
            ? MarkdownView.Build(_markdown!, _highlight,
                open: target => OpenRequested?.Invoke(target),
                reveal: target => ShowInFolderRequested?.Invoke(target))
            : null;
        MarkdownScroll.IsVisible = rendered;
        MarkdownScroll.Offset = default;
        Editor.IsVisible = !rendered;
        WrapToggle.IsVisible = !rendered;
        MessageText.IsVisible = false;

        if (rendered) MarkdownScroll.Focus();
        else Editor.TextArea.Focus();
    }

    /// <summary>Selects the first match of the content search query and scrolls to it.</summary>
    private void SelectHighlight(string text)
    {
        if (_highlight is null) return;
        var index = text.IndexOf(_highlight, StringComparison.OrdinalIgnoreCase);
        if (index < 0) return;

        Editor.Select(index, _highlight.Length);
        var location = Editor.Document.GetLocation(index);
        Editor.ScrollTo(location.Line, location.Column);
    }

    private void ShowMessage(string message)
    {
        MessageText.Text = message;
        MessageText.IsVisible = true;
    }

    private void SetMarkdown(MarkdownPreview? markdown)
    {
        MarkdownHost.Content = null;
        foreach (var image in _markdown?.Images.Values ?? []) image.Dispose();
        _markdown = markdown;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        // Space closes like Quick Look, except while typing in the search box.
        var typing = FocusManager?.GetFocusedElement() is TextBox;
        if (e.Key == Key.Escape || (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !typing))
        {
            Close();
            e.Handled = true;
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        SetMarkdown(null);
        base.OnClosed(e);
    }
}
