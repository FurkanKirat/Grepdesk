using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using System.Collections.ObjectModel;
using Grepdesk.Core.Editor;
using Grepdesk.UI.Helpers;

namespace Grepdesk.UI;

public partial class MainWindow : Window
{
    // Short alias — LocalizationService.Instance is verbose to repeat at every call site.
    private static LocalizationService Loc => LocalizationService.Instance;
    private static AppSettings Settings => AppSettings.Current;

    // Results are shown a page at a time; the full match list is still sorted
    // as a whole, so the first page really is the top of the chosen order.
    private const int PageSize = 500;

    // Below this width the preview panel would squeeze the result list too much.
    private const double MinWidthForPreview = 760;

    private readonly IPlatformShell _shell = PlatformShellFactory.CreatePlatformShell();
    private readonly EditorDetector _editorDetector;
    private readonly IShellIntegration? _shellIntegration = PlatformShellFactory.CreateShellIntegration();

    // ---- file-name search ----
    private readonly FileIndex _index = new();
    private readonly ObservableCollection<ResultItem> _results = [];
    private CancellationTokenSource _indexCts = new();
    private CancellationTokenSource _searchCts = new();
    private string _lastQuery = "";
    private List<string>? _selectedRoots; // null = whole PC
    private bool _scopeChosen;
    private List<SearchResult> _allMatches = []; // every match for the last query, unsorted

    // ---- content search ----
    private readonly ObservableCollection<ResultItem> _contentResults = [];
    private CancellationTokenSource _contentSearchCts = new();
    private string? _contentSearchFolder;

    // ---- shared ----
    private readonly Dictionary<TextBlock, Func<string>> _statusTexts = [];
    private bool _updatingControls;

    public MainWindow() : this(null) { }

    public MainWindow(string? startFolder)
    {
        InitializeComponent();
        _editorDetector = new EditorDetector(_shell);

        InitNavigation();

        // --- File name search wiring ---
        ResultsList.ItemsSource = _results;

        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ResultsList.PointerReleased += OnAnyResultRightClick;
        ShowMoreButton.Click += async (_, _) => await ShowMoreAsync();

        ChooseFolderButton.Click += async (_, _) => await ChooseFolderAsync();
        ScanAllButton.Click += async (_, _) => await ScanWholeComputerAsync();
        ReindexButton.Click += async (_, _) => await StartIndexingAsync();

        _index.ProgressChanged += count =>
            Dispatcher.UIThread.Post(() =>
                SetStatus(StatusText, () => Loc.Get("IndexingProgress", count)));

        _index.IndexingComplete += () =>
            Dispatcher.UIThread.Post(() =>
            {
                SetStatus(StatusText, () => Loc.Get("ReadyIndexed", _index.Count));
                ReindexButton.IsEnabled = true;
            });

        // No automatic scan on launch — user picks a folder or "scan whole PC" first.
        SetStatus(StatusText, () => Loc.Get("StatusChooseOption"));

        // --- Content search wiring ---
        ContentResultsList.ItemsSource = _contentResults;
        ContentResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ContentResultsList.PointerReleased += OnAnyResultRightClick;
        ContentChooseFolderButton.Click += async (_, _) => await ChooseContentFolderAsync();
        ContentSearchBox.KeyDown += OnContentSearchKeyDown;
        SetStatus(ContentStatusText, () => Loc.Get("SelectFolderFirst"));

        InitPreview();
        InitSorting();
        InitLanguageSetting();
        InitExplorerMenuSettings();
        ApplyLanguage();

        if (startFolder is not null)
            Opened += async (_, _) => await OpenWithFolderAsync(startFolder);
    }

    // =====================================================================
    // Navigation, language, preview, sorting
    // =====================================================================

