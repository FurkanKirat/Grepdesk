using Grepdesk.Core.Browse;

namespace Grepdesk.Core.Programs;

/// <summary>
/// Pinned favorite programs, recently launched programs, and user-added custom program paths.
/// Kept normalized and deduplicated.
/// </summary>
public sealed class ProgramBookmarks
{
    public const int DefaultMaxRecent = 20;

    private readonly List<string> _favorites = [];
    private readonly List<string> _recent = [];
    private readonly List<string> _custom = [];
    private readonly int _maxRecent;

    public ProgramBookmarks(
        IEnumerable<string>? favorites = null,
        IEnumerable<string>? recent = null,
        IEnumerable<string>? custom = null,
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

        if (custom is not null)
        {
            foreach (var path in custom)
                AddUnique(_custom, path);
        }
    }

    public IReadOnlyList<string> Favorites => _favorites;
    public IReadOnlyList<string> Recent => _recent;
    public IReadOnlyList<string> Custom => _custom;

    public bool IsFavorite(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _favorites.Any(f => PathUtil.Comparer.Equals(f, normalized));
    }

    public bool IsFavorite(ProgramEntry entry) =>
        IsFavorite(entry.LaunchPath) || (!string.Equals(entry.LaunchPath, entry.TargetPath, PathUtil.Comparison) && IsFavorite(entry.TargetPath));

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

    /// <summary>Toggles favorite state for a <see cref="ProgramEntry"/>, clearing both LaunchPath and TargetPath when unpinning.</summary>
    public bool ToggleFavorite(ProgramEntry entry)
    {
        if (IsFavorite(entry))
        {
            RemoveFavorite(entry.LaunchPath);
            RemoveFavorite(entry.TargetPath);
            return false;
        }
        AddFavorite(entry.LaunchPath);
        return true;
    }

    /// <summary>Moves or inserts <paramref name="path"/> at the top of the recent programs list.</summary>
    public void RecordLaunch(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var normalized = PathUtil.Normalize(path);
        _recent.RemoveAll(r => PathUtil.Comparer.Equals(r, normalized));
        _recent.Insert(0, normalized);
        if (_recent.Count > _maxRecent)
            _recent.RemoveRange(_maxRecent, _recent.Count - _maxRecent);
    }

    public int RecentIndex(ProgramEntry entry)
    {
        var launch = PathUtil.Normalize(entry.LaunchPath);
        var target = PathUtil.Normalize(entry.TargetPath);
        for (var i = 0; i < _recent.Count; i++)
        {
            if (PathUtil.Comparer.Equals(_recent[i], launch) || PathUtil.Comparer.Equals(_recent[i], target))
                return i;
        }
        return -1;
    }

    public bool RemoveRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _recent.RemoveAll(r => PathUtil.Comparer.Equals(r, normalized)) > 0;
    }

    public void ClearRecent() => _recent.Clear();

    public bool IsCustom(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _custom.Any(c => PathUtil.Comparer.Equals(c, normalized));
    }

    public bool AddCustom(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        return AddUnique(_custom, path);
    }

    public bool RemoveCustom(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        return _custom.RemoveAll(c => PathUtil.Comparer.Equals(c, normalized)) > 0;
    }

    private static bool AddUnique(List<string> list, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var normalized = PathUtil.Normalize(path);
        if (list.Any(p => PathUtil.Comparer.Equals(p, normalized))) return false;
        list.Add(normalized);
        return true;
    }
}
