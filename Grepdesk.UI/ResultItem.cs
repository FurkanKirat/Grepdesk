using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Grepdesk.Core;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI;

// Order matches the sort combo box entries and the "Sort{name}" localization keys.
public enum SortMode { NameAsc, NameDesc, SizeDesc, SizeAsc, DateDesc, DateAsc }

// ViewModel wrapper for list items — used by both pages, Snippet only populated
// by content search results.
public class ResultItem(SearchResult result, string? snippet = null)
{
    public SearchResult Result { get; } = result;
    public string FileName => Result.FileName;
    public string Directory => Result.Directory;
    public string FullPath => Result.FullPath;
    public bool IsDirectory => Result.IsDirectory;

    // Folder sizes would need a full recursive walk, so they stay blank.
    public string SizeText => IsDirectory ? "" : Format.Size(Result.Size);
    public string ModifiedText => Result.Modified == default ? "" : Result.Modified.ToString("g");

    public string? Snippet { get; } = snippet;
    public bool HasSnippet => Snippet != null;

    public FileKind Kind { get; } = FileKind.For(result);
    public bool ShowFolderIcon => IsDirectory;
    public bool ShowBadge => !IsDirectory && Kind.Label.Length > 0;
    public bool ShowDocumentIcon => !IsDirectory && Kind.Label.Length == 0;
}

/// <summary>
/// How a file type is drawn: a short extension label ("PDF", "XLSX") on a
/// badge tinted by category, so documents, code, media, archives etc. are
/// told apart at a glance without relying on emoji fonts.
/// </summary>
public sealed record FileKind(string Label, IBrush Foreground, IBrush Background)
{
    private static readonly Dictionary<string, FileKind> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Color, string[] Extensions)[] Categories =
    [
        ("#f38ba8", [".pdf"]),
        ("#89b4fa", [".doc", ".docx", ".odt", ".rtf"]),
        ("#a6e3a1", [".xls", ".xlsx", ".xlsm", ".csv", ".ods"]),
        ("#fab387", [".ppt", ".pptx", ".odp", ".key"]),
        ("#cba6f7", [".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp",
                     ".go", ".rs", ".rb", ".php", ".swift", ".sql", ".sh", ".ps1", ".html", ".htm", ".css",
                     ".scss", ".xml", ".json", ".yaml", ".yml", ".toml", ".axaml", ".xaml", ".csproj", ".sln",
                     ".vue", ".dart", ".lua"]),
        ("#f5c2e7", [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".psd"]),
        ("#eba0ac", [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"]),
        ("#94e2d5", [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac"]),
        ("#f9e2af", [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso"]),
        ("#74c7ec", [".exe", ".msi", ".dll", ".bat", ".cmd", ".appx", ".msix"]),
        ("#bac2de", [".txt", ".md", ".log", ".ini", ".cfg", ".conf", ".env"]),
    ];

    private static readonly Dictionary<string, Color> ColorByExtension = BuildColorMap();
    private static readonly Color Other = Color.Parse("#7f849c");

    private static Dictionary<string, Color> BuildColorMap()
    {
        var map = new Dictionary<string, Color>(StringComparer.OrdinalIgnoreCase);
        foreach (var (color, extensions) in Categories)
            foreach (var ext in extensions)
                map[ext] = Color.Parse(color);
        return map;
    }

    public static FileKind For(SearchResult result)
    {
        var ext = result.IsDirectory ? "" : Path.GetExtension(result.FullPath);

        // Results are built on a worker thread; brushes are cached per extension.
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var kind)) return kind;

            var label = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "";
            if (label.Length > 4) label = label[..4];

            var color = ColorByExtension.GetValueOrDefault(ext, Other);
            kind = new FileKind(label,
                new ImmutableSolidColorBrush(color),
                new ImmutableSolidColorBrush(color, 0.16));
            Cache[ext] = kind;
            return kind;
        }
    }
}

public static class ResultOrdering
{
    private static readonly CompareInfo Culture = CultureInfo.CurrentCulture.CompareInfo;

    public static Comparison<SearchResult> For(SortMode mode) => mode switch
    {
        SortMode.NameAsc => ByName,
        SortMode.NameDesc => (a, b) => ByName(b, a),
        // Folders have no size; keep them after the files either way.
        SortMode.SizeDesc => (a, b) => a.IsDirectory != b.IsDirectory
            ? a.IsDirectory.CompareTo(b.IsDirectory)
            : Then(b.Size.CompareTo(a.Size), a, b),
        SortMode.SizeAsc => (a, b) => a.IsDirectory != b.IsDirectory
            ? a.IsDirectory.CompareTo(b.IsDirectory)
            : Then(a.Size.CompareTo(b.Size), a, b),
        SortMode.DateDesc => (a, b) => Then(b.Modified.CompareTo(a.Modified), a, b),
        SortMode.DateAsc => (a, b) => Then(a.Modified.CompareTo(b.Modified), a, b),
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };

    public static Comparison<ResultItem> ForItems(SortMode mode)
    {
        var compare = For(mode);
        return (a, b) => compare(a.Result, b.Result);
    }

    /// <summary>
    /// The first <paramref name="count"/> results in sorted order, without
    /// sorting the whole list: a bounded heap keeps this fast even when a
    /// short query matches millions of indexed entries.
    /// </summary>
    public static List<SearchResult> TakeSorted(List<SearchResult> all, int count, Comparison<SearchResult> compare, CancellationToken ct)
    {
        if (all.Count <= count)
        {
            var copy = new List<SearchResult>(all);
            copy.Sort(compare);
            return copy;
        }

        // Max-heap of the best `count` seen so far: the root is the worst kept item.
        var heap = new PriorityQueue<SearchResult, SearchResult>(count, Comparer<SearchResult>.Create((a, b) => compare(b, a)));
        for (var i = 0; i < all.Count; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();

            var r = all[i];
            if (heap.Count < count) heap.Enqueue(r, r);
            else if (compare(r, heap.Peek()) < 0) heap.EnqueueDequeue(r, r);
        }

        var top = heap.UnorderedItems.Select(x => x.Element).ToList();
        top.Sort(compare);
        return top;
    }

    private static int ByName(SearchResult a, SearchResult b) =>
        Then(Culture.Compare(a.FileName, b.FileName, CompareOptions.IgnoreCase), a, b);

    // Full path as the final tie-breaker keeps the order total, so paging
    // ("show more") never repeats or skips an item.
    private static int Then(int primary, SearchResult a, SearchResult b) =>
        primary != 0 ? primary : StringComparer.OrdinalIgnoreCase.Compare(a.FullPath, b.FullPath);
}
