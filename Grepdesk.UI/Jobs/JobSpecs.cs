using System.IO.Compression;
using Grepdesk.Core;
using Grepdesk.Core.Archive;
using Grepdesk.Core.Jobs;
using Grepdesk.Core.Transfer;
using Grepdesk.UI.Helpers;
using Grepdesk.UI.Startup;

namespace Grepdesk.UI.Jobs;

/// <summary>What a job window shows and runs.</summary>
public sealed record JobSpec(CommandKind Kind, string Title, Func<JobContext, Task> Run, bool Benchmark)
{
    /// <summary>Placeholder for the XAML designer.</summary>
    public static JobSpec Empty { get; } = new(CommandKind.OpenSearch, "", _ => Task.CompletedTask, false);
}

/// <summary>Turns a (possibly merged) command into the job that carries it out.</summary>
public static class JobSpecs
{
    private static LocalizationService Loc => LocalizationService.Instance;
    private static readonly IDriveKindProvider Drives = PlatformShellFactory.CreateDriveKindProvider();

    public static JobSpec From(AppCommand command) => command.Kind switch
    {
        CommandKind.ExtractHere or CommandKind.ExtractTo => Extract(command),
        CommandKind.Compress => Compress(command),
        CommandKind.Paste => Paste(command),
        _ => throw new ArgumentOutOfRangeException(nameof(command), command.Kind, null)
    };

    private static string Subject(IReadOnlyList<string> paths, string manyKey) =>
        paths.Count == 1 ? Path.GetFileName(paths[0]) : Loc.Get(manyKey, paths.Count);

    private static JobSpec Extract(AppCommand command)
    {
        var here = command.Kind == CommandKind.ExtractHere;
        var title = Loc.Get("JobExtracting", Subject(command.Paths, "JobManyArchives"));

        return new JobSpec(command.Kind, title, async ctx =>
        {
            var requests = new List<ZipExtractor.Request>();
            foreach (var archive in command.Paths)
            {
                try
                {
                    var destination = here
                        ? ZipExtractor.ResolveExtractHereDestination(archive)
                        : ZipExtractor.ResolveExtractToDestination(archive);
                    requests.Add(new ZipExtractor.Request(archive, destination));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
                {
                    ctx.ReportError(archive, ex);
                }
            }

            if (requests.Count == 0)
                return;

            var workers = WorkerPolicy.FileOperations(Drives.GetKind(requests[0].ArchivePath), Drives.GetKind(requests[0].Destination));
            await ZipExtractor.ExtractAsync(requests, ctx, workers);
        }, command.Benchmark);
    }

    private static JobSpec Compress(AppCommand command)
    {
        var title = Loc.Get("JobCompressing", Subject(command.Paths, "JobManyItems"));

        return new JobSpec(command.Kind, title, async ctx =>
        {
            var archive = ZipCompressor.DefaultArchivePath(command.Paths);
            ctx.Progress.SetCurrent(Path.GetFileName(archive));

            var workers = WorkerPolicy.Compression(Drives.GetKind(command.Paths[0]), Drives.GetKind(archive));
            await ZipCompressor.CompressAsync(command.Paths, archive, CompressionLevel.Optimal, ctx, workers);
        }, command.Benchmark);
    }

    private static JobSpec Paste(AppCommand command)
    {
        var destination = command.Paths.FirstOrDefault() ?? Environment.CurrentDirectory;
        var clipboard = PlatformShellFactory.CreateFileClipboard();

        // Read now, on the UI thread, while Explorer's clipboard content is fresh.
        if (clipboard?.GetFiles() is not { } files)
        {
            return new JobSpec(command.Kind, Loc.Get("MenuPaste"), ctx =>
            {
                ctx.ReportError(destination, JobErrorKind.Io, Loc.Get("JobClipboardEmpty"));
                return Task.CompletedTask;
            }, command.Benchmark);
        }

        var title = Loc.Get(files.IsCut ? "JobMoving" : "JobCopying", Subject(files.Paths, "JobManyItems"));

        return new JobSpec(command.Kind, title, async ctx =>
        {
            var workers = WorkerPolicy.FileOperations(Drives.GetKind(files.Paths[0]), Drives.GetKind(destination));
            var engine = new TransferEngine(PlatformShellFactory.CreateFileCopier());
            await engine.RunAsync(files.Paths, destination, files.IsCut, ctx, workers);

            // The cut files now live at the destination; a second paste would
            // find nothing at the source.
            if (files.IsCut && ctx.Errors.Count == 0)
                clipboard.Clear();
        }, command.Benchmark);
    }
}
