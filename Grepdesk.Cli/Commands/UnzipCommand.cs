using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Grepdesk.Core;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Cli.Commands;

internal static class UnzipCommand
{
    public sealed record UnzipResultDto(
        string Archive,
        string Destination,
        int Files,
        long Bytes,
        long ElapsedMs,
        double ThroughputMbps,
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
        TextReader stdin,
        IDriveKindProvider driveKindProvider,
        bool showProgress,
        bool isInteractive,
        CancellationToken ct)
    {
        string? archiveArg = null;
        string? destinationArg = null;
        bool benchmark = false;
        bool jsonOutput = false;
        ConflictChoice? conflictOverride = null;

        for (int i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            switch (arg)
            {
                case "-h":
                case "--help":
                    PrintHelp(stdout);
                    return 0;

                case "-o":
                case "--output":
                    if (i + 1 >= args.Length) return Fail(stderr, "Missing value for -o/--output.");
                    destinationArg = args[++i];
                    break;

                case "--benchmark":
                    benchmark = true;
                    break;

                case "--overwrite":
                    conflictOverride = ConflictChoice.Overwrite;
                    break;

                case "--skip":
                    conflictOverride = ConflictChoice.Skip;
                    break;

                case "--keep-both":
                    conflictOverride = ConflictChoice.KeepBoth;
                    break;

                case "--json":
                    jsonOutput = true;
                    break;

                default:
                    if (arg.StartsWith('-'))
                        return Fail(stderr, $"Unknown option '{arg}'. Run 'grepdesk unzip --help' for usage.");
                    if (archiveArg is null)
                        archiveArg = arg;
                    else
                        return Fail(stderr, $"Unexpected positional argument '{arg}'.");
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(archiveArg))
            return Fail(stderr, "Usage: grepdesk unzip <archive.zip> [-o <destination>] [--benchmark]");

        var archivePath = Path.GetFullPath(archiveArg);
        if (!File.Exists(archivePath))
            return Fail(stderr, $"Archive not found: {archivePath}");

        string destination;
        try
        {
            destination = destinationArg is not null
                ? Path.GetFullPath(destinationArg)
                : ZipExtractor.ResolveExtractHereDestination(archivePath);
        }
        catch (Exception ex)
        {
            return Fail(stderr, $"Failed to open archive '{archivePath}': {ex.Message}");
        }

        var driveKinds = new[]
        {
            driveKindProvider.GetKind(archivePath),
            driveKindProvider.GetKind(destination)
        };
        var workerCount = WorkerPolicy.FileOperations(driveKinds);

        IConflictResolver resolver = conflictOverride.HasValue
            ? new StaticConflictResolver(conflictOverride.Value)
            : isInteractive && !jsonOutput
                ? new InteractiveCliConflictResolver(stderr, stdin)
                : new StaticConflictResolver(ConflictChoice.KeepBoth);

        var ctx = new JobContext(resolver, ct);
        var sw = Stopwatch.StartNew();

        using var progressCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var progressTask = (showProgress && !jsonOutput && !isInteractive)
            ? ZipCommand.RunProgressBarAsync("Extracting", ctx.Progress, stderr, progressCts.Token)
            : Task.CompletedTask;

        try
        {
            await ZipExtractor.ExtractAsync([new ZipExtractor.Request(archivePath, destination)], ctx, workerCount);
        }
        finally
        {
            progressCts.Cancel();
            try { await progressTask; } catch (OperationCanceledException) { }
        }

        sw.Stop();
        var snap = ctx.Progress.Snapshot();
        double seconds = Math.Max(0.001, sw.Elapsed.TotalSeconds);
        double throughputMbps = Math.Round((snap.TotalBytes / (1024.0 * 1024.0)) / seconds, 2);

        foreach (var err in ctx.Errors)
            await stderr.WriteLineAsync($"Warning [{err.Kind}]: {err.Path} {err.Detail}".TrimEnd());

        if (jsonOutput)
        {
            var dto = new UnzipResultDto(
                archivePath.Replace('\\', '/'),
                destination.Replace('\\', '/'),
                snap.DoneFiles,
                snap.TotalBytes,
                sw.ElapsedMilliseconds,
                throughputMbps,
                ctx.Errors.Count);
            await stdout.WriteLineAsync(JsonSerializer.Serialize(dto, JsonIndented));
        }
        else if (benchmark)
        {
            await stdout.WriteLineAsync(
                $"Extracted {snap.DoneFiles} files ({CliFormatting.FormatBytes(snap.TotalBytes)}) to {destination} in {sw.ElapsedMilliseconds} ms ({throughputMbps.ToString("0.00", CultureInfo.InvariantCulture)} MB/s, {workerCount} workers)");
        }
        else
        {
            await stdout.WriteLineAsync(
                $"Extracted {snap.DoneFiles} files ({CliFormatting.FormatBytes(snap.TotalBytes)}) to {destination}");
        }

        return ctx.Errors.Count > 0 && snap.DoneFiles == 0 ? 2 : 0;
    }

    private static int Fail(TextWriter stderr, string message)
    {
        stderr.WriteLine($"Error: {message}");
        return 2;
    }

    private static void PrintHelp(TextWriter stdout)
    {
        stdout.WriteLine("""
            Usage: grepdesk unzip <archive.zip> [options]

            Parallel multi-core zip extraction powered by ZipExtractor.

            Options:
              -o, --output <dir>  Destination directory (default: smart extract-here)
              --benchmark         Print extraction timing, worker count, and MB/s throughput
              --overwrite         Overwrite conflicting files
              --skip              Skip conflicting files
              --keep-both         Keep both files on conflict by renaming (default in non-interactive mode)
              --json              Output structured JSON summary
              -h, --help          Show this help message
            """);
    }
}

internal sealed class InteractiveCliConflictResolver(TextWriter stderr, TextReader stdin) : IConflictResolver
{
    private readonly SemaphoreSlim _lock = new(1, 1);
    private ConflictChoice? _applyToAll;

    public async Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            if (_applyToAll.HasValue)
                return _applyToAll.Value;

            await stderr.WriteLineAsync($"Conflict: '{conflict.DestinationPath}' already exists.");
            await stderr.WriteAsync("[O]verwrite / [S]kip / [K]eep both / [A]ll keep both (default K): ");
            await stderr.FlushAsync(ct);

            var line = await stdin.ReadLineAsync(ct);
            var key = line?.Trim().ToUpperInvariant() ?? "";
            return key switch
            {
                "O" or "OVERWRITE" => ConflictChoice.Overwrite,
                "S" or "SKIP" => ConflictChoice.Skip,
                "A" or "ALL" => (_applyToAll = ConflictChoice.KeepBoth).Value,
                _ => ConflictChoice.KeepBoth
            };
        }
        finally
        {
            _lock.Release();
        }
    }
}
