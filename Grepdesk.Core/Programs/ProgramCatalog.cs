using Grepdesk.Core.Browse;

namespace Grepdesk.Core.Programs;

public enum ProgramSource
{
    StartMenu,
    Desktop,
    Applications,
    Custom,
}

public enum ProgramFilter
{
    All,
    Favorites,
    Recent,
}

/// <summary>
/// A discovered or user-added program that can be launched and favorited.
/// </summary>
public sealed record ProgramEntry(
    string Name,
    string LaunchPath,
    string TargetPath,
    string Category,
    ProgramSource Source)
{
    public bool IsCustom => Source == ProgramSource.Custom;

    /// <summary>The best path to reveal in the file manager (the shortcut or executable itself).</summary>
    public string RevealPath => File.Exists(TargetPath) || Directory.Exists(TargetPath) ? TargetPath : LaunchPath;

    /// <summary>Directory containing the target executable (or launch file).</summary>
    public string? WorkingDirectory => Path.GetDirectoryName(RevealPath);
}

/// <summary>
/// Discovers installed applications from standard OS locations (Start Menu, Desktop, Applications folders),
/// combines them with user-added custom programs, and filters/ranks them for instant launching.
/// </summary>
public static class ProgramCatalog
{
    private static readonly string[] NoiseKeywords =
    [
        "uninstall", "unins0", "uninst", "kaldır",
        "readme", "read me", "release notes", "changelog",
        "documentation", "user manual", "help", "yardım",
        "license", "lisans", "website", "web site"
    ];

