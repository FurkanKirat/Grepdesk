using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
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
    private List<SearchResult> _allMatches = []; // every match for the last query (after filters), unsorted
    private ResultFilter _filter = new();

    // ---- content search ----
    private readonly ObservableCollection<ResultItem> _contentResults = [];
    private CancellationTokenSource _contentSearchCts = new();
    private string? _contentSearchFolder;
    private string? _contentQuery;

    // ---- shared ----
    private readonly Dictionary<TextBlock, Func<string>> _statusTexts = [];
    private bool _updatingControls;

    // ---- drag and drop ----
    private Point? _dragStart;
    private bool _draggingOut; // our own drag: don't treat it as a drop onto the page

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
        InitFilters();
        InitKeyboard();
        InitDragAndDrop();
        InitDiskPage();
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
        Control[] featurePages = [NameSearchPage, ContentSearchPage, DiskPage];

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
        DiskPage.IsVisible = page == DiskPage;
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

    private TextBlock CurrentStatusText =>
        ContentSearchPage.IsVisible ? ContentStatusText : DiskPage.IsVisible ? DiskPage.StatusLine : StatusText;

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
            NavDiskText.Text = Loc.Get("DiskTab");
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

            // Filters (indices kept; labels follow the language)
            TypeFilterList.ItemsSource = Enum.GetValues<FileCategory>().Select(c => Loc.Get("Category" + c)).ToList();
            TypeFilterList.SelectedIndex = (int)_filter.Category;
            SizeFilterLabel.Text = Loc.Get("FilterSize");
            DateFilterLabel.Text = Loc.Get("FilterDate");
            var sizeIndex = Math.Max(0, SizeFilterBox.SelectedIndex);
            SizeFilterBox.ItemsSource = ResultFilter.SizeSteps
                .Select(b => b == 0 ? Loc.Get("FilterAny") : "≥ " + Jobs.Format.Size(b)).ToList();
            SizeFilterBox.SelectedIndex = sizeIndex;
            var dateIndex = Math.Max(0, DateFilterBox.SelectedIndex);
            DateFilterBox.ItemsSource = new[] { "FilterAny", "FilterToday", "FilterWeek", "FilterMonth", "FilterYear" }
                .Select(k => Loc.Get(k)).ToList();
            DateFilterBox.SelectedIndex = dateIndex;

            // Settings: shortcuts
            ShortcutsHeader.Text = Loc.Get("ShortcutsHeader");
            FillShortcutsTable();

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
            DiskPage.ApplyLanguage();
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
        ContentResultsList.SelectionChanged += (_, _) => ContentPreview.Show(ContentResultsList.SelectedItem as ResultItem, _contentQuery);

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

    // ↓ into the results is handled with the other shortcuts (OnPreviewKeyDown).
    private void OnSearchKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
            SearchBox.Text = "";
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
        var filter = _filter;
        var (all, firstPage) = await Task.Run(() =>
        {
            var matches = _index.Search(query, token);
            if (!filter.IsEmpty)
                matches = matches.Where(filter.ToPredicate()).ToList();
            return (matches, ResultOrdering.TakeSorted(matches, PageSize, ResultOrdering.For(sortMode), token));
        }, token);
        if (token.IsCancellationRequested) return;

        _allMatches = all;
        foreach (var r in firstPage)
            _results.Add(NameItem(r));

        UpdateNameStatus(query);
    }

    private ResultItem NameItem(SearchResult r) => new(r, nameHighlight: _lastQuery.Trim());

    private void UpdateNameStatus(string query)
    {
        var total = _allMatches.Count;
        var shown = _results.Count;
        var browsing = string.IsNullOrWhiteSpace(query);
        var filtered = !_filter.IsEmpty;
        SetStatus(StatusText, () => (browsing, total > shown) switch
        {
            (true, true) => Loc.Get("AllItemsShowingTop", total, shown),
            (true, false) => Loc.Get("AllItemsCount", total),
            (false, true) => Loc.Get("ResultsShowingTop", total, query, shown),
            (false, false) => Loc.Get("ResultsCount", total, query),
        } + (filtered ? Loc.Get("FilteredSuffix") : ""));
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
            _results.Add(NameItem(r));

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
                _results.Add(NameItem(r));
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

            var openItem = new MenuItem { Header = Loc.Get("ContextMenuOpen"), InputGesture = new KeyGesture(Key.Enter) };
            openItem.Click += (_, _) =>
            {
                var result = _shell.OpenPath(item.Result.FullPath);
                ReportShellResult(result, Loc.Get("FileOpenFailed"));
            };

            var showItem = new MenuItem { Header = Loc.Get("ContextMenuShowInFolder"), InputGesture = new KeyGesture(Key.Enter, KeyModifiers.Control) };
            showItem.Click += (_, _) =>
            {
                var result = _shell.ShowInFileManager(item.Result.FullPath);
                ReportShellResult(result, Loc.Get("ShowInFolderFailed"));
            };

            var copyItem = new MenuItem { Header = Loc.Get("ContextMenuCopyPath"), InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
            copyItem.Click += async (_, _) => await CopyPathAsync(item);

            var copyFileItem = new MenuItem { Header = Loc.Get("ContextMenuCopyFile"), InputGesture = new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift) };
            copyFileItem.Click += async (_, _) => await CopyFileAsync(item);

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
            menuItems.Add(copyFileItem);

            menuItems.Add(new Separator());
            menuItems.Add(terminalItem);

            // Zip jobs reuse the Explorer-menu code path: a job process with its own progress window.
            menuItems.Add(new Separator());
            if (!item.IsDirectory && string.Equals(Path.GetExtension(item.FullPath), ".zip", StringComparison.OrdinalIgnoreCase))
            {
                menuItems.Add(JobMenuItem("ContextMenuExtractHere", "--extract-here", item.FullPath));
                menuItems.Add(JobMenuItem("ContextMenuExtractTo", "--extract-to", item.FullPath));
            }
            menuItems.Add(JobMenuItem("ContextMenuCompress", "--compress", item.FullPath));

            var trashItem = new MenuItem { Header = Loc.Get("ContextMenuMoveToTrash"), InputGesture = new KeyGesture(Key.Delete) };
            trashItem.Click += async (_, _) => await MoveToTrashAsync(item);
            menuItems.Add(trashItem);

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

    private MenuItem JobMenuItem(string labelKey, string flag, string path)
    {
        var menuItem = new MenuItem { Header = Loc.Get(labelKey) };
        menuItem.Click += (_, _) => StartJob(flag, path);
        return menuItem;
    }

    private void StartJob(string flag, string path)
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            var psi = new ProcessStartInfo(exe) { UseShellExecute = false };
            psi.ArgumentList.Add(flag);
            psi.ArgumentList.Add(path);
            Process.Start(psi);
        }
        catch (Exception ex)
        {
            SetStatus(CurrentStatusText, () => $"{Loc.Get("JobStartFailed")}: {ex.Message}");
        }
    }

    private async Task CopyPathAsync(ResultItem item)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(item.FullPath);
        SetStatus(CurrentStatusText, () => Loc.Get("PathCopied"));
    }

    private async Task<IStorageItem?> GetStorageItemAsync(ResultItem item) => item.IsDirectory
        ? await StorageProvider.TryGetFolderFromPathAsync(item.FullPath)
        : await StorageProvider.TryGetFileFromPathAsync(item.FullPath);

    /// <summary>Puts the file itself on the clipboard, ready to paste in Explorer.</summary>
    private async Task CopyFileAsync(ResultItem item)
    {
        if (Clipboard is null || await GetStorageItemAsync(item) is not { } storageItem) return;
        await Clipboard.SetFileAsync(storageItem);
        var name = item.FileName;
        SetStatus(CurrentStatusText, () => Loc.Get("FileCopied", name));
    }

    private async Task MoveToTrashAsync(ResultItem item)
    {
        var confirmed = await ConfirmDialog.ShowAsync(this,
            Loc.Get("TrashConfirmTitle"),
            Loc.Get(item.IsDirectory ? "TrashConfirmFolder" : "TrashConfirmFile", item.FileName),
            item.FullPath,
            Loc.Get("TrashConfirmButton"),
            Loc.Get("JobCancel"));
        if (!confirmed) return;

        var result = _shell.MoveToTrash(item.FullPath);
        if (!result.IsSuccess)
        {
            ReportShellResult(result, Loc.Get("TrashFailed"));
            return;
        }

        // Drop it everywhere right away instead of waiting for the file watcher.
        _index.Remove(item.FullPath);
        _allMatches.RemoveAll(r => string.Equals(r.FullPath, item.FullPath, StringComparison.OrdinalIgnoreCase));
        _results.Remove(item);
        foreach (var match in _contentResults.Where(r => r.FullPath == item.FullPath).ToList())
            _contentResults.Remove(match);
        DiskPage.Remove(item.FullPath);

        var name = item.FileName;
        SetStatus(CurrentStatusText, () => Loc.Get("MovedToTrash", name));
        UpdateShowMoreButton();
    }

    // =====================================================================
    // Disk usage
    // =====================================================================

    private void InitDiskPage()
    {
        // A whole-PC (or whole-drive) scan from the name search page already has
        // everything the disk page needs; reuse it instead of scanning again.
        DiskPage.FindExistingIndex = root =>
            !_index.IsIndexing && _index.Count > 0
            && _index.Roots.Any(r => string.Equals(r, root, StringComparison.OrdinalIgnoreCase))
                ? _index
                : null;

        foreach (var list in DiskPage.ResultLists)
            list.PointerReleased += OnAnyResultRightClick;

        // Folder list: double-click drills down (handled by the page); files list: open.
        DiskPage.ResultLists[1].DoubleTapped += OnAnyResultDoubleTapped;
        DiskPage.OpenRequested += path => ReportShellResult(_shell.OpenPath(path), Loc.Get("FileOpenFailed"));
    }

    // =====================================================================
    // Filters
    // =====================================================================

    private void InitFilters()
    {
        TypeFilterList.SelectionChanged += async (_, _) => await OnFilterChangedAsync();
        SizeFilterBox.SelectionChanged += async (_, _) => await OnFilterChangedAsync();
        DateFilterBox.SelectionChanged += async (_, _) => await OnFilterChangedAsync();
    }

    private async Task OnFilterChangedAsync()
    {
        if (_updatingControls) return;

        var filter = new ResultFilter(
            (FileCategory)Math.Max(0, TypeFilterList.SelectedIndex),
            ResultFilter.SizeSteps[Math.Max(0, SizeFilterBox.SelectedIndex)],
            ResultFilter.DateSteps[Math.Max(0, DateFilterBox.SelectedIndex)]);
        if (filter == _filter) return;

        _filter = filter;
        if (_index.Count > 0 && !_index.IsIndexing)
            await Search(_lastQuery);
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    private sealed record Shortcut(string Keys, string DescriptionKey);

    // Shown in Settings; the handling itself is in OnPreviewKeyDown.
    private static readonly Shortcut[] Shortcuts =
    [
        new("Ctrl+F", "ShortcutFocusSearch"),
        new("Ctrl+1 / Ctrl+2 / Ctrl+3", "ShortcutPages"),
        new("Ctrl+,", "ShortcutSettings"),
        new("↓ / ↑", "ShortcutMoveToResults"),
        new("Enter", "ShortcutOpen"),
        new("Ctrl+Enter", "ShortcutShowInFolder"),
        new("Ctrl+C", "ShortcutCopyPath"),
        new("Ctrl+Shift+C", "ShortcutCopyFile"),
        new("Delete", "ShortcutTrash"),
        new("Esc", "ShortcutEscape"),
        new("F5", "ShortcutRescan"),
    ];

    private void FillShortcutsTable()
    {
        ShortcutsGrid.Children.Clear();
        ShortcutsGrid.RowDefinitions.Clear();

        for (var i = 0; i < Shortcuts.Length; i++)
        {
            ShortcutsGrid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

            var keys = new Border
            {
                Background = Avalonia.Media.Brush.Parse("#313244"),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(7, 2),
                Margin = new Thickness(0, 3, 16, 3),
                HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
                Child = new TextBlock { Text = Shortcuts[i].Keys, FontSize = 11, Foreground = Avalonia.Media.Brush.Parse("#cdd6f4") }
            };
            var description = new TextBlock
            {
                Text = Loc.Get(Shortcuts[i].DescriptionKey),
                FontSize = 12,
                Foreground = Avalonia.Media.Brush.Parse("#a6adc8"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            };
            Grid.SetRow(keys, i);
            Grid.SetRow(description, i);
            Grid.SetColumn(description, 1);
            ShortcutsGrid.Children.Add(keys);
            ShortcutsGrid.Children.Add(description);
        }
    }

    private void InitKeyboard()
    {
        // Tunnel: runs before the focused control, so Enter/Delete on a result
        // aren't swallowed by the list, and Ctrl+F works from anywhere.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
    }

    private ListBox? ActiveList =>
        NameSearchPage.IsVisible ? ResultsList
        : ContentSearchPage.IsVisible ? ContentResultsList
        : DiskPage.IsVisible ? DiskPage.ActiveList
        : null;

    private TextBox? ActiveSearchBox =>
        NameSearchPage.IsVisible ? SearchBox : ContentSearchPage.IsVisible ? ContentSearchBox : null;

    private bool ResultsHaveFocus =>
        ActiveList is { } list && FocusManager?.GetFocusedElement() is Visual focused
        && (focused == list || list.IsVisualAncestorOf(focused));

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var list = ActiveList;
        var selected = list?.SelectedItem as ResultItem;

        switch (e.Key)
        {
            case Key.F when ctrl:
                if (ActiveSearchBox is null) FeatureNav.SelectedIndex = 0;
                ActiveSearchBox?.Focus();
                ActiveSearchBox?.SelectAll();
                break;
            case Key.D1 when ctrl:
                FeatureNav.SelectedIndex = 0;
                break;
            case Key.D2 when ctrl:
                FeatureNav.SelectedIndex = 1;
                break;
            case Key.D3 when ctrl:
                FeatureNav.SelectedIndex = 2;
                break;
            case Key.OemComma when ctrl:
                SettingsNav.SelectedIndex = 0;
                break;
            case Key.F5 when NameSearchPage.IsVisible && ReindexButton.IsEnabled:
                await StartIndexingAsync();
                break;
            case Key.Down when ctrl == false && ActiveSearchBox is { IsFocused: true } && list is { ItemCount: > 0 }:
                list.Focus();
                list.SelectedIndex = Math.Max(0, list.SelectedIndex);
                break;
            case Key.Up when ResultsHaveFocus && list!.SelectedIndex <= 0:
                ActiveSearchBox?.Focus();
                break;
            case Key.Escape when ResultsHaveFocus:
                ActiveSearchBox?.Focus();
                break;
            case Key.Enter when ResultsHaveFocus && selected is not null:
                ReportShellResult(ctrl ? _shell.ShowInFileManager(selected.FullPath) : _shell.OpenPath(selected.FullPath),
                    Loc.Get(ctrl ? "ShowInFolderFailed" : "FileOpenFailed"));
                break;
            case Key.C when ctrl && e.KeyModifiers.HasFlag(KeyModifiers.Shift) && ResultsHaveFocus && selected is not null:
                await CopyFileAsync(selected);
                break;
            case Key.C when ctrl && ResultsHaveFocus && selected is not null:
                await CopyPathAsync(selected);
                break;
            case Key.Delete when ResultsHaveFocus && selected is not null:
                await MoveToTrashAsync(selected);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    // =====================================================================
    // Drag and drop
    // =====================================================================

    private void InitDragAndDrop()
    {
        // Out: drag a result into Explorer, an editor, a mail... (copied, never moved).
        foreach (var list in new[] { ResultsList, ContentResultsList })
        {
            list.AddHandler(PointerPressedEvent, (_, e) =>
            {
                _dragStart = e.GetCurrentPoint(list).Properties.IsLeftButtonPressed ? e.GetPosition(list) : null;
            }, RoutingStrategies.Tunnel);
            list.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
            list.PointerMoved += async (_, e) => await TryStartDragAsync(list, e);
        }

        // In: drop a folder on a search page to search there.
        foreach (var page in new Control[] { NameSearchPage, ContentSearchPage })
        {
            page.AddHandler(DragDrop.DragOverEvent, (_, e) =>
            {
                e.DragEffects = !_draggingOut && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            });
            page.AddHandler(DragDrop.DropEvent, async (_, e) => await OnDropAsync(page, e));
        }
    }

    private async Task TryStartDragAsync(ListBox list, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _draggingOut) return;
        if (!e.GetCurrentPoint(list).Properties.IsLeftButtonPressed) { _dragStart = null; return; }

        var delta = e.GetPosition(list) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        _dragStart = null;

        if ((e.Source as Control)?.DataContext is not ResultItem item) return;

        if (await GetStorageItemAsync(item) is not { } storageItem) return;

        var data = new DataTransfer();
        data.Add(DataTransferItem.CreateFile(storageItem));

        _draggingOut = true;
        try
        {
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    private async Task OnDropAsync(Control page, DragEventArgs e)
    {
        if (_draggingOut || e.DataTransfer.TryGetFiles() is not { } items) return;

        // A dropped file means "search where this file is".
        var folders = items
            .Select(i => i.TryGetLocalPath())
            .OfType<string>()
            .Select(p => Directory.Exists(p) ? p : Path.GetDirectoryName(p))
            .OfType<string>()
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (folders.Count == 0) return;

        if (page == ContentSearchPage)
        {
            _contentSearchFolder = folders[0];
            ContentFolderText.Text = folders[0];
            SetStatus(ContentStatusText, () => Loc.Get("ContentReadyPrompt"));
            ContentSearchBox.Focus();
        }
        else
        {
            _selectedRoots = folders;
            await StartIndexingAsync();
        }
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
        _contentQuery = query;
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
                var item = new ResultItem(SearchResult.FromDisk(match.FullPath), match.Snippet, snippetHighlight: query);
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
