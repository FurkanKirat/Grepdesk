using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.VisualTree;
using Grepdesk.Core;
using Grepdesk.Core.Cleanup;
using Grepdesk.UI.DiskUsage;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Cleanup;

/// <summary>
/// "Free up space": every way to get space back in one list, each with what it
/// is, whether it can be undone, and the items to pick from. Uses the same
/// drive scan as the Disk usage page.
/// </summary>
public partial class CleanupView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private const long DuplicateMinSize = 1024 * 1024;
    private const int MaxDuplicateGroups = 300;

    private DriveScans? _scans;
    private FileIndex? _index;
    private string? _root;
    private List<CleanupSuggestion> _suggestions = [];
    private List<DuplicateGroup>? _duplicates;
    private long _trashBytes;
    private List<SuggestionRow> _rows = [];
    private CancellationTokenSource _cts = new();
    private int _scanProgress;
    private bool _updating;

    public IPlatformShell? Shell { get; set; }

    /// <summary>Asks the window to open a folder on the Organize page (old downloads).</summary>
    public event Action<string>? OrganizeRequested;

    /// <summary>Raised after files were deleted, so other views can drop them.</summary>
    public event Action<IReadOnlyList<string>>? Removed;

    public DriveScans? Scans
    {
        get => _scans;
        set
        {
            _scans = value;
            if (value is null) return;
            value.Progress += count =>
            {
                if (!CancelButton.IsVisible) return;
                _scanProgress = count;
                StatusText.Text = Loc.Get("DiskScanning", count);
            };
        }
    }

    public CleanupView()
    {
        InitializeComponent();

        ScanButton.Click += async (_, _) => await ScanAsync();
        CancelButton.Click += (_, _) => _cts.Cancel();
        SuggestionList.SelectionChanged += (_, _) => ShowDetail();
        SelectAllButton.Click += (_, _) => SetAll(true);
        SelectNoneButton.Click += (_, _) => SetAll(false);
        ActionButton.Click += async (_, _) => await ActAsync();
        FindDuplicatesButton.Click += async (_, _) => await FindDuplicatesAsync();
        AskButton.Click += async (_, _) => await AskAiAsync();
        DriveBox.SelectionChanged += (_, _) => { if (!_updating) UpdateScanButton(); };

        // Look before deleting: row buttons, double-click, right-click.
        ItemList.AddHandler(Button.ClickEvent, OnRowButtonClick);
        ItemList.DoubleTapped += (_, e) =>
        {
            // Double-clicking the checkbox or a row button is not "show me this".
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (ItemList.SelectedItem is ItemRow { Item: { } item }) ShowInFolder(item.Path);
        };
        ItemList.PointerReleased += OnItemRightClick;

        ApplyLanguage();
    }

    private string? SelectedRoot => (DriveBox.SelectedItem as DriveRow)?.Root;
    private SuggestionRow? Selected => SuggestionList.SelectedItem as SuggestionRow;

    public void ApplyLanguage()
    {
        TitleText.Text = Loc.Get("CleanupTab");
        SubtitleText.Text = Loc.Get("CleanupSubtitle");
        DriveLabel.Text = Loc.Get("CleanupDrive");
        CancelButton.Content = Loc.Get("JobCancel");
        EmptyText.Text = Loc.Get("CleanupEmpty");
        SelectAllButton.Content = Loc.Get("CleanupSelectAll");
        SelectNoneButton.Content = Loc.Get("CleanupSelectNone");
        FindDuplicatesButton.Content = Loc.Get("CleanupFindDuplicates");
        AskButton.Content = Loc.Get("AskButton");
        ToolTip.SetTip(AskButton, Loc.Get("AskButtonTip"));

        _updating = true;
        var rows = DriveRow.LoadAll();
        var selected = SelectedRoot;
        DriveBox.ItemsSource = rows;
        DriveBox.SelectedItem = DriveRow.Pick(rows, selected);
        _updating = false;

        UpdateScanButton();

        // Relabel the cards in place: rebuilding them from the analysis would
        // bring back items that have been deleted since.
        foreach (var row in _rows)
        {
            row.Title = Loc.Get("Cleanup" + row.Kind);
            Refresh(row);
        }
        if (Selected is not null) ShowDetail();
    }

    private void UpdateScanButton()
    {
        ScanButton.Content = Loc.Get(_root is not null && SelectedRoot == _root ? "CleanupRescan" : "CleanupScan");
        ScanButton.IsEnabled = !CancelButton.IsVisible && SelectedRoot is not null;
    }

    // =====================================================================
    // Scan + analysis
    // =====================================================================

    private async Task ScanAsync()
    {
        if (SelectedRoot is not { } root || _scans is not { } scans) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        CancelButton.IsVisible = true;
        UpdateScanButton();

        try
        {
            var rescan = _root == root;
            var index = rescan ? null : scans.TryGet(root);
            if (index is null)
            {
                _scanProgress = 0;
                StatusText.Text = Loc.Get("DiskScanning", 0);
                index = await scans.ScanAsync(root, token);
            }

            StatusText.Text = Loc.Get("CleanupAnalyzing");
            var ctx = CleanupContext.ForCurrentUser();
            _suggestions = await Task.Run(() => CleanupAnalyzer.Analyze(index, root, ctx, token), token);
            _trashBytes = Shell?.GetTrashSize() ?? 0;
            _duplicates = null;
            _index = index;
            _root = root;

            BuildRows();
            StatusText.Text = Loc.Get("CleanupReady");
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.Get("DiskCancelled");
        }
        finally
        {
            CancelButton.IsVisible = false;
            UpdateScanButton();
        }
    }

    /// <summary>(Re)builds the cards from the analysis, keeping the selected card.</summary>
    private void BuildRows()
    {
        var selectedKind = Selected?.Kind;
        var rows = new List<SuggestionRow>();

        foreach (var s in _suggestions)
        {
            var row = NewRow(s.Kind, s.Action);
            var canCheck = s.Action is CleanupAction.DeletePermanently or CleanupAction.MoveToTrash;
            row.Items = s.Items.Select(i => ItemRow.For(i, canCheck, ItemDetail(i))).ToList();
            row.TotalItems = s.TotalItems;
            row.TotalBytes = s.TotalBytes;
            rows.Add(row);
        }

        if (_trashBytes > 0)
            rows.Add(NewRow(CleanupKind.RecycleBin, CleanupAction.EmptyTrash, fixedBytes: _trashBytes));

        var duplicates = NewRow(CleanupKind.Duplicates, CleanupAction.MoveToTrash);
        if (_duplicates is not null) duplicates.Items = DuplicateRows(_duplicates);
        rows.Add(duplicates);

        if (OperatingSystem.IsWindows())
            rows.Add(NewRow(CleanupKind.SystemTools, CleanupAction.None));

        // Biggest wins first; cards that only explain go last.
        _rows = rows
            .OrderBy(r => r.Action == CleanupAction.None)
            .ThenBy(r => r.Kind == CleanupKind.Duplicates && _duplicates is null)
            .ThenByDescending(r => r.Bytes)
            .ToList();
        foreach (var row in _rows) Refresh(row);

        EmptyState.IsVisible = false;
        Body.IsVisible = true;
        SuggestionList.ItemsSource = _rows;
        SuggestionList.SelectedItem = _rows.FirstOrDefault(r => r.Kind == selectedKind) ?? _rows.FirstOrDefault();
        UpdateTotal();
    }

    private SuggestionRow NewRow(CleanupKind kind, CleanupAction action, long? fixedBytes = null) => new()
    {
        Kind = kind,
        Action = action,
        Title = Loc.Get("Cleanup" + kind),
        FixedBytes = fixedBytes,
    };

    private string ItemDetail(CleanupItem item)
    {
        var where = Path.GetDirectoryName(item.Path) ?? "";
        var detail = item.LastModified == default ? where : Loc.Get("CleanupItemDetail", where, item.LastModified.ToString("d"));
        return item.Note is null ? detail : Loc.Get("CleanupNote" + item.Note) + "  ·  " + detail;
    }

    private List<ItemRow> DuplicateRows(List<DuplicateGroup> groups)
    {
        var rows = new List<ItemRow>();
        var groupId = 0;
        foreach (var group in groups.Take(MaxDuplicateGroups))
        {
            rows.Add(ItemRow.Header(Loc.Get("CleanupDuplicateHeader", group.Files.Count, Format.Size(group.FileSize), Format.Size(group.WastedBytes))));

            // Keep the oldest copy (most likely the original), then the shortest path.
            var keep = group.Files.OrderBy(f => f.Modified).ThenBy(f => f.FullPath.Length).First();
            foreach (var f in group.Files.OrderBy(f => f != keep).ThenBy(f => f.FullPath))
            {
                var item = new CleanupItem(f.FullPath, f.Size, false, f.Modified, SelectedByDefault: f != keep, Group: groupId);
                var detail = ItemDetail(item) + (f == keep ? "  ·  " + Loc.Get("CleanupKeep") : "");
                rows.Add(ItemRow.For(item, canCheck: true, detail));
            }
            groupId++;
        }
        return rows;
    }

    // =====================================================================
    // Details panel
    // =====================================================================

    private void ShowDetail()
    {
        if (Selected is not { } row) return;

        DetailTitle.Text = row.Title;
        DetailText.Text = Loc.Get("CleanupText" + row.Kind);

        var (noticeKey, color) = row.Action switch
        {
            CleanupAction.DeletePermanently => ("CleanupNoticePermanent", "#f9e2af"),
            CleanupAction.MoveToTrash => ("CleanupNoticeTrash", "#89b4fa"),
            CleanupAction.EmptyTrash => ("CleanupNoticeEmptyTrash", "#f9e2af"),
            _ => (null, "#6c7086"),
        };
        NoticeBox.IsVisible = noticeKey is not null;
        if (noticeKey is not null)
        {
            NoticeText.Text = Loc.Get(noticeKey);
            NoticeText.Foreground = Brush.Parse(color);
            NoticeBox.BorderBrush = Brush.Parse(color);
            NoticeBox.Background = new SolidColorBrush(Color.Parse(color), 0.08);
        }

        foreach (var item in row.Items) item.CheckedChanged = () => { Refresh(row); UpdateActionButton(row); };
        ItemList.ItemsSource = row.Items;

        var checkable = row.Items.Any(i => i.CanCheck);
        SelectionBar.IsVisible = checkable;
        ItemList.IsVisible = row.Items.Count > 0;

        FindDuplicatesButton.IsVisible = row.Kind == CleanupKind.Duplicates;
        FindDuplicatesButton.Content = Loc.Get(_duplicates is null ? "CleanupFindDuplicates" : "CleanupFindDuplicatesAgain");

        // Only where the path itself says what the item is (caches, temp, system).
        // For the user's own files the names are hidden for privacy, so an AI
        // would be guessing; and whether two files are identical is already
        // known exactly from their hashes.
        AskButton.IsVisible = row.Kind is not (CleanupKind.Duplicates or CleanupKind.OldDownloads or CleanupKind.LargeOldFiles);

        ShowTools(row.Kind);
        UpdateActionButton(row);
    }

    private void ShowTools(CleanupKind kind)
    {
        var tools = kind switch
        {
            CleanupKind.SystemTools =>
            [
                ("CleanupToolDiskCleanup", (Action)(() => Launch("cleanmgr.exe", $"/d {_root?[0]}"))),
                ("CleanupToolStorageSense", () => Launch("ms-settings:storagesense")),
                ("CleanupToolRestorePoints", () => Launch("SystemPropertiesProtection.exe")),
            ],
            CleanupKind.Hibernation => [("CleanupToolHibernateOff", (Action)(async () => await TurnOffHibernationAsync()))],
            // A pile of old downloads is easier to judge in groups (series, site, session).
            CleanupKind.OldDownloads => [("CleanupToolOrganize", (Action)(() => OrganizeRequested?.Invoke(KnownFolders.Downloads())))],
            _ => Array.Empty<(string, Action)>(),
        };

        var buttons = new[] { ToolButton1, ToolButton2, ToolButton3 };
        for (var i = 0; i < buttons.Length; i++)
        {
            var button = buttons[i];
            button.IsVisible = i < tools.Length;
            button.Tag = i < tools.Length ? tools[i].Item2 : null;
            if (i < tools.Length) button.Content = Loc.Get(tools[i].Item1);
            button.Click -= OnToolClick;
            button.Click += OnToolClick;
        }
    }

    private static void OnToolClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is Action action) action();
    }

    private void UpdateActionButton(SuggestionRow row)
    {
        var selected = row.SelectedBytes;
        var selectedCount = row.Items.Count(i => i.IsChecked);
        SelectionText.Text = Loc.Get("CleanupSelection", selectedCount, Format.Size(selected));

        switch (row.Action)
        {
            case CleanupAction.DeletePermanently:
                ActionButton.IsVisible = true;
                ActionButton.Content = Loc.Get("CleanupActionDelete", Format.Size(selected));
                ActionButton.IsEnabled = selectedCount > 0;
                break;
            case CleanupAction.MoveToTrash:
                ActionButton.IsVisible = row.Items.Count > 0;
                ActionButton.Content = Loc.Get("CleanupActionTrash", Format.Size(selected));
                ActionButton.IsEnabled = selectedCount > 0;
                break;
            case CleanupAction.EmptyTrash:
                ActionButton.IsVisible = true;
                ActionButton.Content = Loc.Get("CleanupActionEmptyTrash", Format.Size(row.Bytes));
                ActionButton.IsEnabled = row.Bytes > 0;
                break;
            default:
                ActionButton.IsVisible = false;
                break;
        }
    }

    private void SetAll(bool value)
    {
        if (Selected is not { } row) return;
        foreach (var item in row.Items.Where(i => i.CanCheck)) item.IsChecked = value;
    }

    private void Refresh(SuggestionRow row)
    {
        row.SizeText = row.Kind == CleanupKind.Duplicates && _duplicates is null ? "?"
            : row.Action == CleanupAction.None && row.Bytes == 0 ? ""
            : Format.Size(row.Bytes);

        var items = row.Items.Count(i => !i.IsHeader);
        row.Summary = row switch
        {
            { Kind: CleanupKind.Duplicates } when _duplicates is null => Loc.Get("CleanupDuplicatesNotSearched"),
            { Kind: CleanupKind.Duplicates } => Loc.Get("CleanupDuplicatesSummary", _duplicates!.Count, row.Items.Count(i => i.IsChecked)),
            { Action: CleanupAction.EmptyTrash } => Loc.Get("CleanupTrashSummary"),
            { Kind: CleanupKind.Hibernation } => Loc.Get("CleanupHibernationSummary"),
            { Action: CleanupAction.None } => Loc.Get("CleanupInfoSummary"),
            // Only the largest part is listed: say how much there is in all.
            _ when row.TotalItems > items => Loc.Get("CleanupSummaryCapped", items, row.TotalItems, Format.Size(row.TotalBytes),
                row.Items.Count(i => i.IsChecked)),
            _ => Loc.Get("CleanupSummary", items, row.Items.Count(i => i.IsChecked)),
        };
        UpdateTotal();
    }

    private void UpdateTotal()
    {
        var possible = _rows.Where(r => r.Action != CleanupAction.None).Sum(r => r.Bytes);
        var selected = _rows.Sum(r => r.Action == CleanupAction.EmptyTrash ? r.Bytes : r.SelectedBytes);
        TotalText.Text = Loc.Get("CleanupTotal", Format.Size(possible));
        TotalHint.Text = Loc.Get("CleanupTotalHint", Format.Size(selected));
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private async Task ActAsync()
    {
        if (Selected is not { } row || Shell is not { } shell) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var items = row.Items.Where(i => i.IsChecked && i.Item is not null).Select(i => i.Item!).ToList();
        var (messageKey, count, bytes) = row.Action switch
        {
            CleanupAction.EmptyTrash => ("CleanupConfirmEmptyTrash", 0, row.Bytes),
            CleanupAction.DeletePermanently => ("CleanupConfirmDelete", items.Count, items.Sum(i => i.Bytes)),
            _ => ("CleanupConfirmTrash", items.Count, items.Sum(i => i.Bytes)),
        };

        var confirmed = await ConfirmDialog.ShowAsync(owner,
            row.Title,
            Loc.Get(messageKey, count, Format.Size(bytes)),
            NoticeText.Text ?? "",
            Loc.Get(row.Action switch
            {
                CleanupAction.EmptyTrash => "CleanupConfirmEmptyTrashButton",
                CleanupAction.DeletePermanently => "CleanupConfirmDeleteButton",
                _ => "TrashConfirmButton",
            }),
            Loc.Get("JobCancel"));
        if (!confirmed) return;

        ActionButton.IsEnabled = false;
        StatusText.Text = Loc.Get("CleanupWorking");

        if (row.Action == CleanupAction.EmptyTrash)
        {
            var emptied = await Task.Run(shell.EmptyTrash);
            StatusText.Text = emptied.IsSuccess ? Loc.Get("CleanupFreed", Format.Size(row.Bytes)) : Loc.Get("CleanupFailed");
            UpsertTrashRow();
            UpdateActionButton(row);
            RefreshDrives();
            return;
        }

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        CancelButton.IsVisible = true;
        ScanButton.IsEnabled = false;
        SuggestionList.IsEnabled = false;

        var total = items.Sum(i => i.Bytes);
        var verb = row.Action == CleanupAction.MoveToTrash ? "CleanupTrashing" : "CleanupDeleting";
        var progress = new Progress<CleanupProgress>(p =>
            StatusText.Text = Loc.Get(verb, Format.Size(p.FreedBytes), Format.Size(total), p.ItemsDone, p.ItemCount));

        try
        {
            // Everything heavy stays off the UI thread: the deleting itself, and
            // dropping the deleted paths from a multi-million-entry index.
            var executor = new CleanupExecutor(shell);
            var index = _index;
            var result = await Task.Run(() =>
            {
                var r = executor.Run(row.Action, items, token, progress);
                index?.RemoveMany(
                    removed: r.Done.Where(i => !i.ContentsOnly).Select(i => i.Path),
                    emptied: r.Done.Where(i => i.ContentsOnly).Select(i => i.Path));
                return r;
            });

            row.Items = ApplyOutcomes(row, result.Outcomes);
            Removed?.Invoke(result.Done.Select(i => i.Path).ToList());

            var message = row.Action == CleanupAction.MoveToTrash
                ? Loc.Get("CleanupMovedToTrash", Format.Size(result.FreedBytes))
                : Loc.Get("CleanupFreed", Format.Size(result.FreedBytes));
            if (result.SkippedFiles > 0) message += " " + Loc.Get("CleanupSkipped", result.SkippedFiles);
            if (result.Cancelled) message = Loc.Get("CleanupStopped") + " " + message;
            StatusText.Text = message;

            // Trashed files now sit in the bin: show it so the space can actually be reclaimed.
            if (row.Action == CleanupAction.MoveToTrash) UpsertTrashRow();

            ShowDetail();
            Refresh(row);
            RefreshDrives();
        }
        finally
        {
            CancelButton.IsVisible = false;
            SuggestionList.IsEnabled = true;
            UpdateScanButton();
        }
    }

    /// <summary>Adds the Recycle Bin card, or updates its size, after the bin changed.</summary>
    private void UpsertTrashRow()
    {
        _trashBytes = Shell?.GetTrashSize() ?? _trashBytes;
        var trash = _rows.FirstOrDefault(r => r.Kind == CleanupKind.RecycleBin);
        if (trash is null)
        {
            if (_trashBytes <= 0) return;
            trash = NewRow(CleanupKind.RecycleBin, CleanupAction.EmptyTrash, _trashBytes);
            var selected = Selected;
            _rows.Insert(0, trash);
            SuggestionList.ItemsSource = null;
            SuggestionList.ItemsSource = _rows;
            SuggestionList.SelectedItem = selected;
        }
        trash.FixedBytes = _trashBytes;
        Refresh(trash);
    }

    /// <summary>
    /// Removes finished items; items with files left behind (in use) stay, with
    /// what is left of their size.
    /// </summary>
    private static List<ItemRow> ApplyOutcomes(SuggestionRow row, IReadOnlyList<ItemOutcome> outcomes)
    {
        var byItem = outcomes.ToDictionary(o => o.Item);
        var kept = new List<ItemRow>();
        foreach (var r in row.Items)
        {
            if (r.IsHeader || r.Item is null || !byItem.TryGetValue(r.Item, out var outcome)) { kept.Add(r); continue; }
            if (outcome.Complete) continue;
            kept.Add(outcome.Freed > 0 ? r.WithItem(r.Item with { Bytes = Math.Max(0, r.Item.Bytes - outcome.Freed) }) : r);
        }
        return row.Kind == CleanupKind.Duplicates ? DropBrokenGroups(kept) : kept;
    }

    private static List<ItemRow> DropBrokenGroups(List<ItemRow> kept)
    {
        // A duplicate group with one file left isn't a duplicate any more.
        var result = new List<ItemRow>();
        for (var i = 0; i < kept.Count; i++)
        {
            if (!kept[i].IsHeader) continue;
            var members = kept.Skip(i + 1).TakeWhile(r => !r.IsHeader).ToList();
            if (members.Count < 2) continue;
            result.Add(kept[i]);
            result.AddRange(members);
        }
        return result;
    }

    private async Task FindDuplicatesAsync()
    {
        if (_index is not { } index || _root is not { } root) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        CancelButton.IsVisible = true;
        FindDuplicatesButton.IsEnabled = false;
        UpdateScanButton();

        try
        {
            StatusText.Text = Loc.Get("CleanupDuplicatesPreparing");
            var candidates = await Task.Run(() => DuplicateFinder.UserFileCandidates(index, root, DuplicateMinSize, token), token);

            var progress = new Progress<(long Done, long Total)>(p =>
                StatusText.Text = Loc.Get("CleanupDuplicatesProgress",
                    p.Total > 0 ? (double)p.Done / p.Total : 0, Format.Size(p.Done), Format.Size(p.Total)));
            _duplicates = await Task.Run(() => DuplicateFinder.FindAsync(candidates, DuplicateMinSize, progress, token), token);

            StatusText.Text = Loc.Get("CleanupDuplicatesDone", _duplicates.Count, Format.Size(_duplicates.Sum(g => g.WastedBytes)));
            if (_rows.FirstOrDefault(r => r.Kind == CleanupKind.Duplicates) is { } duplicateRow)
            {
                duplicateRow.Items = DuplicateRows(_duplicates);
                Refresh(duplicateRow);
                SuggestionList.SelectedItem = duplicateRow;
                ShowDetail();
            }
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.Get("DiskCancelled");
        }
        finally
        {
            CancelButton.IsVisible = false;
            FindDuplicatesButton.IsEnabled = true;
            UpdateScanButton();
        }
    }

    private async Task TurnOffHibernationAsync()
    {
        if (TopLevel.GetTopLevel(this) is not Window owner) return;
        var confirmed = await ConfirmDialog.ShowAsync(owner, Loc.Get("CleanupHibernation"),
            Loc.Get("CleanupHibernateConfirm"), "powercfg /hibernate off",
            Loc.Get("CleanupToolHibernateOff"), Loc.Get("JobCancel"));
        if (!confirmed) return;

        try
        {
            // Needs an administrator: Windows shows its own permission prompt.
            using var process = Process.Start(new ProcessStartInfo("powercfg", "/hibernate off")
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            if (process is not null) await process.WaitForExitAsync();
            StatusText.Text = process?.ExitCode == 0 ? Loc.Get("CleanupHibernateDone") : Loc.Get("CleanupFailed");
        }
        catch (Win32Exception)
        {
            StatusText.Text = Loc.Get("CleanupHibernateCancelled"); // the permission prompt was declined
        }
    }

    private void Launch(string target, string? arguments = null)
    {
        try
        {
            Process.Start(new ProcessStartInfo(target, arguments ?? "") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText.Text = $"{Loc.Get("CleanupFailed")}: {ex.Message}";
        }
    }

    private void RefreshDrives()
    {
        _updating = true;
        var selected = SelectedRoot;
        var rows = DriveRow.LoadAll();
        DriveBox.ItemsSource = rows;
        DriveBox.SelectedItem = DriveRow.Pick(rows, selected);
        _updating = false;
    }

    // =====================================================================
    // Looking at an item before deleting it
    // =====================================================================

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: ItemRow { Item: { } item } }) return;
        e.Handled = true;
        if (action == "show") ShowInFolder(item.Path);
        else Open(item.Path);
    }

    private void OnItemRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (e.Source is not Control { DataContext: ItemRow { Item: { } item } row }) return;
        ItemList.SelectedItem = row;

        var open = new MenuItem { Header = Loc.Get("ContextMenuOpen") };
        open.Click += (_, _) => Open(item.Path);
        var show = new MenuItem { Header = Loc.Get("ContextMenuShowInFolder") };
        show.Click += (_, _) => ShowInFolder(item.Path);
        var copy = new MenuItem { Header = Loc.Get("ContextMenuCopyPath") };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(item.Path);
        };

        new ContextMenu { ItemsSource = new Control[] { open, show, new Separator(), copy } }.Open(ItemList);
    }

    private void ShowInFolder(string path)
    {
        var result = Shell?.ShowInFileManager(path);
        if (result is { IsSuccess: false }) StatusText.Text = Loc.Get("ShowInFolderFailed");
    }

    private void Open(string path)
    {
        var result = Shell?.OpenPath(path);
        if (result is { IsSuccess: false }) StatusText.Text = Loc.Get("FileOpenFailed");
    }

    // =====================================================================
    // "Is it safe to delete?" prompt for an AI chat
    // =====================================================================

    private const int MaxPromptItems = 40;

    // Windows 11 still reports itself as 10.0; build 22000 and later is 11.
    private static string OsName()
    {
        if (!OperatingSystem.IsWindows()) return RuntimeInformation.OSDescription;
        var build = Environment.OSVersion.Version.Build;
        return $"Windows {(build >= 22000 ? 11 : 10)} (build {build})";
    }

    private async Task AskAiAsync()
    {
        if (Selected is not { } row || TopLevel.GetTopLevel(this) is not Window owner) return;
        await AskAiDialog.ShowAsync(owner, BuildPrompt(row));
    }

    /// <summary>
    /// A question about the selected items (or the largest listed, if none are
    /// selected), with paths anonymized: see <see cref="PathAnonymizer"/>.
    /// </summary>
    private string BuildPrompt(SuggestionRow row)
    {
        var anonymizer = PathAnonymizer.ForCurrentUser();
        var all = row.Items.Where(i => !i.IsHeader && i.Item is not null).Select(i => i.Item!).ToList();
        var chosen = all.Where(i => row.Items.Any(r => r.Item == i && r.IsChecked)).ToList();
        if (chosen.Count == 0) chosen = all;

        var sb = new StringBuilder();
        sb.AppendLine(Loc.Get("AskPromptIntro", OsName()));
        sb.AppendLine();
        sb.AppendLine(Loc.Get("AskPromptCategory", row.Title, Loc.Get("CleanupText" + row.Kind)));
        sb.AppendLine(Loc.Get("AskPromptAction" + row.Action));
        sb.AppendLine();

        if (chosen.Count == 0)
        {
            // Info cards (hibernation, Windows tools) and the Recycle Bin have no item list.
            if (row.Bytes > 0) sb.AppendLine(Loc.Get("AskPromptSize", Format.Size(row.Bytes)));
        }
        else
        {
            foreach (var item in chosen.OrderByDescending(i => i.Bytes).Take(MaxPromptItems))
            {
                var note = item.Note is null ? "" : $" — {Loc.Get("CleanupNote" + item.Note)}";
                sb.AppendLine(Loc.Get("AskPromptItem",
                    anonymizer.Anonymize(item.Path),
                    Loc.Get(item.IsDirectory ? "AskPromptFolder" : "AskPromptFile"),
                    Format.Size(item.Bytes),
                    item.LastModified == default ? "?" : item.LastModified.ToString("yyyy-MM-dd")) + note);
            }
            if (chosen.Count > MaxPromptItems)
                sb.AppendLine(Loc.Get("AskPromptMore", chosen.Count - MaxPromptItems));
        }

        sb.AppendLine();
        sb.AppendLine(Loc.Get("AskPromptQuestion"));
        return sb.ToString();
    }
}
