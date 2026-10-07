using System.Globalization;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Grepdesk.Core;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI;

// Order matches the sort combo box entries and the "Sort{name}" localization keys.
public enum SortMode { NameAsc, NameDesc, SizeDesc, SizeAsc, DateDesc, DateAsc }

// Order matches the type filter chips and the "Category{name}" localization keys.
public enum FileCategory { All, Folder, Document, Image, Code, Archive, Video, Audio, Other }

// ViewModel wrapper for list items — used by both pages, Snippet only populated
// by content search results.
public class ResultItem
{
    /// <param name="nameHighlight">Part of the file name to mark (the name search query).</param>
    /// <param name="snippetHighlight">Part of the snippet to mark (the content search query).</param>
    public ResultItem(SearchResult result, string? snippet = null, string? nameHighlight = null, string? snippetHighlight = null)
    {
        Result = result;
        Snippet = snippet;
        Kind = FileKind.For(result);
        (NameBefore, NameMatch, NameAfter) = Split(result.FileName, nameHighlight);
        (SnippetBefore, SnippetMatch, SnippetAfter) = Split(snippet ?? "", snippetHighlight);
    }

    public SearchResult Result { get; }
    public string FileName => Result.FileName;
    public string Directory => Result.Directory;
    public string FullPath => Result.FullPath;
    public bool IsDirectory => Result.IsDirectory;

    public string SizeText => IsDirectory && Result.Size == 0 ? "" : Format.Size(Result.Size);
    public string ModifiedText => Result.Modified == default ? "" : Result.Modified.ToString("g");

    // Name and snippet are shown as before / match / after runs, so the
    // matched part can be drawn highlighted from a plain data template.
    public string NameBefore { get; }
    public string NameMatch { get; }
    public string NameAfter { get; }

    public string? Snippet { get; }
    public bool HasSnippet => Snippet != null;
    public string SnippetBefore { get; }
    public string SnippetMatch { get; }
    public string SnippetAfter { get; }

    public FileKind Kind { get; }
    public bool ShowFolderIcon => IsDirectory;
    public bool ShowBadge => !IsDirectory && Kind.Label.Length > 0;
    public bool ShowDocumentIcon => !IsDirectory && Kind.Label.Length == 0;

    private static (string, string, string) Split(string text, string? highlight)
    {
        var index = string.IsNullOrEmpty(highlight) ? -1 : text.IndexOf(highlight, StringComparison.OrdinalIgnoreCase);
        return index < 0
            ? (text, "", "")
            : (text[..index], text.Substring(index, highlight!.Length), text[(index + highlight.Length)..]);
    }
}

