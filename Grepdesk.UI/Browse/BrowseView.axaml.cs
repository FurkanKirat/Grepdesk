using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Editor;
using Grepdesk.Core.Transfer;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Browse;

/// <summary>
/// The folder browser: walk folders like in Explorer, open, preview, rename,
/// copy, cut, paste and trash files, and hand a folder to search, Organize or a
/// zip job. Pastes run as the same job windows Explorer's menu starts.
/// </summary>
public partial class BrowseView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;
    private static AppSettings Settings => AppSettings.Current;

    /// <summary>"This PC", the list of drives. Not a path, so it never collides with one.</summary>
    internal const string ThisPc = "";

    private readonly NavigationHistory _history = new();
    private readonly IFileClipboard? _clipboard = PlatformShellFactory.CreateFileClipboard();
    private readonly ShellIconCache? _icons = PlatformShellFactory.CreateShellIcons() is { } icons ? new ShellIconCache(icons) : null;
    private readonly DispatcherTimer _reloadTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    private ObservableCollection<BrowseRow> _rows = [];
    private Dictionary<string, BrowseRow> _rowsByPath = new(PathUtil.Comparer);
    private IReadOnlyList<FolderEntry> _entries = [];
    private FolderListingError _error;
    private FolderSort _sort = new(Settings.BrowseSortColumn, Settings.BrowseSortDescending);
    private CancellationTokenSource _loadCts = new();
    private FileSystemWatcher? _watcher;
    private HashSet<string> _cutPaths = new(PathUtil.Comparer);

    private Func<string>? _message;       // the last operation's outcome; cleared on navigation
    private string? _selectAfterLoad;     // a renamed or new item to select once the list has it
    private bool _renameAfterLoad;
    private BrowseRow? _renaming;
    private TextBox? _renameBox;
    private bool _updatingPlaces;
    private bool _clearingFilter;
    private Dictionary<string, string> _driveDetails = new(PathUtil.Comparer); // "120 GB free of 476 GB", per drive root

    public IPlatformShell? Shell { get; set; }
    public IReadOnlyList<AvailableEditor> Editors { get; set; } = [];

    /// <summary>The folder shown, or <see cref="ThisPc"/>; null before the first navigation.</summary>
    public string? CurrentFolder => _history.Current;
    public TextBlock StatusLine => StatusText;
    public PreviewPane PreviewPanel => Preview;

    /// <summary>Space on a file: the main window's viewer.</summary>
    public event Action<ResultItem>? ViewRequested;
    /// <summary>The single selected item changed (null when none or several), so an open viewer can follow.</summary>
    public event Action<ResultItem?>? SelectionFollowed;
    /// <summary>Search the folder and everything under it by name: (folder, query).</summary>
    public event Action<string, string>? SearchRequested;
    public event Action<string>? OrganizeRequested;
    /// <summary>Items moved to the Recycle Bin or renamed away, so other pages can drop them.</summary>
    public event Action<IReadOnlyList<string>>? Removed;

    public BrowseView()
    {
        InitializeComponent();

        FileList.ItemsSource = _rows;
        FileList.ContainerPrepared += OnContainerPrepared;
        FileList.SelectionChanged += (_, _) => OnSelectionChanged();
        FileList.DoubleTapped += OnDoubleTapped;
        FileList.AddHandler(PointerReleasedEvent, OnListPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);

        BackButton.Click += async (_, _) => await GoBackAsync();
        ForwardButton.Click += async (_, _) => await GoForwardAsync();
        UpButton.Click += async (_, _) => await GoUpAsync();
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        NewFolderButton.Click += (_, _) => NewFolder();
        SearchBelowButton.Click += (_, _) => RequestSearch();
        HiddenToggle.IsChecked = Settings.ShowHiddenFiles;
        HiddenToggle.IsCheckedChanged += async (_, _) => await SetShowHiddenAsync(HiddenToggle.IsChecked == true);

        NameColumn.Click += (_, _) => SortBy(FolderSortColumn.Name);
        ModifiedColumn.Click += (_, _) => SortBy(FolderSortColumn.Modified);
        TypeColumn.Click += (_, _) => SortBy(FolderSortColumn.Type);
        SizeColumn.Click += (_, _) => SortBy(FolderSortColumn.Size);

        FilterBox.TextChanged += (_, _) =>
        {
            if (!_clearingFilter) Render(reset: true);
        };

        AddressBar.PointerPressed += (_, e) =>
        {
            // Empty space in the bar (not a crumb) switches to typing a path.
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is null) BeginEditAddress();
        };
        AddressBox.LostFocus += (_, _) => EndEditAddress();
        // A deep path shows its end, where you are, rather than its start.
        CrumbScroller.PropertyChanged += (_, e) =>
        {
            if (e.Property == ScrollViewer.ExtentProperty || e.Property == ScrollViewer.ViewportProperty)
                CrumbScroller.Offset = new Vector(Math.Max(0, CrumbScroller.Extent.Width - CrumbScroller.Viewport.Width), 0);
        };

        PlacesList.SelectionChanged += async (_, _) => await OnPlacePickedAsync(PlacesList);
        DrivesList.SelectionChanged += async (_, _) => await OnPlacePickedAsync(DrivesList);

        _reloadTimer.Tick += async (_, _) =>
        {
            // A reload could replace the row being renamed; wait until the name is in.
            if (_renaming is not null) return;
            _reloadTimer.Stop();
            await ReloadAsync();
        };

        // Tunnel: before the list or a text box acts on the key.
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        // Mouse back / forward buttons.
        AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        ApplyLanguage();
    }

    private IEnumerable<BrowseRow> SelectedRows => FileList.SelectedItems?.OfType<BrowseRow>() ?? [];
    private BrowseRow? SingleSelected => FileList.SelectedItems is { Count: 1 } items ? items[0] as BrowseRow : null;
    private bool InFolder => !string.IsNullOrEmpty(CurrentFolder);

    public void ApplyLanguage()
    {
        ToolTip.SetTip(BackButton, Loc.Get("BrowseBack"));
        ToolTip.SetTip(ForwardButton, Loc.Get("BrowseForward"));
        ToolTip.SetTip(UpButton, Loc.Get("BrowseUp"));
        ToolTip.SetTip(RefreshButton, Loc.Get("BrowseRefresh"));
        ToolTip.SetTip(NewFolderButton, Loc.Get("BrowseNewFolder"));
        ToolTip.SetTip(SearchBelowButton, Loc.Get("BrowseSearchBelow"));
        ToolTip.SetTip(HiddenToggle, Loc.Get("BrowseShowHidden"));
        FilterBox.Watermark = Loc.Get("BrowseFilterWatermark");
        UpdateColumnHeaders();
        FillPlaces();
        if (CurrentFolder is { } folder) ShowCrumbs(folder);
        // Type names ("TXT file") are made with the rows; a full render remakes them.
        _rowsByPath.Clear();
        Render(reset: true);
    }

    // =====================================================================
    // Navigation
    // =====================================================================

    /// <summary>Opens a folder (or <see cref="ThisPc"/>), adding it to the history.</summary>
    public async Task NavigateAsync(string folder, string? select = null)
    {
        folder = folder == ThisPc ? ThisPc : PathUtil.Normalize(folder);
        var changed = _history.Current is null || !string.Equals(_history.Current, folder, PathUtil.Comparison);
        _history.Navigate(folder);
        await LoadAsync(changed, select);
    }

    // Back to a parent selects the folder we came out of, as Explorer does (Render ignores it otherwise).
    private async Task GoBackAsync()
    {
        var from = _history.Current;
        if (_history.GoBack() is not null) await LoadAsync(folderChanged: true, select: from);
    }

    private async Task GoForwardAsync()
    {
        var from = _history.Current;
        if (_history.GoForward() is not null) await LoadAsync(folderChanged: true, select: from);
    }

    private async Task GoUpAsync()
    {
        if (!InFolder) return;
        var from = CurrentFolder!;
        // Above a drive root is "This PC", as in Explorer.
        await NavigateAsync(PathUtil.Parent(from) ?? ThisPc, select: from);
    }

    private Task ReloadAsync() => _history.Current is null ? Task.CompletedTask : LoadAsync(folderChanged: false, select: null);

    /// <param name="folderChanged">A different folder: start at the top with the filter cleared, instead of keeping selection and scroll.</param>
    /// <param name="select">Select this item once listed (the folder we came up from, a renamed file).</param>
    private async Task LoadAsync(bool folderChanged, string? select)
    {
        var folder = _history.Current!;
        // Opening a folder replaces the focused row; unless the focus was elsewhere, give it back to the list.
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;
        var refocus = folderChanged && (focused is null || (this.IsVisualAncestorOf(focused) && focused != AddressBox && focused != FilterBox));
        await _loadCts.CancelAsync();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;

        if (folderChanged)
        {
            CancelRename();
            _message = null;
            _clearingFilter = true;
            FilterBox.Text = "";
            _clearingFilter = false;
            Watch(folder);
            if (folder != ThisPc)
            {
                Settings.LastBrowseFolder = folder;
                Settings.Save();
            }
        }
        if (select is not null) _selectAfterLoad = select;

        UpdateNavigationButtons();
        ShowCrumbs(folder);
        SelectPlace(folder);

        var showHidden = Settings.ShowHiddenFiles;
        FolderContents contents;
        try
        {
            contents = folder == ThisPc
                ? await Task.Run(ListDrives, ct)
                : await Task.Run(() => FolderListing.List(folder, showHidden, ct), ct);
        }
        catch (OperationCanceledException)
        {
            return; // another folder was opened meanwhile
        }
        if (ct.IsCancellationRequested) return;

        // The folder was deleted or renamed while shown: go to what is left of it, as Explorer does.
        if (contents.Error == FolderListingError.NotFound && !folderChanged)
        {
            _history.Forget(folder);
            var existing = PathUtil.Parent(folder);
            while (existing is not null && !Directory.Exists(existing)) existing = PathUtil.Parent(existing);
            await NavigateAsync(existing ?? ThisPc);
            return;
        }

        _entries = contents.Entries;
        _error = contents.Error;
        RefreshCutMarks();
        Render(reset: folderChanged);
        if (refocus && IsEffectivelyVisible)
            Dispatcher.UIThread.Post(() => FocusList(selectFirst: false), DispatcherPriority.Loaded);
    }

    private void UpdateNavigationButtons()
    {
        BackButton.IsEnabled = _history.CanGoBack;
        ForwardButton.IsEnabled = _history.CanGoForward;
        UpButton.IsEnabled = InFolder;
        NewFolderButton.IsEnabled = SearchBelowButton.IsEnabled = InFolder;
        FilterBox.IsEnabled = true;
    }

    /// <summary>The drives as entries; runs off the UI thread (a sleeping network drive can take seconds to answer).</summary>
    private FolderContents ListDrives()
    {
        var entries = new List<FolderEntry>();
        var details = new Dictionary<string, string>(PathUtil.Comparer);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady) continue;
                var root = drive.RootDirectory.FullName;
                entries.Add(new FolderEntry(root, DriveLabel(drive), true, drive.TotalSize - drive.TotalFreeSpace,
                    default, FileAttributes.Directory));
                details[root] = DriveDetail(drive);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _driveDetails = details;
        return new FolderContents(ThisPc, entries);
    }

    private static string DriveLabel(DriveInfo drive)
    {
        var letter = drive.Name.TrimEnd(Path.DirectorySeparatorChar); // "C:" (TrimEndingDirectorySeparator keeps a root's)
        var label = drive.VolumeLabel;
        if (string.IsNullOrWhiteSpace(label))
            label = Loc.Get(drive.DriveType switch
            {
                DriveType.Removable => "DriveRemovable",
                DriveType.Network => "DriveNetwork",
                DriveType.CDRom => "DriveOptical",
                _ => "DriveLocal"
            });
        return OperatingSystem.IsWindows() ? $"{label} ({letter})" : drive.Name;
    }

    private static string DriveDetail(DriveInfo drive) =>
        Loc.Get("DriveFree", Format.Size(drive.TotalFreeSpace), Format.Size(drive.TotalSize));

    // =====================================================================
    // Listing
    // =====================================================================

    /// <param name="reset">
    /// Rebuild the list from scratch (new folder, sort, filter). Otherwise only
    /// the rows that came or went are changed, so a file appearing while you
    /// look keeps your selection and scroll position.
    /// </param>
    private void Render(bool reset)
    {
        var filter = FilterBox.Text;
        var view = FolderListing.Filter(_entries, filter).ToList();
        view.Sort(FolderListing.Comparer(_sort));

        var previous = _rowsByPath;
        var desired = new List<BrowseRow>(view.Count);
        var byPath = new Dictionary<string, BrowseRow>(view.Count, PathUtil.Comparer);
        foreach (var entry in view)
        {
            BrowseRow row;
            if (previous.TryGetValue(entry.FullPath, out var old) && old.Entry == entry)
            {
                row = old;
            }
            else
            {
                row = new BrowseRow(entry, CurrentFolder == ThisPc ? DriveTypeText(entry) : null)
                {
                    Icon = old?.Icon // same file, new size or date: keep its icon
                };
            }
            row.IsCut = _cutPaths.Contains(entry.FullPath);
            desired.Add(row);
            byPath[entry.FullPath] = row;
        }
        _rowsByPath = byPath;

        if (reset)
        {
            _rows = new ObservableCollection<BrowseRow>(desired);
            FileList.ItemsSource = _rows;
        }
        else
        {
            Merge(_rows, desired);
        }

        var select = _selectAfterLoad;
        _selectAfterLoad = null; // one render's worth: a folder we came back from may not be listed here
        if (select is not null && byPath.TryGetValue(select, out var target))
        {
            FileList.SelectedItems!.Clear();
            FileList.SelectedItem = target;
            FileList.ScrollIntoView(target);
            if (_renameAfterLoad)
            {
                _renameAfterLoad = false;
                StartRename(target);
            }
        }
        else if (reset && desired.Count > 0 && FileList.SelectedItem is null)
        {
            // Something to move from with the arrow keys, without selecting it.
            FileList.ScrollIntoView(0);
        }

        UpdateEmptyText(filter);
        UpdateStatus();
    }

    /// <summary>
    /// Changes <paramref name="rows"/> into <paramref name="desired"/> in place.
    /// Rows that stay keep their relative order (same sort), so after taking out
    /// the ones that went, the new ones only need inserting at their places.
    /// </summary>
    private static void Merge(ObservableCollection<BrowseRow> rows, List<BrowseRow> desired)
    {
        var keep = desired.ToHashSet();
        for (var i = rows.Count - 1; i >= 0; i--)
            if (!keep.Contains(rows[i])) rows.RemoveAt(i);

        for (var i = 0; i < desired.Count; i++)
        {
            if (i < rows.Count && ReferenceEquals(rows[i], desired[i])) continue;
            rows.Insert(i, desired[i]);
        }
        while (rows.Count > desired.Count) rows.RemoveAt(rows.Count - 1);
    }

    private string DriveTypeText(FolderEntry drive) => _driveDetails.GetValueOrDefault(drive.FullPath, "");

    private void UpdateEmptyText(string? filter)
    {
        var text = _error switch
        {
            FolderListingError.AccessDenied => Loc.Get("BrowseAccessDenied"),
            FolderListingError.NotFound => Loc.Get("BrowseNotFound"),
            FolderListingError.Failed => Loc.Get("BrowseReadFailed"),
            _ when _rows.Count == 0 && !string.IsNullOrWhiteSpace(filter) => Loc.Get("BrowseNoMatches", filter.Trim()),
            _ when _rows.Count == 0 => Loc.Get("BrowseEmptyFolder"),
            _ => null
        };
        EmptyText.Text = text;
        EmptyText.IsVisible = text is not null;
    }

    private void UpdateStatus()
    {
        if (_message is { } message)
        {
            StatusText.Text = message();
            return;
        }

        var selected = SelectedRows.ToList();
        var count = Loc.Get("BrowseItemCount", _rows.Count);
        if (selected.Count == 0)
        {
            StatusText.Text = count;
            return;
        }

        var files = selected.Where(r => !r.IsDirectory).ToList();
        var size = files.Count > 0 ? "  ·  " + Format.Size(files.Sum(r => r.Entry.Size)) : "";
        StatusText.Text = $"{count}  ·  {Loc.Get("BrowseSelectedCount", selected.Count)}{size}";
    }

    private void SetMessage(Func<string> message)
    {
        _message = message;
        UpdateStatus();
    }

    private void UpdateColumnHeaders()
    {
        string Header(FolderSortColumn column, string key) =>
            Loc.Get(key) + (_sort.Column == column ? (_sort.Descending ? "  ↓" : "  ↑") : "");

        NameColumn.Content = Header(FolderSortColumn.Name, "BrowseColumnName");
        ModifiedColumn.Content = Header(FolderSortColumn.Modified, "BrowseColumnModified");
        TypeColumn.Content = Header(FolderSortColumn.Type, "BrowseColumnType");
        SizeColumn.Content = Header(FolderSortColumn.Size, "BrowseColumnSize");
    }

    private void SortBy(FolderSortColumn column)
    {
        _sort = _sort.Toggle(column);
        Settings.BrowseSortColumn = _sort.Column;
        Settings.BrowseSortDescending = _sort.Descending;
        Settings.Save();
        UpdateColumnHeaders();

        var selected = SelectedRows.ToList();
        Render(reset: true);
        foreach (var row in selected) FileList.SelectedItems!.Add(row);
        if (selected.Count > 0) FileList.ScrollIntoView(selected[0]);
    }

    private async Task SetShowHiddenAsync(bool show)
    {
        if (Settings.ShowHiddenFiles == show) return;
        Settings.ShowHiddenFiles = show;
        Settings.Save();
        HiddenToggle.IsChecked = show;
        await ReloadAsync();
    }

    private void OnContainerPrepared(object? sender, ContainerPreparedEventArgs e)
    {
        // Icons only for rows that are actually on screen: a folder of 50,000
        // files shouldn't queue 50,000 icon requests.
        if (_icons is null || e.Container.DataContext is not BrowseRow { Icon: null } row) return;
        var ct = _loadCts.Token;
        _ = SetIconAsync(row, ct);
    }

    private async Task SetIconAsync(BrowseRow row, CancellationToken ct)
    {
        var icon = await _icons!.Get(row.Entry, ct);
        if (icon is not null && !ct.IsCancellationRequested)
            Dispatcher.UIThread.Post(() => row.Icon ??= icon);
    }

    private void Watch(string folder)
    {
        _watcher?.Dispose();
        _watcher = null;
        _reloadTimer.Stop();
        if (folder == ThisPc) return;

        try
        {
            var watcher = new FileSystemWatcher(folder)
            {
                IncludeSubdirectories = false,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite
                               | NotifyFilters.Size | NotifyFilters.Attributes
            };
            FileSystemEventHandler changed = (_, _) => ScheduleReload();
            watcher.Created += changed;
            watcher.Deleted += changed;
            watcher.Changed += changed;
            watcher.Renamed += (_, _) => ScheduleReload();
            watcher.Error += (_, _) => ScheduleReload(); // buffer overflow: just read the folder again
            watcher.EnableRaisingEvents = true;
            _watcher = watcher;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Some locations (a few network shares, virtual drives) can't be watched; F5 still works.
        }
    }

    // Many changes in a row (a copy landing, a build writing files) become one reload.
    private void ScheduleReload() => Dispatcher.UIThread.Post(() =>
    {
        _reloadTimer.Stop();
        _reloadTimer.Start();
    });

    // =====================================================================
    // Address bar and places
    // =====================================================================

    private void ShowCrumbs(string folder)
    {
        Crumbs.Children.Clear();
        AddCrumb(Loc.Get("PlaceThisPc"), ThisPc);
        if (folder == ThisPc) return;

        var segments = AddressResolver.Segments(folder);
        var first = segments[0];
        foreach (var segment in segments)
        {
            Crumbs.Children.Add(new PathIcon
            {
                Data = Icon("IconChevronRight"),
                Width = 10,
                Height = 10,
                Foreground = Brush.Parse("#6c7086"),
                VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
            });
            AddCrumb(segment == first ? RootCrumbLabel(segment) : segment.Label, segment.Path);
        }

        void AddCrumb(string label, string path)
        {
            var button = new Button { Content = label, Classes = { "crumb" } };
            button.Click += async (_, _) => await NavigateAsync(path);
            Crumbs.Children.Add(button);
        }
    }

    /// <summary>"Local Disk (C:)" when the drive list (read in the background) knows the drive; "C:" until then.</summary>
    private string RootCrumbLabel(AddressSegment root) =>
        DrivesList.ItemsSource?.OfType<PlaceRow>()
            .FirstOrDefault(p => string.Equals(p.Path, root.Path, PathUtil.Comparison))?.Label ?? root.Label;

    private void BeginEditAddress()
    {
        AddressBox.Text = InFolder ? CurrentFolder : "";
        AddressBox.IsVisible = true;
        CrumbScroller.IsVisible = false;
        AddressBox.Focus();
        AddressBox.SelectAll();
    }

    private void EndEditAddress()
    {
        AddressBox.IsVisible = false;
        CrumbScroller.IsVisible = true;
    }

    private async Task GoToTypedAddressAsync()
    {
        var typed = AddressBox.Text ?? "";
        var target = AddressResolver.Resolve(typed, InFolder ? CurrentFolder : null);
        switch (target.Kind)
        {
            case AddressKind.Folder:
                EndEditAddress();
                FileList.Focus();
                await NavigateAsync(target.Path);
                break;
            case AddressKind.File:
                EndEditAddress();
                await NavigateAsync(Path.GetDirectoryName(target.Path)!, select: target.Path);
                FileList.Focus();
                Report(Shell?.OpenPath(target.Path), "FileOpenFailed");
                break;
            case AddressKind.Shell:
                // shell:startup, ::{CLSID}… only Explorer can show those.
                EndEditAddress();
                Report(Shell?.OpenPath(target.Path), "FileOpenFailed");
                break;
            default:
                SetMessage(() => Loc.Get("BrowseAddressNotFound", typed.Trim()));
                AddressBox.SelectAll();
                break;
        }
    }

    private void FillPlaces()
    {
        _updatingPlaces = true;
        try
        {
            var places = new List<PlaceRow>();
            void Add(string key, string? path, string icon)
            {
                if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
                    places.Add(new PlaceRow(Loc.Get(key), PathUtil.Normalize(path), Icon(icon)));
            }

            Add("PlaceHome", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "IconHome");
            Add("PlaceDesktop", Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "IconDesktop");
            Add("PlaceDownloads", KnownFolders.Downloads(), "IconDownload");
            Add("PlaceDocuments", Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "IconDocument");
            Add("PlacePictures", Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "IconImage");
            Add("PlaceMusic", Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), "IconMusic");
            Add("PlaceVideos", Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), "IconVideo");
            PlacesList.ItemsSource = places;
        }
        finally
        {
            _updatingPlaces = false;
        }

        _ = FillDrivesAsync();
    }

    /// <summary>Drives are read off the UI thread: a disconnected network drive can take seconds to answer.</summary>
    private async Task FillDrivesAsync()
    {
        var driveIcon = Icon("IconDrive");
        var drives = await Task.Run(() =>
        {
            var rows = new List<PlaceRow>();
            foreach (var drive in DriveInfo.GetDrives())
            {
                try
                {
                    if (!drive.IsReady) continue;
                    var used = drive.TotalSize > 0 ? 1 - (double)drive.TotalFreeSpace / drive.TotalSize : -1;
                    rows.Add(new PlaceRow(DriveLabel(drive), drive.RootDirectory.FullName, driveIcon, DriveDetail(drive), used));
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
            return rows;
        });

        _updatingPlaces = true;
        try { DrivesList.ItemsSource = drives; }
        finally { _updatingPlaces = false; }
        if (CurrentFolder is { } folder)
        {
            SelectPlace(folder);
            ShowCrumbs(folder); // the root crumb can now say "Local Disk (C:)"
        }
    }

    /// <summary>Highlights the place that is the current folder, if it is one.</summary>
    private void SelectPlace(string folder)
    {
        _updatingPlaces = true;
        try
        {
            static PlaceRow? Find(ListBox list, string path) =>
                list.ItemsSource?.OfType<PlaceRow>().FirstOrDefault(p => string.Equals(PathUtil.Normalize(p.Path), path, PathUtil.Comparison));

            var normalized = folder == ThisPc ? ThisPc : PathUtil.Normalize(folder);
            PlacesList.SelectedItem = Find(PlacesList, normalized);
            DrivesList.SelectedItem = normalized == ThisPc ? null : Find(DrivesList, normalized);
        }
        finally
        {
            _updatingPlaces = false;
        }
    }

    private async Task OnPlacePickedAsync(ListBox list)
    {
        if (_updatingPlaces || list.SelectedItem is not PlaceRow place) return;
        await NavigateAsync(place.Path);
    }

    private static Geometry Icon(string key) =>
        Application.Current!.TryGetResource(key, null, out var value) && value is Geometry geometry ? geometry : new StreamGeometry();

    // =====================================================================
    // Opening, selection, preview
    // =====================================================================

    private void OnSelectionChanged()
    {
        if (_renaming is not null && !SelectedRows.Contains(_renaming)) CommitRename();

        var single = SingleSelected;
        Preview.Show(single?.ToResultItem());
        SelectionFollowed?.Invoke(single is { IsDirectory: false } ? single.ToResultItem() : null);
        UpdateStatus();
    }

    private void OnDoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        if ((e.Source as Control)?.DataContext is BrowseRow row) _ = OpenAsync(row);
    }

    private async Task OpenAsync(BrowseRow row)
    {
        if (row.IsDirectory)
            await NavigateAsync(row.FullPath);
        else
            Report(Shell?.OpenPath(row.FullPath), "FileOpenFailed");
    }

    private async Task OpenSelectionAsync()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 1)
        {
            await OpenAsync(selected[0]);
            return;
        }
        // Several: open the files (like Explorer, up to a sane number); folders would each replace the view.
        foreach (var row in selected.Where(r => !r.IsDirectory).Take(20))
            Report(Shell?.OpenPath(row.FullPath), "FileOpenFailed");
    }

    private void RequestSearch()
    {
        if (InFolder) SearchRequested?.Invoke(CurrentFolder!, FilterBox.Text?.Trim() ?? "");
    }

    // =====================================================================
    // Keyboard and mouse
    // =====================================================================

    private async void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        // Decided before anything is awaited: Handled must be set while the key is still being routed.
        if (KeyAction(e) is not { } action) return;
        e.Handled = true;
        await action();
    }

    private Func<Task>? KeyAction(KeyEventArgs e)
    {
        var mods = e.KeyModifiers;
        var ctrl = mods.HasFlag(KeyModifiers.Control);
        var shift = mods.HasFlag(KeyModifiers.Shift);
        var alt = mods.HasFlag(KeyModifiers.Alt);
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Visual;

        static Func<Task> Do(Action action) => () =>
        {
            action();
            return Task.CompletedTask;
        };

        // Text boxes get their keys, apart from the few that end editing.
        if (focused is TextBox box)
        {
            if (box == AddressBox)
                return e.Key switch
                {
                    Key.Enter => GoToTypedAddressAsync,
                    Key.Escape => Do(() => { EndEditAddress(); FocusList(selectFirst: false); }),
                    _ => null
                };
            if (box == FilterBox)
                return e.Key switch
                {
                    Key.Escape when !string.IsNullOrEmpty(FilterBox.Text) => Do(() => FilterBox.Text = ""),
                    Key.Escape or Key.Down or Key.Enter => Do(() => FocusList()),
                    _ => null
                };
            if (_renaming is not null && box.DataContext == _renaming)
                return e.Key switch
                {
                    Key.Enter => Do(() => { CommitRename(); FileList.Focus(); }),
                    Key.Escape => Do(() => { CancelRename(); FileList.Focus(); }),
                    _ => null
                };
            return null;
        }

        // Keys that act on the selection only when the list has the focus, so
        // Enter or Space on a toolbar button still presses that button.
        var inList = focused is not null && (focused == FileList || FileList.IsVisualAncestorOf(focused));
        var hasSelection = inList && FileList.SelectedItems?.Count > 0;

        return e.Key switch
        {
            Key.Left when alt => GoBackAsync,
            Key.Back when mods == KeyModifiers.None => GoBackAsync,
            Key.BrowserBack => GoBackAsync,
            Key.Right when alt => GoForwardAsync,
            Key.BrowserForward => GoForwardAsync,
            Key.Up when alt => GoUpAsync,
            Key.F5 or Key.BrowserRefresh => ReloadAsync,
            Key.L when ctrl => Do(BeginEditAddress),
            Key.D when alt => Do(BeginEditAddress),
            Key.F4 when mods == KeyModifiers.None => Do(BeginEditAddress),
            Key.H when ctrl => () => SetShowHiddenAsync(!Settings.ShowHiddenFiles),
            Key.N when ctrl && shift => Do(NewFolder),
            Key.Enter when mods == KeyModifiers.None && hasSelection => OpenSelectionAsync,
            Key.Space when mods == KeyModifiers.None && inList && SingleSelected is { IsDirectory: false } file =>
                Do(() => ViewRequested?.Invoke(file.ToResultItem())),
            Key.F2 when inList && InFolder && SingleSelected is { } row => Do(() => StartRename(row)),
            Key.Delete when hasSelection && InFolder => TrashSelectionAsync,
            Key.C when ctrl && shift && (hasSelection || InFolder) => CopyPathsAsync,
            Key.C when ctrl && hasSelection => Do(() => PutOnClipboard(cut: false)),
            Key.X when ctrl && hasSelection && InFolder => Do(() => PutOnClipboard(cut: true)),
            Key.V when ctrl && InFolder => Do(() => Paste(CurrentFolder!)),
            _ => null
        };
    }

    private void OnPreviewPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsXButton1Pressed) { _ = GoBackAsync(); e.Handled = true; }
        else if (props.IsXButton2Pressed) { _ = GoForwardAsync(); e.Handled = true; }
    }

    /// <summary>Focus the list on the selected row, so the arrow keys work from there.</summary>
    /// <param name="selectFirst">With nothing selected, select the first row (moving down from the filter box).</param>
    public void FocusList(bool selectFirst = true)
    {
        if (selectFirst && FileList.SelectedItem is null && _rows.Count > 0) FileList.SelectedIndex = 0;
        if (FileList.SelectedItem is { } item && FileList.ContainerFromItem(item) is { } container) container.Focus();
        else FileList.Focus();
    }

    public void FocusFilter()
    {
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    // =====================================================================
    // Context menu
    // =====================================================================

    private void OnListPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if ((e.Source as Visual)?.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;

        var row = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as BrowseRow;
        if (row is not null && !SelectedRows.Contains(row))
        {
            FileList.SelectedItems!.Clear();
            FileList.SelectedItem = row;
        }
        else if (row is null)
        {
            FileList.SelectedItems!.Clear();
        }

        var items = row is null ? BackgroundMenu() : ItemMenu(SelectedRows.ToList());
        if (items.Count == 0) return;
        new ContextMenu { ItemsSource = items }.Open(FileList);
        e.Handled = true;
    }

    private List<Control> ItemMenu(List<BrowseRow> selection)
    {
        var menu = new List<Control>();
        var single = selection.Count == 1 ? selection[0] : null;
        var paths = selection.Select(r => r.FullPath).ToList();

        if (single is not null)
        {
            menu.Add(Item("ContextMenuOpen", () => _ = OpenAsync(single), new KeyGesture(Key.Enter)));
            if (!single.IsDirectory)
                menu.Add(Item("ContextMenuView", () => ViewRequested?.Invoke(single.ToResultItem()), new KeyGesture(Key.Space)));
        }
        if (!InFolder) return menu; // drives in This PC: just open them

        menu.Add(new Separator());
        menu.Add(Item("BrowseCut", () => PutOnClipboard(cut: true), new KeyGesture(Key.X, KeyModifiers.Control)));
        menu.Add(Item("BrowseCopy", () => PutOnClipboard(cut: false), new KeyGesture(Key.C, KeyModifiers.Control)));
        if (single is { IsDirectory: true } && ClipboardHasFiles())
            menu.Add(Item("BrowsePasteInto", () => Paste(single.FullPath)));
        menu.Add(Item("BrowseCopyPath", () => _ = CopyPathsAsync(), new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift)));

        menu.Add(new Separator());
        menu.Add(Item("ContextMenuCompress", () => StartJob("--compress", paths)));
        var zips = paths.Where(p => string.Equals(Path.GetExtension(p), ".zip", StringComparison.OrdinalIgnoreCase)).ToList();
        if (zips.Count > 0 && zips.Count == paths.Count)
        {
            menu.Add(Item("ContextMenuExtractHere", () => StartJob("--extract-here", zips)));
            menu.Add(Item("ContextMenuExtractTo", () => StartJob("--extract-to", zips)));
        }

        menu.Add(new Separator());
        if (single is not null)
            menu.Add(Item("BrowseRename", () => StartRename(single), new KeyGesture(Key.F2)));
        menu.Add(Item("ContextMenuMoveToTrash", () => _ = TrashSelectionAsync(), new KeyGesture(Key.Delete)));

        if (single is not null)
        {
            menu.Add(new Separator());
            menu.Add(Item("ContextMenuOpenInTerminal", () =>
                Report(Shell?.OpenInTerminal(single.IsDirectory ? single.FullPath : CurrentFolder!), "TerminalOpenFailed")));
            foreach (var editor in Editors.Where(ed => EditorTargetResolver.IsCompatible(ed, single.FullPath)))
            {
                var menuItem = new MenuItem { Header = editor.Header };
                menuItem.Click += (_, _) => Report(Shell?.OpenInEditor(editor, single.FullPath), "EditorOpenFailed", editor.Header);
                menu.Add(menuItem);
            }
            if (single.IsDirectory)
                menu.Add(Item("ContextMenuOrganize", () => OrganizeRequested?.Invoke(single.FullPath)));
            menu.Add(Item("BrowseShowInExplorer", () => Report(Shell?.ShowInFileManager(single.FullPath), "ShowInFolderFailed")));
        }
        return menu;
    }

    private List<Control> BackgroundMenu()
    {
        if (!InFolder) return [Item("BrowseRefresh", () => _ = ReloadAsync(), new KeyGesture(Key.F5))];

        var folder = CurrentFolder!;
        var menu = new List<Control>();
        if (ClipboardHasFiles())
            menu.Add(Item("BrowsePaste", () => Paste(folder), new KeyGesture(Key.V, KeyModifiers.Control)));
        menu.Add(Item("BrowseNewFolder", NewFolder, new KeyGesture(Key.N, KeyModifiers.Control | KeyModifiers.Shift)));
        menu.Add(Item("BrowseRefresh", () => _ = ReloadAsync(), new KeyGesture(Key.F5)));

        menu.Add(new Separator());
        menu.Add(Item("BrowseSearchBelow", RequestSearch));
        menu.Add(Item("ContextMenuOrganize", () => OrganizeRequested?.Invoke(folder)));
        menu.Add(Item("BrowseCopyPath", () => _ = CopyPathsAsync(), new KeyGesture(Key.C, KeyModifiers.Control | KeyModifiers.Shift)));

        menu.Add(new Separator());
        menu.Add(Item("ContextMenuOpenInTerminal", () => Report(Shell?.OpenInTerminal(folder), "TerminalOpenFailed")));
        foreach (var editor in Editors.Where(ed => EditorTargetResolver.IsCompatible(ed, folder)))
        {
            var menuItem = new MenuItem { Header = editor.Header };
            menuItem.Click += (_, _) => Report(Shell?.OpenInEditor(editor, folder), "EditorOpenFailed", editor.Header);
            menu.Add(menuItem);
        }
        menu.Add(Item("BrowseOpenInExplorer", () => Report(Shell?.OpenPath(folder), "FileOpenFailed")));

        var hidden = new MenuItem
        {
            Header = Loc.Get("BrowseShowHidden"),
            ToggleType = MenuItemToggleType.CheckBox,
            IsChecked = Settings.ShowHiddenFiles,
            InputGesture = new KeyGesture(Key.H, KeyModifiers.Control)
        };
        hidden.Click += async (_, _) => await SetShowHiddenAsync(!Settings.ShowHiddenFiles);
        menu.Add(new Separator());
        menu.Add(hidden);
        return menu;
    }

    private static MenuItem Item(string key, Action action, KeyGesture? gesture = null)
    {
        var item = new MenuItem { Header = Loc.Get(key), InputGesture = gesture };
        item.Click += (_, _) => action();
        return item;
    }

    // =====================================================================
    // File operations
    // =====================================================================

    private void StartJob(string flag, IReadOnlyList<string> paths)
    {
        if (JobLauncher.Start(flag, paths) is { } ex)
            SetMessage(() => $"{Loc.Get("JobStartFailed")}: {ex.Message}");
    }

    private bool ClipboardHasFiles() => _clipboard?.GetFiles() is not null;

    private void PutOnClipboard(bool cut)
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0) return;

        if (_clipboard?.SetFiles(new ClipboardFiles(selected.Select(r => r.FullPath).ToList(), cut)) != true)
        {
            SetMessage(() => Loc.Get("BrowseClipboardFailed"));
            return;
        }

        _cutPaths = cut ? selected.Select(r => r.FullPath).ToHashSet(PathUtil.Comparer) : new HashSet<string>(PathUtil.Comparer);
        foreach (var row in _rows) row.IsCut = _cutPaths.Contains(row.FullPath);

        var count = selected.Count;
        var name = selected[0].Name;
        SetMessage(() => count == 1
            ? Loc.Get(cut ? "BrowseCutOne" : "BrowseCopiedOne", name)
            : Loc.Get(cut ? "BrowseCutMany" : "BrowseCopiedMany", count));
    }

    /// <summary>Rows stay faded only while their cut is still what the clipboard holds.</summary>
    private void RefreshCutMarks()
    {
        if (_cutPaths.Count == 0) return;
        var files = _clipboard?.GetFiles();
        if (files is { IsCut: true } && files.Paths.All(_cutPaths.Contains)) return;
        _cutPaths = new HashSet<string>(PathUtil.Comparer);
    }

    /// <summary>The same job "Paste with Grepdesk" starts from Explorer: it reads the clipboard itself.</summary>
    private void Paste(string destination)
    {
        if (!ClipboardHasFiles())
        {
            SetMessage(() => Loc.Get("JobClipboardEmpty"));
            return;
        }
        StartJob("--paste", [destination]);
    }

    private async Task CopyPathsAsync()
    {
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is null) return;

        var paths = SelectedRows.Select(r => r.FullPath).ToList();
        if (paths.Count == 0 && InFolder) paths.Add(CurrentFolder!);
        // Quoted like Explorer's "Copy as path", so a path with spaces pastes into a terminal as one argument.
        await clipboard.SetTextAsync(string.Join(Environment.NewLine, paths.Select(p => $"\"{p}\"")));
        SetMessage(() => Loc.Get("PathCopied"));
    }

    private async Task TrashSelectionAsync()
    {
        var selected = SelectedRows.ToList();
        if (selected.Count == 0 || Shell is null || TopLevel.GetTopLevel(this) is not Window window) return;

        var first = selected[0];
        var message = selected.Count == 1
            ? Loc.Get(first.IsDirectory ? "TrashConfirmFolder" : "TrashConfirmFile", first.Name)
            : Loc.Get("BrowseTrashConfirmMany", selected.Count);
        var detail = selected.Count == 1 ? first.FullPath : CurrentFolder!;
        if (!await ConfirmDialog.ShowAsync(window, Loc.Get("TrashConfirmTitle"), message, detail,
                Loc.Get("TrashConfirmButton"), Loc.Get("JobCancel")))
            return;

        // Keep the place in the list: select what comes after the deleted rows.
        var after = _rows.Skip(_rows.IndexOf(selected[^1]) + 1).FirstOrDefault(r => !selected.Contains(r))
                    ?? _rows.TakeWhile(r => r != first).LastOrDefault();

        var removed = new List<string>();
        foreach (var row in selected)
        {
            var result = Shell.MoveToTrash(row.FullPath);
            if (!result.IsSuccess)
            {
                Report(result, "TrashFailed");
                break;
            }
            removed.Add(row.FullPath);
        }
        if (removed.Count == 0) return;

        Removed?.Invoke(removed);
        var count = removed.Count;
        var name = first.Name;
        if (count == selected.Count)
            SetMessage(() => count == 1 ? Loc.Get("MovedToTrash", name) : Loc.Get("BrowseTrashedMany", count));
        if (after is not null) _selectAfterLoad = after.FullPath;
        await ReloadAsync();
        FocusList();
    }

    private void NewFolder()
    {
        if (!InFolder) return;
        try
        {
            var path = FileNames.CreateFolder(CurrentFolder!, Loc.Get("BrowseNewFolderName"));
            _selectAfterLoad = path;
            _renameAfterLoad = true;
            FilterBox.Text = ""; // a filter would hide the new folder
            _ = ReloadAsync();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetMessage(() => $"{Loc.Get("BrowseNewFolderFailed")}: {ex.Message}");
        }
    }

    // ---- inline rename ----

    private void StartRename(BrowseRow row)
    {
        CommitRename();
        _renaming = row;
        row.IsRenaming = true;
        FileList.ScrollIntoView(row);

        // The text box exists once the row's container has laid out with IsRenaming on.
        Dispatcher.UIThread.Post(() =>
        {
            if (_renaming != row) return;
            var box = (FileList.ContainerFromItem(row) as Visual)?.GetVisualDescendants().OfType<TextBox>().FirstOrDefault();
            if (box is null)
            {
                CancelRename();
                return;
            }
            _renameBox = box;
            box.Text = row.Name;
            box.Focus();
            // Like Explorer: the name is selected, the extension is not.
            var stem = row.IsDirectory ? row.Name.Length : Path.GetFileNameWithoutExtension(row.Name).Length;
            box.SelectionStart = 0;
            box.SelectionEnd = stem;
            box.LostFocus -= OnRenameLostFocus;
            box.LostFocus += OnRenameLostFocus;
        }, DispatcherPriority.Loaded);
    }

    private void OnRenameLostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox box) box.LostFocus -= OnRenameLostFocus;
        CommitRename();
    }

    private void CommitRename()
    {
        if (_renaming is not { } row) return;
        _renaming = null;

        var newName = _renameBox?.Text ?? row.Name;
        _renameBox = null;
        row.IsRenaming = false;

        var result = FileNames.Rename(row.FullPath, newName);
        switch (result.Outcome)
        {
            case RenameOutcome.Renamed:
                if (row.IsDirectory) _history.Forget(row.FullPath);
                Removed?.Invoke([row.FullPath]);
                _selectAfterLoad = result.Path;
                _ = ReloadAsync();
                break;
            case RenameOutcome.Unchanged:
                break;
            case RenameOutcome.InvalidName:
                var problem = result.Problem;
                SetMessage(() => Loc.Get("BrowseRenameInvalid" + problem));
                break;
            case RenameOutcome.AlreadyExists:
                var name = newName.Trim();
                SetMessage(() => Loc.Get("BrowseRenameExists", name));
                break;
            default:
                var error = result.Error?.Message ?? result.Outcome.ToString();
                SetMessage(() => $"{Loc.Get("BrowseRenameFailed")}: {error}");
                break;
        }
    }

    private void CancelRename()
    {
        if (_renaming is not { } row) return;
        _renaming = null;
        _renameBox = null;
        row.IsRenaming = false;
    }

    private void Report(ShellActionResult? result, string failureKey, params object[] args)
    {
        if (result is not { IsSuccess: false } failed) return;
        var prefix = Loc.Get(failureKey, args);
        var message = failed.Exception is not null ? $"{prefix}: {failed.Exception.Message}" : $"{prefix} ({failed.Status})";
        SetMessage(() => message);
    }
}
