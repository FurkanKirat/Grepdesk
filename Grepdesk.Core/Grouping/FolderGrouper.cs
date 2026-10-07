using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;

namespace Grepdesk.Core.Grouping;

public enum GroupingMode
{
    /// <summary>
    /// Series of the same name (dracula_idle_*.png), then families sharing their first
    /// words (Lecture…pdf, Microsoft.Extensions…nupkg), then the rest by type.
    /// </summary>
    Series,
    /// <summary>The website a download came from (Windows' Zone.Identifier).</summary>
    Source,
    /// <summary>Installers, archives, documents, images...</summary>
    Type,
    /// <summary>Files that arrived together (no more than 30 minutes apart).</summary>
    Session,
    /// <summary>Things that are probably not needed: extracted archives, re-downloads.</summary>
    Redundant,
}

public enum TypeBucket { Installers, Archives, DiskImages, Documents, Images, Videos, Audio, Code, Fonts, Torrents, Folders, Other }

public enum RedundancyKind
{
    None,
    /// <summary>An archive whose contents already sit next to it in a folder of the same name.</summary>
    ExtractedArchive,
    /// <summary>"name (1).ext" next to "name.ext" of the same size: the same download twice.</summary>
    RepeatedDownload,
}

/// <param name="Related">For redundant items: the extracted folder or the original download.</param>
public sealed record GroupMember(
    string Path,
    bool IsDirectory,
    long Size,
    DateTime Modified,
    RedundancyKind Redundancy = RedundancyKind.None,
    string? Related = null)
{
    public string Name => System.IO.Path.GetFileName(Path);
}

/// <param name="Key">Stable identity of the group (to keep it selected after a refresh).</param>
/// <param name="Label">
/// Series: the pattern ("dracula_idle_*.png"); Source: the site; Type: the
/// <see cref="TypeBucket"/> name; Redundant: the <see cref="RedundancyKind"/> name;
/// Session: empty (see Start/End). The UI turns it into a title.
/// </param>
/// <param name="IsLeftover">The catch-all group: files that fit no group, or have no known source.</param>
/// <summary>How a group of the smart (Series) mode was formed.</summary>
public enum SeriesKind
{
    /// <summary>Same name pattern: Label is the pattern ("dracula_idle_*.png").</summary>
    Pattern,
    /// <summary>Same first words: Label is the shared beginning ("Microsoft.Extensions").</summary>
    Family,
    /// <summary>What fit no series or family, by type: Label is the <see cref="TypeBucket"/> name.</summary>
    Rest,
}

public sealed record FileGroup(
    GroupingMode Mode,
    string Key,
    string Label,
    IReadOnlyList<GroupMember> Members,
    bool IsLeftover = false,
    DateTime? Start = null,
    DateTime? End = null,
    SeriesKind Kind = SeriesKind.Pattern)
{
    public long Size { get; } = Members.Sum(m => m.Size);
}

/// <summary>
/// Splits a folder's contents into groups a person can make sense of. Purely
/// rule-based: the same folder always gives the same groups, nothing leaves
/// the machine, and nothing is guessed about what a file contains.
/// </summary>
public static partial class FolderGrouper
{
    private static readonly TimeSpan SessionGap = TimeSpan.FromMinutes(30);

