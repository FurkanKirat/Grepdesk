using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Preview;
using Grepdesk.Core.Programs;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI.Programs;

/// <summary>
/// Fast program launcher and favorites manager: discovers installed applications
/// from Start Menu, Desktop, and OS application folders, allows pinning favorites
/// to the top, and lets the user add custom/portable executables.
/// </summary>
public partial class ProgramsView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;
    private static AppSettings Settings => AppSettings.Current;

    private readonly ProgramBookmarks _bookmarks = new(
        Settings.FavoritePrograms,
        Settings.RecentPrograms,
        Settings.CustomPrograms);
    private readonly IShellIcons? _shellIcons = PlatformShellFactory.CreateShellIcons();
    private readonly Dictionary<string, Task<Bitmap?>> _iconCache = new(PathUtil.Comparer);
    private readonly ObservableCollection<ProgramRow> _rows = [];

    private List<ProgramEntry> _allPrograms = [];
    private ProgramFilter _filter = ProgramFilter.All;
    private CancellationTokenSource _scanCts = new();
    private Func<string>? _statusMessage;
    private bool _loaded;
    private bool _updating;

    public IPlatformShell? Shell { get; set; }
    public TextBlock StatusLine => StatusText;

    public ProgramsView()
    {
        InitializeComponent();

        ProgramList.ItemsSource = _rows;
        ProgramList.ContainerPrepared += OnContainerPrepared;
        ProgramList.AddHandler(Button.ClickEvent, OnRowButtonClick);
        ProgramList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (ProgramList.SelectedItem is ProgramRow row) Launch(row);
        };
        ProgramList.PointerReleased += OnProgramRightClick;

        SearchBox.TextChanged += (_, _) =>
        {
            if (_updating) return;
            _statusMessage = null;
            Render();
        };
        SearchBox.KeyDown += OnSearchBoxKeyDown;

        FilterList.SelectionChanged += (_, _) =>
        {
            if (_updating || FilterList.SelectedIndex < 0) return;
            _filter = (ProgramFilter)FilterList.SelectedIndex;
            _statusMessage = null;
            Render();
        };

        AddButton.Click += async (_, _) => await AddProgramAsync();
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        ClearRecentButton.Click += (_, _) => ClearRecent();

        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?
                .Select(i => i.TryGetLocalPath())
                .OfType<string>()
                .ToList();
            if (paths is { Count: > 0 })
                await AddProgramPathsAsync(paths);
        });

        ApplyLanguage();
    }

    private ProgramRow? SelectedOrFirstRow =>
        ProgramList.SelectedItem as ProgramRow ?? (_rows.Count > 0 ? _rows[0] : null);

    public void FocusSearch()
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    public async Task OnShownAsync()
    {
        FocusSearch();
        if (!_loaded)
            await ReloadAsync();
    }

    public void ApplyLanguage()
    {
        _updating = true;
        try
        {
            TitleText.Text = Loc.Get("ProgramsTab");
            SubtitleText.Text = Loc.Get("ProgramsSubtitle");
            SearchBox.Watermark = Loc.Get("ProgramsSearchWatermark");
            AddButton.Content = Loc.Get("ProgramsAdd");
            RefreshButton.Content = Loc.Get("ProgramsRefresh");
            ClearRecentButton.Content = Loc.Get("BrowseClearHistoryShort");
            ToolTip.SetTip(ClearRecentButton, Loc.Get("ProgramsClearRecent"));

            UpdateFilterChips();
            if (_loaded)
                Render(keepSelectionPath: (ProgramList.SelectedItem as ProgramRow)?.Entry.LaunchPath);
            else
                StatusText.Text = _statusMessage?.Invoke() ?? "";
        }
        finally
        {
            _updating = false;
        }
    }

    public async Task ReloadAsync(string? selectPath = null)
    {
        _scanCts.Cancel();
        _scanCts = new CancellationTokenSource();
        var ct = _scanCts.Token;

        RefreshButton.IsEnabled = false;
        StatusText.Text = Loc.Get("ProgramsScanning");

        try
        {
            var custom = _bookmarks.Custom.ToList();
            var favorites = _bookmarks.Favorites.ToList();
            var programs = await Task.Run(() => ProgramCatalog.Scan(
                roots: null,
                customPaths: custom,
                favoritePaths: favorites,
                ct: ct), ct);

            if (ct.IsCancellationRequested) return;

            _allPrograms = programs;
            _loaded = true;
            Render(keepSelectionPath: selectPath ?? (ProgramList.SelectedItem as ProgramRow)?.Entry.LaunchPath);
        }
        catch (OperationCanceledException) { }
        finally
        {
            RefreshButton.IsEnabled = true;
        }
    }

    private void Render(string? keepSelectionPath = null)
    {
        var query = SearchBox.Text?.Trim() ?? "";
        var filtered = ProgramCatalog.FilterAndSort(_allPrograms, _bookmarks, query, _filter);

        _rows.Clear();
        ProgramRow? toSelect = null;
        foreach (var entry in filtered)
        {
            var row = new ProgramRow(entry, _bookmarks.IsFavorite(entry), query);
            if (_iconCache.TryGetValue(entry.LaunchPath, out var cached) && cached.IsCompletedSuccessfully)
                row.Icon = cached.Result;
            _rows.Add(row);

            if (keepSelectionPath is not null && PathUtil.Comparer.Equals(entry.LaunchPath, keepSelectionPath))
                toSelect = row;
        }

        if (toSelect is not null)
        {
            ProgramList.SelectedItem = toSelect;
            ProgramList.ScrollIntoView(toSelect);
        }
        else if (_rows.Count > 0)
        {
            ProgramList.SelectedIndex = 0;
        }

        UpdateFilterChips();
        ClearRecentButton.IsVisible = _filter == ProgramFilter.Recent && _bookmarks.Recent.Count > 0;

        EmptyState.IsVisible = _rows.Count == 0 && _loaded;
        ProgramList.IsVisible = _rows.Count > 0 || !_loaded;

        if (EmptyState.IsVisible)
        {
            EmptyText.Text = query.Length > 0
                ? Loc.Get("ProgramsNoMatches", query)
                : _filter switch
                {
                    ProgramFilter.Favorites => Loc.Get("ProgramsFavoritesEmpty"),
                    ProgramFilter.Recent => Loc.Get("ProgramsRecentEmpty"),
                    _ => Loc.Get("ProgramsEmpty"),
                };
        }

        UpdateStatusText();
    }

    private void UpdateFilterChips()
    {
        var favCount = _allPrograms.Count(p => _bookmarks.IsFavorite(p));
        var recentCount = _allPrograms.Count(p => _bookmarks.RecentIndex(p) >= 0);

        var prev = _updating;
        _updating = true;
        try
        {
            FilterList.ItemsSource = new[]
            {
                Loc.Get("ProgramsFilterAll", _allPrograms.Count),
                Loc.Get("ProgramsFilterFavorites", favCount),
                Loc.Get("ProgramsFilterRecent", recentCount),
            };
            FilterList.SelectedIndex = (int)_filter;
        }
        finally
        {
            _updating = prev;
        }
    }

    private void UpdateStatusText()
    {
        if (_statusMessage is not null)
        {
            StatusText.Text = _statusMessage();
            return;
        }

        if (!_loaded)
        {
            StatusText.Text = "";
            return;
        }

        var favCount = _allPrograms.Count(p => _bookmarks.IsFavorite(p));
        StatusText.Text = Loc.Get("ProgramsStatusSummary", _rows.Count, favCount);
    }

    private void SetMessage(Func<string> message)
    {
        _statusMessage = message;
        StatusText.Text = message();
    }

    // =====================================================================
    // Icons
    // =====================================================================

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        if (_shellIcons is null || e.Container.DataContext is not ProgramRow { Icon: null } row) return;
        _ = LoadIconForRowAsync(row, _scanCts.Token);
    }

    private async Task LoadIconForRowAsync(ProgramRow row, CancellationToken ct)
    {
        var key = row.Entry.LaunchPath;
        if (!_iconCache.TryGetValue(key, out var task) || task.IsCanceled)
        {
            task = FetchIconBitmapAsync(row.Entry, ct);
            _iconCache[key] = task;
        }

        var bitmap = await task;
        if (bitmap is null || ct.IsCancellationRequested) return;

        Dispatcher.UIThread.Post(() => row.Icon ??= bitmap);
    }

    private async Task<Bitmap?> FetchIconBitmapAsync(ProgramEntry entry, CancellationToken ct)
    {
        if (_shellIcons is null) return null;
        var thumb = await _shellIcons.GetIconAsync(entry.LaunchPath, 32, ct).ConfigureAwait(false);
        if (thumb is null && !string.Equals(entry.LaunchPath, entry.TargetPath, PathUtil.Comparison) && File.Exists(entry.TargetPath))
            thumb = await _shellIcons.GetIconAsync(entry.TargetPath, 32, ct).ConfigureAwait(false);
        return thumb is null ? null : ShellPreviewProvider.ToBitmap(thumb);
    }

    // =====================================================================
    // Actions: Launch, Favorite, Add, Show in Folder
    // =====================================================================

    private void Launch(ProgramRow row)
    {
        if (Shell is null) return;
        SessionTrail.Note($"launch program: {row.Entry.Name}");
        var result = Shell.OpenPath(row.Entry.LaunchPath);
        if (!result.IsSuccess && !string.Equals(row.Entry.LaunchPath, row.Entry.TargetPath, PathUtil.Comparison))
            result = Shell.OpenPath(row.Entry.TargetPath);

        if (!result.IsSuccess)
        {
            SetMessage(() => Loc.Get("ProgramsLaunchFailed", row.Name));
            return;
        }

        _bookmarks.RecordLaunch(row.Entry.LaunchPath);
        SaveBookmarks();
        UpdateFilterChips();
        if (_filter == ProgramFilter.Recent)
            Render(keepSelectionPath: row.Entry.LaunchPath);
        SetMessage(() => Loc.Get("ProgramsLaunched", row.Name));
    }

    public void ToggleFavorite(ProgramRow row)
    {
        var nowFav = _bookmarks.ToggleFavorite(row.Entry);
        row.IsFavorite = nowFav;
        SaveBookmarks();

        var name = row.Name;
        SetMessage(() => Loc.Get(nowFav ? "ProgramsFavoriteAdded" : "ProgramsFavoriteRemoved", name));
        Render(keepSelectionPath: row.Entry.LaunchPath);
    }

    private async Task AddProgramAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;

        var patterns = OperatingSystem.IsWindows()
            ? new[] { "*.exe", "*.lnk", "*.appref-ms", "*.bat", "*.cmd", "*.msc", "*.cpl" }
            : OperatingSystem.IsMacOS()
                ? new[] { "*.app", "*.command", "*.sh", "*" }
                : new[] { "*.desktop", "*.AppImage", "*.sh", "*.run", "*" };

        var files = await provider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Get("ProgramsAddDialogTitle"),
            AllowMultiple = true,
            FileTypeFilter =
            [
                new FilePickerFileType(Loc.Get("ProgramsFileFilter")) { Patterns = patterns },
                FilePickerFileTypes.All,
            ]
        });

        var paths = files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count > 0)
            await AddProgramPathsAsync(paths);
    }

    private async Task AddProgramPathsAsync(IReadOnlyList<string> paths)
    {
        string? lastAdded = null;
        foreach (var path in paths)
        {
            if (ProgramCatalog.TryCreateExplicitEntry(path) is { } entry)
            {
                _bookmarks.AddCustom(entry.LaunchPath);
                lastAdded = entry.LaunchPath;
            }
        }

        if (lastAdded is null) return;
        SaveBookmarks();
        await ReloadAsync(selectPath: lastAdded);
        var addedName = Path.GetFileNameWithoutExtension(lastAdded);
        SetMessage(() => Loc.Get("ProgramsAdded", addedName));
    }

    private void RemoveCustomProgram(ProgramRow row)
    {
        if (!_bookmarks.RemoveCustom(row.Entry.LaunchPath)) return;
        _bookmarks.RemoveFavorite(row.Entry.LaunchPath);
        _bookmarks.RemoveRecent(row.Entry.LaunchPath);
        SaveBookmarks();

        _allPrograms.RemoveAll(p => PathUtil.Comparer.Equals(p.LaunchPath, row.Entry.LaunchPath));
        var name = row.Name;
        SetMessage(() => Loc.Get("ProgramsRemoved", name));
        Render();
    }

    private void ClearRecent()
    {
        _bookmarks.ClearRecent();
        SaveBookmarks();
        SetMessage(() => Loc.Get("ProgramsRecentCleared"));
        Render();
    }

    private void ShowInFolder(ProgramRow row)
    {
        if (Shell?.ShowInFileManager(row.Entry.RevealPath) is { IsSuccess: false })
            SetMessage(() => Loc.Get("ShowInFolderFailed"));
    }

    private void OpenInTerminal(ProgramRow row)
    {
        if (row.Entry.WorkingDirectory is not { Length: > 0 } dir || !Directory.Exists(dir)) return;
        if (Shell?.OpenInTerminal(dir) is { IsSuccess: false })
            SetMessage(() => Loc.Get("TerminalOpenFailed"));
    }

    private async Task CopyPathAsync(ProgramRow row)
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } clipboard) return;
        await clipboard.SetTextAsync(row.Entry.RevealPath);
        SetMessage(() => Loc.Get("PathCopied"));
    }

    private void SaveBookmarks()
    {
        Settings.FavoritePrograms = _bookmarks.Favorites.ToList();
        Settings.RecentPrograms = _bookmarks.Recent.ToList();
        Settings.CustomPrograms = _bookmarks.Custom.ToList();
        Settings.Save();
    }

    // =====================================================================
    // Row buttons, Context Menu & Keyboard
    // =====================================================================

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: ProgramRow row }) return;
        e.Handled = true;
        switch (action)
        {
            case "fav":
                ToggleFavorite(row);
                break;
            case "show":
                ShowInFolder(row);
                break;
            case "open":
                Launch(row);
                break;
        }
    }

    private void OnProgramRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (e.Source is not Control { DataContext: ProgramRow row }) return;
        ProgramList.SelectedItem = row;

        var items = new List<Control>();

        var openItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuOpen"),
            Icon = MenuIcons.For("ContextMenuOpen"),
            InputGesture = new KeyGesture(Key.Enter)
        };
        openItem.Click += (_, _) => Launch(row);
        items.Add(openItem);

        var favKey = row.IsFavorite ? "BrowseRemoveFavorite" : "BrowseAddFavorite";
        var favItem = new MenuItem
        {
            Header = Loc.Get(favKey),
            Icon = MenuIcons.For(favKey),
            InputGesture = new KeyGesture(Key.D, KeyModifiers.Control)
        };
        favItem.Click += (_, _) => ToggleFavorite(row);
        items.Add(favItem);

        items.Add(new Separator());

        var showItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuShowInFolder"),
            Icon = MenuIcons.For("ContextMenuShowInFolder"),
            InputGesture = new KeyGesture(Key.Enter, KeyModifiers.Control)
        };
        showItem.Click += (_, _) => ShowInFolder(row);
        items.Add(showItem);

        var termItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuOpenInTerminal"),
            Icon = MenuIcons.For("ContextMenuOpenInTerminal")
        };
        termItem.Click += (_, _) => OpenInTerminal(row);
        items.Add(termItem);

        var copyItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuCopyPath"),
            Icon = MenuIcons.For("ContextMenuCopyPath"),
            InputGesture = new KeyGesture(Key.C, KeyModifiers.Control)
        };
        copyItem.Click += async (_, _) => await CopyPathAsync(row);
        items.Add(copyItem);

        if (_filter == ProgramFilter.Recent)
        {
            items.Add(new Separator());
            var removeRecentItem = new MenuItem
            {
                Header = Loc.Get("BrowseRemoveFromHistory"),
                Icon = MenuIcons.For("ContextMenuMoveToTrash")
            };
            removeRecentItem.Click += (_, _) =>
            {
                _bookmarks.RemoveRecent(row.Entry.LaunchPath);
                _bookmarks.RemoveRecent(row.Entry.TargetPath);
                SaveBookmarks();
                Render();
            };
            items.Add(removeRecentItem);
        }

        if (_bookmarks.IsCustom(row.Entry.LaunchPath))
        {
            items.Add(new Separator());
            var removeCustomItem = new MenuItem
            {
                Header = Loc.Get("ProgramsRemoveFromList"),
                Icon = MenuIcons.For("ContextMenuMoveToTrash")
            };
            removeCustomItem.Click += (_, _) => RemoveCustomProgram(row);
            items.Add(removeCustomItem);
        }

        if (Shell is { SupportsNativeDialogs: true } shell)
        {
            items.Add(new Separator());
            var propsItem = new MenuItem
            {
                Header = Loc.Get("ContextMenuProperties"),
                Icon = MenuIcons.For("ContextMenuProperties")
            };
            propsItem.Click += (_, _) =>
            {
                if (!shell.ShowProperties(row.Entry.RevealPath).IsSuccess)
                    SetMessage(() => Loc.Get("PropertiesFailed"));
            };
            items.Add(propsItem);
        }

        new ContextMenu { ItemsSource = items }.Open(ProgramList);
    }

    private void OnSearchBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            SearchBox.Text = "";
            e.Handled = true;
        }
        else if (e.Key == Key.Enter && SelectedOrFirstRow is { } row)
        {
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
                ShowInFolder(row);
            else
                Launch(row);
            e.Handled = true;
        }
        else if (e.Key == Key.Down && _rows.Count > 0)
        {
            ProgramList.Focus();
            ProgramList.SelectedIndex = Math.Max(0, ProgramList.SelectedIndex);
            e.Handled = true;
        }
    }

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var listHasFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is Visual focused
                           && (focused == ProgramList || ProgramList.IsVisualAncestorOf(focused));

        switch (e.Key)
        {
            case Key.D when ctrl && SelectedOrFirstRow is { } favRow:
                ToggleFavorite(favRow);
                e.Handled = true;
                break;
            case Key.F5:
                await ReloadAsync();
                e.Handled = true;
                break;
            case Key.Enter when listHasFocus && SelectedOrFirstRow is { } openRow:
                if (ctrl) ShowInFolder(openRow);
                else Launch(openRow);
                e.Handled = true;
                break;
            case Key.C when ctrl && listHasFocus && SelectedOrFirstRow is { } copyRow:
                await CopyPathAsync(copyRow);
                e.Handled = true;
                break;
            case Key.Up when listHasFocus && ProgramList.SelectedIndex <= 0:
                SearchBox.Focus();
                e.Handled = true;
                break;
            case Key.Escape when listHasFocus:
                SearchBox.Focus();
                e.Handled = true;
                break;
        }
    }
}
