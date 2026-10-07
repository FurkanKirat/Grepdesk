using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Grepdesk.Core;
using Grepdesk.Core.DiskUsage;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.DiskUsage;

/// <summary>
/// "Where did my disk space go?": what kinds of files fill a drive, which
/// folders are biggest (drill down from the root), and the largest files.
/// Built on the same <see cref="FileIndex"/> as name search: if the main
/// window already scanned this drive, that scan is reused.
/// </summary>
public partial class DiskUsageView : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private const int LargestPerCategory = 100;
    private const int FolderRowLimit = 300;
    private const double MinBarShare = 0.002;     // thinner segments vanish behind the 2 px gaps
    private const double LabelInsideShare = 0.12; // only segments this wide get an inline label

    private readonly ObservableCollection<DiskRow> _folderRows = [];
    private readonly ObservableCollection<DiskRow> _largestRows = [];

    private DriveScans? _scans;
    private FileIndex? _index;     // the index the current report was built from
    private DiskReport? _report;
    private string? _folder;
    private DiskCategory? _categoryFilter;
    private CancellationTokenSource _cts = new();
    private int _scanProgress;
    private bool _updating;

    /// <summary>The drive scan shared with the other pages; set once by the window.</summary>
    public DriveScans? Scans
    {
        get => _scans;
        set
        {
            _scans = value;
            if (value is null) return;
            value.Progress += count =>
            {
                if (!CancelButton.IsVisible) return; // someone else's scan
                _scanProgress = count;
                StatusText.Text = Loc.Get("DiskScanning", count);
            };
        }
    }

    /// <summary>Raised to open a file (double-click on a file in the folder list).</summary>
    public event Action<string>? OpenRequested;

    public DiskUsageView()
    {
        InitializeComponent();

        FolderList.ItemsSource = _folderRows;
        LargestList.ItemsSource = _largestRows;

        AnalyzeButton.Click += async (_, _) => await AnalyzeAsync();
        CancelButton.Click += (_, _) => _cts.Cancel();
        UpButton.Click += async (_, _) => await GoUpAsync();

        FolderList.DoubleTapped += async (_, _) => await OpenRowAsync(FolderList);
        LargestList.DoubleTapped += async (_, _) => await OpenRowAsync(LargestList);
        FolderList.KeyDown += async (_, e) =>
        {
            if (e.Key == Key.Back) { e.Handled = true; await GoUpAsync(); }
        };

        CategoryList.SelectionChanged += (_, _) =>
        {
            if (_updating || CategoryList.SelectedItem is not CategoryRow row) return;
            _categoryFilter = row.Category;
            RenderLargestFiles();
        };

        DriveList.SelectionChanged += (_, _) =>
        {
            if (!_updating) UpdateAnalyzeButton();
        };

        ApplyLanguage();
    }

    /// <summary>The two result lists, so the window can attach its context menu and shortcuts.</summary>
    public IReadOnlyList<ListBox> ResultLists => [FolderList, LargestList];

    /// <summary>Where the window reports action results (copied, failed to open...) while this page is shown.</summary>
    public TextBlock StatusLine => StatusText;

    public ListBox? ActiveList =>
        FolderList.IsKeyboardFocusWithin ? FolderList : LargestList.IsKeyboardFocusWithin ? LargestList : null;

    private string? SelectedRoot => (DriveList.SelectedItem as DriveRow)?.Root;

    public void ApplyLanguage()
    {
        TitleText.Text = Loc.Get("DiskTab");
        SubtitleText.Text = Loc.Get("DiskSubtitle");
        CancelButton.Content = Loc.Get("JobCancel");
        EmptyText.Text = Loc.Get("DiskEmpty");
        CategoriesTitle.Text = Loc.Get("DiskCategoriesTitle");
        CategoriesHint.Text = Loc.Get("DiskCategoriesHint");
        FoldersTitle.Text = Loc.Get("DiskFoldersTitle");
        FilesTitle.Text = Loc.Get("DiskFilesTitle");
        ToolTip.SetTip(UpButton, Loc.Get("DiskUpTooltip"));

        LoadDrives();
        UpdateAnalyzeButton();
        if (_report is not null)
        {
            RenderReport();
            UpdateFolderHeader();
        }
        if (CancelButton.IsVisible && _scans?.IsScanning == true)
            StatusText.Text = Loc.Get("DiskScanning", _scanProgress);
    }

    /// <summary>Drops a deleted path from the lists (totals stay as analyzed until the next analysis).</summary>
    public void Remove(string path)
    {
        foreach (var list in new[] { _folderRows, _largestRows })
            foreach (var row in list.Where(r => string.Equals(r.FullPath, path, StringComparison.OrdinalIgnoreCase)).ToList())
                list.Remove(row);
    }

    // =====================================================================
    // Drives
    // =====================================================================

    private void LoadDrives()
    {
        var selected = SelectedRoot;
        var rows = DriveRow.LoadAll();

        _updating = true;
        DriveList.ItemsSource = rows;
        DriveList.SelectedItem = DriveRow.Pick(rows, selected);
        _updating = false;
    }

    private void UpdateAnalyzeButton()
    {
        var busy = CancelButton.IsVisible; // visible exactly while an analysis runs
        AnalyzeButton.Content = _report is not null && string.Equals(_report.Root, SelectedRoot, StringComparison.OrdinalIgnoreCase)
            ? Loc.Get("DiskReanalyze")
            : Loc.Get("DiskAnalyze");
        AnalyzeButton.IsEnabled = !busy && SelectedRoot is not null;
    }

    // =====================================================================
    // Analysis
    // =====================================================================

    private async Task AnalyzeAsync()
    {
        if (SelectedRoot is not { } root || _scans is not { } scans) return;

        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;

        AnalyzeButton.IsEnabled = false;
        CancelButton.IsVisible = true;

        try
        {
            // Reuse the main window's scan when it already covers this drive and
            // this is a first analysis; "Reanalyze" always scans fresh.
            var reanalyze = _report is not null && string.Equals(_report.Root, root, StringComparison.OrdinalIgnoreCase);
            var index = reanalyze ? null : scans.TryGet(root);

            if (index is null)
            {
                _scanProgress = 0;
                StatusText.Text = Loc.Get("DiskScanning", 0);
                index = await scans.ScanAsync(root, token);
            }

            StatusText.Text = Loc.Get("DiskAnalyzing");
            var report = await Task.Run(() => DiskAnalyzer.Analyze(index, root, LargestPerCategory, token), token);

            _index = index;
            _report = report;
            _categoryFilter = null;
            LoadDrives(); // free space has likely moved since the cards were drawn
            StatusText.Text = Loc.Get("DiskAnalyzed", DateTime.Now.ToString("t"));
            RenderReport();
            await ShowFolderAsync(report.Root);
        }
        catch (OperationCanceledException)
        {
            StatusText.Text = Loc.Get("DiskCancelled");
        }
        finally
        {
            CancelButton.IsVisible = false;
            UpdateAnalyzeButton();
        }
    }

    // =====================================================================
    // Report: headline, stacked bar, category table, largest files
    // =====================================================================

    private void RenderReport()
    {
        if (_report is not { } report) return;

        EmptyState.IsVisible = false;
        ReportPanel.IsVisible = true;

        UsedHeadline.Text = Loc.Get("DiskUsedHeadline", Format.Size(report.UsedBytes));
        FreeHeadline.Text = report.TotalBytes > 0
            ? Loc.Get("DiskFreeHeadline", Format.Size(report.FreeBytes), Format.Size(report.TotalBytes))
            : "";

        RenderUsageBar(report);

        var notes = new List<string>();
        if (report.UnreadableBytes > 0) notes.Add(Loc.Get("DiskNoteUnreadable", Format.Size(report.UnreadableBytes)));
        if (report.CloudOnlyBytes > 0) notes.Add(Loc.Get("DiskNoteCloud", Format.Size(report.CloudOnlyBytes)));
        NotesText.Text = string.Join("  ", notes);
        NotesText.IsVisible = notes.Count > 0;

        RenderCategoryTable(report);
        RenderLargestFiles();
    }

    private void RenderUsageBar(DiskReport report)
    {
        UsageBar.Children.Clear();
        UsageBar.ColumnDefinitions.Clear();

        var whole = (double)Math.Max(report.TotalBytes, report.ScannedBytes + report.UnreadableBytes + report.FreeBytes);
        if (whole <= 0) return;

        var segments = report.Categories
            .Where(c => c.Bytes > 0)
            .OrderByDescending(c => c.Bytes)
            .Select(c => (Name: CategoryName(c.Category), Bytes: c.Bytes, Fill: DiskColors.For(c.Category)))
            .ToList();
        if (report.UnreadableBytes > 0) segments.Add((Loc.Get("DiskUnreadable"), report.UnreadableBytes, DiskColors.Unreadable));
        if (report.FreeBytes > 0) segments.Add((Loc.Get("DiskFree"), report.FreeBytes, DiskColors.Free));
        segments = segments.Where(s => s.Bytes / whole >= MinBarShare).ToList();

        for (var i = 0; i < segments.Count; i++)
        {
            var (name, size, fill) = segments[i];
            var share = size / whole;
            UsageBar.ColumnDefinitions.Add(new ColumnDefinition(new GridLength(size, GridUnitType.Star)));

            var segment = new Border
            {
                Background = fill,
                // 2 px surface gap between segments; 4 px rounded ends on the outer edges only.
                Margin = new Thickness(0, 0, i < segments.Count - 1 ? 2 : 0, 0),
                CornerRadius = new CornerRadius(i == 0 ? 4 : 0, i == segments.Count - 1 ? 4 : 0, i == segments.Count - 1 ? 4 : 0, i == 0 ? 4 : 0),
            };
            ToolTip.SetTip(segment, $"{name}: {Format.Size(size)} ({Percent(share)})");

            if (share >= LabelInsideShare && fill != DiskColors.Free)
            {
                segment.Child = new TextBlock
                {
                    Text = name,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold,
                    Foreground = InkFor(fill),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(8, 0),
                    TextTrimming = TextTrimming.None,
                    ClipToBounds = false,
                };
            }

            Grid.SetColumn(segment, i);
            UsageBar.Children.Add(segment);
        }
    }

    private void RenderCategoryTable(DiskReport report)
    {
        var used = Math.Max(1, report.UsedBytes);
        var rows = new List<CategoryRow>
        {
            new()
            {
                Category = null,
                Name = Loc.Get("DiskAllCategories"),
                Swatch = Brushes.Transparent,
                HasSwatch = false,
                SizeText = Format.Size(report.ScannedBytes),
                PercentText = Percent((double)report.ScannedBytes / used),
                FilesText = Loc.Get("DiskFiles", report.Categories.Sum(c => c.Files)),
            }
        };

        rows.AddRange(report.Categories
            .Where(c => c.Bytes > 0)
            .OrderByDescending(c => c.Bytes)
            .Select(c => new CategoryRow
            {
                Category = c.Category,
                Name = CategoryName(c.Category),
                Swatch = DiskColors.For(c.Category),
                SizeText = Format.Size(c.Bytes),
                PercentText = Percent((double)c.Bytes / used),
                FilesText = c.Category == DiskCategory.Other && report.OtherExtensions.Count > 0
                    ? Loc.Get("DiskFiles", c.Files) + " · " + string.Join(", ", report.OtherExtensions
                        .Take(3).Select(x => x.Extension.Length > 0 ? x.Extension : Loc.Get("DiskNoExtension")))
                    : Loc.Get("DiskFiles", c.Files),
            }));

        _updating = true;
        CategoryList.ItemsSource = rows;
        CategoryList.SelectedItem = rows.FirstOrDefault(r => r.Category == _categoryFilter) ?? rows[0];
        _updating = false;
    }

    private void RenderLargestFiles()
    {
        if (_report is not { } report) return;

        // Games, apps, developer folders...: list whole items (a game with all
        // its files added up), the way Windows' Installed apps page does.
        var groups = _categoryFilter is { } selected
            ? report.Groups.Where(g => g.Category == selected).OrderByDescending(g => g.Bytes).ToList()
            : [];
        if (groups.Count > 0 && _categoryFilter is { } filter)
        {
            var largest = Math.Max(1, groups[0].Bytes);
            _largestRows.Clear();
            foreach (var g in groups)
                _largestRows.Add(new DiskRow(new SearchResult(g.Path, true, g.Bytes, g.LastModified), (double)g.Bytes / largest, g.Category));

            FilesTitle.Text = Loc.Get("DiskGroupsTitle" + filter) is var key && key != "DiskGroupsTitle" + filter
                ? key
                : Loc.Get("DiskGroupsTitle", CategoryName(filter));
            FilesSubtitle.Text = Loc.Get("DiskGroupsSubtitle", groups.Count, groups.Sum(g => g.Files));
            return;
        }

        FilesTitle.Text = Loc.Get("DiskFilesTitle");
        var files = report.LargestFiles
            .Where(f => _categoryFilter is null || f.Category == _categoryFilter)
            .Take(LargestPerCategory)
            .ToList();
        var max = Math.Max(1, files.FirstOrDefault()?.File.Size ?? 1);

        _largestRows.Clear();
        foreach (var f in files)
            _largestRows.Add(new DiskRow(f.File, (double)f.File.Size / max, f.Category));

        FilesSubtitle.Text = _categoryFilter is { } c
            ? Loc.Get("DiskFilesInCategory", CategoryName(c))
            : Loc.Get("DiskFilesAll");
    }

    // =====================================================================
    // Folder drill-down
    // =====================================================================

    // Folders (including a game or app from the right-hand list) open in the
    // folder column; files open in their default app.
    private async Task OpenRowAsync(ListBox list)
    {
        if (list.SelectedItem is not DiskRow row) return;
        if (row.IsDirectory) await ShowFolderAsync(row.FullPath);
        else OpenRequested?.Invoke(row.FullPath);
    }

    private async Task GoUpAsync()
    {
        if (_folder is null || _report is null) return;
        if (string.Equals(_folder, _report.Root, StringComparison.OrdinalIgnoreCase)) return;
        if (Path.GetDirectoryName(_folder) is { } parent)
            await ShowFolderAsync(parent);
    }

    private async Task ShowFolderAsync(string folder)
    {
        if (_index is not { } index || _report is null) return;

        // The shared scan may since have moved to another drive (Free up space page).
        if (index.IsIndexing || !index.Roots.Any(r => string.Equals(r, _report.Root, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = Loc.Get("DiskScanChanged");
            return;
        }

        var previous = _folder;
        _folder = folder;
        UpdateFolderHeader();

        var children = await Task.Run(() => index.ChildrenOf(folder)
            .OrderByDescending(c => c.Size)
            .Take(FolderRowLimit)
            .ToList());
        if (_folder != folder) return; // the user moved on while this was loading

        var total = Math.Max(1, children.Sum(c => c.Size));
        _folderRows.Clear();
        foreach (var child in children)
            _folderRows.Add(new DiskRow(child, (double)child.Size / total));

        UpdateFolderHeader(children.Sum(c => c.Size));

        // Coming back up: keep the folder we came from selected, so it's easy to see where we were.
        if (previous is not null)
            FolderList.SelectedItem = _folderRows.FirstOrDefault(r => string.Equals(r.FullPath, previous, StringComparison.OrdinalIgnoreCase));
    }

    private void UpdateFolderHeader(long? size = null)
    {
        if (_folder is null) return;
        UpButton.IsEnabled = _report is not null && !string.Equals(_folder, _report.Root, StringComparison.OrdinalIgnoreCase);
        FolderPathText.Text = size is { } s ? $"{_folder}  ·  {Format.Size(s)}" : _folder;
    }

    // =====================================================================
    // Helpers
    // =====================================================================

    private static string CategoryName(DiskCategory category) => Loc.Get("DiskCategory" + category);

    private static string Percent(double share) =>
        share > 0 && share < 0.005 ? "<" + 0.01.ToString("P0") : share.ToString("P0");

    // White or near-black text on a colored fill, whichever is readable.
    private static IBrush InkFor(IBrush fill)
    {
        if (fill is not ISolidColorBrush { Color: var c }) return Brushes.White;
        static double Channel(byte v) { var x = v / 255.0; return x <= 0.03928 ? x / 12.92 : Math.Pow((x + 0.055) / 1.055, 2.4); }
        var luminance = 0.2126 * Channel(c.R) + 0.7152 * Channel(c.G) + 0.0722 * Channel(c.B);
        return luminance > 0.25 ? Brush.Parse("#11111b") : Brushes.White;
    }
}
