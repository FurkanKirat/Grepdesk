using System.Diagnostics;
using System.IO.Compression;
using Grepdesk.Core;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;
using Grepdesk.Core.Transfer;
using Grepdesk.Windows;

namespace Grepdesk.Benchmarks;

public enum Operation { Compress, Extract, Copy }

/// <param name="Source">Compress/Copy: the dataset folder. Extract: the zip.</param>
/// <param name="Output">Compress: the zip to create. Extract/Copy: the folder to fill.</param>
/// <param name="Expected">What the dataset holds; every run's output is checked against it.</param>
public sealed record BenchInput(string Source, string Output, TreeStats Expected);

/// <summary>One way of doing an operation: Grepdesk's engine, or a tool it is compared against.</summary>
public sealed record Competitor(string Name, Operation Operation, Func<bool> IsAvailable, Func<BenchInput, Task> Run)
{
    private static readonly TimeSpan ExplorerTimeout = TimeSpan.FromMinutes(30);
    private const string SevenZip = @"C:\Program Files\7-Zip\7z.exe";

    public const string GrepdeskName = "Grepdesk";
    public const string ExplorerName = "Explorer";

    private static readonly IDriveKindProvider Drives =
        OperatingSystem.IsWindows() ? new WindowsDriveKindProvider() : new BasicDriveKindProvider();

    /// <summary>Set by --copier stream to compare the portable copier with the native one.</summary>
    public static bool UseStreamCopier { get; set; }

    private static IFileCopier Copier() =>
        UseStreamCopier ? new StreamFileCopier()
        : OperatingSystem.IsWindows() ? new CopyFile2Copier() : new NativeFileCopier();

    /// <summary>Set by --workers to try other worker counts than <see cref="WorkerPolicy"/> picks.</summary>
    public static int? WorkerOverride { get; set; }

    private static int Workers(BenchInput input, bool compression = false)
    {
        var drives = new[] { Drives.GetKind(input.Source), Drives.GetKind(input.Output) };
        return WorkerOverride ?? (compression ? WorkerPolicy.Compression(drives) : WorkerPolicy.FileOperations(drives));
    }

    private static JobContext NewContext() => new(new AlwaysOverwrite(), CancellationToken.None);

    private static void ThrowIfErrors(JobContext ctx)
    {
        if (ctx.Errors.Count > 0)
            throw new InvalidOperationException($"{ctx.Errors.Count} errors, first: {ctx.Errors.First()}");
    }

    // ------------------------------------------------------------ compress

    public static readonly Competitor GrepdeskCompress = new(GrepdeskName, Operation.Compress, () => true, async input =>
    {
        var ctx = NewContext();
        await ZipCompressor.CompressAsync([input.Source], input.Output, CompressionLevel.Optimal, ctx, Workers(input, compression: true));
        ThrowIfErrors(ctx);
    });

    public static readonly Competitor DotNetCompress = new(".NET (1 thread)", Operation.Compress, () => true, input =>
    {
        ZipFile.CreateFromDirectory(input.Source, input.Output, CompressionLevel.Optimal, includeBaseDirectory: true);
        return Task.CompletedTask;
    });

    public static readonly Competitor SevenZipCompress = new("7-Zip", Operation.Compress, () => File.Exists(SevenZip),
        input => RunTool(SevenZip, $"a -tzip -mx5 -mmt=on \"{input.Output}\" \"{input.Source}\""));

