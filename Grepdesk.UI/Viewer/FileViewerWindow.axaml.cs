using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.TextMate;
using Grepdesk.Core.ContentSearch;
using Grepdesk.Core.Preview;
using Grepdesk.Core.Viewer;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Jobs;
using Grepdesk.UI.Preview;
using TextMateSharp.Grammars;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// Shows any file: text and code in an editor (with syntax highlighting, and
/// saving), Markdown rendered, images, PDFs, video and audio, the text of
/// Office documents, and a summary card for everything else.
/// <para>
/// Two modes. As the quick viewer (Space on a search result) it opens
/// read-only and Esc or Space closes it; there is one, reused for whichever
/// file is viewed next. Standalone (a file opened with Grepdesk from
/// Explorer) it is the app's only window and text opens ready to edit.
/// </para>
/// </summary>
public partial class FileViewerWindow : Window
{
    /// <summary>Bigger Markdown files open as source: rendering builds a control per block.</summary>
    private const int MaxRenderedMarkdownChars = 1_000_000;

    /// <summary>Highlighting tokenizes every line; past this, big logs and dumps stay plain.</summary>
    private const int MaxHighlightedChars = 4_000_000;

    private const long MaxImageBytes = 200 * 1024 * 1024;
    private const long MaxDocumentBytes = 50 * 1024 * 1024;
    private const int ShellImageSize = 2048;
    private const int CardThumbnailSize = 256;

    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly Lazy<RegistryOptions> Grammars = new(() => new RegistryOptions(ThemeName.DarkPlus));
    private static readonly IShellPreview? ShellPreview = PlatformShellFactory.CreateShellPreview();
    private static readonly IMediaPlayback? Playback = PlatformShellFactory.CreateMediaPlayback();