/// <summary>
/// How a file type is drawn: a short extension label ("PDF", "XLSX") on a
/// badge tinted by type, so documents, code, media, archives etc. are told
/// apart at a glance without relying on emoji fonts.
/// </summary>
public sealed record FileKind(string Label, FileCategory Category, IBrush Foreground, IBrush Background)
{
    private static readonly Dictionary<string, FileKind> Cache = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Color, FileCategory Category, string[] Extensions)[] Types =
    [
        ("#f38ba8", FileCategory.Document, [".pdf"]),
        ("#89b4fa", FileCategory.Document, [".doc", ".docx", ".odt", ".rtf"]),
        ("#a6e3a1", FileCategory.Document, [".xls", ".xlsx", ".xlsm", ".csv", ".ods"]),
        ("#fab387", FileCategory.Document, [".ppt", ".pptx", ".odp", ".key"]),
        ("#bac2de", FileCategory.Document, [".txt", ".md", ".log"]),
        ("#cba6f7", FileCategory.Code, [".cs", ".js", ".ts", ".tsx", ".jsx", ".py", ".java", ".kt", ".c", ".h", ".cpp", ".hpp",
                                        ".go", ".rs", ".rb", ".php", ".swift", ".sql", ".sh", ".ps1", ".html", ".htm", ".css",
                                        ".scss", ".xml", ".json", ".yaml", ".yml", ".toml", ".axaml", ".xaml", ".csproj", ".sln",
                                        ".vue", ".dart", ".lua", ".ini", ".cfg", ".conf", ".env"]),
        ("#f5c2e7", FileCategory.Image, [".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".ico", ".tif", ".tiff", ".heic", ".psd"]),
        ("#eba0ac", FileCategory.Video, [".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm"]),
        ("#94e2d5", FileCategory.Audio, [".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac"]),
        ("#f9e2af", FileCategory.Archive, [".zip", ".7z", ".rar", ".tar", ".gz", ".bz2", ".xz", ".iso"]),
        ("#74c7ec", FileCategory.Other, [".exe", ".msi", ".dll", ".bat", ".cmd", ".appx", ".msix"]),
    ];

    private static readonly Dictionary<string, (Color Color, FileCategory Category)> ByExtension = BuildMap();
    private static readonly Color OtherColor = Color.Parse("#7f849c");

    private static Dictionary<string, (Color, FileCategory)> BuildMap()
    {
        var map = new Dictionary<string, (Color, FileCategory)>(StringComparer.OrdinalIgnoreCase);
        foreach (var (color, category, extensions) in Types)
            foreach (var ext in extensions)
                map[ext] = (Color.Parse(color), category);
        return map;
    }

    public static FileCategory CategoryOf(SearchResult result) =>
        result.IsDirectory ? FileCategory.Folder
        : ByExtension.TryGetValue(Path.GetExtension(result.FileName), out var t) ? t.Category
        : FileCategory.Other;

    public static FileKind For(SearchResult result)
    {
        var ext = result.IsDirectory ? "" : Path.GetExtension(result.FileName);

        // Results are built on a worker thread; brushes are cached per extension.
        lock (Cache)
        {
            if (Cache.TryGetValue(ext, out var kind)) return kind;

            var label = ext.Length > 1 ? ext[1..].ToUpperInvariant() : "";
            if (label.Length > 4) label = label[..4];

            var (color, category) = ByExtension.GetValueOrDefault(ext, (OtherColor, FileCategory.Other));
            kind = new FileKind(label, result.IsDirectory ? FileCategory.Folder : category,
                new ImmutableSolidColorBrush(color),
                new ImmutableSolidColorBrush(color, 0.16));
            Cache[ext] = kind;
            return kind;
        }
    }
}

/// <summary>Type / size / date filters of the name search page. Default = everything.</summary>
public sealed record ResultFilter(FileCategory Category = FileCategory.All, long MinSize = 0, TimeSpan? ModifiedWithin = null)
{
    // Entries match the size filter combo box.
    public static readonly long[] SizeSteps = [0, 1L << 20, 10L << 20, 100L << 20, 1L << 30];

    // Entries match the date filter combo box (null = any time).
    public static readonly TimeSpan?[] DateSteps = [null, TimeSpan.FromDays(1), TimeSpan.FromDays(7), TimeSpan.FromDays(30), TimeSpan.FromDays(365)];

    public bool IsEmpty => Category == FileCategory.All && MinSize == 0 && ModifiedWithin is null;

    public Func<SearchResult, bool> ToPredicate()
    {
        var category = Category;
        var minSize = MinSize;
        var since = ModifiedWithin is { } window ? DateTime.Now - window : DateTime.MinValue;

        return r => r.Size >= minSize
                    && r.Modified >= since
                    && (category == FileCategory.All || FileKind.CategoryOf(r) == category);
    }
}

public static class ResultOrdering
{
    private static readonly CompareInfo Culture = CultureInfo.CurrentCulture.CompareInfo;

    // Folders now carry the total size of their contents, so size sorting
    // mixes them with files: a 20 GB folder belongs above a 2 GB video.
    public static Comparison<SearchResult> For(SortMode mode) => mode switch
    {
        SortMode.NameAsc => ByName,
        SortMode.NameDesc => (a, b) => ByName(b, a),
        SortMode.SizeDesc => (a, b) => Then(b.Size.CompareTo(a.Size), a, b),
        SortMode.SizeAsc => (a, b) => Then(a.Size.CompareTo(b.Size), a, b),
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
