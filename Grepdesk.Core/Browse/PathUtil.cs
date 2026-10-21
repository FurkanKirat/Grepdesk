namespace Grepdesk.Core.Browse;

public static class PathUtil
{
    /// <summary>Paths are case-insensitive on Windows (and by default on macOS), case-sensitive on Linux.</summary>
    public static StringComparer Comparer { get; } =
        OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

    public static StringComparison Comparison { get; } =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    /// <summary>Full path without a trailing separator, except for a root ("C:\", "/"), which keeps it.</summary>
    public static string Normalize(string path)
    {
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    public static bool IsSameOrInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        if (string.Equals(p, f, Comparison)) return true;
        var prefix = Path.EndsInDirectorySeparator(f) ? f : f + Path.DirectorySeparatorChar;
        return p.StartsWith(prefix, Comparison);
    }

    /// <summary>The folder above, or null at a drive or share root.</summary>
    public static string? Parent(string path) => Path.GetDirectoryName(Normalize(path));
}
