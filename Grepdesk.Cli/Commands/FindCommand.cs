using System.IO.Enumeration;
using System.Text.Json;
using Grepdesk.Core;
using Grepdesk.Core.Grouping;

namespace Grepdesk.Cli.Commands;

internal static class FindCommand
{
    public sealed record FindEntryDto(
        string Path,
        string Name,
        string Type,
        bool IsDirectory,
        long Size,
        DateTime Modified);

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions JsonCompact = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = false
    };

    public static async Task<int> ExecuteAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        string? pattern = null;
        string rootPath = ".";
        string? typeFilter = null;
        HashSet<string>? extFilter = null;
        bool includeHidden = false;
        int? maxDepth = null;
        bool jsonOutput = false;
        bool jsonlOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp(stdout);
                    return 0;

                case "-p":
                case "--path":
                    if (i + 1 >= args.Length) return Fail(stderr, "Missing value for --path.");
                    rootPath = args[++i];
                    break;

                case "-t":
                case "--type":
                    if (i + 1 >= args.Length) return Fail(stderr, "Missing value for --type.");
                    typeFilter = args[++i].Trim().ToLowerInvariant();
                    if (!IsValidTypeFilter(typeFilter))
                        return Fail(stderr, $"Invalid --type '{typeFilter}'. Expected: file, dir, archive, code, image, doc, video, audio, installer.");
                    break;

                case "-e":
                case "--ext":
                    if (i + 1 >= args.Length) return Fail(stderr, "Missing value for --ext.");
                    extFilter = ParseExtensions(args[++i]);
                    break;

                case "--hidden":
                    includeHidden = true;
                    break;

                case "--max-depth":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var depth) || depth < 1)
                        return Fail(stderr, "Invalid --max-depth value. Must be a positive integer.");
                    maxDepth = depth;
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                case "--jsonl":
                    jsonlOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk find --help' for usage.");
                    if (pattern is null)
                        pattern = arg;
                    else
                        return Fail(stderr, $"Unexpected positional argument '{arg}'.");
                    break;
            }
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));
        if (!Directory.Exists(fullRoot))
            return Fail(stderr, $"Directory not found: {fullRoot}");

        using var index = new FileIndex();
        await index.BuildIndexAsync([fullRoot], ct, watch: false);

        bool isGlob = pattern is not null && (pattern.Contains('*') || pattern.Contains('?'));
        var matches = new List<FindEntryDto>();

        foreach (var (path, entry) in index.Entries)
        {
            ct.ThrowIfCancellationRequested();

            // Skip the root directory entry itself
            if (string.Equals(Path.TrimEndingDirectorySeparator(path), fullRoot, StringComparison.OrdinalIgnoreCase))
                continue;

            if (maxDepth.HasValue && CliFormatting.GetDepth(fullRoot, path) > maxDepth.Value)
                continue;

            if (!includeHidden && CliFormatting.IsHiddenOrSystem(path, fullRoot))
                continue;

            var name = Path.GetFileName(path);
            if (!MatchesPattern(name, pattern, isGlob))
                continue;

            if (extFilter is not null)
            {
                if (entry.IsDirectory) continue;
                var ext = Path.GetExtension(name);
                if (!extFilter.Contains(ext)) continue;
            }

            var bucket = FolderGrouper.BucketOf(new GroupMember(path, entry.IsDirectory, entry.Size, entry.Modified));
            if (typeFilter is not null && !MatchesTypeFilter(entry.IsDirectory, bucket, typeFilter))
                continue;

            var typeLabel = entry.IsDirectory ? "dir" : BucketToShortType(bucket);
            matches.Add(new FindEntryDto(
                CliFormatting.RelativeOrFull(fullRoot, path),
                name,
                typeLabel,
                entry.IsDirectory,
                entry.Size,
                entry.Modified));
        }

        matches.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));

        if (jsonlOutput)
        {
            foreach (var m in matches)
                await stdout.WriteLineAsync(JsonSerializer.Serialize(m, JsonCompact));
        }
        else if (jsonOutput)
        {
            await stdout.WriteLineAsync(JsonSerializer.Serialize(matches, JsonIndented));
        }
        else
        {
            foreach (var m in matches)
            {
                var suffix = m.IsDirectory ? "/" : "";
                await stdout.WriteLineAsync($"{m.Path}{suffix}");
            }
        }

        return matches.Count > 0 ? 0 : 1;
    }

    internal static HashSet<string> ParseExtensions(string raw)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var ext = part.StartsWith('.') ? part : "." + part;
            set.Add(ext);
        }
        return set;
    }

    private static bool MatchesPattern(string name, string? pattern, bool isGlob)
    {
        if (string.IsNullOrEmpty(pattern) || pattern == "*")
            return true;
        if (isGlob)
            return FileSystemName.MatchesSimpleExpression(pattern, name, ignoreCase: true);
        return name.Contains(pattern, StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsValidTypeFilter(string type) => type is
        "file" or "f" or
        "dir" or "d" or "directory" or "folder" or
        "archive" or "archives" or
        "code" or
        "image" or "images" or
        "doc" or "docs" or "document" or "documents" or
        "video" or "videos" or
        "audio" or
        "installer" or "installers";

    private static bool MatchesTypeFilter(bool isDir, TypeBucket bucket, string filter) => filter switch
    {
        "file" or "f" => !isDir,
        "dir" or "d" or "directory" or "folder" => isDir,
        "archive" or "archives" => !isDir && bucket == TypeBucket.Archives,
        "code" => !isDir && bucket == TypeBucket.Code,
        "image" or "images" => !isDir && bucket == TypeBucket.Images,
        "doc" or "docs" or "document" or "documents" => !isDir && bucket == TypeBucket.Documents,
        "video" or "videos" => !isDir && bucket == TypeBucket.Videos,
        "audio" => !isDir && bucket == TypeBucket.Audio,
        "installer" or "installers" => !isDir && bucket == TypeBucket.Installers,
        _ => true
    };

    private static string BucketToShortType(TypeBucket bucket) => bucket switch
    {
        TypeBucket.Archives => "archive",
        TypeBucket.Code => "code",
        TypeBucket.Images => "image",
        TypeBucket.Documents => "doc",
        TypeBucket.Videos => "video",
        TypeBucket.Audio => "audio",
        TypeBucket.Installers => "installer",
        TypeBucket.DiskImages => "disk-image",
        TypeBucket.Fonts => "font",
        TypeBucket.Torrents => "torrent",
        TypeBucket.Folders => "dir",
        _ => "file"
    };

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk find [pattern] [options]

            Instant file and directory name search using FileIndex.

            Options:
              -p, --path <dir>       Root path to search (default: .)
              -t, --type <type>      Filter by type: file|dir|archive|code|image|doc|video|audio
              -e, --ext <ext1,ext2>  Filter by extension list (e.g. cs,xaml)
              --hidden               Include hidden and system files
              --max-depth <n>        Limit directory recursion depth
              --json                 Output as a JSON array of matching file metadata
              --jsonl                Output as streaming JSON Lines
              -h, --help             Show this help message
            """);
    }
}