    /// <summary>Extensions the grammars don't list under their own name.</summary>
    private static readonly Dictionary<string, string> GrammarAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        [".axaml"] = ".xml", [".xaml"] = ".xml", [".csproj"] = ".xml", [".props"] = ".xml", [".targets"] = ".xml",
        [".config"] = ".xml", [".jsonc"] = ".json", [".mjs"] = ".js", [".cjs"] = ".js", [".psm1"] = ".ps1",
        [".psd1"] = ".ps1", [".kts"] = ".kt", [".zsh"] = ".sh", [".bash"] = ".sh", [".env"] = ".ini", [".cfg"] = ".ini",
    };

    private readonly bool _standalone;
    private readonly TextMate.Installation _textMate;
    private CancellationTokenSource _cts = new();
    private string? _highlight;
    private MarkdownPreview? _markdown;
    private bool _markdownStale;
    private bool _preferRendered = true;
    private bool _loading;
    private bool _closeConfirmed;
    private IViewerBody? _body;
    /// <summary>The file is a Word document: Edit switches between its pages and the Word editor.</summary>
    private bool _word;
    private DocxEditor? _docx;
    private FileEncoding? _encoding;
    private CancellationTokenSource? _statusCts;

    /// <summary>Raised with the full path when the user clicks Open / Show in folder (or a link in Markdown).</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public string? FilePath { get; private set; }

    /// <summary>The editor holds edits that aren't saved yet.</summary>
    public bool HasUnsavedChanges =>
        (_encoding is not null && !Editor.Document.UndoStack.IsOriginalFile) || _docx?.IsDirty == true;

    private bool IsEditing => EditToggle.IsVisible && EditToggle.IsChecked == true;

    public FileViewerWindow() : this(standalone: false) { }

    public FileViewerWindow(bool standalone)
    {
        _standalone = standalone;
        InitializeComponent();
        SessionTrail.Track(this, "viewer");
        if (standalone) WindowStartupLocation = WindowStartupLocation.CenterScreen;

        Editor.FontFamily = PreviewPane.Monospace;
        Editor.TextArea.SelectionBrush = new SolidColorBrush(Color.Parse("#89b4fa"), 0.3);
        Editor.TextArea.SelectionForeground = null;
        Editor.TextArea.SelectionBorder = null;
        Editor.Options.HighlightCurrentLine = true;
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<LineNumberMargin>())
            margin.Margin = new Thickness(0, 0, 14, 0);
        _textMate = Editor.InstallTextMate(Grammars.Value);
        // The theme brings its own current-line colours; ours match the window.
        _textMate.AppliedTheme += (_, _) => ApplyCurrentLineColors();
        ApplyCurrentLineColors();

        WrapToggle.IsCheckedChanged += (_, _) => Editor.WordWrap = WrapToggle.IsChecked == true;
        RenderedToggle.IsCheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _preferRendered = RenderedToggle.IsChecked == true;
            if (_preferRendered && EditToggle.IsChecked == true) EditToggle.IsChecked = false;
            ShowTextBody();
        };
        EditToggle.IsCheckedChanged += async (_, _) =>
        {
            if (_word)
            {
                SaveButton.IsVisible = IsEditing;
                if (!_loading) await SwitchWordEditingAsync(IsEditing);
                return;
            }
            Editor.IsReadOnly = !IsEditing;
            SaveButton.IsVisible = IsEditing;
            if (_loading) return;
            if (IsEditing && RenderedToggle.IsChecked == true) RenderedToggle.IsChecked = false; // edit the source
            else if (IsEditing) Editor.TextArea.Focus();
        };
        SaveButton.Click += async (_, _) => await SaveAsync();
        OpenButton.Click += (_, _) => { if (FilePath is not null) OpenRequested?.Invoke(FilePath); };
        ShowInFolderButton.Click += (_, _) => { if (FilePath is not null) ShowInFolderRequested?.Invoke(FilePath); };

        // Bubble, not tunnel: Esc in the Ctrl+F box closes the search box first.
        KeyDown += OnKeyDown;
        ApplyLanguage();
    }

    private void ApplyCurrentLineColors()
    {
        Editor.TextArea.TextView.CurrentLineBackground = new SolidColorBrush(Color.Parse("#cdd6f4"), 0.04);
        Editor.TextArea.TextView.CurrentLineBorder = new Pen(Brushes.Transparent);
    }

    public void ApplyLanguage()
    {
        RenderedToggle.Content = Loc.Get("ViewerRendered");
        WrapToggle.Content = Loc.Get("ViewerWrap");
        EditToggle.Content = Loc.Get("ViewerEdit");
        SaveButton.Content = Loc.Get("ViewerSave");
        ToolTip.SetTip(SaveButton, "Ctrl+S");
        ToolTip.SetTip(EditToggle, "Ctrl+E");
        OpenButton.Content = Loc.Get("ContextMenuOpen");
        ShowInFolderButton.Content = Loc.Get("ContextMenuShowInFolder");
    }

    /// <param name="highlight">Text to find and select (the content search query), or null.</param>
    public async void Load(string path, string? highlight) => await LoadAsync(path, highlight);

    public async Task LoadAsync(string path, string? highlight)
    {
        if (!await ConfirmLeaveAsync()) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        FilePath = path;
        _highlight = string.IsNullOrEmpty(highlight) ? null : highlight;

        var name = Path.GetFileName(path);
        NameText.Text = name;
        InfoText.Text = Path.GetDirectoryName(path) ?? "";
        Reset();
        UpdateTitle();
        ShowMessage(Loc.Get("PreviewLoading"));

        try
        {
            var kind = ViewerFormats.KindOf(path);
            // Type and size only: the log may be shared, file names stay private.
            SessionTrail.Note($"viewer: open {kind} {Path.GetExtension(path)}, {Format.Size(new FileInfo(path).Length)}");
            switch (kind)
            {
                case ViewerKind.Image or ViewerKind.ShellImage:
                    await LoadImageAsync(path, kind, token);
                    break;
                case ViewerKind.Pdf:
                    await LoadPdfAsync(path, token);
                    break;
                case ViewerKind.Video or ViewerKind.Audio:
                    await LoadMediaAsync(path, kind == ViewerKind.Video, token);
                    break;
                case ViewerKind.Word:
                    await LoadWordAsync(path, token, editIfStandalone: true);
                    break;
                case ViewerKind.Document:
                    await LoadDocumentAsync(path, token);
                    break;
                default:
                    await LoadTextAsync(path, kind, token);
                    break;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            if (!token.IsCancellationRequested) ShowMessage(Loc.Get("PreviewFailed"));
        }
    }

    // =====================================================================
    // Text, code and Markdown
    // =====================================================================

    private async Task LoadTextAsync(string path, ViewerKind kind, CancellationToken token)
    {
        var directory = Path.GetDirectoryName(path) ?? "";
        var (file, markdown, size) = await Task.Run(() =>
        {
            var loaded = ViewerFileLoader.Load(path, token);
            var md = loaded.Problem == ViewerProblem.None && kind == ViewerKind.Markdown && loaded.Text.Length <= MaxRenderedMarkdownChars
                ? MarkdownPreviewProvider.Prepare(loaded.Text, directory, token)
                : null;
            return (loaded, md, new FileInfo(path).Length);
        }, token);

        if (token.IsCancellationRequested)
        {
            foreach (var image in markdown?.Images.Values ?? []) image.Dispose();
            return;
        }

        if (file.Problem != ViewerProblem.None)
        {
            // An unknown type that isn't text: say what it is instead.
            if (kind == ViewerKind.Other && file.Problem == ViewerProblem.Binary)
            {
                await ShowCardAsync(path, Loc.Get("ViewerUnsupported"), token);
                return;
            }
            ShowMessage(file.Problem switch
            {
                ViewerProblem.TooLarge => Loc.Get("ViewerTooLarge", Format.Size(ViewerFileLoader.MaxBytes)),
                ViewerProblem.Binary => Loc.Get("ViewerBinary"),
                ViewerProblem.LongLines => Loc.Get("ViewerLongLines"),
                _ => Loc.Get("PreviewFailed"),
            });
            return;
        }

        _encoding = file.Encoding;
        SetInfo(Loc.Get("ViewerLines", file.LineCount), Format.Size(size), file.EncodingName);
        ShowText(file.Text, GrammarFor(path, file.Text.Length));
        SetMarkdown(markdown);

        _loading = true;
        RenderedToggle.IsVisible = markdown is not null;
        RenderedToggle.IsChecked = _preferRendered;
        EditToggle.IsVisible = true;
        // Opened from Explorer, text is there to be edited; Markdown to be read first.
        EditToggle.IsChecked = _standalone && markdown is null;
        _loading = false;
        Editor.IsReadOnly = !IsEditing;
        SaveButton.IsVisible = IsEditing;

        ShowTextBody();
        // After layout: the editor was hidden until now, so it can't scroll yet.
        if (!MarkdownScroll.IsVisible) Dispatcher.UIThread.Post(() => SelectHighlight(file.Text), DispatcherPriority.Loaded);
    }

    /// <summary>Word documents as pages; their text alone when the layout can't be read.</summary>
    private async Task LoadWordAsync(string path, CancellationToken token, bool editIfStandalone)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxDocumentBytes)
        {
            await ShowCardAsync(path, Loc.Get("ViewerTooLarge", Format.Size(MaxDocumentBytes)), token);
            return;
        }

        DocxPreview preview;
        try
        {
            preview = await Task.Run(() => DocxPreview.Load(path, token), token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            // Damaged, password protected (an encrypted package isn't a zip), or something unexpected in the XML.
            ErrorLog.Write("docx viewer", e.ToString());
            if (!token.IsCancellationRequested) await LoadDocumentAsync(path, token);
            return;
        }

        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }
        SetInfo(Format.Size(size));
        ShowBody(new DocxView(preview, _highlight,
            open: target => OpenRequested?.Invoke(target),
            reveal: target => ShowInFolderRequested?.Invoke(target)));

        _word = true;
        SetEditToggle(false);
        EditToggle.IsVisible = true;
        // Opened from Explorer, a document is there to be worked on, as in Word.
        if (_standalone && editIfStandalone) EditToggle.IsChecked = true;
    }

    private void SetEditToggle(bool on)
    {
        _loading = true;
        EditToggle.IsChecked = on;
        _loading = false;
        SaveButton.IsVisible = on;
    }

    /// <summary>From the pages to the editor and back. Leaving the editor with changes asks to save them first.</summary>
    private async Task SwitchWordEditingAsync(bool editing)
    {
        if (FilePath is not { } path) return;
        var token = _cts.Token;

        if (!editing)
        {
            if (_docx is { IsDirty: true } && !await ConfirmLeaveAsync())
            {
                SetEditToggle(true);
                return;
            }
            DisposeBody();
            ShowMessage(Loc.Get("PreviewLoading"));
            await LoadWordAsync(path, token, editIfStandalone: false);
            UpdateTitle();
            return;
        }

        DocxEditDocument document;
        DocxDocument layout;
        Dictionary<byte[], Bitmap> images = new(ReferenceEqualityComparer.Instance);
        try
        {
            (document, layout) = await Task.Run(() =>
            {
                var opened = DocxEditDocument.Open(path, token);
                var laid = opened.Layout(token);
                DocxPreview.From(laid, "", images, token, ownsImages: false);
                return (opened, laid);
            }, token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            ErrorLog.Write("docx editor", e.ToString());
            foreach (var bitmap in images.Values) bitmap.Dispose();
            if (FilePath == path) ShowStatus(Loc.Get("DocxEditFailed", e.Message), ok: false);
            SetEditToggle(false);
            return;
        }
        catch (OperationCanceledException)
        {
            foreach (var bitmap in images.Values) bitmap.Dispose();
            return;
        }
        if (token.IsCancellationRequested || FilePath != path || !IsEditing)
        {
            foreach (var bitmap in images.Values) bitmap.Dispose();
            return;
        }

        DisposeBody();
        var editor = new DocxEditor(document, path, images);
        editor.Changed += UpdateTitle;
        ShowBody(new DocxView(editor.Preview(layout, token), null,
            open: target => OpenRequested?.Invoke(target),
            reveal: target => ShowInFolderRequested?.Invoke(target),
            editor));
        _docx = editor;
        SaveButton.IsVisible = true;
        UpdateTitle();
    }

    /// <summary>Excel and other documents: their text, read-only.</summary>
    private async Task LoadDocumentAsync(string path, CancellationToken token)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxDocumentBytes)
        {
            await ShowCardAsync(path, Loc.Get("ViewerTooLarge", Format.Size(MaxDocumentBytes)), token);
            return;
        }

        ExtractorRegistry.TryGetExtractor(Path.GetExtension(path), out var extractor);
        var text = await Task.Run(() => extractor.ExtractTextAsync(path, token), token);
        if (token.IsCancellationRequested) return;
        if (string.IsNullOrWhiteSpace(text))
        {
            await ShowCardAsync(path, Loc.Get("PreviewFailed"), token);
            return;
        }

        SetInfo(Format.Size(size), Loc.Get("ViewerExtractedText"));
        ShowText(text, null);
        WrapToggle.IsChecked = true;
        ShowTextBody();
        Dispatcher.UIThread.Post(() => SelectHighlight(text), DispatcherPriority.Loaded);
    }

    private void ShowText(string text, string? grammar)
    {
        // A fresh document starts a fresh undo history, marked as "saved".
        Editor.Document = new TextDocument(text);
        Editor.Document.UndoStack.PropertyChanged += (_, _) => UpdateTitle();
        Editor.Document.TextChanged += (_, _) => _markdownStale = true;
        _markdownStale = false;
        _textMate.SetGrammar(grammar);
        WrapToggle.IsVisible = true;
        UpdateTitle();
    }

    private static string? GrammarFor(string path, int length)
    {
        if (length > MaxHighlightedChars) return null;
        var ext = Path.GetExtension(path);
        if (GrammarAliases.TryGetValue(ext, out var alias)) ext = alias;
        if (ext.Length == 0)
            ext = Path.GetFileName(path).ToLowerInvariant() switch { "dockerfile" => ".dockerfile", "makefile" => ".mk", _ => "" };
        if (ext.Length == 0) return null;

        try
        {
            return Grammars.Value.GetLanguageByExtension(ext) is { } language
                ? Grammars.Value.GetScopeByLanguageId(language.Id)
                : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null; // a grammar that fails to load just means no colours
        }
    }

    private async void ShowTextBody()
    {
        var rendered = _markdown is not null && RenderedToggle.IsChecked == true;
        if (rendered && _markdownStale)
        {
            // Edited since it was rendered: render what's in the editor now.
            var text = Editor.Text;
            var directory = Path.GetDirectoryName(FilePath) ?? "";
            var token = _cts.Token;
            _markdownStale = false;
            try
            {
                SetMarkdown(await Task.Run(() => MarkdownPreviewProvider.Prepare(text, directory, token), token));
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        MarkdownHost.Content = rendered
            ? MarkdownView.Build(_markdown!, _highlight,
                open: target => OpenRequested?.Invoke(target),
                reveal: target => ShowInFolderRequested?.Invoke(target))
            : null;
        MarkdownScroll.IsVisible = rendered;
        MarkdownScroll.Offset = default;
        Editor.IsVisible = !rendered;
        WrapToggle.IsVisible = !rendered;
        MessagePanel.IsVisible = false;

        // After layout: a control that was hidden until now can't take focus yet.
        Dispatcher.UIThread.Post(() =>
        {
            if (rendered) MarkdownScroll.Focus();
            else Editor.TextArea.Focus();
        }, DispatcherPriority.Loaded);
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

    private void SetMarkdown(MarkdownPreview? markdown)
    {
        MarkdownHost.Content = null;
        foreach (var image in _markdown?.Images.Values ?? []) image.Dispose();
        _markdown = markdown;
    }

    private async Task<bool> SaveAsync()
    {
        if (_docx is { } docx)
        {
            try
            {
                // On the UI thread: the document is edited there, and saving reads it.
                docx.Save();
                ShowStatus(Loc.Get("ViewerSaved"), ok: true);
                UpdateTitle();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Most often: the file is open in Word, which locks it.
                ShowStatus(Loc.Get("ViewerSaveFailed", e.Message), ok: false);
                return false;
            }
        }
        if (FilePath is null || _encoding is null || !IsEditing) return false;
        var path = FilePath;
        var text = Editor.Text;
        var encoding = _encoding;
        try
        {
            var used = await Task.Run(() => ViewerFileLoader.Save(path, text, encoding));
            if (path != FilePath) return true;

            _encoding = used;
            Editor.Document.UndoStack.MarkAsOriginalFile();
            var size = new FileInfo(path).Length;
            SetInfo(Loc.Get("ViewerLines", Editor.Document.LineCount), Format.Size(size), used.Name);
            ShowStatus(used == encoding ? Loc.Get("ViewerSaved") : Loc.Get("ViewerSavedAsUtf8", encoding.Name), ok: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            ShowStatus(Loc.Get("ViewerSaveFailed", e.Message), ok: false);
            return false;
        }
    }

    /// <summary>
    /// Before the edited file is replaced or the window closes: save, discard or stay.
    /// </summary>
    /// <returns>False to stay on the current file.</returns>
    private async Task<bool> ConfirmLeaveAsync()
    {
        if (!HasUnsavedChanges || FilePath is null) return true;
        return await SaveChangesDialog.ShowAsync(this, Path.GetFileName(FilePath)) switch
        {
            SaveChoice.Save => await SaveAsync(),
            SaveChoice.Discard => true,
            _ => false,
        };
    }

    // =====================================================================
    // Images, PDFs, video and audio
    // =====================================================================

    private async Task LoadImageAsync(string path, ViewerKind kind, CancellationToken token)
    {
        var size = new FileInfo(path).Length;
        var bitmap = await Task.Run(() =>
        {
            if (kind == ViewerKind.Image && size <= MaxImageBytes)
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    return new Bitmap(stream);
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException or NotSupportedException)
                {
                    // Not really a PNG/JPEG... Windows may still have a codec for it.
                }
            }
            return ShellBitmap(path, ShellImageSize);
        }, token);

        if (token.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }
        if (bitmap is null)
        {
            await ShowCardAsync(path, Loc.Get("ViewerImageFailed"), token);
            return;
        }

        SetInfo(Format.Size(size));
        ShowBody(new ImageView(bitmap));
    }

    private async Task LoadPdfAsync(string path, CancellationToken token)
    {
        PdfRenderer renderer;
        try
        {
            renderer = await Task.Run(() => PdfRenderer.Open(path, token), token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            // Corrupt, password protected, or PDFium's library is missing on this platform.
            if (!token.IsCancellationRequested) await ShowCardAsync(path, Loc.Get("ViewerPdfFailed"), token);
            return;
        }

        if (token.IsCancellationRequested)
        {
            renderer.Dispose();
            return;
        }
        SetInfo(Loc.Get("ViewerPages", renderer.TotalPages), Format.Size(new FileInfo(path).Length));
        ShowBody(new PdfView(renderer));
    }

    private async Task LoadMediaAsync(string path, bool video, CancellationToken token)
    {
        if (Playback is null)
        {
            await ShowCardAsync(path, Loc.Get("ViewerMediaUnsupported"), token);
            return;
        }

        var art = video ? null : await Task.Run(() => ShellBitmap(path, 512), token);
        if (token.IsCancellationRequested)
        {
            art?.Dispose();
            return;
        }

        var size = Format.Size(new FileInfo(path).Length);
        SetInfo(size);
        var view = new MediaView(Playback, path, video, art) { SpaceToggles = _standalone };
        view.InfoChanged += info => { if (FilePath == path) SetInfo(info, size); };
        view.Failed += async error =>
        {
            ErrorLog.Write($"media {Path.GetExtension(path)}", error.ToString());
            SessionTrail.Note($"media: failed at {error.Stage}, 0x{error.HResult:X8} ({error.Problem})");
            if (FilePath != path) return;
            DisposeBody();
            var code = $"0x{error.HResult:X8}";
            var message = error.Problem switch
            {
                MediaProblem.CodecMissing => Loc.Get("ViewerMediaCodecMissing", code),
                MediaProblem.NoMediaFoundation => Loc.Get("ViewerMediaNoMediaFoundation"),
                MediaProblem.NoVideoWindow => Loc.Get("ViewerMediaNoVideoWindow", code),
                _ => Loc.Get("ViewerMediaFailed", code),
            };
            await ShowCardAsync(path, message, _cts.Token);
        };
        ShowBody(view);
    }

    private static Bitmap? ShellBitmap(string path, int size)
    {
        if (ShellPreview is null) return null;
        // Explorer's handlers are native code from whoever registered the type.
        SessionTrail.Note($"viewer: shell thumbnail {Path.GetExtension(path)}");
        return ShellPreview.GetThumbnail(path, size) is { } thumbnail ? ShellPreviewProvider.ToBitmap(thumbnail) : null;
    }

    private void ShowBody(IViewerBody body)
    {
        _body = body;
        BodyHost.Content = body;
        BodyHost.IsVisible = true;
        MessagePanel.IsVisible = false;
        Dispatcher.UIThread.Post(body.FocusContent, DispatcherPriority.Loaded);
    }

    private void DisposeBody()
    {
        _docx = null;
        BodyHost.Content = null;
        BodyHost.IsVisible = false;
        _body?.Dispose();
        _body = null;
    }

    // =====================================================================
    // Messages and the summary card
    // =====================================================================

    /// <summary>For files nothing here can show: their thumbnail if the OS has one, what they are, and why.</summary>
    private async Task ShowCardAsync(string path, string message, CancellationToken token)
    {
        var thumbnail = await Task.Run(() => ShellBitmap(path, CardThumbnailSize), token);
        if (token.IsCancellationRequested)
        {
            thumbnail?.Dispose();
            return;
        }

        var info = new FileInfo(path);
        var rows = new List<(string, string)>
        {
            (Loc.Get("ViewerType"), info.Extension.Length > 1 ? info.Extension[1..].ToUpperInvariant() : "—"),
            (Loc.Get("ViewerSize"), Format.Size(info.Length)),
            (Loc.Get("ViewerModified"), info.LastWriteTime.ToString("g", CultureInfo.CurrentCulture)),
        };
        SetInfo(Format.Size(info.Length));
        ShowMessage(message, thumbnail, rows);
    }

    private void ShowMessage(string message, Bitmap? image = null, IReadOnlyList<(string Label, string Value)>? details = null)
    {
        SetMessageImage(image);
        MessageText.Text = message;
        MessageDetails.ItemsSource = details?.Select(DetailRow).ToList();
        MessagePanel.IsVisible = true;
    }

    private static Control DetailRow((string Label, string Value) row) => new StackPanel
    {
        Orientation = Orientation.Horizontal,
        Spacing = 8,
        HorizontalAlignment = HorizontalAlignment.Center,
        Children =
        {
            new TextBlock { Text = row.Label, Foreground = ViewerBar.Muted, FontSize = 12 },
            new TextBlock { Text = row.Value, Foreground = ViewerBar.Text, FontSize = 12 },
        }
    };

    private void SetMessageImage(Bitmap? image)
    {
        (MessageImage.Source as Bitmap)?.Dispose();
        MessageImage.Source = image;
        MessageImage.IsVisible = image is not null;
    }

    private void SetInfo(params string[] parts)
    {
        var directory = Path.GetDirectoryName(FilePath) ?? "";
        InfoText.Text = string.Join("  ·  ", parts.Where(p => p.Length > 0).Prepend(directory));
    }

    private async void ShowStatus(string text, bool ok)
    {
        _statusCts?.Cancel();
        var cts = _statusCts = new CancellationTokenSource();
        StatusText.Text = text;
        StatusText.Foreground = Brush.Parse(ok ? "#a6e3a1" : "#f38ba8");
        StatusText.IsVisible = true;
        try
        {
            await Task.Delay(ok ? 2500 : 8000, cts.Token);
            StatusText.IsVisible = false;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateTitle()
    {
        var name = FilePath is null ? "" : Path.GetFileName(FilePath);
        var dirty = HasUnsavedChanges ? "• " : "";
        Title = $"{dirty}{name} — Grepdesk";
        NameText.Text = dirty + name;
    }

    /// <summary>Back to an empty window before the next file loads.</summary>
    private void Reset()
    {
        _word = false;
        DisposeBody();
        SetMarkdown(null);
        SetMessageImage(null);
        _encoding = null;
        _textMate.SetGrammar(null);
        Editor.Document = new TextDocument();
        StatusText.IsVisible = false;
        _loading = true;
        EditToggle.IsChecked = false;
        _loading = false;
        Editor.IsReadOnly = true;
        Editor.IsVisible = MarkdownScroll.IsVisible = RenderedToggle.IsVisible = WrapToggle.IsVisible = false;
        EditToggle.IsVisible = SaveButton.IsVisible = false;
        MessageDetails.ItemsSource = null;
    }

    // =====================================================================
    // Keyboard and closing
    // =====================================================================

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.S && IsEditing)
        {
            e.Handled = true;
            await SaveAsync();
            return;
        }
        if (ctrl && e.Key == Key.E && EditToggle.IsVisible)
        {
            EditToggle.IsChecked = !IsEditing;
            e.Handled = true;
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox || (IsEditing && Editor.IsKeyboardFocusWithin) ||
                     _body is DocxView { IsEditing: true, IsKeyboardFocusWithin: true };
        if (!typing && !ctrl && _body?.HandleKey(e) == true)
        {
            e.Handled = true;
            return;
        }

        // The quick viewer closes like Quick Look, except while typing.
        if (_standalone) return;
        if (e.Key == Key.Escape || (e.Key == Key.Space && e.KeyModifiers == KeyModifiers.None && !typing))
        {
            Close();
            e.Handled = true;
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (_closeConfirmed || !HasUnsavedChanges) return;

        e.Cancel = true;
        if (!await ConfirmLeaveAsync()) return;
        _closeConfirmed = true;
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts.Cancel();
        DisposeBody();
        SetMarkdown(null);
        SetMessageImage(null);
        _textMate.Dispose();
        base.OnClosed(e);
    }
}
