using System.Text.Json;
using Grepdesk.Core;
using Grepdesk.Core.Cleanup;

namespace Grepdesk.Cli.Commands;

internal static class DuplicatesCommand
{
    public sealed record DuplicateGroupDto(
        long FileSize,
        long WastedBytes,
        IReadOnlyList<string> Files);

    public sealed record DuplicatesReportDto(
        string Path,
        int TotalGroups,
        long TotalWastedBytes,
        IReadOnlyList<DuplicateGroupDto> Groups);

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

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        TextReader stdin,
        IPlatformShell platformShell,
        CancellationToken ct)
    {
        string? rootArg = null;
        long minSize = 1;
        bool deletePrompt = false;
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
                    rootArg = args[++i];
                    break;

                case "--min-size":
                    if (i + 1 >= args.Length || !CliFormatting.TryParseSize(args[++i], out minSize))
                        return Fail(stderr, "Invalid --min-size value (examples: 1024, 500KB, 10MB, 1GB).");
                    break;

                case "--delete-prompt":
                    deletePrompt = true;
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
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk duplicates --help' for usage.");
                    if (rootArg is null)
                        rootArg = arg;
                    else
                        return Fail(stderr, $"Unexpected positional argument '{arg}'.");
                    break;
            }
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(rootArg ?? "."));
        if (!Directory.Exists(fullRoot))
            return Fail(stderr, $"Directory not found: {fullRoot}");

        using var index = new FileIndex();
        await index.BuildIndexAsync([fullRoot], ct, watch: false);

        var candidates = new List<SearchResult>();
        foreach (var (path, entry) in index.Entries)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.IsDirectory || entry.IsCloudOnly || entry.Size < minSize)
                continue;
            if (!includeHidden && CliFormatting.IsHiddenOrSystem(path, fullRoot))
                continue;
            candidates.Add(new SearchResult(path, false, entry.Size, entry.Modified));
        }

        var groups = await DuplicateFinder.FindAsync(candidates, minSize, null, ct);

        var groupDtos = groups
            .Select(g => new DuplicateGroupDto(
                g.FileSize,
                g.WastedBytes,
                g.Files
                    .Select(f => CliFormatting.RelativeOrFull(fullRoot, f.FullPath))
                    .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
                    .ToList()))
            .ToList();

        long totalWasted = groupDtos.Sum(g => g.WastedBytes);

        if (jsonlOutput)
        {
            foreach (var g in groupDtos)
                await stdout.WriteLineAsync(JsonSerializer.Serialize(g, JsonCompact));
        }
        else if (jsonOutput)
        {
            var report = new DuplicatesReportDto(
                fullRoot.Replace('\\', '/'),
                groupDtos.Count,
                totalWasted,
                groupDtos);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(report, JsonIndented));
        }
        else
        {
            if (groups.Count == 0)
            {
                await stdout.WriteLineAsync("No duplicate files found.");
            }
            else
            {
                await stdout.WriteLineAsync(
                    $"Found {groups.Count} duplicate groups ({CliFormatting.FormatBytes(totalWasted)} wasted):");

                for (int gi = 0; gi < groups.Count; gi++)
                {
                    var group = groups[gi];
                    var sortedFiles = group.Files.OrderBy(f => f.FullPath, StringComparer.OrdinalIgnoreCase).ToList();
                    await stdout.WriteLineAsync(
                        $"\nGroup #{gi + 1}: {CliFormatting.FormatBytes(group.FileSize)} each ({CliFormatting.FormatBytes(group.WastedBytes)} wasted)");
                    for (int fi = 0; fi < sortedFiles.Count; fi++)
                    {
                        var rel = CliFormatting.RelativeOrFull(fullRoot, sortedFiles[fi].FullPath);
                        await stdout.WriteLineAsync($"  [{fi + 1}] {rel}");
                    }

                    if (deletePrompt)
                    {
                        await stdout.WriteAsync("Move which copies to Trash? (e.g. 2,3 or Enter to skip, q to quit): ");
                        await stdout.FlushAsync(ct);
                        var response = await stdin.ReadLineAsync(ct);
                        if (response is null || string.Equals(response.Trim(), "q", StringComparison.OrdinalIgnoreCase))
                            break;

                        if (!string.IsNullOrWhiteSpace(response))
                        {
                            foreach (var token in response.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                            {
                                if (int.TryParse(token, out int idx) && idx >= 1 && idx <= sortedFiles.Count)
                                {
                                    var targetPath = sortedFiles[idx - 1].FullPath;
                                    var res = platformShell.MoveToTrash(targetPath);
                                    if (res.IsSuccess)
                                        await stdout.WriteLineAsync($"  Moved to Trash: {CliFormatting.RelativeOrFull(fullRoot, targetPath)}");
                                    else
                                        await stderr.WriteLineAsync($"  Failed to move '{targetPath}' to Trash: {res.Exception?.Message ?? res.Status.ToString()}");
                                }
                            }
                        }
                    }
                }
            }
        }

        return 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk duplicates [path] [options]

            Identifies duplicate files by fast size grouping followed by content hashing.

            Options:
              -p, --path <dir>     Root directory to scan (default: .)
              --min-size <size>    Minimum file size to consider (e.g. 10MB, 1GB; default: 1B)
              --delete-prompt      Interactively prompt to move duplicate copies to Trash
              --hidden             Include hidden and system files
              --json               Output machine-readable JSON report
              --jsonl              Output streaming JSON Lines per duplicate group
              -h, --help           Show this help message
            """);
    }
}