    public static readonly Competitor ExplorerCompress = new(ExplorerName, Operation.Compress, () => OperatingSystem.IsWindows(), input =>
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        ExplorerShell.CreateEmptyZip(input.Output);
        ExplorerShell.Run(
            shell => shell.NameSpace(input.Output).CopyHere(input.Source, ExplorerShell.SilentFlags),
            () => ZipFileCount(input.Output) == input.Expected.Files && ExplorerShell.IsUnlocked(input.Output),
            ExplorerTimeout);
        return Task.CompletedTask;
    });

    // ------------------------------------------------------------ extract

    public static readonly Competitor GrepdeskExtract = new(GrepdeskName, Operation.Extract, () => true, async input =>
    {
        var ctx = NewContext();
        await ZipExtractor.ExtractAsync([new ZipExtractor.Request(input.Source, input.Output)], ctx, Workers(input));
        ThrowIfErrors(ctx);
    });

    public static readonly Competitor DotNetExtract = new(".NET (1 thread)", Operation.Extract, () => true, input =>
    {
        ZipFile.ExtractToDirectory(input.Source, input.Output);
        return Task.CompletedTask;
    });

    public static readonly Competitor SevenZipExtract = new("7-Zip", Operation.Extract, () => File.Exists(SevenZip),
        input => RunTool(SevenZip, $"x -y \"-o{input.Output}\" \"{input.Source}\""));

    public static readonly Competitor ExplorerExtract = new(ExplorerName, Operation.Extract, () => OperatingSystem.IsWindows(), input =>
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        Directory.CreateDirectory(input.Output);
        ExplorerShell.Run(
            shell => shell.NameSpace(input.Output).CopyHere(shell.NameSpace(input.Source).Items(), ExplorerShell.SilentFlags),
            () => TreeStats.Of(input.Output) == input.Expected,
            ExplorerTimeout);
        return Task.CompletedTask;
    });

    // ------------------------------------------------------------ copy

    public static readonly Competitor GrepdeskCopy = new(GrepdeskName, Operation.Copy, () => true, async input =>
    {
        var ctx = NewContext();
        Directory.CreateDirectory(input.Output);
        await new TransferEngine(Copier()).RunAsync([input.Source], input.Output, move: false, ctx, Workers(input));
        ThrowIfErrors(ctx);
    });

    public static readonly Competitor DotNetCopy = new(".NET File.Copy (1 thread)", Operation.Copy, () => true, input =>
    {
        CopyTree(input.Source, Path.Combine(input.Output, Path.GetFileName(input.Source)));
        return Task.CompletedTask;
    });

    public static readonly Competitor RobocopyCopy = new("robocopy /MT:8", Operation.Copy, () => OperatingSystem.IsWindows(),
        input => RunTool("robocopy",
            $"\"{input.Source}\" \"{Path.Combine(input.Output, Path.GetFileName(input.Source))}\" /E /MT:8 /R:0 /W:0 /NFL /NDL /NJH /NJS /NP",
            okExitCodeBelow: 8)); // robocopy: 0-7 are success variants

    public static readonly Competitor ExplorerCopy = new(ExplorerName, Operation.Copy, () => OperatingSystem.IsWindows(), input =>
    {
        if (!OperatingSystem.IsWindows()) return Task.CompletedTask;
        Directory.CreateDirectory(input.Output);
        var target = Path.Combine(input.Output, Path.GetFileName(input.Source));
        ExplorerShell.Run(
            shell => shell.NameSpace(input.Output).CopyHere(input.Source, ExplorerShell.SilentFlags),
            () => TreeStats.Of(target) == input.Expected,
            ExplorerTimeout);
        return Task.CompletedTask;
    });

    public static IReadOnlyList<Competitor> All { get; } =
    [
        GrepdeskCompress, ExplorerCompress, SevenZipCompress, DotNetCompress,
        GrepdeskExtract, ExplorerExtract, SevenZipExtract, DotNetExtract,
        GrepdeskCopy, ExplorerCopy, RobocopyCopy, DotNetCopy
    ];

    // ------------------------------------------------------------ verification

    /// <summary>Checks a run's output holds the whole dataset. Explorer's completion is inferred, so this also catches a run stopped too early.</summary>
    public static string? Verify(Operation operation, BenchInput input)
    {
        var actual = operation == Operation.Compress ? ZipStats(input.Output) : TreeStats.Of(input.Output);
        return actual == input.Expected ? null : $"output has {actual}, expected {input.Expected}";
    }

    public static TreeStats ZipStats(string zipPath)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        var files = archive.Entries.Where(e => !e.FullName.EndsWith('/')).ToList();
        return new TreeStats(files.Count, files.Sum(e => e.Length));
    }

    private static int ZipFileCount(string zipPath)
    {
        try
        {
            // Shared read: must not get in the way of Explorer still writing it.
            using var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            return archive.Entries.Count(e => !e.FullName.EndsWith('/'));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            return -1; // mid-write
        }
    }

    private static async Task RunTool(string exe, string arguments, int okExitCodeBelow = 1)
    {
        using var process = Process.Start(new ProcessStartInfo(exe, arguments)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode >= okExitCodeBelow)
            throw new InvalidOperationException($"{Path.GetFileName(exe)} exited with {process.ExitCode}: {await error}{await output}");
    }

    private static void CopyTree(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (var dir in Directory.EnumerateDirectories(source))
            CopyTree(dir, Path.Combine(target, Path.GetFileName(dir)));
    }

    private sealed class AlwaysOverwrite : IConflictResolver
    {
        public Task<ConflictChoice> ResolveAsync(ConflictInfo conflict, CancellationToken ct) => Task.FromResult(ConflictChoice.Overwrite);
    }
}
