using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Grepdesk.Core;
using Grepdesk.Core.Grouping;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Organize;

/// <summary>
/// "Organize": makes sense of a messy folder (Downloads, typically) by splitting
/// it into groups — name series, download site, type, download session,
/// probably-unneeded leftovers — and lets each group be tidied into a subfolder
/// (undoable) or moved to the Recycle Bin.
/// </summary>
public partial class OrganizeView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private string? _folder;
    private List<GroupMember> _members = [];
    private List<GroupRow> _groups = [];
    private GroupingMode _mode = GroupingMode.Series;
    private OrganizeResult? _lastMove;
    private CancellationTokenSource _cts = new();
    private bool _updating;

    public IPlatformShell? Shell { get; set; }

    public OrganizeView()
    {
        InitializeComponent();

        DownloadsButton.Click += async (_, _) => await OpenFolderAsync(KnownFolders.Downloads());
        ChooseButton.Click += async (_, _) => await ChooseFolderAsync();
        RefreshButton.Click += async (_, _) => await ReloadAsync();
        UndoButton.Click += async (_, _) => await UndoAsync();

        ModeList.SelectionChanged += async (_, _) =>
        {
            if (_updating || ModeList.SelectedIndex < 0) return;
            _mode = (GroupingMode)ModeList.SelectedIndex;
            await RegroupAsync(keepKey: null);
        };
        GroupList.SelectionChanged += (_, _) => ShowGroup();
        SelectAllButton.Click += (_, _) => SetAll(true);
        SelectNoneButton.Click += (_, _) => SetAll(false);
        MoveButton.Click += async (_, _) => await MoveIntoFolderAsync();
        FolderNameBox.TextChanged += (_, _) => UpdateActions();
        TrashButton.Click += async (_, _) => await TrashAsync();

        MemberList.AddHandler(Button.ClickEvent, OnRowButtonClick);
        MemberList.DoubleTapped += (_, e) =>
        {
            if ((e.Source as Visual)?.FindAncestorOfType<Button>(includeSelf: true) is not null) return;
            if (MemberList.SelectedItem is MemberRow row) ShowInFolder(row.Member.Path);
        };
        MemberList.PointerReleased += OnMemberRightClick;

        AddHandler(DragDrop.DragOverEvent, (_, e) =>
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None);
        AddHandler(DragDrop.DropEvent, async (_, e) =>
        {
            var path = e.DataTransfer.TryGetFiles()?.Select(i => i.TryGetLocalPath()).OfType<string>().FirstOrDefault();
            if (path is null) return;
            await OpenFolderAsync(Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        });

        ApplyLanguage();
    }

    private GroupRow? SelectedGroup => GroupList.SelectedItem as GroupRow;

    public void ApplyLanguage()
    {
        TitleText.Text = Loc.Get("OrganizeTab");
        SubtitleText.Text = Loc.Get("OrganizeSubtitle");
        FolderLabel.Text = Loc.Get("OrganizeFolder");
        FolderText.Text = _folder ?? Loc.Get("NotSelected");
        DownloadsButton.Content = Loc.Get("OrganizeDownloads");
        ChooseButton.Content = Loc.Get("ChooseFolder");
        RefreshButton.Content = Loc.Get("OrganizeRefresh");
        UndoButton.Content = Loc.Get("OrganizeUndo");
        EmptyText.Text = Loc.Get("OrganizeEmpty");
        SelectAllButton.Content = Loc.Get("CleanupSelectAll");
        SelectNoneButton.Content = Loc.Get("CleanupSelectNone");
        MoveButton.Content = Loc.Get("OrganizeMove");
        FolderNameBox.Watermark = Loc.Get("OrganizeFolderName");
        ToolTip.SetTip(MoveButton, Loc.Get("OrganizeMoveTip"));

        _updating = true;
        ModeList.ItemsSource = Enum.GetValues<GroupingMode>().Select(m => Loc.Get("OrganizeMode" + m)).ToList();
        ModeList.SelectedIndex = (int)_mode;
        _updating = false;

        if (_members.Count > 0) _ = RegroupAsync(SelectedGroup?.Group.Key);
    }

    /// <summary>Shows a folder's groups (from the Free up space page, a folder's context menu, a drop...).</summary>
    public async Task OpenFolderAsync(string folder)
    {
        if (!Directory.Exists(folder))
        {
            StatusText.Text = Loc.Get("OrganizeMissing");
            return;
        }
        _folder = folder;
        _lastMove = null;
        UndoButton.IsVisible = false;
        FolderText.Text = folder;
        RefreshButton.IsEnabled = true;
        await ReloadAsync();
    }

    private async Task ChooseFolderAsync()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } provider) return;
        var folders = await provider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.Get("OrganizeChooseTitle") });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is { } path)
            await OpenFolderAsync(path);
    }

    private async Task ReloadAsync(string? keepKey = null)
    {
        if (_folder is not { } folder) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        StatusText.Text = Loc.Get("OrganizeReading");

        try
        {
            _members = await Task.Run(() => FolderGrouper.ReadFolder(folder, token), token);
            await RegroupAsync(keepKey ?? SelectedGroup?.Group.Key);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = $"{Loc.Get("OrganizeReadFailed")}: {ex.Message}";
        }
    }

    private async Task RegroupAsync(string? keepKey)
    {
        var members = _members;
        var mode = _mode;
        // Source reads a small stream per file, so even this runs off the UI thread.
        var groups = await Task.Run(() => FolderGrouper.Group(members, mode));
        if (members != _members || mode != _mode) return; // something newer is on the way

        _groups = groups.Select(g => new GroupRow { Group = g, Title = TitleOf(g), Subtitle = SubtitleOf(g) }).ToList();

        EmptyState.IsVisible = false;
        Body.IsVisible = true;
        GroupList.ItemsSource = _groups;
        GroupList.SelectedItem = _groups.FirstOrDefault(g => g.Group.Key == keepKey) ?? _groups.FirstOrDefault();

        var grouped = groups.Where(g => !g.IsLeftover).Sum(g => g.Members.Count);
        SummaryText.Text = Loc.Get("OrganizeSummary", members.Count, Format.Size(members.Sum(m => m.Size)),
            groups.Count(g => !g.IsLeftover), grouped);
        StatusText.Text = groups.Count == 0 ? Loc.Get("OrganizeNothing" + mode) : "";
        if (groups.Count == 0) ShowGroup();
    }

    // =====================================================================
    // Labels
    // =====================================================================

    private static string TitleOf(FileGroup g) => g.Mode switch
    {
        GroupingMode.Series => g.Kind switch
        {
            SeriesKind.Family => FamilyTitle(g),
            SeriesKind.Rest => g.IsLeftover ? Loc.Get("OrganizeSeriesRest") : Loc.Get("OrganizeRestOf", TypeName(g.Label)),
            _ => SeriesTitle(g.Label),
        },
        GroupingMode.Source => g.IsLeftover ? Loc.Get("OrganizeSourceUnknown") : g.Label,
        GroupingMode.Type => Loc.Get("OrganizeType" + g.Label),
        GroupingMode.Session => SessionTitle(g),
        GroupingMode.Redundant => Loc.Get("OrganizeRedundant" + g.Label),
        _ => g.Label,
    };

    /// <summary>
    /// "dracula_idle_*.png" reads as "dracula_idle (.png)": the wildcard is how the
    /// group is found, not something people name things with. Names that are only
    /// numbers ("*.png") get a description instead.
    /// </summary>
    private static string SeriesTitle(string pattern)
    {
        var name = FolderOrganizer.SuggestName(pattern, isPattern: true);
        var ext = Path.GetExtension(pattern);
        if (name.Length == 0) return Loc.Get("OrganizeSeriesNumbered", ext.Length > 0 ? ext : Loc.Get("OrganizeTypeFolders"));
        return ext.Length > 0 ? $"{name} ({ext})" : name;
    }

    // "Lecture… (.pdf)": the shared beginning, then the extension.
    private static string FamilyTitle(FileGroup g)
    {
        var first = g.Members[0];
        var ext = first.IsDirectory ? "" : Path.GetExtension(first.Name);
        return ext.Length > 0 ? $"{g.Label}… ({ext})" : g.Label + "…";
    }

    // Lower-cased for "Other documents" / "Diğer belgeler".
    private static string TypeName(string bucket) =>
        Loc.Get("OrganizeType" + bucket).ToLower(System.Globalization.CultureInfo.CurrentUICulture);

    private static string SessionTitle(FileGroup g)
    {
        if (g.Start is not { } start || g.End is not { } end) return "";
        if (end - start < TimeSpan.FromMinutes(1)) return start.ToString("g");
        return start.Date == end.Date ? $"{start:g} – {end:t}" : $"{start:g} – {end:g}";
    }

    private static string SubtitleOf(FileGroup g) =>
        g.Members.Count == 1 ? Loc.Get("OrganizeOneItem") : Loc.Get("OrganizeItems", g.Members.Count);

    private static string DescriptionOf(FileGroup g) => g.Mode switch
    {
        GroupingMode.Series => g.Kind switch
        {
            SeriesKind.Family => Loc.Get("OrganizeTextFamily", g.Label),
            SeriesKind.Rest => Loc.Get(g.IsLeftover ? "OrganizeTextSeriesRest" : "OrganizeTextRest"),
            _ => Loc.Get("OrganizeTextSeries", g.Label),
        },
        GroupingMode.Source => g.IsLeftover ? Loc.Get("OrganizeTextSourceUnknown") : Loc.Get("OrganizeTextSource", g.Label),
        GroupingMode.Type => Loc.Get("OrganizeTextType"),
        GroupingMode.Session => Loc.Get("OrganizeTextSession"),
        GroupingMode.Redundant => Loc.Get("OrganizeTextRedundant" + g.Label),
        _ => "",
    };

    /// <summary>The name a group's subfolder gets by default.</summary>
    private static string FolderNameFor(FileGroup g) => g.IsLeftover ? "" : g.Mode switch
    {
        GroupingMode.Series => g.Kind switch
        {
            SeriesKind.Family => FolderOrganizer.Sanitize(g.Label),
            SeriesKind.Rest => FolderOrganizer.Sanitize(Loc.Get("OrganizeType" + g.Label)),
            _ => FolderOrganizer.SuggestName(g.Label, isPattern: true),
        },
        GroupingMode.Source => FolderOrganizer.Sanitize(g.Label),
        GroupingMode.Type => FolderOrganizer.Sanitize(Loc.Get("OrganizeType" + g.Label)),
        GroupingMode.Session => g.Start?.ToString("yyyy-MM-dd") ?? "",
        GroupingMode.Redundant => FolderOrganizer.Sanitize(Loc.Get("OrganizeRedundantFolder")),
        _ => "",
    };

    private static string MemberDetail(GroupMember m)
    {
        var detail = m.Modified.ToString("g");
        return m.Redundancy switch
        {
            RedundancyKind.ExtractedArchive => detail + "  ·  " + Loc.Get("OrganizeNoteExtracted", m.Related ?? ""),
            RedundancyKind.RepeatedDownload => detail + "  ·  " + Loc.Get("OrganizeNoteRepeated", m.Related ?? ""),
            _ => detail,
        };
    }

    // =====================================================================
    // Selected group
    // =====================================================================

    private void ShowGroup()
    {
        if (SelectedGroup is not { } row)
        {
            DetailTitle.Text = "";
            DetailText.Text = "";
            MemberList.ItemsSource = null;
            UpdateActions();
            return;
        }

        var g = row.Group;
        DetailTitle.Text = row.Title;
        DetailText.Text = DescriptionOf(g);
        // The suggestion never lands in an existing folder by accident ("asdfg" taken → "asdfg 2");
        // typing an existing name on purpose is allowed and announced (see UpdateActions).
        FolderNameBox.Text = _folder is null ? FolderNameFor(g) : FolderOrganizer.UniqueName(_folder, FolderNameFor(g));

        // Series and families start fully selected (tidying a whole series is the point);
        // the "everything else of this type" groups and the catch-all start empty.
        var preselect = !g.IsLeftover && !(g.Mode == GroupingMode.Series && g.Kind == SeriesKind.Rest);
        var members = g.Members.Select(m => new MemberRow { Member = m, Detail = MemberDetail(m), IsChecked = preselect }).ToList();
        foreach (var m in members) m.CheckedChanged = UpdateActions;
        MemberList.ItemsSource = members;
        UpdateActions();
    }

    private List<MemberRow> Members => MemberList.ItemsSource as List<MemberRow> ?? [];
    private List<MemberRow> Checked => Members.Where(m => m.IsChecked).ToList();

    private void UpdateActions()
    {
        var chosen = Checked;
        var bytes = chosen.Sum(m => m.Member.Size);
        SelectionText.Text = Loc.Get("CleanupSelection", chosen.Count, Format.Size(bytes));
        TrashButton.Content = Loc.Get("CleanupActionTrash", Format.Size(bytes));
        TrashButton.IsEnabled = chosen.Count > 0;
        FolderNameBox.IsEnabled = chosen.Count > 0;

        // Say what the typed name will do before anything moves.
        var name = FolderOrganizer.Sanitize(FolderNameBox.Text ?? "");
        var state = _folder is null || name.Length == 0 ? TargetState.Invalid : FolderOrganizer.CheckTarget(_folder, name);
        TargetHint.IsVisible = state is TargetState.ExistingFolder or TargetState.ExistingFile;
        if (state == TargetState.ExistingFolder)
        {
            var selfSelected = chosen.Any(m => m.Member.IsDirectory && string.Equals(m.Member.Name, name, StringComparison.OrdinalIgnoreCase));
            TargetHint.Text = Loc.Get(selfSelected ? "OrganizeHintExistingSelected" : "OrganizeHintExisting", name);
            TargetHint.Foreground = Avalonia.Media.Brush.Parse("#f9e2af");
        }
        else if (state == TargetState.ExistingFile)
        {
            TargetHint.Text = Loc.Get("OrganizeHintFile", name);
            TargetHint.Foreground = Avalonia.Media.Brush.Parse("#f38ba8");
        }
        MoveButton.IsEnabled = chosen.Count > 0 && state is TargetState.New or TargetState.ExistingFolder;
    }

    private void SetAll(bool value)
    {
        foreach (var m in Members) m.IsChecked = value;
    }

    // =====================================================================
    // Actions
    // =====================================================================

    private async Task MoveIntoFolderAsync()
    {
        if (_folder is not { } folder || Checked is not { Count: > 0 } chosen) return;

        var name = FolderOrganizer.Sanitize(FolderNameBox.Text ?? "");
        if (name.Length == 0)
        {
            StatusText.Text = Loc.Get("OrganizeNameNeeded");
            FolderNameBox.Focus();
            return;
        }

        try
        {
            var paths = chosen.Select(m => m.Member.Path).ToList();
            var result = await Task.Run(() => FolderOrganizer.MoveInto(folder, name, paths));
            _lastMove = result.Moved.Count > 0 ? result : null;
            UndoButton.IsVisible = _lastMove is not null;

            var message = MoveMessage(result, name);
            await ReloadAsync(SelectedGroup?.Group.Key);
            StatusText.Text = message; // set after the reload, which shows its own progress
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            StatusText.Text = $"{Loc.Get("CleanupFailed")}: {ex.Message}";
        }
    }

    /// <summary>What happened, with a reason for everything that stayed put.</summary>
    private static string MoveMessage(OrganizeResult result, string name)
    {
        var parts = new List<string>
        {
            Loc.Get(result.FolderExisted ? "OrganizeMovedExisting" : "OrganizeMoved", result.Moved.Count, name),
        };
        foreach (var reason in result.Skipped.GroupBy(s => s.Reason))
            parts.Add(Loc.Get("OrganizeSkipped" + reason.Key, reason.Count(), name));
        return string.Join(" ", parts);
    }

    private async Task UndoAsync()
    {
        if (_lastMove is not { } move) return;
        var restored = await Task.Run(() => FolderOrganizer.Undo(move));
        _lastMove = null;
        UndoButton.IsVisible = false;
        await ReloadAsync();
        StatusText.Text = Loc.Get("OrganizeUndone", restored);
    }

    private async Task TrashAsync()
    {
        if (Shell is not { } shell || Checked is not { Count: > 0 } chosen) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;

        var bytes = chosen.Sum(m => m.Member.Size);
        var confirmed = await ConfirmDialog.ShowAsync(owner,
            DetailTitle.Text ?? "",
            Loc.Get("CleanupConfirmTrash", chosen.Count, Format.Size(bytes)),
            Loc.Get("CleanupNoticeTrash"),
            Loc.Get("TrashConfirmButton"),
            Loc.Get("JobCancel"));
        if (!confirmed) return;

        StatusText.Text = Loc.Get("CleanupWorking");
        var paths = chosen.Select(m => m.Member.Path).ToList();
        var failed = await Task.Run(() => paths.Count(p => !shell.MoveToTrash(p).IsSuccess));

        await ReloadAsync(SelectedGroup?.Group.Key);
        StatusText.Text = Loc.Get("CleanupMovedToTrash", Format.Size(bytes))
                          + (failed > 0 ? " " + Loc.Get("OrganizeSkippedInUse", failed, "") : "");
    }

    // =====================================================================
    // Looking at an item
    // =====================================================================

    private void OnRowButtonClick(object? sender, RoutedEventArgs e)
    {
        if (e.Source is not Button { Tag: string action, DataContext: MemberRow row }) return;
        e.Handled = true;
        if (action == "show") ShowInFolder(row.Member.Path);
        else Open(row.Member.Path);
    }

    private void OnMemberRightClick(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton != MouseButton.Right) return;
        if (e.Source is not Control { DataContext: MemberRow row }) return;
        MemberList.SelectedItem = row;

        var open = new MenuItem { Header = Loc.Get("ContextMenuOpen") };
        open.Click += (_, _) => Open(row.Member.Path);
        var show = new MenuItem { Header = Loc.Get("ContextMenuShowInFolder") };
        show.Click += (_, _) => ShowInFolder(row.Member.Path);
        var copy = new MenuItem { Header = Loc.Get("ContextMenuCopyPath") };
        copy.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(row.Member.Path);
        };
        new ContextMenu { ItemsSource = new Control[] { open, show, new Separator(), copy } }.Open(MemberList);
    }

    private void ShowInFolder(string path)
    {
        if (Shell?.ShowInFileManager(path) is { IsSuccess: false }) StatusText.Text = Loc.Get("ShowInFolderFailed");
    }

    private void Open(string path)
    {
        if (Shell?.OpenPath(path) is { IsSuccess: false }) StatusText.Text = Loc.Get("FileOpenFailed");
    }
}
