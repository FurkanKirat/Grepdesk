namespace Grepdesk.Core.Browse;

/// <summary>Back / forward history of the folder browser, like a web browser's.</summary>
public sealed class NavigationHistory
{
    private readonly List<string> _back = [];
    private readonly List<string> _forward = [];

    public string? Current { get; private set; }

    public bool CanGoBack => _back.Count > 0;
    public bool CanGoForward => _forward.Count > 0;

    /// <summary>Most recent first.</summary>
    public IReadOnlyList<string> BackEntries => Enumerable.Reverse(_back).ToList();

    /// <summary>
    /// Goes to a new place: the current one joins the back list and the
    /// forward list is dropped. Going to where we already are does nothing.
    /// </summary>
    public void Navigate(string path)
    {
        if (Current is not null && SamePath(Current, path)) return;
        if (Current is not null) _back.Add(Current);
        _forward.Clear();
        Current = path;
    }

    public string? GoBack()
    {
        if (!CanGoBack) return null;
        _forward.Add(Current!);
        Current = Pop(_back);
        return Current;
    }

    public string? GoForward()
    {
        if (!CanGoForward) return null;
        _back.Add(Current!);
        Current = Pop(_forward);
        return Current;
    }

    /// <summary>
    /// Takes a deleted or renamed folder (and everything under it) out of the
    /// history, so Back never lands on a place that no longer exists.
    /// </summary>
    public void Forget(string path)
    {
        _back.RemoveAll(p => PathUtil.IsSameOrInside(p, path));
        _forward.RemoveAll(p => PathUtil.IsSameOrInside(p, path));
        // Neighbours that became equal after the removal would make Back a no-op step.
        Dedupe(_back);
        Dedupe(_forward);
    }

    private static string Pop(List<string> list)
    {
        var last = list[^1];
        list.RemoveAt(list.Count - 1);
        return last;
    }

    private static void Dedupe(List<string> list)
    {
        for (var i = list.Count - 1; i > 0; i--)
            if (SamePath(list[i], list[i - 1])) list.RemoveAt(i);
    }

    private static bool SamePath(string a, string b) => PathUtil.Comparer.Equals(PathUtil.Normalize(a), PathUtil.Normalize(b));
}
