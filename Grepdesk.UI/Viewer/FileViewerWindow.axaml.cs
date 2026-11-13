using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using AvaloniaEdit.Document;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Folding;
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

    private const double DefaultEditorFontSize = 12.5;
    private const double MinEditorFontSize = 8.0;
    private const double MaxEditorFontSize = 32.0;

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
    private readonly CodeEditorHighlighter _highlighter = new();
    private readonly DispatcherTimer _structureTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private FoldingManager? _foldingManager;
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
    private bool _excel;
    private bool _powerPoint;
    private DocxEditor? _docx;
    private XlsxEditDocument? _xlsxEdit;
    private PptxEditDocument? _pptxEdit;
    private FileEncoding? _encoding;
    private bool _encodingChanged;
    private CancellationTokenSource? _statusCts;
    private string _cachedText = "";
    private bool _cachedTextDirty;
    private IReadOnlyList<CodeSymbol> _symbols = [];
    private IReadOnlyList<TextSpan> _searchMatches = [];
    private int _activeMatchIndex = -1;

    /// <summary>Raised with the full path when the user clicks Open / Show in folder (or a link in Markdown).</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public string? FilePath { get; private set; }

    /// <summary>The editor holds edits that aren't saved yet.</summary>
    public bool HasUnsavedChanges =>
        (_encoding is not null && (!Editor.Document.UndoStack.IsOriginalFile || _encodingChanged))
        || _docx?.IsDirty == true
        || _xlsxEdit?.IsDirty == true
        || _pptxEdit?.IsDirty == true;

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
        SetupEditorFeatures();

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
            if (_excel)
            {
                SaveButton.IsVisible = IsEditing;
                if (!_loading) await SwitchExcelEditingAsync(IsEditing);
                return;
            }
            if (_powerPoint)
            {
                SaveButton.IsVisible = IsEditing;
                if (!_loading) await SwitchPowerPointEditingAsync(IsEditing);
                return;
            }
            Editor.IsReadOnly = !IsEditing;
            SaveButton.IsVisible = IsEditing;
            ReplaceButton.IsEnabled = IsEditing;
            ReplaceAllButton.IsEnabled = IsEditing;
            UpdateEditorStatusBar();
            if (_loading) return;
            if (IsEditing && RenderedToggle.IsChecked == true) RenderedToggle.IsChecked = false; // edit the source
            else if (IsEditing) Editor.TextArea.Focus();
        };
        SaveButton.Click += async (_, _) => await SaveAsync();
        PrevFileButton.Click += async (_, _) => await ShowNeighborAsync(-1);
        NextFileButton.Click += async (_, _) => await ShowNeighborAsync(+1);
        OpenButton.Click += (_, _) => { if (FilePath is not null) OpenRequested?.Invoke(FilePath); };
        ShowInFolderButton.Click += (_, _) => { if (FilePath is not null) ShowInFolderRequested?.Invoke(FilePath); };

        // Tunnel catches IDE shortcuts (Ctrl+F/H/G, Ctrl+/, F3, Esc in find bar) before AvaloniaEdit.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
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
        FindButton.Content = Loc.Get("ViewerFind");
        OutlineButton.Content = Loc.Get("ViewerOutline");
        FormatButton.Content = Loc.Get("ViewerFormat");
        RenderedToggle.Content = Loc.Get("ViewerRendered");
        WrapToggle.Content = Loc.Get("ViewerWrap");
        EditToggle.Content = Loc.Get("ViewerEdit");
        SaveButton.Content = Loc.Get("ViewerSave");

        FindBox.Watermark = Loc.Get("ViewerFindWatermark");
        ReplaceBox.Watermark = Loc.Get("ViewerReplaceWatermark");
        ReplaceButton.Content = Loc.Get("ViewerReplace");
        ReplaceAllButton.Content = Loc.Get("ViewerReplaceAll");
        GoToLineBox.Watermark = Loc.Get("ViewerGoToLineWatermark");
        GoToLineButton.Content = Loc.Get("ViewerGoToLineGo");

        ToolTip.SetTip(FindButton, "Ctrl+F");
        ToolTip.SetTip(OutlineButton, "Ctrl+Shift+O");
        ToolTip.SetTip(FormatButton, "Shift+Alt+F");
        ToolTip.SetTip(SaveButton, "Ctrl+S");
        ToolTip.SetTip(EditToggle, "Ctrl+E");
        ToolTip.SetTip(PrevFileButton, Loc.Get("ViewerPrevFile"));
        ToolTip.SetTip(NextFileButton, Loc.Get("ViewerNextFile"));
        ToolTip.SetTip(MatchCaseToggle, Loc.Get("ViewerMatchCase"));
        ToolTip.SetTip(WholeWordToggle, Loc.Get("ViewerWholeWord"));
        ToolTip.SetTip(RegexToggle, Loc.Get("ViewerRegex"));
        ToolTip.SetTip(FindPrevButton, Loc.Get("ViewerFindPrev"));
        ToolTip.SetTip(FindNextButton, Loc.Get("ViewerFindNext"));
        ToolTip.SetTip(WhitespaceToggle, Loc.Get("ViewerWhitespace"));
        ToolTip.SetTip(CursorPosButton, Loc.Get("ViewerGoToLine"));
        ToolTip.SetTip(SymbolCrumbButton, Loc.Get("ViewerOutline"));
        ToolTip.SetTip(LineEndingButton, Loc.Get("ViewerLineEndingTip"));
        ToolTip.SetTip(EncodingButton, Loc.Get("ViewerEncodingTip"));
        ToolTip.SetTip(EditorZoomButton, Loc.Get("ViewerZoomResetTip"));

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
                case ViewerKind.Excel:
                    await LoadExcelAsync(path, token, editIfStandalone: true);
                    break;
                case ViewerKind.PowerPoint:
                    await LoadPowerPointAsync(path, token, editIfStandalone: true);
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

    /// <summary>Excel workbooks as a spreadsheet grid with sheet tabs and formula bar.</summary>
    private async Task LoadExcelAsync(string path, CancellationToken token, bool editIfStandalone)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxDocumentBytes)
        {
            await ShowCardAsync(path, Loc.Get("ViewerTooLarge", Format.Size(MaxDocumentBytes)), token);
            return;
        }

        XlsxDocument document;
        try
        {
            document = await Task.Run(() => XlsxDocument.Load(path, token), token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            ErrorLog.Write("xlsx viewer", e.ToString());
            if (!token.IsCancellationRequested) await LoadDocumentAsync(path, token);
            return;
        }

        if (token.IsCancellationRequested) return;
        SetInfo(Loc.Get("ViewerSheets", document.Sheets.Count), Format.Size(size));
        ShowBody(new XlsxView(document, _highlight));

        _excel = true;
        SetEditToggle(false);
        EditToggle.IsVisible = true;
        if (_standalone && editIfStandalone) EditToggle.IsChecked = true;
    }

    private async Task SwitchExcelEditingAsync(bool editing)
    {
        if (FilePath is not { } path) return;
        var token = _cts.Token;

        if (!editing)
        {
            if (_xlsxEdit is { IsDirty: true } && !await ConfirmLeaveAsync())
            {
                SetEditToggle(true);
                return;
            }
            DisposeBody();
            ShowMessage(Loc.Get("PreviewLoading"));
            await LoadExcelAsync(path, token, editIfStandalone: false);
            UpdateTitle();
            return;
        }

        XlsxEditDocument editDoc;
        XlsxDocument layout;
        try
        {
            (editDoc, layout) = await Task.Run(() =>
            {
                var opened = XlsxEditDocument.Open(path, token);
                var laid = opened.Layout(token);
                return (opened, laid);
            }, token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            ErrorLog.Write("xlsx editor", e.ToString());
            if (FilePath == path) ShowStatus(Loc.Get("DocxEditFailed", e.Message), ok: false);
            SetEditToggle(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || FilePath != path || !IsEditing) return;

        DisposeBody();
        var view = new XlsxView(layout, _highlight, editDoc);
        view.Changed += UpdateTitle;
        ShowBody(view);
        _xlsxEdit = editDoc;
        SaveButton.IsVisible = true;
        UpdateTitle();
    }

    /// <summary>PowerPoint presentations as slides with outline sidebar and speaker notes.</summary>
    private async Task LoadPowerPointAsync(string path, CancellationToken token, bool editIfStandalone)
    {
        var size = new FileInfo(path).Length;
        if (size > MaxDocumentBytes)
        {
            await ShowCardAsync(path, Loc.Get("ViewerTooLarge", Format.Size(MaxDocumentBytes)), token);
            return;
        }

        PptxPreview preview;
        try
        {
            preview = await Task.Run(() => PptxPreview.Load(path, token), token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            ErrorLog.Write("pptx viewer", e.ToString());
            if (!token.IsCancellationRequested) await LoadDocumentAsync(path, token);
            return;
        }

        if (token.IsCancellationRequested)
        {
            preview.Dispose();
            return;
        }

        SetInfo(Loc.Get("ViewerSlides", preview.Document.Slides.Count), Format.Size(size));
        ShowBody(new PptxView(preview, _highlight));

        var canEdit = PptxDocument.CanEdit(path);
        _powerPoint = true;
        SetEditToggle(false);
        EditToggle.IsVisible = canEdit;
        if (canEdit && _standalone && editIfStandalone) EditToggle.IsChecked = true;
    }

    private async Task SwitchPowerPointEditingAsync(bool editing)
    {
        if (FilePath is not { } path) return;
        if (editing && !PptxDocument.CanEdit(path))
        {
            SetEditToggle(false);
            return;
        }
        var token = _cts.Token;

        if (!editing)
        {
            if (_pptxEdit is { IsDirty: true } && !await ConfirmLeaveAsync())
            {
                SetEditToggle(true);
                return;
            }
            DisposeBody();
            ShowMessage(Loc.Get("PreviewLoading"));
            await LoadPowerPointAsync(path, token, editIfStandalone: false);
            UpdateTitle();
            return;
        }

        PptxEditDocument editDoc;
        PptxPreview preview;
        try
        {
            (editDoc, preview) = await Task.Run(() =>
            {
                var opened = PptxEditDocument.Open(path, token);
                var laid = opened.Layout(token);
                var images = new Dictionary<byte[], Bitmap>(ReferenceEqualityComparer.Instance);
                return (opened, PptxPreview.From(laid, images, token, ownsImages: true));
            }, token);
        }
        catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
        {
            ErrorLog.Write("pptx editor", e.ToString());
            if (FilePath == path) ShowStatus(Loc.Get("DocxEditFailed", e.Message), ok: false);
            SetEditToggle(false);
            return;
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (token.IsCancellationRequested || FilePath != path || !IsEditing)
        {
            preview.Dispose();
            return;
        }

        DisposeBody();
        var view = new PptxView(preview, _highlight, editDoc);
        view.Changed += UpdateTitle;
        ShowBody(view);
        _pptxEdit = editDoc;
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
        _cachedText = text;
        _cachedTextDirty = false;
        _encodingChanged = false;
        Editor.Document = new TextDocument(text);
        Editor.Document.UndoStack.PropertyChanged += (_, _) => UpdateTitle();
        Editor.Document.TextChanged += (_, _) =>
        {
            _markdownStale = true;
            _cachedTextDirty = true;
            _structureTimer.Stop();
            _structureTimer.Start();
            UpdateEditorStatusBar();
        };
        _markdownStale = false;
        _textMate.SetGrammar(grammar);
        ReinstallFoldingManager();
        RefreshCodeStructure();
        UpdateEditorStatusBar();
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
        EditorContainer.IsVisible = !rendered;
        FindButton.IsVisible = true;
        OutlineButton.IsVisible = _symbols.Count > 0;
        FormatButton.IsVisible = !rendered && _encoding is not null && CodeFormatter.CanFormat(FilePath);
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
        ScrollEditorTo(location.Line, location.Column, _highlight.Length);
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
        if (_xlsxEdit is { } xlsxEdit && FilePath is { } xlsxPath)
        {
            try
            {
                xlsxEdit.Save(xlsxPath);
                ShowStatus(Loc.Get("ViewerSaved"), ok: true);
                UpdateTitle();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                ShowStatus(Loc.Get("ViewerSaveFailed", e.Message), ok: false);
                return false;
            }
        }
        if (_pptxEdit is { } pptxEdit && FilePath is { } pptxPath)
        {
            try
            {
                pptxEdit.Save(pptxPath);
                ShowStatus(Loc.Get("ViewerSaved"), ok: true);
                UpdateTitle();
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
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
            _encodingChanged = false;
            Editor.Document.UndoStack.MarkAsOriginalFile();
            UpdateTitle();
            UpdateEditorStatusBar();
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
        _xlsxEdit = null;
        _pptxEdit = null;
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
        _excel = false;
        _powerPoint = false;
        _structureTimer.Stop();
        DisposeBody();
        SetMarkdown(null);
        SetMessageImage(null);
        _encoding = null;
        _encodingChanged = false;
        _cachedText = "";
        _cachedTextDirty = false;
        _symbols = [];
        _searchMatches = [];
        _activeMatchIndex = -1;
        _textMate.SetGrammar(null);
        Editor.Document = new TextDocument();
        _foldingManager?.UpdateFoldings([], -1);
        _highlighter.SetMatches(Editor.TextArea.TextView, [], -1);
        _highlighter.SetBrackets(Editor.TextArea.TextView, null);
        StatusText.IsVisible = false;
        _loading = true;
        EditToggle.IsChecked = false;
        _loading = false;
        Editor.IsReadOnly = true;
        EditorFindBar.IsVisible = false;
        SymbolCrumbButton.IsVisible = false;
        EditorContainer.IsVisible = MarkdownScroll.IsVisible = RenderedToggle.IsVisible = WrapToggle.IsVisible = false;
        FindButton.IsVisible = OutlineButton.IsVisible = FormatButton.IsVisible = false;
        EditToggle.IsVisible = SaveButton.IsVisible = false;
        MessageDetails.ItemsSource = null;
    }

    // =====================================================================
    // IDE features: Find/Replace, Go to Line, Folding, Outline, Format, Status
    // =====================================================================

    private void SetupEditorFeatures()
    {
        Editor.TextArea.TextView.BackgroundRenderers.Add(_highlighter);
        ReinstallFoldingManager();

        _structureTimer.Tick += (_, _) =>
        {
            _structureTimer.Stop();
            RefreshCodeStructure();
            if (EditorFindBar.IsVisible && FindRow.IsVisible)
                UpdateFindMatches(jumpToNearest: false);
        };

        Editor.TextArea.Caret.PositionChanged += (_, _) => OnEditorCaretOrSelectionChanged();
        Editor.TextArea.SelectionChanged += (_, _) => OnEditorCaretOrSelectionChanged();
        Editor.AddHandler(PointerWheelChangedEvent, OnEditorPointerWheelChanged, RoutingStrategies.Tunnel);

        FindButton.Click += (_, _) => OpenFindBar(replace: false, goToLine: false);
        OutlineButton.Click += (_, _) => ShowOutlineMenu(OutlineButton);
        FormatButton.Click += (_, _) => FormatDocument();

        FindBox.TextChanged += (_, _) => UpdateFindMatches(jumpToNearest: true);
        MatchCaseToggle.IsCheckedChanged += (_, _) => UpdateFindMatches(jumpToNearest: true);
        WholeWordToggle.IsCheckedChanged += (_, _) => UpdateFindMatches(jumpToNearest: true);
        RegexToggle.IsCheckedChanged += (_, _) => UpdateFindMatches(jumpToNearest: true);
        FindPrevButton.Click += (_, _) => StepSearchMatch(backward: true);
        FindNextButton.Click += (_, _) => StepSearchMatch(backward: false);
        CloseFindButton.Click += (_, _) => CloseFindBar();
        CloseGoToLineButton.Click += (_, _) => CloseFindBar();

        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            StepSearchMatch(backward: e.KeyModifiers.HasFlag(KeyModifiers.Shift));
            e.Handled = true;
        };
        ReplaceBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ReplaceCurrentMatch();
            e.Handled = true;
        };
        ReplaceButton.Click += (_, _) => ReplaceCurrentMatch();
        ReplaceAllButton.Click += (_, _) => ReplaceAllMatches();

        GoToLineBox.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ExecuteGoToLine();
            e.Handled = true;
        };
        GoToLineButton.Click += (_, _) => ExecuteGoToLine();

        CursorPosButton.Click += (_, _) => OpenFindBar(replace: false, goToLine: true);
        SymbolCrumbButton.Click += (_, _) => ShowOutlineMenu(SymbolCrumbButton);
        WhitespaceToggle.IsCheckedChanged += (_, _) =>
        {
            var show = WhitespaceToggle.IsChecked == true;
            Editor.Options.ShowSpaces = show;
            Editor.Options.ShowTabs = show;
        };
        LineEndingButton.Click += (_, _) => ToggleLineEnding();
        EncodingButton.Click += (_, _) => ShowEncodingMenu();
        EditorZoomButton.Click += (_, _) => SetEditorFontSize(DefaultEditorFontSize);
    }

    private void ReinstallFoldingManager()
    {
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
        _foldingManager = FoldingManager.Install(Editor.TextArea);
        StyleFoldingMargin();
    }

    private void StyleFoldingMargin()
    {
        foreach (var margin in Editor.TextArea.LeftMargins.OfType<FoldingMargin>())
        {
            margin.FoldingMarkerBrush = new SolidColorBrush(Color.Parse("#585b70"));
            margin.FoldingMarkerBackgroundBrush = new SolidColorBrush(Color.Parse("#11111b"));
            margin.SelectedFoldingMarkerBrush = new SolidColorBrush(Color.Parse("#89b4fa"));
            margin.SelectedFoldingMarkerBackgroundBrush = new SolidColorBrush(Color.Parse("#1e1e2e"));
        }
    }

    private string GetEditorText()
    {
        if (_cachedTextDirty)
        {
            _cachedText = Editor.Document.Text;
            _cachedTextDirty = false;
        }
        return _cachedText;
    }

    private void RefreshCodeStructure()
    {
        if (FilePath is null) return;
        var text = GetEditorText();
        var ext = Path.GetExtension(FilePath);

        if (_foldingManager is not null && text.Length <= MaxHighlightedChars)
        {
            var foldSpans = CodeFolder.GetFoldings(text, ext);
            var foldings = new List<NewFolding>(foldSpans.Count);
            foreach (var f in foldSpans)
                foldings.Add(new NewFolding(f.StartOffset, f.EndOffset) { Name = f.Title });
            _foldingManager.UpdateFoldings(foldings, -1);
        }
        else
        {
            _foldingManager?.UpdateFoldings([], -1);
        }

        _symbols = CodeSymbolExtractor.ExtractSymbols(text, ext);
        OutlineButton.IsVisible = _symbols.Count > 0;
        UpdateSymbolCrumb();
    }

    private void UpdateEditorStatusBar()
    {
        var caret = Editor.TextArea.Caret;
        var selLen = Editor.SelectionLength;
        CursorPosButton.Content = selLen > 0
            ? Loc.Get("ViewerStatusLnColSel", caret.Line, caret.Column, selLen)
            : Loc.Get("ViewerStatusLnCol", caret.Line, caret.Column);

        var text = GetEditorText();
        IndentText.Text = CodeFormatter.DetectIndentation(text);
        LineEndingButton.Content = CodeFormatter.DetectLineEnding(text);
        EncodingButton.Content = _encoding?.Name ?? "";
        EncodingButton.IsVisible = _encoding is not null;
        EditorZoomButton.Content = $"{Math.Round(Editor.FontSize / DefaultEditorFontSize * 100)}%";
        UpdateSymbolCrumb();
    }

    private void UpdateSymbolCrumb()
    {
        var current = CodeSymbolExtractor.FindEnclosingSymbol(_symbols, Editor.TextArea.Caret.Line);
        if (current is null)
        {
            SymbolCrumbButton.IsVisible = false;
            return;
        }
        SymbolCrumbButton.Content = $"{current.Kind}: {current.Name}";
        SymbolCrumbButton.IsVisible = true;
    }

    private void OnEditorCaretOrSelectionChanged()
    {
        var caret = Editor.TextArea.Caret;
        var selLen = Editor.SelectionLength;
        CursorPosButton.Content = selLen > 0
            ? Loc.Get("ViewerStatusLnColSel", caret.Line, caret.Column, selLen)
            : Loc.Get("ViewerStatusLnCol", caret.Line, caret.Column);

        UpdateSymbolCrumb();

        var text = GetEditorText();
        var brackets = text.Length <= MaxHighlightedChars
            ? BracketMatcher.FindMatchingBracket(text, Editor.CaretOffset)
            : null;
        _highlighter.SetBrackets(Editor.TextArea.TextView, brackets);
    }

    private void OnEditorPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetEditorFontSize(Editor.FontSize + (e.Delta.Y > 0 ? 1.5 : -1.5));
        e.Handled = true;
    }

    private void SetEditorFontSize(double size)
    {
        Editor.FontSize = Math.Clamp(size, MinEditorFontSize, MaxEditorFontSize);
        EditorZoomButton.Content = $"{Math.Round(Editor.FontSize / DefaultEditorFontSize * 100)}%";
    }

    private void OpenFindBar(bool replace, bool goToLine)
    {
        if (_markdown is not null && RenderedToggle.IsChecked == true)
            RenderedToggle.IsChecked = false;
        if (!EditorContainer.IsVisible) return;

        if (replace && EditToggle.IsVisible && !IsEditing)
            EditToggle.IsChecked = true;

        EditorFindBar.IsVisible = true;
        GoToLineRow.IsVisible = goToLine;
        FindRow.IsVisible = !goToLine;
        ReplaceRow.IsVisible = !goToLine && replace && EditToggle.IsVisible;
        ReplaceButton.IsEnabled = IsEditing;
        ReplaceAllButton.IsEnabled = IsEditing;

        if (goToLine)
        {
            GoToLineBox.Text = Editor.TextArea.Caret.Line.ToString(CultureInfo.InvariantCulture);
            Dispatcher.UIThread.Post(() =>
            {
                GoToLineBox.Focus();
                GoToLineBox.SelectAll();
            }, DispatcherPriority.Loaded);
            return;
        }

        if (Editor.SelectionLength is > 0 and <= 200)
        {
            var selected = Editor.SelectedText;
            if (!selected.Contains('\n') && !selected.Contains('\r'))
                FindBox.Text = selected;
        }

        UpdateFindMatches(jumpToNearest: true);
        Dispatcher.UIThread.Post(() =>
        {
            FindBox.Focus();
            FindBox.SelectAll();
        }, DispatcherPriority.Loaded);
    }

    private void CloseFindBar()
    {
        EditorFindBar.IsVisible = false;
        _searchMatches = [];
        _activeMatchIndex = -1;
        FindCountText.Text = "";
        FindBox.Classes.Set("invalid", false);
        _highlighter.SetMatches(Editor.TextArea.TextView, [], -1);
        Editor.TextArea.Focus();
    }

    private void UpdateFindMatches(bool jumpToNearest)
    {
        var query = FindBox.Text ?? "";
        var matchCase = MatchCaseToggle.IsChecked == true;
        var wholeWord = WholeWordToggle.IsChecked == true;
        var useRegex = RegexToggle.IsChecked == true;

        _searchMatches = CodeEditorSearch.FindMatches(
            GetEditorText(), query, matchCase, wholeWord, useRegex, out var validRegex);

        FindBox.Classes.Set("invalid", !validRegex);

        if (query.Length == 0 || !validRegex)
        {
            _activeMatchIndex = -1;
            FindCountText.Text = "";
            _highlighter.SetMatches(Editor.TextArea.TextView, [], -1);
            return;
        }

        if (_searchMatches.Count == 0)
        {
            _activeMatchIndex = -1;
            FindCountText.Text = Loc.Get("ViewerFindNoMatches");
            _highlighter.SetMatches(Editor.TextArea.TextView, [], -1);
            return;
        }

        if (jumpToNearest)
            _activeMatchIndex = CodeEditorSearch.FindMatchIndex(_searchMatches, Editor.SelectionStart, backward: false);
        else if (_activeMatchIndex >= _searchMatches.Count || _activeMatchIndex < 0)
            _activeMatchIndex = 0;

        FindCountText.Text = Loc.Get("ViewerFindCount", _activeMatchIndex + 1, _searchMatches.Count);
        _highlighter.SetMatches(Editor.TextArea.TextView, _searchMatches, _activeMatchIndex);
        if (jumpToNearest) SelectSearchMatch(_activeMatchIndex);
    }

    private void StepSearchMatch(bool backward)
    {
        if (_searchMatches.Count == 0) return;
        var caretOffset = backward ? Editor.SelectionStart : Editor.SelectionStart + Editor.SelectionLength;
        _activeMatchIndex = CodeEditorSearch.FindMatchIndex(_searchMatches, caretOffset, backward);
        if (_activeMatchIndex < 0) return;

        FindCountText.Text = Loc.Get("ViewerFindCount", _activeMatchIndex + 1, _searchMatches.Count);
        _highlighter.SetMatches(Editor.TextArea.TextView, _searchMatches, _activeMatchIndex);
        SelectSearchMatch(_activeMatchIndex);
    }

    private void SelectSearchMatch(int index)
    {
        if (index < 0 || index >= _searchMatches.Count) return;
        var span = _searchMatches[index];
        if (span.Offset + span.Length > Editor.Document.TextLength) return;

        Editor.Select(span.Offset, span.Length);
        var loc = Editor.Document.GetLocation(span.Offset);
        ScrollEditorTo(loc.Line, loc.Column, span.Length);
    }

    /// <summary>
    /// Scrolls vertically and horizontally to the target location.
    /// AvaloniaEdit's ScrollTo resets the horizontal offset to 0, so horizontal
    /// scrolling is adjusted explicitly when horizontal scrolling is enabled.
    /// </summary>
    private void ScrollEditorTo(int line, int column, int selectionLength = 0)
    {
        var textView = Editor.TextArea.TextView;
        var scrollable = textView as Avalonia.Controls.Primitives.ILogicalScrollable;
        var prevX = scrollable?.Offset.X ?? 0;

        Editor.ScrollTo(line, column);

        if (scrollable is not { CanHorizontallyScroll: true }) return;
        var viewportWidth = Editor.ViewportWidth;
        if (viewportWidth <= 0) return;

        var startPos = textView.GetVisualPosition(
            new AvaloniaEdit.TextViewPosition(line, Math.Max(1, column)),
            AvaloniaEdit.Rendering.VisualYPosition.LineMiddle);
        var endX = startPos.X;
        if (selectionLength > 0)
        {
            var docLine = Editor.Document.GetLineByNumber(Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount)));
            var endColumn = Math.Min(column + selectionLength, docLine.Length + 1);
            var endPos = textView.GetVisualPosition(
                new AvaloniaEdit.TextViewPosition(line, Math.Max(1, endColumn)),
                AvaloniaEdit.Rendering.VisualYPosition.LineMiddle);
            endX = Math.Max(startPos.X, endPos.X);
        }

        const double margin = 24;
        double ComputeTargetX()
        {
            if (startPos.X >= prevX + margin && endX <= prevX + viewportWidth - margin)
                return prevX;
            if (endX - startPos.X < viewportWidth - margin * 2)
                return Math.Max(0, (startPos.X + endX - viewportWidth) / 2);
            return Math.Max(0, startPos.X - viewportWidth * 0.25);
        }

        var rawTargetX = ComputeTargetX();
        var clampedX = Math.Clamp(rawTargetX, 0, Math.Max(0, Editor.ExtentWidth - viewportWidth));
        if (Math.Abs(scrollable.Offset.X - clampedX) > 0.5)
            scrollable.Offset = new Vector(clampedX, scrollable.Offset.Y);

        if (endX > Editor.ExtentWidth)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (scrollable is not { CanHorizontallyScroll: true }) return;
                var vw = Editor.ViewportWidth;
                if (vw <= 0) return;
                var x = Math.Clamp(rawTargetX, 0, Math.Max(0, Editor.ExtentWidth - vw));
                if (Math.Abs(scrollable.Offset.X - x) > 0.5)
                    scrollable.Offset = new Vector(x, scrollable.Offset.Y);
            }, DispatcherPriority.Loaded);
        }
    }

    private void ReplaceCurrentMatch()
    {
        if (!IsEditing || Editor.IsReadOnly || _searchMatches.Count == 0) return;

        if (_activeMatchIndex < 0 || _activeMatchIndex >= _searchMatches.Count)
        {
            StepSearchMatch(backward: false);
            return;
        }

        var span = _searchMatches[_activeMatchIndex];
        if (Editor.SelectionStart != span.Offset || Editor.SelectionLength != span.Length)
        {
            SelectSearchMatch(_activeMatchIndex);
            return;
        }

        var replacement = CodeEditorSearch.ReplaceSingle(
            Editor.SelectedText,
            FindBox.Text ?? "",
            ReplaceBox.Text ?? "",
            MatchCaseToggle.IsChecked == true,
            RegexToggle.IsChecked == true);

        Editor.Document.Replace(span.Offset, span.Length, replacement);
        Editor.CaretOffset = span.Offset + replacement.Length;
        UpdateFindMatches(jumpToNearest: true);
    }

    private void ReplaceAllMatches()
    {
        if (!IsEditing || Editor.IsReadOnly) return;
        var query = FindBox.Text ?? "";
        if (query.Length == 0) return;

        var replaced = CodeEditorSearch.ReplaceAll(
            GetEditorText(),
            query,
            ReplaceBox.Text ?? "",
            MatchCaseToggle.IsChecked == true,
            WholeWordToggle.IsChecked == true,
            RegexToggle.IsChecked == true,
            out var count);

        if (count == 0) return;

        Editor.Document.Replace(0, Editor.Document.TextLength, replaced);
        UpdateFindMatches(jumpToNearest: false);
        ShowStatus(Loc.Get("ViewerReplacedCount", count), ok: true);
    }

    private void ExecuteGoToLine()
    {
        if (!CodeEditorSearch.TryParseGoToLine(GoToLineBox.Text ?? "", Editor.Document.LineCount, out var line, out var col))
            return;
        GoToLine(line, col);
        CloseFindBar();
    }

    private void GoToLine(int line, int column)
    {
        line = Math.Clamp(line, 1, Math.Max(1, Editor.Document.LineCount));
        var docLine = Editor.Document.GetLineByNumber(line);
        column = Math.Clamp(column, 1, docLine.Length + 1);
        var offset = docLine.Offset + column - 1;
        Editor.CaretOffset = offset;
        Editor.Select(offset, 0);
        ScrollEditorTo(line, column);
        Editor.TextArea.Focus();
    }

    private void ShowOutlineMenu(Control anchor)
    {
        var items = new List<Control>();
        if (_symbols.Count == 0)
        {
            items.Add(new MenuItem { Header = Loc.Get("ViewerOutlineEmpty"), IsEnabled = false });
        }
        else
        {
            foreach (var sym in _symbols)
            {
                var s = sym;
                var item = new MenuItem { Header = $"{s.Kind}  {s.Name}  (:{s.Line})" };
                item.Click += (_, _) =>
                {
                    if (_markdown is not null && RenderedToggle.IsChecked == true)
                        RenderedToggle.IsChecked = false;
                    GoToLine(s.Line, 1);
                };
                items.Add(item);
            }
        }
        new ContextMenu { ItemsSource = items }.Open(anchor);
    }

    private void FormatDocument()
    {
        if (FilePath is null || !CodeFormatter.CanFormat(FilePath) || _encoding is null) return;
        if (!IsEditing && EditToggle.IsVisible)
            EditToggle.IsChecked = true;

        var current = GetEditorText();
        var result = CodeFormatter.TryFormat(current, FilePath);
        if (result.Success && result.FormattedText is not null)
        {
            if (result.FormattedText != current)
                Editor.Document.Replace(0, Editor.Document.TextLength, result.FormattedText);
            ShowStatus(Loc.Get("ViewerFormatted"), ok: true);
        }
        else
        {
            if (result.ErrorLine > 0) GoToLine(result.ErrorLine, 1);
            ShowStatus(Loc.Get("ViewerFormatInvalid", result.ErrorLine, result.ErrorMessage ?? ""), ok: false);
        }
    }

    private void ToggleLineComment()
    {
        if (!EditorContainer.IsVisible || !IsEditing || Editor.IsReadOnly) return;
        var (replaced, segStart, segLen, newSelStart, newSelLen) = CodeFormatter.ToggleLineComment(
            GetEditorText(), Editor.SelectionStart, Editor.SelectionLength, FilePath);
        Editor.Document.Replace(segStart, segLen, replaced);
        Editor.Select(newSelStart, newSelLen);
    }

    private void ToggleLineEnding()
    {
        if (!IsEditing || Editor.IsReadOnly) return;
        var text = GetEditorText();
        var toCrlf = CodeFormatter.DetectLineEnding(text) == "LF";
        var converted = CodeFormatter.ConvertLineEndings(text, toCrlf);
        if (converted != text)
            Editor.Document.Replace(0, Editor.Document.TextLength, converted);
        UpdateEditorStatusBar();
    }

    private void ShowEncodingMenu()
    {
        if (!IsEditing || _encoding is null) return;
        FileEncoding[] options = [FileEncoding.Utf8, FileEncoding.Utf8Bom, FileEncoding.Utf16Le, FileEncoding.Utf16Be];
        var items = new List<Control>(options.Length);
        foreach (var enc in options)
        {
            var target = enc;
            var isCurrent = _encoding.Name == target.Name;
            var item = new MenuItem
            {
                Header = target.Name,
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = isCurrent,
            };
            item.Click += (_, _) =>
            {
                if (_encoding?.Name == target.Name) return;
                _encoding = target;
                _encodingChanged = true;
                UpdateEditorStatusBar();
                UpdateTitle();
                if (FilePath is not null)
                    SetInfo(Loc.Get("ViewerLines", Editor.Document.LineCount), Format.Size(new FileInfo(FilePath).Length), target.Name);
            };
            items.Add(item);
        }
        new ContextMenu { ItemsSource = items }.Open(EncodingButton);
    }

    // =====================================================================
    // Keyboard and closing
    // =====================================================================

    /// <summary>Shows the file before / after this one in its folder (natural name order).</summary>
    private async Task ShowNeighborAsync(int step)
    {
        if (FilePath is not { } current) return;
        var next = await Task.Run(() => SiblingFiles.Neighbor(current, step));
        if (FilePath != current) return; // the viewer moved on while the folder was read
        if (next is null)
        {
            ShowStatus(Loc.Get(step < 0 ? "ViewerFirstFile" : "ViewerLastFile"), ok: false);
            return;
        }
        await LoadAsync(next, null);
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (EditorFindBar.IsVisible && e.Key == Key.Escape)
        {
            CloseFindBar();
            e.Handled = true;
            return;
        }

        if (!EditorContainer.IsVisible && !MarkdownScroll.IsVisible) return;

        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrl && !alt && !shift && e.Key == Key.F)
        {
            OpenFindBar(replace: false, goToLine: false);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && !shift && e.Key == Key.H && EditToggle.IsVisible)
        {
            OpenFindBar(replace: true, goToLine: false);
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && !shift && e.Key == Key.G)
        {
            OpenFindBar(replace: false, goToLine: true);
            e.Handled = true;
            return;
        }
        if (!ctrl && !alt && e.Key == Key.F3 && EditorContainer.IsVisible)
        {
            if (_searchMatches.Count > 0) StepSearchMatch(backward: shift);
            else OpenFindBar(replace: false, goToLine: false);
            e.Handled = true;
            return;
        }
        if (ctrl && shift && !alt && e.Key == Key.O)
        {
            ShowOutlineMenu(OutlineButton.IsVisible ? OutlineButton : SymbolCrumbButton);
            e.Handled = true;
            return;
        }
        if (shift && alt && !ctrl && e.Key == Key.F && EditorContainer.IsVisible && _encoding is not null && CodeFormatter.CanFormat(FilePath))
        {
            FormatDocument();
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && e.Key is Key.OemQuestion or Key.Divide && EditorContainer.IsVisible && IsEditing)
        {
            ToggleLineComment();
            e.Handled = true;
            return;
        }
        if (ctrl && !alt && EditorContainer.IsVisible)
        {
            if (e.Key is Key.OemPlus or Key.Add)
            {
                SetEditorFontSize(Editor.FontSize + 1.5);
                e.Handled = true;
                return;
            }
            if (e.Key is Key.OemMinus or Key.Subtract)
            {
                SetEditorFontSize(Editor.FontSize - 1.5);
                e.Handled = true;
                return;
            }
            if (!shift && e.Key is Key.D0 or Key.NumPad0)
            {
                SetEditorFontSize(DefaultEditorFontSize);
                e.Handled = true;
                return;
            }
        }
    }

    private async void OnKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        // Alt+←/→ always steps through the folder, even where plain arrows turn pages or seek.
        if (e.KeyModifiers == KeyModifiers.Alt && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            await ShowNeighborAsync(e.Key == Key.Left ? -1 : +1);
            return;
        }
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
                     _body is DocxView { IsEditing: true, IsKeyboardFocusWithin: true } ||
                     _body is XlsxView { IsEditing: true, IsKeyboardFocusWithin: true } ||
                     _body is PptxView { IsEditing: true, IsKeyboardFocusWithin: true };
        if (!typing && !ctrl && _body?.HandleKey(e) == true)
        {
            e.Handled = true;
            return;
        }

        // Where the body has no use for the arrows (images, text, unknown files), they step through the folder.
        if (!typing && e.KeyModifiers == KeyModifiers.None && e.Key is Key.Left or Key.Right)
        {
            e.Handled = true;
            await ShowNeighborAsync(e.Key == Key.Left ? -1 : +1);
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
        _structureTimer.Stop();
        if (_foldingManager is not null)
        {
            FoldingManager.Uninstall(_foldingManager);
            _foldingManager = null;
        }
        DisposeBody();
        SetMarkdown(null);
        SetMessageImage(null);
        _textMate.Dispose();
        base.OnClosed(e);
    }
}
