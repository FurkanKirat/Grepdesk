using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
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

    private Bitmap? _icon;
    private bool _isRenaming;
    private bool _isCut;

    public BrowseRow(FolderEntry entry, string? typeText = null)
    {
        Entry = entry;
        Result = new SearchResult(entry.FullPath, entry.IsDirectory, entry.Size, entry.Modified, entry.Name);
        Kind = FileKind.For(Result);
        TypeText = typeText ?? (entry.IsDirectory ? Loc.Get("BrowseTypeFolder")
            : entry.Extension.Length > 0 ? Loc.Get("BrowseTypeFile", entry.Extension) : Loc.Get("BrowseTypeFileNoExtension"));
    }

    public FolderEntry Entry { get; }
    public SearchResult Result { get; }
    public string FullPath => Entry.FullPath;
    public string Name => Entry.Name;
    public bool IsDirectory => Entry.IsDirectory;

    public string ModifiedText => Entry.Modified == default ? "" : Entry.Modified.ToString("g");
    public string TypeText { get; }
    public string SizeText => IsDirectory ? "" : Format.Size(Entry.Size);

    public FileKind Kind { get; }

    public Bitmap? Icon
    {
        get => _icon;
        set { if (_icon != value) { _icon = value; Changed(); Changed(nameof(ShowFallbackIcon)); Changed(nameof(ShowFolderIcon)); Changed(nameof(ShowBadge)); } }
    }

    // Until (or unless) the shell's icon arrives, the same type badges as the search results.
    public bool ShowFallbackIcon => _icon is null;
    public bool ShowFolderIcon => _icon is null && IsDirectory;
    public bool ShowBadge => _icon is null && !IsDirectory;

    public bool IsRenaming
    {
        get => _isRenaming;
        set { if (_isRenaming != value) { _isRenaming = value; Changed(); Changed(nameof(IsNotRenaming)); } }
    }
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

/// <summary>A place in the browser's sidebar: a user folder or a drive.</summary>
public sealed record PlaceRow(string Label, string Path, Geometry Icon, string Detail = "", double UsedFraction = -1)
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

    private async Task<Bitmap?> Load(string path, CancellationToken ct)
    {
        var icon = await shell.GetIconAsync(path, Size, ct).ConfigureAwait(false);
        return icon is null ? null : ShellPreviewProvider.ToBitmap(icon);
    }

    private static void Forget(Dictionary<string, Task<Bitmap?>> cache, string key, Task<Bitmap?> task)
    {
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            if (cache.TryGetValue(key, out var current) && current == task) cache.Remove(key);
        });
    }
}
