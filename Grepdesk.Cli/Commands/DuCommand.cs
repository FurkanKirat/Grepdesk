using System.Text.Json;
using Grepdesk.Core;
using Grepdesk.Core.DiskUsage;

namespace Grepdesk.Cli.Commands;

internal static class DuCommand
{
    public sealed record DuEntryDto(
        string Path,
        string Name,
        bool IsDirectory,
        long Bytes,
        string FormattedSize);

    public sealed record DuCategoryDto(
        string Category,
        long Bytes,
        int Files);

    public sealed record DuReportDto(
        string Root,
        long ScannedBytes,
        long CloudOnlyBytes,
        long TotalDriveBytes,
        long FreeDriveBytes,
        IReadOnlyList<DuEntryDto> TopEntries,
        IReadOnlyList<DuCategoryDto> Categories,
        IReadOnlyList<DuEntryDto> LargestFiles);

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
        string? rootArg = null;
        int topN = 10;
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

                case "--top":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out topN) || topN < 1)
                        return Fail(stderr, "Invalid --top value. Must be a positive integer.");
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                case "--jsonl":
                    jsonlOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk du --help' for usage.");
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

        var diskReport = DiskAnalyzer.Analyze(index, fullRoot, topN, ct);
        var topChildren = index.ChildrenOf(fullRoot, ct)
            .OrderByDescending(c => c.Size)
            .Take(topN)
            .Select(c => new DuEntryDto(
                CliFormatting.RelativeOrFull(fullRoot, c.FullPath),
                c.FileName,
                c.IsDirectory,
                c.Size,
                CliFormatting.FormatBytes(c.Size)))
            .ToList();

        var categories = diskReport.Categories
            .Where(c => c.Bytes > 0 || c.Files > 0)
            .OrderByDescending(c => c.Bytes)
            .Select(c => new DuCategoryDto(c.Category.ToString(), c.Bytes, c.Files))
            .ToList();

        var largestFiles = diskReport.LargestFiles
            .Take(topN)
            .Select(lf => new DuEntryDto(
                CliFormatting.RelativeOrFull(fullRoot, lf.File.FullPath),
                lf.File.FileName,
                false,
                lf.File.Size,
                CliFormatting.FormatBytes(lf.File.Size)))
            .ToList();

        if (jsonlOutput)
        {
            foreach (var entry in topChildren)
                await stdout.WriteLineAsync(JsonSerializer.Serialize(entry, JsonCompact));
        }
        else if (jsonOutput)
        {
            var dto = new DuReportDto(
                fullRoot.Replace('\\', '/'),
                diskReport.ScannedBytes,
                diskReport.CloudOnlyBytes,
                diskReport.TotalBytes,
                diskReport.FreeBytes,
                topChildren,
                categories,
                largestFiles);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(dto, JsonIndented));
        }
        else
        {
            await stdout.WriteLineAsync($"Disk Usage for {fullRoot}");
            await stdout.WriteLineAsync($"Total Scanned: {CliFormatting.FormatBytes(diskReport.ScannedBytes)}");
            if (diskReport.CloudOnlyBytes > 0)
                await stdout.WriteLineAsync($"Cloud-Only:    {CliFormatting.FormatBytes(diskReport.CloudOnlyBytes)}");

            if (topChildren.Count > 0)
            {
                await stdout.WriteLineAsync($"\nTop {topChildren.Count} Items:");
                foreach (var item in topChildren)
                {
                    var suffix = item.IsDirectory ? "/" : "";
                    await stdout.WriteLineAsync($"  {item.FormattedSize,10}  {item.Path}{suffix}");
                }
            }

            if (categories.Count > 0)
            {
                await stdout.WriteLineAsync("\nBy Category:");
                foreach (var cat in categories)
                    await stdout.WriteLineAsync($"  {CliFormatting.FormatBytes(cat.Bytes),10}  {cat.Category} ({cat.Files} files)");
            }

            if (largestFiles.Count > 0)
            {
                await stdout.WriteLineAsync($"\nLargest Files (Top {largestFiles.Count}):");
                foreach (var file in largestFiles)
                    await stdout.WriteLineAsync($"  {file.FormattedSize,10}  {file.Path}");
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
            Usage: grepdesk du [path] [options]

            Summarizes folder disk usage, categories, and largest consumers.

            Options:
              -p, --path <dir>  Directory to analyze (default: .)
              --top <n>         Number of top items and largest files to show (default: 10)
              --json            Output structured JSON report
              --jsonl           Output streaming JSON Lines of top items
              -h, --help        Show this help message
            """);
    }
}