    private static readonly HashSet<string> NonProgramTargetExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".txt", ".md", ".pdf", ".chm", ".htm", ".html", ".url", ".website",
        ".doc", ".docx", ".rtf", ".ini", ".cfg", ".log", ".xml", ".json",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico", ".svg", ".zip", ".7z", ".rar"
    };

    private static readonly HashSet<string> CustomProgramExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".exe", ".lnk", ".appref-ms", ".bat", ".cmd", ".ps1", ".msc", ".cpl",
        ".desktop", ".sh", ".appimage", ".run", ".bin", ".command", ".app"
    };

    private static readonly HashSet<string> IgnoredStartMenuFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Startup", "Başlangıç", "Administrative Tools", "Windows Administrative Tools",
        "Accessibility", "Windows Ease of Access", "Maintenance"
    };

    public static IReadOnlyList<(string Path, ProgramSource Source)> DefaultRoots()
    {
        var roots = new List<(string Path, ProgramSource Source)>();
        void AddIfExists(string? path, ProgramSource source)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            var normalized = PathUtil.Normalize(path);
            if (!Directory.Exists(normalized)) return;
            if (roots.Any(r => PathUtil.Comparer.Equals(r.Path, normalized))) return;
            roots.Add((normalized, source));
        }

        if (OperatingSystem.IsWindows())
        {
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.Programs), ProgramSource.StartMenu);
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms), ProgramSource.StartMenu);
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProgramSource.Desktop);
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory), ProgramSource.Desktop);
        }
        else if (OperatingSystem.IsMacOS())
        {
            AddIfExists("/Applications", ProgramSource.Applications);
            AddIfExists("/System/Applications", ProgramSource.Applications);
            var userApps = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Applications");
            AddIfExists(userApps, ProgramSource.Applications);
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProgramSource.Desktop);
        }
        else
        {
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            AddIfExists(Path.Combine(home, ".local", "share", "applications"), ProgramSource.Applications);
            AddIfExists("/usr/local/share/applications", ProgramSource.Applications);
            AddIfExists("/usr/share/applications", ProgramSource.Applications);
            AddIfExists("/var/lib/flatpak/exports/share/applications", ProgramSource.Applications);
            AddIfExists("/var/lib/snapd/desktop/applications", ProgramSource.Applications);
            AddIfExists(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), ProgramSource.Desktop);
        }

        return roots;
    }

    /// <summary>
    /// Scans <paramref name="roots"/> (or <see cref="DefaultRoots"/> when null), merges user custom/favorite program
    /// paths, filters out uninstallers and broken shortcuts, and deduplicates entries.
    /// </summary>
    public static List<ProgramEntry> Scan(
        IEnumerable<(string Path, ProgramSource Source)>? roots = null,
        IEnumerable<string>? customPaths = null,
        IEnumerable<string>? favoritePaths = null,
        CancellationToken ct = default)
    {
        var effectiveRoots = roots?.ToList() ?? DefaultRoots().ToList();
        var entries = new List<ProgramEntry>();
        var seenLaunchPaths = new HashSet<string>(PathUtil.Comparer);
        var seenTargetsByName = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Scan OS program roots
        foreach (var (rootPath, source) in effectiveRoots)
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrWhiteSpace(rootPath) || !Directory.Exists(rootPath)) continue;

            var normalizedRoot = PathUtil.Normalize(rootPath);
            var recursive = source != ProgramSource.Desktop;
            ScanDirectory(normalizedRoot, normalizedRoot, source, recursive, entries, seenLaunchPaths, seenTargetsByName, ct);
        }

        // 2. Include user-added custom programs and favorited paths that may live outside standard roots
        var extraPaths = new List<(string Path, bool IsCustom)>();
        if (customPaths is not null)
        {
            foreach (var p in customPaths)
                if (!string.IsNullOrWhiteSpace(p)) extraPaths.Add((p, true));
        }
        if (favoritePaths is not null)
        {
            foreach (var p in favoritePaths)
                if (!string.IsNullOrWhiteSpace(p)) extraPaths.Add((p, false));
        }

        foreach (var (rawPath, isCustom) in extraPaths)
        {
            ct.ThrowIfCancellationRequested();
            var normalized = PathUtil.Normalize(rawPath);
            if (seenLaunchPaths.Contains(normalized)) continue;

            var entry = TryCreateExplicitEntry(normalized, isCustom ? ProgramSource.Custom : ProgramSource.Applications);
            if (entry is not null && seenLaunchPaths.Add(entry.LaunchPath))
                entries.Add(entry);
        }

        entries.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        return entries;
    }

    /// <summary>Creates a <see cref="ProgramEntry"/> for a user-picked file or `.app` bundle, or null if it does not exist.</summary>
    public static ProgramEntry? TryCreateExplicitEntry(string path, ProgramSource source = ProgramSource.Custom)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalized = PathUtil.Normalize(path);

        if (Directory.Exists(normalized))
        {
            if (!normalized.EndsWith(".app", StringComparison.OrdinalIgnoreCase)) return null;
            var appName = Path.GetFileNameWithoutExtension(normalized);
            if (string.IsNullOrWhiteSpace(appName)) return null;
            var parentName = Path.GetFileName(Path.GetDirectoryName(normalized)) ?? "";
            return new ProgramEntry(appName, normalized, normalized, parentName, source);
        }

        if (!File.Exists(normalized)) return null;

        var ext = Path.GetExtension(normalized);
        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var rawTarget = ShortcutFile.ReadTarget(normalized);
            var target = ResolveShortcutTarget(rawTarget) ?? normalized;
            var name = CleanDisplayName(Path.GetFileNameWithoutExtension(normalized));
            var category = Path.GetFileName(Path.GetDirectoryName(normalized)) ?? "";
            return string.IsNullOrWhiteSpace(name) ? null : new ProgramEntry(name, normalized, target, category, source);
        }

        if (string.Equals(ext, ".desktop", StringComparison.OrdinalIgnoreCase))
        {
            if (TryParseDesktopFile(normalized, source, categoryOverride: null) is { } desktopEntry)
                return desktopEntry with { Source = source };
        }

        var fileName = Path.GetFileNameWithoutExtension(normalized);
        if (string.IsNullOrWhiteSpace(fileName)) return null;
        var folder = Path.GetFileName(Path.GetDirectoryName(normalized)) ?? "";
        return new ProgramEntry(fileName, normalized, normalized, folder, source);
    }

    /// <summary>
    /// Filters and orders <paramref name="programs"/> according to <paramref name="filter"/>,
    /// <paramref name="bookmarks"/>, and search <paramref name="query"/>.
    /// </summary>
    public static List<ProgramEntry> FilterAndSort(
        IReadOnlyList<ProgramEntry> programs,
        ProgramBookmarks bookmarks,
        string? query = null,
        ProgramFilter filter = ProgramFilter.All)
    {
        var trimmed = query?.Trim() ?? "";
        var hasQuery = trimmed.Length > 0;
        var scored = new List<(ProgramEntry Entry, bool IsFav, int RecentIdx, int MatchRank)>(programs.Count);

        foreach (var entry in programs)
        {
            var isFav = bookmarks.IsFavorite(entry);
            var recentIdx = bookmarks.RecentIndex(entry);

            if (filter == ProgramFilter.Favorites && !isFav) continue;
            if (filter == ProgramFilter.Recent && recentIdx < 0) continue;

            var rank = 0;
            if (hasQuery)
            {
                rank = MatchRank(entry, trimmed);
                if (rank < 0) continue;
            }

            scored.Add((entry, isFav, recentIdx, rank));
        }

        scored.Sort((a, b) =>
        {
            if (hasQuery)
            {
                var rankCmp = a.MatchRank.CompareTo(b.MatchRank);
                if (rankCmp != 0) return rankCmp;
            }

            if (filter == ProgramFilter.Recent && !hasQuery)
            {
                return a.RecentIdx.CompareTo(b.RecentIdx);
            }

            // Favorites always sort to the top in All / Favorites views and within equal match ranks
            var favCmp = b.IsFav.CompareTo(a.IsFav);
            if (favCmp != 0) return favCmp;

            var nameCmp = StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry.Name, b.Entry.Name);
            if (nameCmp != 0) return nameCmp;

            return StringComparer.CurrentCultureIgnoreCase.Compare(a.Entry.Category, b.Entry.Category);
        });

        var result = new List<ProgramEntry>(scored.Count);
        foreach (var item in scored)
            result.Add(item.Entry);
        return result;
    }

    private static int MatchRank(ProgramEntry entry, string query)
    {
        var name = entry.Name;
        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
            return 0;
        if (name.StartsWith(query, StringComparison.OrdinalIgnoreCase))
            return 1;

        // Word prefix match (e.g., "code" in "Visual Studio Code")
        var idx = name.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx > 0 && !char.IsLetterOrDigit(name[idx - 1]))
            return 2;
        if (idx >= 0)
            return 3;

        // Acronym match (e.g., "vsc" -> "Visual Studio Code")
        if (query.Length >= 2 && MatchesAcronym(name, query))
            return 3;

        var targetExe = Path.GetFileNameWithoutExtension(entry.TargetPath);
        if (!string.IsNullOrEmpty(targetExe) && targetExe.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 4;

        if (!string.IsNullOrEmpty(entry.Category) && entry.Category.Contains(query, StringComparison.OrdinalIgnoreCase))
            return 5;

        return -1;
    }

    private static bool MatchesAcronym(ReadOnlySpan<char> name, ReadOnlySpan<char> query)
    {
        var qIdx = 0;
        var atWordStart = true;
        for (var i = 0; i < name.Length && qIdx < query.Length; i++)
        {
            var c = name[i];
            if (!char.IsLetterOrDigit(c))
            {
                atWordStart = true;
                continue;
            }
            if (atWordStart || char.IsUpper(c))
            {
                if (char.ToUpperInvariant(c) == char.ToUpperInvariant(query[qIdx]))
                    qIdx++;
            }
            atWordStart = false;
        }
        return qIdx == query.Length;
    }

    private static void ScanDirectory(
        string rootDir,
        string currentDir,
        ProgramSource source,
        bool recursive,
        List<ProgramEntry> entries,
        HashSet<string> seenLaunchPaths,
        HashSet<string> seenTargetsByName,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(currentDir);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var dir = pending.Pop();

            // Enumerate subdirectories (and handle macOS .app bundles)
            IEnumerable<string> subDirs = [];
            try { subDirs = Directory.EnumerateDirectories(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            foreach (var sub in subDirs)
            {
                if (sub.EndsWith(".app", StringComparison.OrdinalIgnoreCase))
                {
                    var appName = CleanDisplayName(Path.GetFileNameWithoutExtension(sub));
                    if (string.IsNullOrWhiteSpace(appName) || IsNoiseName(appName)) continue;
                    var normalizedApp = PathUtil.Normalize(sub);
                    if (!seenLaunchPaths.Add(normalizedApp)) continue;

                    var dedupKey = $"{appName.ToUpperInvariant()}|{normalizedApp}";
                    if (!seenTargetsByName.Add(dedupKey)) continue;

                    var category = GetRelativeCategory(rootDir, dir, source);
                    entries.Add(new ProgramEntry(appName, normalizedApp, normalizedApp, category, source));
                    continue;
                }

                if (!recursive) continue;

                try
                {
                    var info = new DirectoryInfo(sub);
                    if ((info.Attributes & FileAttributes.ReparsePoint) != 0 && info.LinkTarget is not null)
                        continue;
                    if (IgnoredStartMenuFolders.Contains(info.Name))
                        continue;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                pending.Push(sub);
            }

            // Enumerate files
            IEnumerable<string> files = [];
            try { files = Directory.EnumerateFiles(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }

            var dirCategory = GetRelativeCategory(rootDir, dir, source);
            foreach (var file in files)
            {
                var entry = TryReadProgramFile(file, dirCategory, source);
                if (entry is null) continue;

                if (!seenLaunchPaths.Add(entry.LaunchPath)) continue;

                // Deduplicate identical app entries across Start Menu and Desktop
                var targetKey = PathUtil.Normalize(entry.TargetPath);
                var nameKey = entry.Name.ToUpperInvariant();
                if (!seenTargetsByName.Add($"{nameKey}|{targetKey}") || !seenTargetsByName.Add($"NAME:{nameKey}"))
                    continue;

                entries.Add(entry);
            }
        }
    }

    private static ProgramEntry? TryReadProgramFile(string filePath, string category, ProgramSource source)
    {
        var ext = Path.GetExtension(filePath);
        if (string.IsNullOrEmpty(ext)) return null;

        var normalized = PathUtil.Normalize(filePath);

        if (string.Equals(ext, ".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var rawName = Path.GetFileNameWithoutExtension(normalized);
            var name = CleanDisplayName(rawName);
            if (string.IsNullOrWhiteSpace(name) || IsNoiseName(name)) return null;

            var rawTarget = ShortcutFile.ReadTarget(normalized);
            string target;
            if (rawTarget is not null)
            {
                var resolved = ResolveShortcutTarget(rawTarget);
                if (resolved is null) return null; // points to a folder, non-program file, or missing target
                if (IsNoiseName(Path.GetFileNameWithoutExtension(resolved))) return null;
                target = resolved;
            }
            else
            {
                // Shortcuts pointing at shell items (UWP / Store apps) have no LinkInfo path in the .lnk
                target = normalized;
            }

            return new ProgramEntry(name, normalized, target, category, source);
        }

        if (string.Equals(ext, ".appref-ms", StringComparison.OrdinalIgnoreCase))
        {
            var name = CleanDisplayName(Path.GetFileNameWithoutExtension(normalized));
            if (string.IsNullOrWhiteSpace(name) || IsNoiseName(name)) return null;
            return new ProgramEntry(name, normalized, normalized, category, source);
        }

        if (string.Equals(ext, ".exe", StringComparison.OrdinalIgnoreCase))
        {
            var name = CleanDisplayName(Path.GetFileNameWithoutExtension(normalized));
            if (string.IsNullOrWhiteSpace(name) || IsNoiseName(name)) return null;
            return new ProgramEntry(name, normalized, normalized, category, source);
        }

        if (string.Equals(ext, ".desktop", StringComparison.OrdinalIgnoreCase))
        {
            return TryParseDesktopFile(normalized, source, category);
        }

        return null;
    }

    private static string? ResolveShortcutTarget(string? rawTarget)
    {
        if (string.IsNullOrWhiteSpace(rawTarget)) return null;
        try
        {
            var expanded = Environment.ExpandEnvironmentVariables(rawTarget);
            if (Directory.Exists(expanded)) return null; // folder shortcut

            var ext = Path.GetExtension(expanded);
            if (!string.IsNullOrEmpty(ext) && NonProgramTargetExtensions.Contains(ext))
                return null;

            if (File.Exists(expanded))
                return PathUtil.Normalize(expanded);

            // Handle 32-bit/64-bit Program Files redirection in .lnk files
            const string pf86 = "Program Files (x86)";
            const string pf64 = "Program Files";
            if (expanded.Contains(pf86, StringComparison.OrdinalIgnoreCase))
            {
                var alt = expanded.Replace(pf86, pf64, StringComparison.OrdinalIgnoreCase);
                if (File.Exists(alt)) return PathUtil.Normalize(alt);
            }
            else if (expanded.Contains(pf64, StringComparison.OrdinalIgnoreCase))
            {
                var alt = expanded.Replace(pf64, pf86, StringComparison.OrdinalIgnoreCase);
                if (File.Exists(alt)) return PathUtil.Normalize(alt);
            }

            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static ProgramEntry? TryParseDesktopFile(string filePath, ProgramSource source, string? categoryOverride)
    {
        try
        {
            string? name = null;
            string? exec = null;
            string? categories = null;
            var inDesktopEntry = false;
            var isApp = true;

            foreach (var rawLine in File.ReadLines(filePath))
            {
                var line = rawLine.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                if (line[0] == '[')
                {
                    inDesktopEntry = string.Equals(line, "[Desktop Entry]", StringComparison.OrdinalIgnoreCase);
                    continue;
                }
                if (!inDesktopEntry) continue;

                var eq = line.IndexOf('=');
                if (eq <= 0) continue;

                var key = line[..eq].Trim();
                var val = line[(eq + 1)..].Trim();

                if (string.Equals(key, "NoDisplay", StringComparison.OrdinalIgnoreCase) && string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (string.Equals(key, "Hidden", StringComparison.OrdinalIgnoreCase) && string.Equals(val, "true", StringComparison.OrdinalIgnoreCase))
                    return null;
                if (string.Equals(key, "Type", StringComparison.OrdinalIgnoreCase) && !string.Equals(val, "Application", StringComparison.OrdinalIgnoreCase))
                    isApp = false;
                if (string.Equals(key, "Name", StringComparison.Ordinal) && name is null)
                    name = val;
                if (string.Equals(key, "Exec", StringComparison.OrdinalIgnoreCase) && exec is null)
                    exec = val;
                if (string.Equals(key, "Categories", StringComparison.OrdinalIgnoreCase) && categories is null)
                    categories = val;
            }

            if (!isApp || string.IsNullOrWhiteSpace(name) || IsNoiseName(name)) return null;

            var target = ExtractDesktopExecBinary(exec) ?? filePath;
            var category = !string.IsNullOrWhiteSpace(categoryOverride)
                ? categoryOverride
                : categories?.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

            return new ProgramEntry(CleanDisplayName(name), filePath, target, category, source);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string? ExtractDesktopExecBinary(string? exec)
    {
        if (string.IsNullOrWhiteSpace(exec)) return null;
        var span = exec.AsSpan().Trim();
        if (span.Length == 0) return null;

        string token;
        if (span[0] == '"')
        {
            var close = span[1..].IndexOf('"');
            token = close >= 0 ? span.Slice(1, close).ToString() : span[1..].ToString();
        }
        else
        {
            var space = span.IndexOf(' ');
            token = space >= 0 ? span[..space].ToString() : span.ToString();
        }

        return token.Length > 0 ? token : null;
    }

    private static string GetRelativeCategory(string rootDir, string currentDir, ProgramSource source)
    {
        if (PathUtil.Comparer.Equals(rootDir, currentDir))
            return "";

        var rel = Path.GetRelativePath(rootDir, currentDir);
        if (rel == "." || string.IsNullOrWhiteSpace(rel))
            return "";

        var firstSegment = rel.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)[0];
        return firstSegment;
    }

    private static string CleanDisplayName(string name)
    {
        var trimmed = name.Trim();
        const string shortcutSuffixEn = " - Shortcut";
        const string shortcutSuffixTr = " - Kısayol";
        if (trimmed.EndsWith(shortcutSuffixEn, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^shortcutSuffixEn.Length].Trim();
        else if (trimmed.EndsWith(shortcutSuffixTr, StringComparison.OrdinalIgnoreCase))
            trimmed = trimmed[..^shortcutSuffixTr.Length].Trim();
        return trimmed;
    }

    private static bool IsNoiseName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        foreach (var keyword in NoiseKeywords)
        {
            if (name.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    public static IReadOnlyCollection<string> SupportedCustomExtensions => CustomProgramExtensions;
}
