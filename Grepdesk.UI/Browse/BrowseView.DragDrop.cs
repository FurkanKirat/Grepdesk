using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Platform.Storage;
using Avalonia.VisualTree;
using Grepdesk.Core;
using Grepdesk.Core.Browse;

namespace Grepdesk.UI.Browse;

/// <summary>
/// Drag and drop: rows dragged out of the list reach Explorer and other apps as
/// real files, and files dropped from anywhere (Explorer, another app, this list)
/// onto a folder row, a sidebar place or the open folder are copied or moved by
/// the same transfer job paste uses.
/// </summary>
public partial class BrowseView
{
    private static readonly IBrush DropHighlight = new ImmutableSolidColorBrush(Color.Parse("#89b4fa"), 0.3);

    private Point? _dragStart;
    private List<string>? _dragPaths;
    private bool _draggingOut;
    private ListBoxItem? _dropRow;

    private void SetupDragDrop()
    {
        // Out
        FileList.AddHandler(PointerPressedEvent, OnDragPointerPressed, RoutingStrategies.Tunnel);
        FileList.AddHandler(PointerReleasedEvent, (_, _) => { _dragStart = null; _dragPaths = null; }, RoutingStrategies.Tunnel);
        FileList.PointerMoved += OnDragPointerMoved;

        // In: the whole view accepts drops; the target is worked out from what is under the pointer.
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DragLeaveEvent, (_, _) => SetDropRow(null));
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    // ---- out ----

    private void OnDragPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        _dragStart = null;
        _dragPaths = null;
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed || _renaming is not null) return;
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Shift)) != 0) return; // selection gestures

        var source = e.Source as Visual;
        if (source?.FindAncestorOfType<TextBox>(includeSelf: true) is not null) return;
        if (source?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is not BrowseRow row) return;

        // Decided now, before the press changes the selection: dragging a selected row takes the whole selection.
        var selected = SelectedRows.ToList();
        _dragPaths = selected.Contains(row) ? selected.Select(r => r.FullPath).ToList() : [row.FullPath];
        _dragStart = e.GetPosition(FileList);
    }

    private async void OnDragPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragPaths is not { } paths || _draggingOut) return;
        if (!e.GetCurrentPoint(FileList).Properties.IsLeftButtonPressed) { _dragStart = null; _dragPaths = null; return; }

        var delta = e.GetPosition(FileList) - start;
        if (Math.Abs(delta.X) < 6 && Math.Abs(delta.Y) < 6) return;
        _dragStart = null;
        _dragPaths = null;

        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage) return;

        _draggingOut = true;
        try
        {
            var data = new DataTransfer();
            foreach (var path in paths)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await storage.TryGetFolderFromPathAsync(path)
                    : await storage.TryGetFileFromPathAsync(path);
                if (item is not null) data.Add(DataTransferItem.CreateFile(item));
            }
            if (data.Items.Count == 0) return;

            SessionTrail.Note($"Dragging {paths.Count} item(s) out of the folder browser");
            await DragDrop.DoDragDropAsync(e, data, DragDropEffects.Copy | DragDropEffects.Move | DragDropEffects.Link);
        }
        finally
        {
            _draggingOut = false;
        }
    }

    // ---- in ----

    private static List<string> DroppedPaths(DragEventArgs e) =>
        e.DataTransfer.TryGetFiles()?.Select(i => i.TryGetLocalPath()).OfType<string>().ToList() ?? [];

    /// <summary>The folder a drop lands in: the folder row or sidebar place under the pointer, else the open folder.</summary>
    private (string? Folder, ListBoxItem? Row) DropTarget(DragEventArgs e)
    {
        var item = (e.Source as Visual)?.FindAncestorOfType<ListBoxItem>(includeSelf: true);
        switch (item?.DataContext)
        {
            case PlaceRow { Path.Length: > 0 } place:
                return (place.Path, item);
            case BrowseRow { IsDirectory: true } row:
                return (row.FullPath, item);
        }
        return (InFolder ? CurrentFolder : null, null);
    }

    /// <summary>Explorer's rules: Ctrl copies, Shift moves, otherwise a move within a drive and a copy across drives.</summary>
    private static DragDropEffects ChooseEffect(IReadOnlyList<string> sources, string target, DragEventArgs e)
    {
        var allowed = e.DragEffects;
        DragDropEffects effect;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) effect = DragDropEffects.Copy;
        else if (e.KeyModifiers.HasFlag(KeyModifiers.Shift)) effect = DragDropEffects.Move;
        else
        {
            var targetRoot = Path.GetPathRoot(Path.GetFullPath(target));
            var sameDrive = sources.All(s => string.Equals(Path.GetPathRoot(Path.GetFullPath(s)), targetRoot, PathUtil.Comparison));
            effect = sameDrive ? DragDropEffects.Move : DragDropEffects.Copy;
        }

        if (!allowed.HasFlag(effect))
            effect = allowed.HasFlag(DragDropEffects.Copy) ? DragDropEffects.Copy
                : allowed.HasFlag(DragDropEffects.Move) ? DragDropEffects.Move
                : DragDropEffects.None;

        if (effect == DragDropEffects.None) return effect;

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(target));
        foreach (var source in sources)
        {
            var src = Path.TrimEndingDirectorySeparator(Path.GetFullPath(source));
            // A folder can't go into itself or below itself.
            if (string.Equals(full, src, PathUtil.Comparison) ||
                full.StartsWith(src + Path.DirectorySeparatorChar, PathUtil.Comparison))
                return DragDropEffects.None;
        }
        // Moving things to where they already are does nothing.
        if (effect == DragDropEffects.Move &&
            sources.All(s => string.Equals(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(s))), full, PathUtil.Comparison)))
            return DragDropEffects.None;

        return effect;
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        var sources = DroppedPaths(e);
        var (folder, row) = DropTarget(e);
        var effect = sources.Count == 0 || folder is null ? DragDropEffects.None : ChooseEffect(sources, folder, e);

        e.DragEffects = effect;
        SetDropRow(effect != DragDropEffects.None ? row : null);
        e.Handled = true;
    }

    private void OnDrop(object? sender, DragEventArgs e)
    {
        SetDropRow(null);
        var sources = DroppedPaths(e);
        var (folder, _) = DropTarget(e);
        if (sources.Count == 0 || folder is null) return;

        var effect = ChooseEffect(sources, folder, e);
        e.Handled = true;
        e.DragEffects = effect;
        if (effect == DragDropEffects.None) return;

        SessionTrail.Note($"Dropped {sources.Count} item(s) into {folder} ({effect})");
        StartJob(effect == DragDropEffects.Move ? "--move-to" : "--copy-to", [.. sources, folder]);
    }

    private void SetDropRow(ListBoxItem? row)
    {
        if (ReferenceEquals(_dropRow, row)) return;
        _dropRow?.ClearValue(BackgroundProperty);
        _dropRow = row;
        if (row is not null) row.Background = DropHighlight;
    }
}
