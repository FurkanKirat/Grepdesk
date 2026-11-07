using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using Grepdesk.Core;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Cli.Commands;

internal static class ZipCommand
{
    public sealed record ZipResultDto(
        string Archive,
        int Files,
        long SourceBytes,
        long ArchiveBytes,
        long ElapsedMs,
        int Errors);

    private static readonly JsonSerializerOptions JsonIndented = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        WriteIndented = true
    };

    public static async Task<int> ExecuteAsync(
        string[] args,
        TextWriter stdout,
        TextWriter stderr,
        IDriveKindProvider driveKindProvider,
        bool showProgress,
        CancellationToken ct)
    {
        var positional = new List<string>();
        int levelNum = 6;
        bool jsonOutput = false;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp(stdout);
                    return 0;

                case "-l":
                case "--level":
                    if (i + 1 >= args.Length || !int.TryParse(args[++i], out levelNum) || levelNum is < 0 or > 9)
                        return Fail(stderr, "Invalid compression level. Expected an integer between 0 and 9.");
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk zip --help' for usage.");
                    positional.Add(arg);
                    break;
            }
        }

        if (positional.Count < 2)
            return Fail(stderr, "Usage: grepdesk zip <output.zip> <inputs...> [-l <0-9>]");

        var outputZip = Path.GetFullPath(positional[0]);
        var inputs = positional.Skip(1).Select(Path.GetFullPath).ToList();

        foreach (var input in inputs)
        {
            if (!File.Exists(input) && !Directory.Exists(input))
                return Fail(stderr, $"Input path not found: {input}");
        }

        var outputDir = Path.GetDirectoryName(outputZip);
        if (!string.IsNullOrEmpty(outputDir))
            Directory.CreateDirectory(outputDir);

        var level = MapCompressionLevel(levelNum);
        var driveKinds = inputs
            .Select(driveKindProvider.GetKind)
            .Append(driveKindProvider.GetKind(outputDir ?? outputZip))
            .Distinct()
            .ToArray();
        var workerCount = WorkerPolicy.Compression(driveKinds);

        var ctx = new JobContext(new StaticConflictResolver(ConflictChoice.KeepBoth), ct);
        var sw = Stopwatch.StartNew();

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = (showProgress && !jsonOutput)
            ? RunProgressBarAsync("Compressing", ctx.Progress, stderr, progressCts.Token)
            : Task.CompletedTask;

        try
        {
            await ZipCompressor.CompressAsync(inputs, outputZip, level, ctx, workerCount);
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { }
        }

        sw.Stop();
        var snap = ctx.Progress.Snapshot();
        var archiveInfo = new FileInfo(outputZip);
        long archiveBytes = archiveInfo.Exists ? archiveInfo.Length : 0;

        foreach (var err in ctx.Errors)
            await stderr.WriteLineAsync($"Warning [{err.Kind}]: {err.Path} {err.Detail}".TrimEnd());

        if (jsonOutput)
        {
            var dto = new ZipResultDto(
                outputZip.Replace('\\', '/'),
                snap.DoneFiles,
                snap.TotalBytes,
                archiveBytes,
                sw.ElapsedMilliseconds,
                ctx.Errors.Count);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(dto, JsonIndented));
        }
        else
        {
            await stdout.WriteLineAsync(
                $"Created {outputZip} ({snap.DoneFiles} files, {CliFormatting.FormatBytes(snap.TotalBytes)} -> {CliFormatting.FormatBytes(archiveBytes)} in {sw.ElapsedMilliseconds} ms)");
        }

        return ctx.Errors.Count > 0 && snap.DoneFiles == 0 ? 2 : 0;
    }

    internal static CompressionLevel MapCompressionLevel(int level) => level switch
    {
        0 => CompressionLevel.NoCompression,
        <= 3 => CompressionLevel.Fastest,
        <= 6 => CompressionLevel.Optimal,
        _ => CompressionLevel.SmallestSize
    };

    internal static async Task RunProgressBarAsync(string label, JobProgress progress, TextWriter stderr, CancellationToken ct)
    {
        const int barWidth = 24;
        while (!ct.IsCancellationRequested)
        {
            var s = progress.Snapshot();
            int filled = (int)Math.Round(s.Fraction * barWidth);
            var bar = new string('#', filled) + new string('-', Math.Max(0, barWidth - filled));
            int pct = (int)Math.Round(s.Fraction * 100);
            await stderr.WriteAsync($"\r{label}: [{bar}] {pct,3}% ({s.DoneFiles}/{s.TotalFiles} files, {CliFormatting.FormatBytes(s.DoneBytes)}/{CliFormatting.FormatBytes(s.TotalBytes)})");
            await stderr.FlushAsync(ct);
            await Task.Delay(100, ct);
        }
        await stderr.WriteLineAsync();
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk zip <output.zip> <inputs...> [options]

            Parallel multi-core zip compression powered by ZipCompressor.

            Options:
              -l, --level <0-9>  Compression level (0=store, 1-3=fast, 4-6=optimal, 7-9=smallest; default: 6)
              --json             Output structured JSON summary
              -h, --help         Show this help message
            """);
    }
}

internal sealed class StaticConflictResolver(ConflictChoice choice) : IConflictResolver
{
    public Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct) =>
        Task.FromResult(choice);
}
