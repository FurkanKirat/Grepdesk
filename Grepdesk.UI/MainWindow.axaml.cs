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

    private readonly IPlatformShell _shell = PlatformShellFactory.CreatePlatformShell();
    private readonly EditorDetector _editorDetector;

    // ---- shared file-name index (tab 1) ----
    private readonly FileIndex _index = new();
    private readonly ObservableCollection<ResultItem> _results = [];
    private CancellationTokenSource _indexCts = new();
    private CancellationTokenSource _searchCts = new();
    private string _lastQuery = "";
    private List<string>? _selectedRoots; // null = whole PC

    // ---- content search (tab 2) ----
    private readonly ObservableCollection<ResultItem> _contentResults = [];
    private CancellationTokenSource _contentSearchCts = new();
    private string? _contentSearchFolder;

    public MainWindow() : this(null) { }

    public MainWindow(string? startFolder)
    {
        InitializeComponent();
        _editorDetector = new EditorDetector(_shell);
        ApplyStaticLocalizedText();

        // --- Tab 1 wiring ---
        ResultsList.ItemsSource = _results;

        SearchBox.TextChanged += OnSearchTextChanged;
        SearchBox.KeyDown += OnSearchKeyDown;
        ResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ResultsList.PointerReleased += OnAnyResultRightClick;

        ChooseFolderButton.Click += async (_, _) => await ChooseFolderAsync();
        ScanAllButton.Click += async (_, _) => await ScanWholeComputerAsync();
        ReindexButton.Click += async (_, _) => await StartIndexingAsync();

        _index.ProgressChanged += count =>
            Dispatcher.UIThread.Post(() =>
                StatusText.Text = Loc.Get("IndexingProgress", count));

        _index.IndexingComplete += () =>
            Dispatcher.UIThread.Post(() =>
            {
                StatusText.Text = Loc.Get("ReadyIndexed", _index.Count);
                ReindexButton.IsEnabled = true;
            });

        // No automatic scan on launch — user picks a folder or "scan whole PC" first.

        // --- Tab 2 wiring ---
        ContentResultsList.ItemsSource = _contentResults;
        ContentResultsList.DoubleTapped += OnAnyResultDoubleTapped;
        ContentResultsList.PointerReleased += OnAnyResultRightClick;
        ContentChooseFolderButton.Click += async (_, _) => await ChooseContentFolderAsync();
        ContentSearchBox.KeyDown += OnContentSearchKeyDown;

        InitContextMenuIntegration();

        if (startFolder is not null)
            Opened += async (_, _) => await OpenWithFolderAsync(startFolder);
    }

    /// <summary>
    /// Opt-in "Open with Grepdesk" entry in the file manager's context menu.
    /// The registry itself is the source of truth for the checkbox state.
    /// </summary>
    private void InitContextMenuIntegration()
    {
        if (!_shell.SupportsFolderContextMenu || Environment.ProcessPath is not { } exePath)
            return;

        ContextMenuCheckBox.Content = Loc.Get("ContextMenuIntegration");
        ContextMenuCheckBox.IsVisible = true;
        ContextMenuCheckBox.IsChecked = _shell.IsFolderContextMenuRegistered();

        // Already opted in: rewrite the entry so it follows the exe if it was
        // moved, and picks up the current language for the menu label.
        if (ContextMenuCheckBox.IsChecked == true)
            _shell.RegisterFolderContextMenu(exePath, Loc.Get("ContextMenuEntryLabel"));

        ContextMenuCheckBox.IsCheckedChanged += (_, _) =>
        {
            var enable = ContextMenuCheckBox.IsChecked == true;
            var result = enable
                ? _shell.RegisterFolderContextMenu(exePath, Loc.Get("ContextMenuEntryLabel"))
                : _shell.UnregisterFolderContextMenu();

            if (!result.IsSuccess)
            {
                StatusText.Text = Loc.Get("ContextMenuUpdateFailed");
                ContextMenuCheckBox.IsChecked = _shell.IsFolderContextMenuRegistered();
            }
        };
    }

    /// <summary>
    /// Launched with a folder (e.g. from Explorer's context menu): use it
    /// as the root for both tabs and start indexing right away.
    /// </summary>
    private async Task OpenWithFolderAsync(string folder)
    {
        _contentSearchFolder = folder;
        ContentFolderText.Text = folder;
        ContentStatusText.Text = Loc.Get("ContentReadyPrompt");

        _selectedRoots = [folder];
        await StartIndexingAsync();
    }

    /// <summary>
    /// Sets the text on controls whose XAML values are just design-time
    /// placeholders (Watermark, button Content, initial status text).
    /// These aren't bound, so they need to be set once in code after
    /// InitializeComponent() using the currently loaded language.
    /// </summary>
    private void ApplyStaticLocalizedText()
    {
        SearchBox.Watermark = Loc.Get("SearchWatermark");
        StatusText.Text = Loc.Get("StatusChooseOption");
        ChooseFolderButton.Content = Loc.Get("ChooseFolder");
        ScanAllButton.Content = Loc.Get("ScanAllPc");
        ReindexButton.Content = Loc.Get("Rescan");

        ContentFolderText.Text = Loc.Get("NotSelected");
        ContentSearchBox.Watermark = Loc.Get("ContentSearchWatermark");
        ContentStatusText.Text = Loc.Get("SelectFolderFirst");
        SupportedFormatsText.Text = Loc.Get("SupportedFormats");
        ContentChooseFolderButton.Content = Loc.Get("ChooseFolder");

        FileNameSearchTabItem.Header = Loc.Get("FileNameSearchTab");
        ContentSearchTabItem.Header = Loc.Get("ContentSearchTab");
    }

    // =====================================================================
    // TAB 1 — file name search
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
        StatusText.Text = Loc.Get("Indexing");
        ReindexButton.IsEnabled = false;
        _results.Clear();

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

            RunSearch(query);
        }
        catch (TaskCanceledException) { }
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

    private void RunSearch(string query)
    {
        _results.Clear();

        if (string.IsNullOrWhiteSpace(query))
        {
            StatusText.Text = _index.Count > 0
                ? Loc.Get("ReadyIndexed", _index.Count)
                : Loc.Get("StatusChooseOption");
            return;
        }

        if (_index.IsIndexing)
        {
            StatusText.Text = Loc.Get("StillIndexing");
            return;
        }

        if (_index.Count == 0)
        {
            StatusText.Text = Loc.Get("SelectFolderOrScanAll");
            return;
        }

        var results = _index.Search(query).ToList();

        foreach (var r in results)
            _results.Add(new ResultItem(r));

        StatusText.Text = results.Count >= 500
            ? Loc.Get("ShowingFirst500", query)
            : Loc.Get("ResultsCount", results.Count, query);
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
                    StatusText.Text = Loc.Get("PathCopied");
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
        StatusText.Text = result.IsSuccess
            ? StatusText.Text // silently keep current status on success
            : result.Exception is not null
                ? $"{failureMessagePrefix}: {result.Exception.Message}"
                : $"{failureMessagePrefix} ({result.Status})";
    }

    // =====================================================================
    // TAB 2 — content search (searches inside file contents, not just names)
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
        ContentStatusText.Text = Loc.Get("ContentReadyPrompt");
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
            ContentStatusText.Text = Loc.Get("SelectFolderFirstContent");
            return;
        }

        _contentSearchCts.Cancel();
        _contentSearchCts = new CancellationTokenSource();
        var token = _contentSearchCts.Token;

        _contentResults.Clear();
        ContentStatusText.Text = Loc.Get("ScanningFiles");

        // Walk the chosen folder directly — content search doesn't depend on
        // tab 1's name index, so it works even if that index was never built.
        var candidates = EnumerateSearchableFiles(_contentSearchFolder);

        var matchCount = 0;
        var skippedCount = 0; // too large, or extraction failed (corrupt/locked/unsupported-compression)

        void OnSkipped(SkippedFile _)
        {
            Interlocked.Increment(ref skippedCount);
            Dispatcher.UIThread.Post(() =>
                ContentStatusText.Text = Loc.Get("ScanningProgress", matchCount, skippedCount));
        }

        try
        {
            await foreach (var match in ContentSearcher.SearchAsync(candidates, query, OnSkipped, token))
            {
                _contentResults.Add(new ResultItem(new SearchResult(match.FullPath), match.Snippet));
                matchCount++;
                ContentStatusText.Text = skippedCount == 0
                    ? Loc.Get("ScanningMatches", matchCount)
                    : Loc.Get("ScanningProgress", matchCount, skippedCount);
            }

            if (!token.IsCancellationRequested)
            {
                var summary = matchCount == 0 ? Loc.Get("NoMatchesFound") : Loc.Get("MatchesFound", matchCount);
                ContentStatusText.Text = skippedCount == 0
                    ? summary
                    : Loc.Get("MatchesFoundWithSkipped", summary, skippedCount);
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

// ViewModel wrapper for list items — used by both tabs, Snippet only populated
// by content search results.
public class ResultItem(SearchResult result, string? snippet = null)
{
    public SearchResult Result { get; } = result;
    public string FileName => Result.FileName;
    public string Directory => Result.Directory;
    public string FullPath => Result.FullPath;
    public string Icon => Result.IsDirectory ? "📁" : "📄";
    public string? Snippet { get; } = snippet;
    public bool HasSnippet => Snippet != null;
}