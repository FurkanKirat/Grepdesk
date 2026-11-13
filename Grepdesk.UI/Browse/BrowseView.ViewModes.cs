using System.Collections.ObjectModel;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Threading;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Preview;

namespace Grepdesk.UI.Browse;

/// <summary>
/// View modes of the folder list: Details (the column list), and Medium / Large icons, a
/// wrapping grid of thumbnails. The choice is global, saved in the settings and used for every folder.
/// </summary>
public partial class BrowseView
{
    private IDataTemplate? _detailsTemplate;
    private FolderViewMode _appliedMode = FolderViewMode.Details;

    private void SetupViewModes()
    {
        _detailsTemplate = FileList.ItemTemplate;
        FileList.ContainerClearing += OnContainerClearing;
        ViewButton.Click += (_, _) => ShowViewMenu();
        FileList.AddHandler(ScrollViewer.ScrollChangedEvent, (_, _) => ScheduleThumbnailPass());
    }

    private const int FirstIconBatch = 48; // more than fits on a screen
    private const int IconBatch = 40;
    private int _fillGeneration;
    private List<BrowseRow>? _fillAll;

    /// <summary>Adds the rest of a big folder to the icon grid, a batch per UI turn, until done or superseded.</summary>
    private async Task FillRowsAsync(ObservableCollection<BrowseRow> target, List<BrowseRow> all, int from, int generation)
    {
        _fillAll = all;
        for (var i = from; i < all.Count; i += IconBatch)
        {
            // Let input and painting go first; this runs when the UI has nothing more urgent.
            await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background);
            if (generation != _fillGeneration || !ReferenceEquals(target, _rows)) return;
            for (var j = i; j < Math.Min(i + IconBatch, all.Count); j++) target.Add(all[j]);
        }
        if (generation == _fillGeneration) _fillAll = null;
    }

    /// <summary>Adds everything still waiting (for Select All, which must not miss rows).</summary>
    private void CompleteFill()
    {
        if (_fillAll is not { } all || _rows.Count >= all.Count) return;
        _fillGeneration++;
        for (var i = _rows.Count; i < all.Count; i++) _rows.Add(all[i]);
        _fillAll = null;
    }

    private bool InIconView => _appliedMode != FolderViewMode.Details;

    /// <summary>Makes the list match the chosen view; cheap when nothing changed. Called after every render.</summary>
    private void ApplyViewMode()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew(); // TMP-TRACE
        var chosen = FolderViewModes.Normalize(Settings.BrowseViewMode);
        var mode = FolderViewModes.Effective(chosen, _totalRows);
        if (mode == _appliedMode) return;

        if (mode != chosen) SetMessage(() => Loc.Get("BrowseViewTooLarge", FolderViewModes.MaxIconItems));
        _appliedMode = mode;

        var selected = SelectedRows.ToList();
        var icons = mode != FolderViewMode.Details;

        // Pictures are fetched per view (a different size), so start over.
        foreach (var row in _rows) { row.Icon = null; row.ThumbnailRequested = false; }
        FileList.Classes.Set("icons", icons);
        DetailsHeader.IsVisible = !icons;
        FileList.ItemTemplate = mode switch
        {
            FolderViewMode.MediumIcons => (IDataTemplate)this.FindResource("MediumIconsTemplate")!,
            FolderViewMode.LargeIcons => (IDataTemplate)this.FindResource("LargeIconsTemplate")!,
            _ => _detailsTemplate,
        };
        FileList.ItemsPanel = icons
            ? new FuncTemplate<Panel?>(() => new WrapPanel())
            : new FuncTemplate<Panel?>(() => new VirtualizingStackPanel());
        FileList.ItemsSource = null;
        FileList.ItemsSource = _rows;

        foreach (var row in selected)
            if (_rows.Contains(row)) FileList.SelectedItems!.Add(row);
        if (selected.Count > 0 && _rows.Contains(selected[0])) FileList.ScrollIntoView(selected[0]);
        Tr.Log($"ApplyViewMode switched to {mode} in {sw.ElapsedMilliseconds} ms"); // TMP-TRACE
    }

    private void SetViewMode(FolderViewMode mode)
    {
        if (Settings.BrowseViewMode == mode) return;
        Settings.BrowseViewMode = mode;
        Settings.Save();
        ApplyViewMode();
    }

    private void ShowViewMenu()
    {
        var items = new List<Control>();
        void AddMode(string key, FolderViewMode mode, KeyGesture gesture)
        {
            var item = new MenuItem
            {
                Header = Loc.Get(key),
                InputGesture = gesture,
                ToggleType = MenuItemToggleType.Radio,
                GroupName = "folderview",
                IsChecked = FolderViewModes.Normalize(Settings.BrowseViewMode) == mode,
            };
            item.Click += (_, _) => SetViewMode(mode);
            items.Add(item);
        }
        AddMode("BrowseViewLargeIcons", FolderViewMode.LargeIcons, new KeyGesture(Key.D2, KeyModifiers.Control | KeyModifiers.Shift));
        AddMode("BrowseViewMediumIcons", FolderViewMode.MediumIcons, new KeyGesture(Key.D3, KeyModifiers.Control | KeyModifiers.Shift));
        AddMode("BrowseViewDetails", FolderViewMode.Details, new KeyGesture(Key.D6, KeyModifiers.Control | KeyModifiers.Shift));

        // The column headers are hidden in the icon views, so sorting is offered here too.
        items.Add(new Separator());
        void AddSort(string key, FolderSortColumn column)
        {
            var item = new MenuItem
            {
                Header = Loc.Get(key) + (_sort.Column == column ? (_sort.Descending ? "  ↓" : "  ↑") : ""),
                ToggleType = MenuItemToggleType.CheckBox,
                IsChecked = _sort.Column == column,
            };
            item.Click += (_, _) => SortBy(column);
            items.Add(item);
        }
        AddSort("BrowseColumnName", FolderSortColumn.Name);
        AddSort("BrowseColumnModified", FolderSortColumn.Modified);
        AddSort("BrowseColumnType", FolderSortColumn.Type);
        AddSort("BrowseColumnSize", FolderSortColumn.Size);

        new ContextMenu { ItemsSource = items }.Open(ViewButton);
    }

    // ---- thumbnails ----

    /// <summary>
    /// A thumbnail is requested when its cell is near the visible part of the list, not when the cell is
    /// created: the icon views create every cell of the folder up front, and a big folder must not queue
    /// every file's thumbnail (PDFs, videos, archives) through the shell.
    /// </summary>
    private void ScheduleThumbnailPass()
    {
        if (_thumbnailPassQueued || !InIconView) return;
        _thumbnailPassQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _thumbnailPassQueued = false;
            RequestVisibleThumbnails();
        }, DispatcherPriority.Background);
    }

    private bool _thumbnailPassQueued;

    private void RequestVisibleThumbnails()
    {
        if (!InIconView || _icons is null || FileList.Scroll is not ScrollViewer scroll) return;
        var top = scroll.Offset.Y - 300; // a little beyond the edges, so scrolling finds them ready
        var bottom = scroll.Offset.Y + scroll.Viewport.Height + 300;
        foreach (var cell in FileList.GetRealizedContainers())
        {
            if (cell.DataContext is not BrowseRow { Icon: null, ThumbnailRequested: false } row) continue;
            var bounds = cell.Bounds;
            if (bounds.Height <= 0 || bounds.Bottom < top || bounds.Top > bottom) continue; // not laid out yet, or far away
            row.ThumbnailRequested = true;
            _ = SetThumbnailAsync(row, _appliedMode, _loadCts.Token);
        }
    }

    private async Task SetThumbnailAsync(BrowseRow row, FolderViewMode mode, CancellationToken ct)
    {
        var t0 = System.Diagnostics.Stopwatch.StartNew(); // TMP-TRACE
        var thumbnail = await _icons!.GetThumbnail(row.Entry, FolderViewModes.ThumbnailPixels(mode), ct);
        if (t0.ElapsedMilliseconds > 400) Tr.Log($"slow thumbnail {t0.ElapsedMilliseconds} ms: {System.IO.Path.GetFileName(row.FullPath)}"); if (++Tr.Thumbs % 50 == 0) Tr.Log($"thumbnails done: {Tr.Thumbs}"); // TMP-TRACE
        if (thumbnail is null || ct.IsCancellationRequested) return;

        // How much the cell enlarges the picture (cells draw it fitted into 64 / 128 DIP).
        var cell = mode == FolderViewMode.LargeIcons ? 128.0 : 64.0;
        var size = thumbnail.PixelSize;
        var scale = cell * (TopLevel.GetTopLevel(this)?.RenderScaling ?? 1) / Math.Max(1, Math.Max(size.Width, size.Height));
        var hardPixels = ImageScaling.UseNearestNeighbor(size.Width, size.Height, scale);

        Dispatcher.UIThread.Post(() =>
        {
            if (_appliedMode != mode) return;
            row.HardPixels = hardPixels;
            row.Icon ??= thumbnail;
        });
    }

    /// <summary>A cell that scrolled away lets go of its picture, so a big folder doesn't keep every thumbnail in memory.</summary>
    private void OnContainerClearing(object? sender, ContainerClearingEventArgs e)
    {
        if (InIconView && e.Container.DataContext is BrowseRow row) { row.Icon = null; row.ThumbnailRequested = false; }
    }
}
