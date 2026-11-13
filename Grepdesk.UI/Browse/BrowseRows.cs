using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Grepdesk.Core.Viewer;
using Grepdesk.Core;
using Grepdesk.Core.Browse;
using Grepdesk.Core.Preview;
using Grepdesk.UI.Jobs;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI.Browse;

/// <summary>One file or folder in the browser's list.</summary>
public sealed class BrowseRow : INotifyPropertyChanged
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private FolderEntry _entry;
    private Bitmap? _icon;
    private bool _isRenaming;
    private bool _hardPixels;
    private bool _isCut;

    public BrowseRow(FolderEntry entry, string? typeText = null)
    {
        _entry = entry;
        Result = new SearchResult(entry.FullPath, entry.IsDirectory, Math.Max(0, entry.Size), entry.Modified, entry.Name);
        Kind = FileKind.For(Result);
        TypeText = typeText ?? (entry.IsDirectory ? Loc.Get("BrowseTypeFolder")
            : entry.Extension.Length > 0 ? Loc.Get("BrowseTypeFile", entry.Extension) : Loc.Get("BrowseTypeFileNoExtension"));
    }

    public FolderEntry Entry
    {
        get => _entry;
        set
        {
            if (_entry == value) return;
            var sizeChanged = _entry.Size != value.Size;
            var modifiedChanged = _entry.Modified != value.Modified;
            _entry = value;
            Result = new SearchResult(value.FullPath, value.IsDirectory, Math.Max(0, value.Size), value.Modified, value.Name);
            Changed();
            if (sizeChanged) Changed(nameof(SizeText));
            if (modifiedChanged) Changed(nameof(ModifiedText));
        }
    }

    public SearchResult Result { get; private set; }
    public string FullPath => _entry.FullPath;
    public string Name => _entry.Name;
    public bool IsDirectory => _entry.IsDirectory;

    public string ModifiedText => _entry.Modified == default ? "" : _entry.Modified.ToString("g");
    public string TypeText { get; }
    public string SizeText => _entry.Size < 0 ? "" : Format.Size(_entry.Size);

    public FileKind Kind { get; }

    public Bitmap? Icon
    {
        get => _icon;
        set { if (_icon != value) { _icon = value; Changed(); Changed(nameof(ShowFallbackIcon)); Changed(nameof(FallbackRow)); Changed(nameof(ShowFolderIcon)); Changed(nameof(ShowBadge)); } }
    }

    /// <summary>True: hard pixels for a small picture enlarged in an icon view (pixel art, icons), smoothing otherwise.</summary>
    public bool HardPixels
    {
        get => _hardPixels;
        set { if (_hardPixels != value) { _hardPixels = value; Changed(); } }
    }

    // Until (or unless) the shell's icon arrives, the same type badges as the search results.
    public bool ShowFallbackIcon => _icon is null;

    /// <summary>This row until its picture arrives, else null: the icon views build the folder / type badge only while it is shown.</summary>
    public BrowseRow? FallbackRow => _icon is null ? this : null;
    public bool ShowFolderIcon => _icon is null && IsDirectory;
    public bool ShowBadge => _icon is null && !IsDirectory;

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming != value) { _isRenaming = value; Changed(); Changed(nameof(IsNotRenaming)); Changed(nameof(RenamingRow)); } }
    }

    /// <summary>This row while it is being renamed, else null: the list builds the (heavy) rename box only then.</summary>
    public BrowseRow? RenamingRow => _isRenaming ? this : null;

    /// <summary>A thumbnail has been asked for in the current icon view.</summary>
    public bool ThumbnailRequested { get; set; }
    public bool IsNotRenaming => !_isRenaming;

    /// <summary>Cut to the clipboard: drawn faded until pasted, as in Explorer.</summary>
    public bool IsCut
    {
        get => _isCut;
        set { if (_isCut != value) { _isCut = value; Changed(); Changed(nameof(Opacity)); } }
    }

    public double Opacity => _isCut ? 0.45 : Entry.IsHidden ? 0.6 : 1.0;

    public ResultItem ToResultItem() => new(Result);

    // Type-ahead search in the list matches on this.
    public override string ToString() => Name;

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A place in the browser's sidebar: a favorite, recent folder, user folder, or drive.</summary>
public sealed record PlaceRow(string Label, string Path, Geometry Icon, string Detail = "", double UsedFraction = -1, string ActionTip = "")
{
    public bool HasDetail => Detail.Length > 0;
    public bool HasUsage => UsedFraction >= 0;
    public double UsedPercent => UsedFraction * 100;
}

