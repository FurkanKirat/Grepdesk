using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using UglyToad.PdfPig;

namespace Grepdesk.Cli.Commands;

internal static class GrepCommand
{
    public sealed record GrepMatchDto(
        string File,
        string Format,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Page,
        int Line,
        string Match,
        string Snippet);

    public sealed record GrepReportDto(
        string Query,
        int TotalMatches,
        long ElapsedMs,
        IReadOnlyList<GrepMatchDto> Results);

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

    private const long MaxFileSizeBytes = 20L * 1024 * 1024;

    public static async Task<int> ExecuteAsync(string[] args, TextWriter stdout, TextWriter stderr, CancellationToken ct)
    {
        string? query = null;
        string rootPath = ".";
        HashSet<string>? extFilter = null;
        bool caseSensitive = false;
        int contextChars = 60;
        int? maxMatches = null;
        bool includeHidden = false;
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

                case "-e":
                case "--ext":
                    if (i + 1 >= args.Length) return Fail(stderr, "Missing value for --ext.");
                    extFilter = FindCommand.ParseExtensions(args[++i]);
                    break;

                case "-i":
                case "--ignore-case":
                    caseSensitive = false;
                    break;

                case "-s":
                case "--case-sensitive":
                    caseSensitive = true;
                    break;

                case "-c":
                case "--context":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var ctx) || ctx < 0)
                        return Fail(stderr, "Invalid --context value. Must be a non-negative integer.");
                    contextChars = ctx;
                    break;

                case "--max-matches":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out var maxM) || maxM < 1)
                        return Fail(stderr, "Invalid --max-matches value. Must be a positive integer.");
                    maxMatches = maxM;
                    break;

                case "--hidden":
                    includeHidden = true;
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                case "--jsonl":
                    jsonlOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk grep --help' for usage.");
                    if (query is null)
                        query = arg;
                    else
                        return Fail(stderr, $"Unexpected positional argument '{arg}'.");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(query))
            return Fail(stderr, "Missing required <query> argument. Run 'grepdesk grep --help' for usage.");

        var sw = Stopwatch.StartNew();
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootPath));

        List<string> candidateFiles;
        string displayRoot;

        if (File.Exists(fullRoot))
        {
            candidateFiles = [fullRoot];
            displayRoot = Path.GetDirectoryName(fullRoot) ?? fullRoot;
        }
        else if (Directory.Exists(fullRoot))
        {
            displayRoot = fullRoot;
            using var index = new FileIndex();
            await index.BuildIndexAsync([fullRoot], ct, watch: false);

            candidateFiles = index.GetIndexedFilesUnder([fullRoot])
                .Where(f => includeHidden || !CliFormatting.IsHiddenOrSystem(f, fullRoot))
                .Where(f =>
                {
                    var ext = Path.GetExtension(f);
                    if (extFilter is not null && !extFilter.Contains(ext))
                        return false;
                    return ExtractorRegistry.TryGetExtractor(ext, out _);
                })
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        else
        {
            return Fail(stderr, $"Path not found: {fullRoot}");
        }

        var comparison = caseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        var results = new List<GrepMatchDto>();

        foreach (var file in candidateFiles)
        {
            ct.ThrowIfCancellationRequested();
            if (maxMatches.HasValue && results.Count >= maxMatches.Value)
                break;

            var info = new FileInfo(file);
            if (!info.Exists || info.Length > MaxFileSizeBytes)
                continue;

            int remaining = maxMatches.HasValue ? maxMatches.Value - results.Count : int.MaxValue;
            var relFile = CliFormatting.RelativeOrFull(displayRoot, file);
            var ext = Path.GetExtension(file);
            var format = ext.TrimStart('.').ToLowerInvariant();

            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                SearchPdfFile(file, relFile, format, query, comparison, contextChars, remaining, results, ct);
            }
            else if (ExtractorRegistry.TryGetExtractor(ext, out var extractor))
            {
                var content = await extractor.ExtractTextAsync(file, ct);
                if (content is not null)
                    FindMatchesInText(content, relFile, format, page: null, query, comparison, contextChars, remaining, results);
            }
        }

        sw.Stop();

        if (jsonlOutput)
        {
            foreach (var r in results)
                await stdout.WriteLineAsync(JsonSerializer.Serialize(r, JsonCompact));
        }
        else if (jsonOutput)
        {
            var report = new GrepReportDto(query, results.Count, sw.ElapsedMilliseconds, results);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, JsonIndented));
        }
        else
        {
            foreach (var r in results)
            {
                var loc = r.Page.HasValue ? $"{r.File}:p{r.Page.Value}:{r.Line}" : $"{r.File}:{r.Line}";
                await stdout.WriteLineAsync($"{loc}: {r.Snippet}");
            }
        }

        return results.Count > 0 ? 0 : 1;
    }

    private static void SearchPdfFile(
        string fullPath,
        string displayPath,
        string format,
        string query,
        StringComparison comparison,
        int contextChars,
        int maxRemaining,
        List<GrepMatchDto> results,
        CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(fullPath);
            int pageNum = 0;
            int added = 0;
            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                pageNum++;
                var text = page.Text;
                if (string.IsNullOrEmpty(text)) continue;

                int before = results.Count;
                FindMatchesInText(text, displayPath, format, pageNum, query, comparison, contextChars, maxRemaining - added, results);
                added += results.Count - before;
                if (added >= maxRemaining) break;
            }
        }
        catch
        {
            // Corrupt or unreadable PDF — skip gracefully
        }
    }

    private static void FindMatchesInText(
        string content,
        string displayPath,
        string format,
        int? page,
        string query,
        StringComparison comparison,
        int contextChars,
        int maxToAdd,
        List<GrepMatchDto> results)
    {
        int searchFrom = 0;
        int added = 0;
        int currentLine = 1;
        int lastCountedPos = 0;

        while (searchFrom < content.Length && added < maxToAdd)
        {
            int idx = content.IndexOf(query, searchFrom, comparison);
            if (idx < 0) break;

            for (int i = lastCountedPos; i < idx; i++)
            {
                if (content[i] == '\n')
                    currentLine++;
            }
            lastCountedPos = idx;

            var matchedText = content.Substring(idx, query.Length);
            var snippet = BuildSnippet(content, idx, query.Length, contextChars);
            results.Add(new GrepMatchDto(displayPath, format, page, currentLine, matchedText, snippet));
            added++;

            searchFrom = idx + Math.Max(1, query.Length);
        }
    }

    private static string BuildSnippet(string content, int matchIndex, int matchLength, int contextChars)
    {
        int start = Math.Max(0, matchIndex - contextChars);
        int end = Math.Min(content.Length, matchIndex + matchLength + contextChars);
        var snippet = content[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (start > 0 ? "..." : "") + snippet + (end < content.Length ? "..." : "");
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk grep <query> [options]

            Deep content search across source code, text, Markdown, .docx, .xlsx, and .pdf.

            Options:
              -p, --path <dir>       Search root or file (default: .)
              -e, --ext <ext1,ext2>  Restrict search to extensions (e.g. pdf,docx,cs,md)
              -i, --ignore-case      Case-insensitive search (default)
              -s, --case-sensitive   Force case-sensitive search
              -c, --context <n>      Context characters around the match (default: 60)
              --max-matches <n>      Stop after finding N matches
              --hidden               Include hidden and system files
              --json                 Output structured JSON with match metadata
              --jsonl                Output streaming JSON Lines
              -h, --help             Show this help message
            """);
    }
}