    /// <summary>
    /// Sidebar: features at the top, settings pinned to the bottom. They are
    /// two separate lists, so selecting in one clears the other.
    /// </summary>
    private void InitNavigation()
    {
        Control[] featurePages = [NameSearchPage, ContentSearchPage];

        FeatureNav.SelectionChanged += (_, _) =>
        {
            if (FeatureNav.SelectedIndex < 0) return;
            SettingsNav.SelectedIndex = -1;
            ShowPage(featurePages[FeatureNav.SelectedIndex]);
        };

        SettingsNav.SelectionChanged += (_, _) =>
        {
            if (SettingsNav.SelectedIndex < 0) return;
            FeatureNav.SelectedIndex = -1;
            ShowPage(SettingsPage);
        };

        FeatureNav.SelectedIndex = 0;
    }

    private void ShowPage(Control page)
    {
        NameSearchPage.IsVisible = page == NameSearchPage;
        ContentSearchPage.IsVisible = page == ContentSearchPage;
        SettingsPage.IsVisible = page == SettingsPage;

        if (page == NameSearchPage) SearchBox.Focus();
        else if (page == ContentSearchPage) ContentSearchBox.Focus();
    }

    /// <summary>
    /// Status lines are stored as functions so a language switch can re-render
    /// them ("1.234 results" stays a result count, just in the new language).
    /// </summary>
    private void SetStatus(TextBlock target, Func<string> text)
    {
        _statusTexts[target] = text;
        target.Text = text();
    }

    private TextBlock CurrentStatusText => ContentSearchPage.IsVisible ? ContentStatusText : StatusText;

    private void InitLanguageSetting()
    {
        LanguageBox.SelectionChanged += (_, _) =>
        {
            if (_updatingControls || LanguageBox.SelectedIndex < 0) return;

            var languages = LocalizationService.AvailableLanguages();
            Settings.Language = LanguageBox.SelectedIndex == 0 ? null : languages[LanguageBox.SelectedIndex - 1];
            Settings.Save();

            Loc.Load(Settings.Language ?? LocalizationService.DetectSystemLanguage());
            ApplyLanguage();
            RefreshExplorerMenuLabels();
        };
    }

