namespace Grepdesk.Core.Browse;

/// <summary>
/// Pinned favorite folders and recently visited folders for the folder browser.
/// Kept normalized, deduplicated, and free of deleted paths.
/// </summary>
public sealed class FolderBookmarks
{
    public const int DefaultMaxRecent = 15;

    private readonly List<string> _favorites = [];
    private readonly List<string> _recent = [];
    private readonly int _maxRecent;

    public FolderBookmarks(
        IEnumerable<string>? favorites = null,
        IEnumerable<string>? recent = null,
        int maxRecent = DefaultMaxRecent)
    {
        _maxRecent = Math.Max(1, maxRecent);

        if (favorites is not null)
        {
            foreach (var path in favorites)
                AddUnique(_favorites, path);
        }

        if (recent is not null)
        {
            foreach (var path in recent)
            {
                if (_recent.Count >= _maxRecent) break;
                AddUnique(_recent, path);
            }
        }
    }

    public IReadOnlyList<string> Favorites => _favorites;

    /// <summary>Most recently visited folders first.</summary>
    public IReadOnlyList<string> Recent => _recent;

    public bool IsFavorite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _favorites.Any(f => PathUtil.Comparer.Equals(f, normalized));
    }

    public bool AddFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return AddUnique(_favorites, path);
    }

    public bool RemoveFavorite(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _favorites.RemoveAll(f => PathUtil.Comparer.Equals(f, normalized)) > 0;
    }

    /// <summary>Pins <paramref name="path"/> if not yet a favorite, or unpins it; returns the new state.</summary>
    public bool ToggleFavorite(string path) =>
        IsFavorite(path) ? !RemoveFavorite(path) : AddFavorite(path);

    /// <summary>Moves or inserts <paramref name="path"/> at the top of the recent folders list.</summary>
    public void RecordVisit(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = PathUtil.Normalize(path);
        _recent.RemoveAll(r => PathUtil.Comparer.Equals(r, normalized));
        _recent.Insert(0, normalized);
        if (_recent.Count > _maxRecent)
            _recent.RemoveRange(_maxRecent, _recent.Count - _maxRecent);
    }

    public bool RemoveRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _recent.RemoveAll(r => PathUtil.Comparer.Equals(r, normalized)) > 0;
    }

    public void ClearRecent() => _recent.Clear();

    /// <summary>Removes a deleted folder (and any subfolder under it) from both favorites and history.</summary>
    public void Forget(string deletedPath)
    {
        if (string.IsNullOrWhiteSpace(deletedPath)) return;
        _favorites.RemoveAll(p => PathUtil.IsSameOrInside(p, deletedPath));
        _recent.RemoveAll(p => PathUtil.IsSameOrInside(p, deletedPath));
    }

    /// <summary>Updates a renamed folder (and any subfolder under it) in both favorites and history.</summary>
    public void Rename(string oldPath, string newPath)
    {
        if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath)) return;
        var oldNorm = PathUtil.Normalize(oldPath);
        var newNorm = PathUtil.Normalize(newPath);

        RewriteList(_favorites, oldNorm, newNorm);
        RewriteList(_recent, oldNorm, newNorm);
    }

    /// <summary>Short folder name for display ("Grepdesk"), or the root itself ("C:\") when at a root.</summary>
    public static string DisplayName(string path)
    {
        var normalized = PathUtil.Normalize(path);
        var name = Path.GetFileName(Path.TrimEndingDirectorySeparator(normalized));
        return string.IsNullOrEmpty(name) ? normalized : name;
    }

    /// <summary>
    /// Labels each path with its folder name, adding the parent folder name in
    /// parentheses when two entries in the same list share a folder name.
    /// </summary>
    public static IReadOnlyList<(string Path, string Label)> LabelItems(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return [];

        var names = paths.Select(DisplayName).ToArray();
        var counts = new Dictionary<string, int>(PathUtil.Comparer);
        foreach (var name in names)
            counts[name] = counts.GetValueOrDefault(name) + 1;

        var result = new List<(string Path, string Label)>(paths.Count);
        for (var i = 0; i < paths.Count; i++)
        {
            var path = paths[i];
            var name = names[i];
            if (counts[name] > 1 && PathUtil.Parent(path) is { Length: > 0 } parent)
            {
                var parentName = DisplayName(parent);
                result.Add((path, $"{name} ({parentName})"));
            }
            else
            {
                result.Add((path, name));
            }
        }
        return result;
    }

    private static bool AddUnique(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        if (list.Any(p => PathUtil.Comparer.Equals(p, normalized))) return false;
        list.Add(normalized);
        return true;
    }

    private static void RewriteList(List<string> list, string oldNorm, string newNorm)
    {
        var prefix = Path.EndsInDirectorySeparator(oldNorm) ? oldNorm : oldNorm + Path.DirectorySeparatorChar;
        for (var i = 0; i < list.Count; i++)
        {
            var current = list[i];
            if (PathUtil.Comparer.Equals(current, oldNorm))
                list[i] = newNorm;
            else if (current.StartsWith(prefix, PathUtil.Comparison))
                list[i] = Path.Combine(newNorm, current[prefix.Length..]);
        }

        // Deduplicate while keeping the first occurrence's position.
        var seen = new HashSet<string>(PathUtil.Comparer);
        for (var i = 0; i < list.Count; i++)
        {
            if (!seen.Add(list[i]))
            {
                list.RemoveAt(i);
                i--;
            }
        }
    }
}