    /// <summary>The folder's direct children; subfolders carry the total size of their contents.</summary>
    public static List<GroupMember> ReadFolder(string folder, CancellationToken ct)
    {
        var members = new List<GroupMember>();
        foreach (var info in new DirectoryInfo(folder).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (info is FileInfo file)
                {
                    members.Add(new GroupMember(file.FullName, false, file.Length, file.LastWriteTime));
                }
                else if (info is DirectoryInfo dir)
                {
                    long size = 0;
                    foreach (var f in dir.EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }))
                    {
                        ct.ThrowIfCancellationRequested();
                        size += f.Length;
                    }
                    members.Add(new GroupMember(dir.FullName, true, size, dir.LastWriteTime));
                }
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return members;
    }

    /// <param name="sourceOf">Where a file was downloaded from; defaults to <see cref="ReadSource"/>.</param>
    public static List<FileGroup> Group(IReadOnlyList<GroupMember> items, GroupingMode mode, Func<string, string?>? sourceOf = null) =>
        mode switch
        {
            GroupingMode.Series => BySeries(items),
            GroupingMode.Source => BySource(items, sourceOf ?? ReadSource),
            GroupingMode.Type => ByType(items),
            GroupingMode.Session => BySession(items),
            GroupingMode.Redundant => Redundant(items),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    // =====================================================================
    // Series
    // =====================================================================

    [GeneratedRegex(@"(\s*-\s*(copy|kopya|kopyası)(\s*\(\d+\))?|\s*\(\d+\)|[ _]copy\d*)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyMarker();

    // 2026-10-07, 20261007, 2026_10_07 14.22.10, "2026-10-07 at 14.22.10"
    [GeneratedRegex(@"(19|20)\d{2}[-_.]?(0[1-9]|1[0-2])[-_.]?(0[1-9]|[12]\d|3[01])([ _T.-]*(at\s*)?\d{1,2}[-_.:]\d{2}([-_.:]\d{2})?)?", RegexOptions.IgnoreCase)]
    private static partial Regex DateStamp();

    // Dates written with a month name, English or Turkish, with an optional time:
    // "24 May 2025 21 13", "May 24, 2025", "24 Mayıs 2025 21.13", "2025 Jun 3".
    private const string Month =
        "jan(uary)?|feb(ruary)?|mar(ch)?|apr(il)?|may|june?|july?|aug(ust)?|sep(t(ember)?)?|oct(ober)?|nov(ember)?|dec(ember)?"
        + "|ocak|şubat|subat|mart|nisan|mayıs|mayis|haziran|temmuz|ağustos|agustos|eylül|eylul|ekim|kasım|kasim|aralık|aralik";

    private const string OptionalTime = @"([ _T.,-]*(at\s*)?\d{1,2}[ _.:h-]\d{2}([ _.:-]\d{2})?(\s*[ap]\.?m\.?)?)?";

    [GeneratedRegex(
        @"(\d{1,2}[ _.-]*(" + Month + @")(?!\p{L})[ _.,-]*(\d{4})?" + OptionalTime + @")"
        + @"|((" + Month + @")(?!\p{L})[ _.-]*\d{1,2}(st|nd|rd|th)?,?[ _.-]*(\d{4})?" + OptionalTime + @")"
        + @"|(\d{4}[ _.-]*(" + Month + @")(?!\p{L})[ _.-]*\d{1,2}" + OptionalTime + @")",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex TextDate();

    [GeneratedRegex(@"[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}", RegexOptions.IgnoreCase)]
    private static partial Regex Uuid();

    // A long run of hex with both digits and letters: hashes, ids.
    [GeneratedRegex(@"(?=[0-9a-f]*\d)(?=[0-9a-f]*[a-f])[0-9a-f]{12,}", RegexOptions.IgnoreCase)]
    private static partial Regex HexId();

    [GeneratedRegex(@"\d+")]
    private static partial Regex Digits();

    [GeneratedRegex(@"\*+")]
    private static partial Regex Stars();

    // "IMG_*_*" (date then time) or "* *" are one varying part, not two.
    [GeneratedRegex(@"\*([ _.\-]+\*)+")]
    private static partial Regex SeparatedStars();

    /// <summary>
    /// The pattern a name belongs to: numbers, dates (also "24 May 2025"), ids and
    /// copy markers become '*'. "dracula_idle_12.png" → "dracula_idle_*.png",
    /// "Report (1).pdf" → "Report.pdf", "ChatGPT Image 24 May 2025 21 13.png" → "ChatGPT Image *.png".
    /// </summary>
    public static string SeriesPattern(string name, bool isDirectory)
    {
        var ext = isDirectory ? "" : Path.GetExtension(name);
        var stem = isDirectory ? name : Path.GetFileNameWithoutExtension(name);

        // A repeated copy marker ("x (1) (2)") is stripped repeatedly.
        string previous;
        do
        {
            previous = stem;
            stem = CopyMarker().Replace(stem, "");
        } while (stem != previous);

        stem = Uuid().Replace(stem, "*");
        stem = DateStamp().Replace(stem, "*");
        stem = TextDate().Replace(stem, "*");
        stem = HexId().Replace(stem, "*");
        stem = Digits().Replace(stem, "*");
        stem = Stars().Replace(stem, "*");
        stem = SeparatedStars().Replace(stem, "*");
        return stem.Trim() + ext;
    }

    private static readonly char[] WordSeparators = [' ', '.', '_', '-', '(', ')', '[', ']', ',', '+'];

    /// <summary>
    /// Three passes, each taking what the previous one left:
    /// 1. series — the exact same pattern (dracula_idle_*.png);
    /// 2. families — the same extension and first word ("Lecture 1 - Intro.pdf" and
    ///    "Lecture 2 - Sorting.pdf"; "Microsoft.Extensions.Logging…" and "…Hosting…");
    /// 3. the rest by type, so nothing ends up in one huge "everything else" pile.
    /// Only files of an unknown type are left over at the end.
    /// </summary>
    private static List<FileGroup> BySeries(IReadOnlyList<GroupMember> items)
    {
        var patterns = items
            .GroupBy(m => SeriesPattern(m.Name, m.IsDirectory), StringComparer.OrdinalIgnoreCase)
            .ToList();

        var result = patterns
            .Where(g => g.Count() > 1)
            .Select(g => new FileGroup(GroupingMode.Series, "series:" + g.Key.ToLowerInvariant(),
                SeriesPattern(g.First().Name, g.First().IsDirectory), Sorted(g)))
            .ToList();

        var singles = patterns.Where(g => g.Count() == 1).SelectMany(g => g).ToList();

        // Families: same extension, same first word (of at least 3 letters).
        var families = singles
            .Where(m => FirstWord(m) is not null)
            .GroupBy(m => (Ext: m.IsDirectory ? "/" : Path.GetExtension(m.Name).ToLowerInvariant(), Word: FirstWord(m)!.ToLowerInvariant()))
            .Where(g => g.Count() > 1)
            .ToList();
        foreach (var family in families)
        {
            var members = family.ToList();
            result.Add(new FileGroup(GroupingMode.Series, $"family:{family.Key.Ext}:{family.Key.Word}",
                SharedBeginning(members), Sorted(members), Kind: SeriesKind.Family));
        }
        var grouped = families.SelectMany(f => f).ToHashSet();
        singles = singles.Where(m => !grouped.Contains(m)).ToList();

        result = result.OrderByDescending(g => g.Members.Count).ThenByDescending(g => g.Size).ToList();

        // The rest, by type; unknown types last.
        result.AddRange(singles
            .GroupBy(BucketOf)
            .Select(g => new FileGroup(GroupingMode.Series, "rest:" + g.Key, g.Key.ToString(), Sorted(g),
                IsLeftover: g.Key == TypeBucket.Other, Kind: SeriesKind.Rest))
            .OrderBy(g => g.IsLeftover).ThenByDescending(g => g.Size));
        return result;
    }

    /// <summary>The first real word of a name: letters, 3+ characters, not a number or date.</summary>
    private static string? FirstWord(GroupMember m)
    {
        var pattern = SeriesPattern(m.Name, m.IsDirectory);
        var stem = m.IsDirectory ? pattern : Path.GetFileNameWithoutExtension(pattern);
        var word = stem.Split(WordSeparators, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return word is { Length: >= 3 } && !word.Contains('*') && word.Any(char.IsLetter) ? word : null;
    }

    /// <summary>
    /// The longest beginning the names share, cut at a word boundary:
    /// "Microsoft.Extensions.Logging.8.0.0" + "Microsoft.Extensions.Hosting.8.0.0" → "Microsoft.Extensions".
    /// </summary>
    private static string SharedBeginning(List<GroupMember> members)
    {
        var stems = members.Select(m => m.IsDirectory ? m.Name : Path.GetFileNameWithoutExtension(m.Name)).ToList();
        var prefix = stems[0];
        foreach (var stem in stems.Skip(1))
        {
            var length = 0;
            while (length < prefix.Length && length < stem.Length && char.ToLowerInvariant(prefix[length]) == char.ToLowerInvariant(stem[length]))
                length++;
            prefix = prefix[..length];
        }

        // Don't end mid-word: "Lecture 1" and "Lecture 12" share "Lecture 1", which isn't a word.
        var all = stems.All(s => s.Length == prefix.Length || WordSeparators.Contains(s[prefix.Length]));
        if (!all)
        {
            var cut = prefix.LastIndexOfAny(WordSeparators);
            prefix = cut > 0 ? prefix[..cut] : prefix;
        }
        return prefix.TrimEnd(WordSeparators);
    }

    // =====================================================================
    // Source
    // =====================================================================

    /// <summary>Reads the site a file was downloaded from, if the browser recorded it.</summary>
    public static string? ReadSource(string path)
    {
        if (!OperatingSystem.IsWindows() || Directory.Exists(path)) return null;
        try
        {
            // Browsers attach this alternate data stream to every download.
            return ParseZoneIdentifier(File.ReadAllText(path + ":Zone.Identifier"));
        }
        catch (IOException) { return null; }               // no stream: not downloaded, or copied from elsewhere
        catch (UnauthorizedAccessException) { return null; }
        catch (NotSupportedException) { return null; }     // FAT/exFAT drives have no streams
    }

    /// <summary>
    /// The site from a Zone.Identifier stream. The page the download was started
    /// from (ReferrerUrl) names the site better than the file's host, which is
    /// often a CDN ("objects.githubusercontent.com").
    /// </summary>
    public static string? ParseZoneIdentifier(string content)
    {
        string? referrer = null, host = null;
        foreach (var raw in content.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith("ReferrerUrl=", StringComparison.OrdinalIgnoreCase)) referrer = line["ReferrerUrl=".Length..];
            else if (line.StartsWith("HostUrl=", StringComparison.OrdinalIgnoreCase)) host = line["HostUrl=".Length..];
        }
        return SiteOf(referrer) ?? SiteOf(host);
    }

    private static string? SiteOf(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || !Uri.TryCreate(url, UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme is not ("http" or "https")) return null; // about:blank, blob:, data:
        var host = uri.Host.ToLowerInvariant();
        return host.StartsWith("www.") ? host[4..] : host;
    }

    private static List<FileGroup> BySource(IReadOnlyList<GroupMember> items, Func<string, string?> sourceOf)
    {
        var bySite = items.GroupBy(m => sourceOf(m.Path) ?? "", StringComparer.OrdinalIgnoreCase).ToList();
        var result = bySite
            .Where(g => g.Key.Length > 0)
            .Select(g => new FileGroup(GroupingMode.Source, "source:" + g.Key, g.Key, Sorted(g)))
            .OrderByDescending(g => g.Members.Count).ThenByDescending(g => g.Size)
            .ToList();

        var unknown = bySite.FirstOrDefault(g => g.Key.Length == 0);
        if (unknown is not null)
            result.Add(new FileGroup(GroupingMode.Source, "source:", "", Sorted(unknown), IsLeftover: true));
        return result;
    }

    // =====================================================================
    // Type
    // =====================================================================

    private static readonly Dictionary<string, TypeBucket> Buckets = BuildBuckets();

    private static Dictionary<string, TypeBucket> BuildBuckets()
    {
        var map = new Dictionary<string, TypeBucket>(StringComparer.OrdinalIgnoreCase);
        void Add(TypeBucket b, params string[] exts) { foreach (var e in exts) map[e] = b; }

        Add(TypeBucket.Installers, ".exe", ".msi", ".msix", ".msixbundle", ".appx", ".appxbundle", ".dmg", ".pkg", ".deb", ".rpm", ".appimage", ".apk");
        Add(TypeBucket.Archives, ".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz", ".zst",
            ".nupkg", ".whl", ".jar", ".vsix", ".crx", ".xpi");
        Add(TypeBucket.DiskImages, ".iso", ".img", ".vhd", ".vhdx", ".vmdk");
        Add(TypeBucket.Documents, ".pdf", ".doc", ".docx", ".xls", ".xlsx", ".csv", ".ppt", ".pptx", ".odt", ".ods", ".odp", ".rtf", ".txt", ".md", ".epub");
        Add(TypeBucket.Images, ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg", ".heic", ".tif", ".tiff", ".psd", ".ico", ".avif");
        Add(TypeBucket.Videos, ".mp4", ".mkv", ".avi", ".mov", ".wmv", ".webm", ".m4v");
        Add(TypeBucket.Audio, ".mp3", ".wav", ".flac", ".ogg", ".m4a", ".aac", ".opus");
        Add(TypeBucket.Code, ".cs", ".js", ".ts", ".py", ".java", ".cpp", ".c", ".h", ".json", ".xml", ".yaml", ".yml", ".html", ".css", ".sql", ".sh", ".ps1", ".ipynb");
        Add(TypeBucket.Fonts, ".ttf", ".otf", ".woff", ".woff2");
        Add(TypeBucket.Torrents, ".torrent");
        return map;
    }

    public static TypeBucket BucketOf(GroupMember m) =>
        m.IsDirectory ? TypeBucket.Folders : Buckets.GetValueOrDefault(Path.GetExtension(m.Name), TypeBucket.Other);

    private static List<FileGroup> ByType(IReadOnlyList<GroupMember> items) =>
        items.GroupBy(BucketOf)
            .Select(g => new FileGroup(GroupingMode.Type, "type:" + g.Key, g.Key.ToString(), Sorted(g), IsLeftover: g.Key == TypeBucket.Other))
            .OrderBy(g => g.IsLeftover).ThenByDescending(g => g.Size)
            .ToList();

    // =====================================================================
    // Session
    // =====================================================================

    private static List<FileGroup> BySession(IReadOnlyList<GroupMember> items)
    {
        var result = new List<FileGroup>();
        var current = new List<GroupMember>();

        void Close()
        {
            if (current.Count == 0) return;
            var start = current[0].Modified;
            var end = current[^1].Modified;
            result.Add(new FileGroup(GroupingMode.Session, "session:" + start.Ticks, "", current.OrderByDescending(m => m.Size).ToList(),
                Start: start, End: end));
            current = [];
        }

        foreach (var m in items.OrderBy(m => m.Modified))
        {
            if (current.Count > 0 && m.Modified - current[^1].Modified > SessionGap) Close();
            current.Add(m);
        }
        Close();

        result.Reverse(); // newest first
        return result;
    }

    // =====================================================================
    // Redundant
    // =====================================================================

    private static readonly HashSet<string> ArchiveExtensions =
        new([".zip", ".rar", ".7z", ".tar", ".gz", ".tgz", ".bz2", ".xz"], StringComparer.OrdinalIgnoreCase);

    private static List<FileGroup> Redundant(IReadOnlyList<GroupMember> items)
    {
        var folders = items.Where(m => m.IsDirectory).ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);
        var files = items.Where(m => !m.IsDirectory).ToList();
        var filesByName = files.ToDictionary(m => m.Name, StringComparer.OrdinalIgnoreCase);

        var extracted = new List<GroupMember>();
        var repeated = new List<GroupMember>();

        foreach (var file in files)
        {
            // "project.zip" next to "project\" (also "project.tar.gz").
            if (ArchiveExtensions.Contains(Path.GetExtension(file.Name)))
            {
                var stem = Path.GetFileNameWithoutExtension(file.Name);
                if (stem.EndsWith(".tar", StringComparison.OrdinalIgnoreCase)) stem = stem[..^4];
                var baseStem = CopyMarker().Replace(stem, "");
                if (folders.TryGetValue(stem, out var folder) || folders.TryGetValue(baseStem, out folder))
                {
                    extracted.Add(file with { Redundancy = RedundancyKind.ExtractedArchive, Related = folder.Name });
                    continue;
                }
            }

            // "setup (1).exe" next to "setup.exe" of the same size.
            var ext = Path.GetExtension(file.Name);
            var bare = Path.GetFileNameWithoutExtension(file.Name);
            var original = CopyMarker().Replace(bare, "");
            if (original != bare && filesByName.TryGetValue(original + ext, out var first) && first.Size == file.Size)
                repeated.Add(file with { Redundancy = RedundancyKind.RepeatedDownload, Related = first.Name });
        }

        var result = new List<FileGroup>();
        if (extracted.Count > 0)
            result.Add(new FileGroup(GroupingMode.Redundant, "redundant:extracted", nameof(RedundancyKind.ExtractedArchive), Sorted(extracted)));
        if (repeated.Count > 0)
            result.Add(new FileGroup(GroupingMode.Redundant, "redundant:repeated", nameof(RedundancyKind.RepeatedDownload), Sorted(repeated)));
        return result.OrderByDescending(g => g.Size).ToList();
    }

    // Natural order: dracula_idle_2 before dracula_idle_10.
    private static readonly StringComparer NaturalOrder =
        StringComparer.Create(System.Globalization.CultureInfo.CurrentCulture,
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.NumericOrdering);

    private static List<GroupMember> Sorted(IEnumerable<GroupMember> members) =>
        members.OrderBy(m => m.Name, NaturalOrder).ToList();
}