/// <summary>
/// Shell icons as bitmaps. Most files share their type's icon, so those are
/// asked for once per extension; apps, shortcuts, icon files and folders can
/// each have their own and are asked for per item.
/// </summary>
internal sealed class ShellIconCache(IShellIcons shell)
{
    // Pixels, so the bitmap stays sharp at 150–200% scaling; drawn at 18 DIP.
    private const int Size = 32;
    private const int MaxPerItem = 4000;

    private static readonly HashSet<string> OwnIconExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".ico", ".url", ".cur", ".ani", ".scr", ".cpl", ".msc", ".appref-ms", ".website"
    };

    private readonly Dictionary<string, Task<Bitmap?>> _byType = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _byItem = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Task<Bitmap?>> _thumbnails = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxThumbnails = 500; // bitmaps are large; rows that are on screen hold their own

    /// <summary>Call on the UI thread; the task completes off it.</summary>
    public Task<Bitmap?> Get(FolderEntry entry, CancellationToken ct)
    {
        var perItem = entry.IsDirectory || OwnIconExtensions.Contains(Path.GetExtension(entry.Name));
        var cache = perItem ? _byItem : _byType;
        var key = perItem ? entry.FullPath : Path.GetExtension(entry.Name);

        if (cache.TryGetValue(key, out var cached) && !cached.IsCanceled) return cached;
        if (perItem && _byItem.Count > MaxPerItem) _byItem.Clear();

        var task = Load(entry.FullPath, ct);
        // A request cancelled by leaving the folder comes back empty; don't keep that as the answer.
        cache[key] = task;
        task.ContinueWith(t => { if (t.Result is null && ct.IsCancellationRequested) Forget(cache, key, task); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    /// <summary>A thumbnail for the icon views (per file, per size). Call on the UI thread; the task completes off it.</summary>
    public Task<Bitmap?> GetThumbnail(FolderEntry entry, int size, CancellationToken ct)
    {
        var key = $"{size}|{entry.Modified.Ticks}|{entry.FullPath}";
        if (_thumbnails.TryGetValue(key, out var cached) && !cached.IsCanceled) return cached;
        if (_thumbnails.Count > MaxThumbnails) _thumbnails.Clear();

        var task = LoadThumbnail(entry.FullPath, size, ct);
        _thumbnails[key] = task;
        task.ContinueWith(t => { if (t.Result is null && ct.IsCancellationRequested) Forget(_thumbnails, key, task); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    // Small picture files are decoded here, not by the shell: its thumbnail handler enlarges a
    // 16 px sprite with smoothing, which blurs pixel art. Big photos still use the shell's (cached) thumbnails.
    private const long OwnDecodeMaxBytes = 300 * 1024;

    private async Task<Bitmap?> LoadThumbnail(string path, int size, CancellationToken ct)
    {
        if (ViewerFormats.ImageExtensions.Contains(Path.GetExtension(path)) && await Task.Run(() => DecodeSmallImage(path), ct).ConfigureAwait(false) is { } own)
            return own;
        var thumbnail = await shell.GetThumbnailAsync(path, size, ct).ConfigureAwait(false);
        return thumbnail is null ? null : ShellPreviewProvider.ToBitmap(thumbnail);
    }

    private async Task<Bitmap?> Load(string path, CancellationToken ct)
    {
        var icon = await shell.GetIconAsync(path, Size, ct).ConfigureAwait(false);
        return icon is null ? null : ShellPreviewProvider.ToBitmap(icon);
    }

    private static Bitmap? DecodeSmallImage(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length is 0 or > OwnDecodeMaxBytes) return null;
            using var stream = info.OpenRead();
            return new Bitmap(stream);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return null; // not decodable here: the shell gets a try
        }
    }

    private static void Forget(Dictionary<string, Task<Bitmap?>> cache, string key, Task<Bitmap?> task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cache.TryGetValue(key, out var current) && current == task) cache.Remove(key);
        });
    }
}