    /// <summary>
    /// Sets every piece of UI text from the current language. Runs once at
    /// startup and again whenever the language is changed in Settings, so it
    /// must not reset any state (chosen folders, results, status).
    /// </summary>
    private void ApplyLanguage()
    {
        _updatingControls = true;
        try
        {
            NavNameSearchText.Text = NameSearchTitle.Text = Loc.Get("FileNameSearchTab");
            NavContentSearchText.Text = ContentSearchTitle.Text = Loc.Get("ContentSearchTab");
            NavSettingsText.Text = SettingsTitle.Text = Loc.Get("SettingsTab");
            NameSearchSubtitle.Text = Loc.Get("NameSearchSubtitle");
            ContentSearchSubtitle.Text = Loc.Get("ContentSearchSubtitle");
            SettingsSubtitle.Text = Loc.Get("SettingsSubtitle");

            // File name search
            SearchBox.Watermark = Loc.Get("SearchWatermark");
            ChooseFolderButton.Content = Loc.Get("ChooseFolder");
            ScanAllButton.Content = Loc.Get("ScanAllPc");
            ReindexButton.Content = Loc.Get("Rescan");
            ScopeLabel.Text = Loc.Get("ScopeLabel");
            UpdateScopeText();
            UpdateShowMoreButton();

            // Content search
            ContentScopeLabel.Text = Loc.Get("ScopeLabel");
            ContentFolderText.Text = _contentSearchFolder ?? Loc.Get("NotSelected");
            ContentSearchBox.Watermark = Loc.Get("ContentSearchWatermark");
            SupportedFormatsText.Text = Loc.Get("SupportedFormats");
            ContentChooseFolderButton.Content = Loc.Get("ChooseFolder");

            // Sorting
            NameSortLabel.Text = ContentSortLabel.Text = Loc.Get("SortLabel");
            var sortLabels = Enum.GetValues<SortMode>().Select(m => Loc.Get("Sort" + m)).ToList();
            foreach (var box in new[] { NameSortBox, ContentSortBox })
            {
                box.ItemsSource = sortLabels;
                box.SelectedIndex = (int)Settings.Sort;
            }

            // Settings: language
            LanguageHeader.Text = Loc.Get("LanguageHeader");
            LanguageDescription.Text = Loc.Get("LanguageDescription");
            var languages = LocalizationService.AvailableLanguages();
            var systemName = LocalizationService.DisplayName(LocalizationService.DetectSystemLanguage());
            LanguageBox.ItemsSource = languages
                .Select(LocalizationService.DisplayName)
                .Prepend(Loc.Get("LanguageSystemDefault", systemName))
                .ToList();
            LanguageBox.SelectedIndex = Settings.Language is { } code && languages.Contains(code)
                ? languages.ToList().IndexOf(code) + 1
                : 0;

            // Settings: display
            DisplayHeader.Text = Loc.Get("DisplayHeader");
            ShowPreviewCheckBox.Content = Loc.Get("ShowPreview");
            ShowPreviewHint.Text = Loc.Get("ShowPreviewHint");

            // Settings: Explorer integration
            var isWindows = OperatingSystem.IsWindows();
            IntegrationHeader.Text = Loc.Get(isWindows ? "ExplorerMenuHeader" : "FileManagerMenuHeader");
            IntegrationDescription.Text = Loc.Get(isWindows ? "ExplorerMenuDescription" : "FileManagerMenuDescription");
            IntegrationUnsupportedText.Text = Loc.Get("IntegrationUnsupported");
            SetFeatureText(MenuOpenWithCheckBox, MenuOpenWithHint, "FeatureOpenWith");
            SetFeatureText(MenuExtractCheckBox, MenuExtractHint, "FeatureExtract");
            SetFeatureText(MenuCompressCheckBox, MenuCompressHint, "FeatureCompress");
            SetFeatureText(MenuPasteCheckBox, MenuPasteHint, "FeaturePaste");
            if (SettingsStatusText.IsVisible)
                SettingsStatusText.Text = Loc.Get("ContextMenuUpdateFailed");

            foreach (var (target, text) in _statusTexts)
                target.Text = text();

            NamePreview.ApplyLanguage();
            ContentPreview.ApplyLanguage();
        }
        finally
        {
            _updatingControls = false;
        }

        static void SetFeatureText(CheckBox box, TextBlock hint, string key)
        {
            box.Content = Loc.Get(key);
            hint.Text = Loc.Get(key + "Hint");
        }
    }

    private void InitPreview()
    {
        ResultsList.SelectionChanged += (_, _) => NamePreview.Show(ResultsList.SelectedItem as ResultItem);
        ContentResultsList.SelectionChanged += (_, _) => ContentPreview.Show(ContentResultsList.SelectedItem as ResultItem);

        foreach (var pane in new[] { NamePreview, ContentPreview })
        {
            pane.OpenRequested += path => ReportShellResult(_shell.OpenPath(path), Loc.Get("FileOpenFailed"));
            pane.ShowInFolderRequested += path => ReportShellResult(_shell.ShowInFileManager(path), Loc.Get("ShowInFolderFailed"));
        }

        ShowPreviewCheckBox.IsChecked = Settings.ShowPreview;
        ShowPreviewCheckBox.IsCheckedChanged += (_, _) =>
        {
            Settings.ShowPreview = ShowPreviewCheckBox.IsChecked == true;
            Settings.Save();
            UpdatePreviewVisibility();
        };

        PageHost.SizeChanged += (_, _) => UpdatePreviewVisibility();
        UpdatePreviewVisibility();
    }

    private void UpdatePreviewVisibility()
    {
        var visible = Settings.ShowPreview && PageHost.Bounds.Width >= MinWidthForPreview;
        NamePreview.IsVisible = ContentPreview.IsVisible = visible;
    }

    /// <summary>
    /// One sort order for both result lists; each page has its own combo box,
    /// kept in sync so switching pages never shows a different order.
    /// </summary>
    private void InitSorting()
    {
        foreach (var box in new[] { NameSortBox, ContentSortBox })
        {
            box.SelectionChanged += async (_, _) =>
            {
                if (_updatingControls || box.SelectedIndex < 0 || (SortMode)box.SelectedIndex == Settings.Sort) return;

                Settings.Sort = (SortMode)box.SelectedIndex;
                Settings.Save();

                _updatingControls = true;
                NameSortBox.SelectedIndex = ContentSortBox.SelectedIndex = box.SelectedIndex;
                _updatingControls = false;

                ResortContentResults();
                await ResortNameResultsAsync();
            };
        }
    }

    private void ResortContentResults()
    {
        var sorted = _contentResults.ToList();
        sorted.Sort(ResultOrdering.ForItems(Settings.Sort));
        _contentResults.Clear();
        foreach (var item in sorted)
            _contentResults.Add(item);
    }

    /// <summary>Keeps a streaming list (content search) ordered as items arrive.</summary>
    private static void InsertSorted(ObservableCollection<ResultItem> list, ResultItem item, Comparison<ResultItem> compare)
    {
        int lo = 0, hi = list.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) / 2;
            if (compare(list[mid], item) <= 0) lo = mid + 1;
            else hi = mid;
        }
        list.Insert(lo, item);
    }

    /// <summary>
    /// Opt-in Explorer context-menu entries, one checkbox per feature.
    /// The registry itself is the source of truth for the checkbox state.
    /// </summary>
    private void InitExplorerMenuSettings()
    {
        if (_shellIntegration is null || Environment.ProcessPath is not { } exePath)
        {
            IntegrationOptions.IsVisible = false;
            IntegrationUnsupportedText.IsVisible = true;
            return;
        }

        Bind(MenuOpenWithCheckBox, ShellFeature.OpenWith);
        Bind(MenuExtractCheckBox, ShellFeature.Extract);
        Bind(MenuCompressCheckBox, ShellFeature.Compress);
        Bind(MenuPasteCheckBox, ShellFeature.Paste);

        // Already opted in: rewrite the entries so they follow the exe if
        // it was moved, and pick up the current language for the labels.
        RefreshExplorerMenuLabels();

        void Bind(CheckBox box, ShellFeature feature)
        {
            var shell = _shellIntegration;
            box.IsChecked = shell.IsEnabled(feature);

            box.IsCheckedChanged += (_, _) =>
            {
                var result = box.IsChecked == true
                    ? shell.Enable(feature, exePath, Loc.Get)
                    : shell.Disable(feature);

                SettingsStatusText.IsVisible = !result.IsSuccess;
                if (!result.IsSuccess)
                {
                    SettingsStatusText.Text = Loc.Get("ContextMenuUpdateFailed");
                    box.IsChecked = shell.IsEnabled(feature);
                }
            };
        }
    }

    private void RefreshExplorerMenuLabels()
    {
        if (_shellIntegration is null || Environment.ProcessPath is not { } exePath) return;

        foreach (var feature in Enum.GetValues<ShellFeature>())
            if (_shellIntegration.IsEnabled(feature))
                _shellIntegration.Enable(feature, exePath, Loc.Get);
    }

    /// <summary>
    /// Launched with a folder (e.g. from Explorer's context menu): use it
    /// as the root for both pages and start indexing right away.
    /// </summary>
    private async Task OpenWithFolderAsync(string folder)
    {
        _contentSearchFolder = folder;
        ContentFolderText.Text = folder;
        SetStatus(ContentStatusText, () => Loc.Get("ContentReadyPrompt"));

        _selectedRoots = [folder];
        await StartIndexingAsync();
    }

    private void UpdateScopeText()
    {
        ScopeText.Text = !_scopeChosen
            ? Loc.Get("NotSelected")
            : _selectedRoots is null
                ? Loc.Get("ScopeEntirePc")
                : string.Join(";  ", _selectedRoots);
    }

    // =====================================================================
    // File name search
    // =====================================================================

    private async Task ChooseFolderAsync()
    {
        var provider = StorageProvider;
        if (provider is null) return;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Get("ChooseFolderDialogTitle"),
            AllowMultiple = true
        });

        if (folders.Count == 0) return;

        var paths = folders
            .Select(f => f.TryGetLocalPath())
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (paths.Count == 0) return;

        _selectedRoots = paths;
        await StartIndexingAsync();
    }

    private async Task ScanWholeComputerAsync()
    {
        _selectedRoots = null;
        await StartIndexingAsync();
    }

    private async Task StartIndexingAsync()
    {
        _indexCts.Cancel();
        _indexCts = new CancellationTokenSource();
        SetStatus(StatusText, () => Loc.Get("Indexing"));
        ReindexButton.IsEnabled = false;
        _scopeChosen = true;
        UpdateScopeText();
        ClearNameResults();

        await _index.BuildIndexAsync(_selectedRoots, _indexCts.Token);
        await Search(SearchBox.Text ?? "");
    }

    private async void OnSearchTextChanged(object? sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text ?? "";
        if (query == _lastQuery) return;
        await Search(query);
    }

    private async Task Search(string query)
    {
        _lastQuery = query;

        await _searchCts.CancelAsync();
        _searchCts = new CancellationTokenSource();
        var token = _searchCts.Token;

        try
        {
            await Task.Delay(150, token);
            if (token.IsCancellationRequested) return;

            await RunSearchAsync(query, token);
        }
        catch (OperationCanceledException) { }
    }

    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Down && _results.Count > 0)
        {
            ResultsList.Focus();
            ResultsList.SelectedIndex = 0;
        }
        else if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
        }
    }

    private void ClearNameResults()
    {
        _allMatches = [];
        _results.Clear();
        UpdateShowMoreButton();
    }

    private async Task RunSearchAsync(string query, CancellationToken token)
    {
        ClearNameResults();

        if (_index.IsIndexing)
        {
            SetStatus(StatusText, () => Loc.Get("StillIndexing"));
            return;
        }

        if (_index.Count == 0)
        {
            SetStatus(StatusText, () => Loc.Get(_scopeChosen ? "SelectFolderOrScanAll" : "StatusChooseOption"));
            return;
        }

        // An empty query lists everything in the scanned folders, so the sort
        // alone answers questions like "what are the biggest files here?".

        var sortMode = Settings.Sort;
        var (all, firstPage) = await Task.Run(() =>
        {
            var matches = _index.Search(query, token);
            return (matches, ResultOrdering.TakeSorted(matches, PageSize, ResultOrdering.For(sortMode), token));
        }, token);
        if (token.IsCancellationRequested) return;

        _allMatches = all;
        foreach (var r in firstPage)
            _results.Add(new ResultItem(r));

        UpdateNameStatus(query);
    }

    private void UpdateNameStatus(string query)
    {
        var total = _allMatches.Count;
        var shown = _results.Count;
        var browsing = string.IsNullOrWhiteSpace(query);
        SetStatus(StatusText, () => (browsing, total > shown) switch
        {
            (true, true) => Loc.Get("AllItemsShowingTop", total, shown),
            (true, false) => Loc.Get("AllItemsCount", total),
            (false, true) => Loc.Get("ResultsShowingTop", total, query, shown),
            (false, false) => Loc.Get("ResultsCount", total, query),
        });
        UpdateShowMoreButton();
    }

    private void UpdateShowMoreButton()
    {
        var remaining = _allMatches.Count - _results.Count;
        ShowMoreButton.IsVisible = remaining > 0;
        ShowMoreButton.Content = Loc.Get("ShowMore", Math.Min(PageSize, remaining));
    }

    /// <summary>Appends the next page; the order is total, so the shown prefix never changes.</summary>
    private async Task ShowMoreAsync()
    {
        var token = _searchCts.Token;
        var all = _allMatches;
        var sortMode = Settings.Sort;
        var target = _results.Count + PageSize;

        var page = await Task.Run(() => ResultOrdering.TakeSorted(all, target, ResultOrdering.For(sortMode), token), token);
        if (token.IsCancellationRequested || all != _allMatches || sortMode != Settings.Sort) return;

        foreach (var r in page.Skip(_results.Count))
            _results.Add(new ResultItem(r));

        UpdateNameStatus(_lastQuery);
    }

    private async Task ResortNameResultsAsync()
    {
        if (_allMatches.Count == 0) return;

        var token = _searchCts.Token;
        var all = _allMatches;
        var sortMode = Settings.Sort;
        var count = Math.Max(_results.Count, PageSize);

        try
        {
            var page = await Task.Run(() => ResultOrdering.TakeSorted(all, count, ResultOrdering.For(sortMode), token), token);
            if (token.IsCancellationRequested || all != _allMatches || sortMode != Settings.Sort) return;

            _results.Clear();
            foreach (var r in page)
                _results.Add(new ResultItem(r));
            UpdateNameStatus(_lastQuery);
        }
        catch (OperationCanceledException) { }
    }

    private void OnAnyResultDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (sender is not ListBox { SelectedItem: ResultItem item }) return;
        var result = _shell.OpenPath(item.Result.FullPath);
        ReportShellResult(result, Loc.Get("FileOpenFailed"));
    }

    private void OnAnyResultRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (sender is not ListBox list) return;

        // Find and select the right-clicked item
        if (e.Source is Control { DataContext: ResultItem item })
        {
            list.SelectedItem = item;

            var menuItems = new List<Control>();

            var openItem = new MenuItem { Header = Loc.Get("ContextMenuOpen") };
            openItem.Click += (_, _) =>
            {
                var result = _shell.OpenPath(item.Result.FullPath);
                ReportShellResult(result, Loc.Get("FileOpenFailed"));
            };

            var showItem = new MenuItem { Header = Loc.Get("ContextMenuShowInFolder") };
            showItem.Click += (_, _) =>
            {
                var result = _shell.ShowInFileManager(item.Result.FullPath);
                ReportShellResult(result, Loc.Get("ShowInFolderFailed"));
            };

            var copyItem = new MenuItem { Header = Loc.Get("ContextMenuCopyPath") };
            copyItem.Click += async (_, _) =>
            {
                var clipboard = TopLevel.GetTopLevel(list)?.Clipboard;
                if (clipboard is not null)
                {
                    await clipboard.SetTextAsync(item.Result.FullPath);
                    SetStatus(CurrentStatusText, () => Loc.Get("PathCopied"));
                }
            };

            var terminalItem = new MenuItem { Header = Loc.Get("ContextMenuOpenInTerminal") };
            terminalItem.Click += (_, _) =>
            {
                var res = item.Result;
                var result = _shell.OpenInTerminal(res.IsDirectory ? res.FullPath : res.Directory);
                ReportShellResult(result, Loc.Get("TerminalOpenFailed"));
            };

            menuItems.Add(openItem);
            menuItems.Add(showItem);
            menuItems.Add(copyItem);

            menuItems.Add(new Separator());
            menuItems.Add(terminalItem);

            if (_editorDetector.AvailableEditors.Count > 0)
            {
                menuItems.Add(new Separator());

                foreach (var editor in _editorDetector.AvailableEditors)
                {
                    if (!EditorTargetResolver.IsCompatible(editor, item.Result.FullPath))
                        continue;

                    var editorItem = new MenuItem { Header = editor.Header };
                    editorItem.Click += (_, _) =>
                    {
                        var result = _shell.OpenInEditor(editor, item.Result.FullPath);
                        ReportShellResult(result, Loc.Get("EditorOpenFailed", editor.Header));
                    };
                    menuItems.Add(editorItem);
                }
            }

            var menu = new ContextMenu
            {
                ItemsSource = menuItems
            };

            menu.Open(list);
        }
    }

    private void ReportShellResult(ShellActionResult result, string failureMessagePrefix)
    {
        if (result.IsSuccess) return; // silently keep current status on success

        var message = result.Exception is not null
            ? $"{failureMessagePrefix}: {result.Exception.Message}"
            : $"{failureMessagePrefix} ({result.Status})";
        SetStatus(CurrentStatusText, () => message);
    }

    // =====================================================================
    // Content search (searches inside file contents, not just names)
    // =====================================================================

    private async Task ChooseContentFolderAsync()
    {
        var provider = StorageProvider;
        if (provider is null) return;

        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Get("ChooseContentFolderDialogTitle"),
            AllowMultiple = false
        });

        if (folders.Count == 0) return;

        var path = folders[0].TryGetLocalPath();
        if (path is null) return;

        _contentSearchFolder = path;
        ContentFolderText.Text = path;
        SetStatus(ContentStatusText, () => Loc.Get("ContentReadyPrompt"));
    }

    private async void OnContentSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        await RunContentSearchAsync(ContentSearchBox.Text ?? "");
    }

    private async Task RunContentSearchAsync(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return;

        if (_contentSearchFolder is null)
        {
            SetStatus(ContentStatusText, () => Loc.Get("SelectFolderFirstContent"));
            return;
        }

        _contentSearchCts.Cancel();
        _contentSearchCts = new CancellationTokenSource();
        var token = _contentSearchCts.Token;

        _contentResults.Clear();
        SetStatus(ContentStatusText, () => Loc.Get("ScanningFiles"));

        // Walk the chosen folder directly — content search doesn't depend on
        // the name index, so it works even if that index was never built.
        var candidates = EnumerateSearchableFiles(_contentSearchFolder);

        var matchCount = 0;
        var skippedCount = 0; // too large, or extraction failed (corrupt/locked/unsupported-compression)

        void OnSkipped(SkippedFile _)
        {
            Interlocked.Increment(ref skippedCount);
            Dispatcher.UIThread.Post(() =>
                SetStatus(ContentStatusText, () => Loc.Get("ScanningProgress", matchCount, skippedCount)));
        }

        try
        {
            await foreach (var match in ContentSearcher.SearchAsync(candidates, query, OnSkipped, token))
            {
                var item = new ResultItem(SearchResult.FromDisk(match.FullPath), match.Snippet);
                InsertSorted(_contentResults, item, ResultOrdering.ForItems(Settings.Sort));
                matchCount++;
                SetStatus(ContentStatusText, () => skippedCount == 0
                    ? Loc.Get("ScanningMatches", matchCount)
                    : Loc.Get("ScanningProgress", matchCount, skippedCount));
            }

            if (!token.IsCancellationRequested)
            {
                SetStatus(ContentStatusText, () =>
                {
                    var summary = matchCount == 0 ? Loc.Get("NoMatchesFound") : Loc.Get("MatchesFound", matchCount);
                    return skippedCount == 0
                        ? summary
                        : Loc.Get("MatchesFoundWithSkipped", summary, skippedCount);
                });
            }
        }
        catch (OperationCanceledException) { }
    }

    private static IEnumerable<string> EnumerateSearchableFiles(string root)
    {
        var extensions = new HashSet<string>(ExtractorRegistry.AllSupportedExtensions, System.StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var dir = pending.Pop();

            IEnumerable<string> files = System.Array.Empty<string>();
            try { files = Directory.EnumerateFiles(dir).ToList(); }
            catch { /* locked / unauthorized — skip this directory's files */ }

            foreach (var f in files)
                if (extensions.Contains(Path.GetExtension(f)))
                    yield return f;

            IEnumerable<string> subDirs = System.Array.Empty<string>();
            try { subDirs = Directory.EnumerateDirectories(dir).ToList(); }
            catch { /* locked / unauthorized — skip subdirectories */ }

            foreach (var sub in subDirs)
                pending.Push(sub);
        }
    }
}
