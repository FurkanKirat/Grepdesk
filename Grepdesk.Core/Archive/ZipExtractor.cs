using System.Buffers;
using System.IO.Compression;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Core.Archive;

/// <summary>
/// Parallel zip extraction. <see cref="ZipArchive"/> is not thread-safe, so
/// every worker opens the archive with its own stream and pulls entry indices
/// from a shared queue. Entries are ordered largest-first so the job doesn't
/// end with one worker alone on a big file while the others sit idle.
/// </summary>
public static class ZipExtractor
{
    private const int BufferSize = 1 << 20;

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public sealed record Request(string ArchivePath, string Destination);

    private sealed record PlannedEntry(int Index, string EntryName, string TargetPath, long Length, long CompressedLength, DateTime LastWrite);

    private sealed record PlannedArchive(string ArchivePath, IReadOnlyCollection<string> Directories, PlannedEntry[] Files);

    /// <summary>
    /// "Extract here": when every entry sits under one top-level folder, that
    /// folder lands next to the archive. Otherwise a folder named after the
    /// archive is used, so loose files don't spill into the current folder.
    /// </summary>
    public static string ResolveExtractHereDestination(string archivePath)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        return HasSingleRootFolder(archive)
            ? Path.GetDirectoryName(Path.GetFullPath(archivePath))!
            : ResolveExtractToDestination(archivePath);
    }

    /// <summary>"Extract to folder": always a folder named after the archive, next to it.</summary>
    public static string ResolveExtractToDestination(string archivePath) =>
        Path.Combine(Path.GetDirectoryName(Path.GetFullPath(archivePath))!, Path.GetFileNameWithoutExtension(archivePath));

    internal static bool HasSingleRootFolder(ZipArchive archive)
    {
        string? root = null;
        foreach (var entry in archive.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            var slash = name.IndexOf('/');
            if (slash <= 0)
                return false; // a file at the top level

            var first = name[..slash];
            if (root is null)
                root = first;
            else if (!string.Equals(root, first, PathComparison))
                return false;
        }
        return root is not null;
    }

    /// <summary>
    /// Extracts each archive in turn. All archives are planned up front so the
    /// progress total is known before the first byte is written.
    /// </summary>
    public static async Task ExtractAsync(IReadOnlyList<Request> requests, JobContext ctx, int workerCount)
    {
        var plans = new List<PlannedArchive>();
        foreach (var request in requests)
        {
            ctx.Token.ThrowIfCancellationRequested();
            try
            {
                plans.Add(Plan(request, ctx));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
            {
                ctx.ReportError(request.ArchivePath, ex);
            }
        }
        ctx.Progress.MarkTotalFinal();

        foreach (var plan in plans)
            await ExtractPlannedAsync(plan, ctx, workerCount);
    }

    private static PlannedArchive Plan(Request request, JobContext ctx)
    {
        var destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(request.Destination));
        var destinationPrefix = destination + Path.DirectorySeparatorChar;

        var directories = new HashSet<string>(StringComparer.FromComparison(PathComparison)) { destination };
        var files = new List<PlannedEntry>();

        using var archive = ZipFile.OpenRead(request.ArchivePath);
        for (var i = 0; i < archive.Entries.Count; i++)
        {
            var entry = archive.Entries[i];
            var target = Path.GetFullPath(Path.Combine(destination, entry.FullName));

            // Zip Slip: "../../Windows/x.dll" must never escape the destination.
            if (!target.StartsWith(destinationPrefix, PathComparison) &&
                !string.Equals(Path.TrimEndingDirectorySeparator(target), destination, PathComparison))
            {
                ctx.ReportError(entry.FullName, JobErrorKind.UnsafePath);
                continue;
            }

            if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
            {
                directories.Add(Path.TrimEndingDirectorySeparator(target));
                continue;
            }

            if (entry.IsEncrypted)
            {
                ctx.ReportError(entry.FullName, JobErrorKind.Encrypted);
                continue;
            }

            directories.Add(Path.GetDirectoryName(target)!);
            files.Add(new PlannedEntry(i, entry.FullName, target, entry.Length, entry.CompressedLength, entry.LastWriteTime.DateTime));
            ctx.Progress.AddToTotal(entry.Length);
        }

        var ordered = files.OrderByDescending(f => f.CompressedLength).ToArray();
        return new PlannedArchive(request.ArchivePath, directories, ordered);
    }

    private static async Task ExtractPlannedAsync(PlannedArchive plan, JobContext ctx, int workerCount)
    {
        // Parents before children; CreateDirectory is recursive anyway, the
        // ordering just avoids redundant work.
        foreach (var dir in plan.Directories.OrderBy(d => d.Length))
        {
            try { Directory.CreateDirectory(dir); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ctx.ReportError(dir, ex); }
        }

        var next = -1;
        var workers = Math.Clamp(workerCount, 1, Math.Max(1, plan.Files.Length));
        var tasks = Enumerable.Range(0, workers)
            .Select(_ => Task.Factory.StartNew(
                () => RunWorker(plan, ctx, ref next),
                ctx.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToArray();

        await Task.WhenAll(tasks);
    }

    private static void RunWorker(PlannedArchive plan, JobContext ctx, ref int next)
    {
        using var stream = new FileStream(plan.ArchivePath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.RandomAccess);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        var buffer = ArrayPool<byte>.Shared.Rent(BufferSize);

        try
        {
            int i;
            while ((i = Interlocked.Increment(ref next)) < plan.Files.Length)
            {
                ctx.Checkpoint();
                var file = plan.Files[i];
                ctx.Progress.SetCurrent(file.EntryName);

                long written = 0;
                try
                {
                    ExtractOne(archive.Entries[file.Index], file, ctx, buffer, ref written);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or NotSupportedException)
                {
                    ctx.ReportError(file.EntryName, ex);
                }

                // Skipped or failed files still count as "done" so the bar reaches 100%.
                ctx.Progress.AddDoneBytes(file.Length - written);
                ctx.Progress.FileDone();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void ExtractOne(ZipArchiveEntry entry, PlannedEntry file, JobContext ctx, byte[] buffer, ref long written)
    {
        var target = file.TargetPath;

        if (File.Exists(target))
        {
            var existing = new FileInfo(target);
            var conflict = new ConflictInfo(file.EntryName, file.Length, file.LastWrite, target, existing.Length, existing.LastWriteTime);
            if (ConflictPolicy.LooksIdentical(conflict))
                return;

            switch (ctx.Resolve(conflict))
            {
                case ConflictChoice.Skip:
                    return;
                case ConflictChoice.KeepBoth:
                    target = ConflictPolicy.NextFreePath(target);
                    break;
            }
        }

        // Opened before the destination: if the entry can't be decoded, the
        // existing file at the target must stay untouched.
        using var source = entry.Open();

        var created = false;
        var completed = false;
        try
        {
            using (var destination = new FileStream(target, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize: 0))
            {
                created = true;

                // Pre-sizing lets the file system allocate one contiguous run.
                if (file.Length > 0)
                    destination.SetLength(file.Length);

                int read;
                while ((read = source.Read(buffer, 0, buffer.Length)) > 0)
                {
                    destination.Write(buffer, 0, read);
                    written += read;
                    ctx.Progress.AddDoneBytes(read);
                    ctx.Checkpoint();
                }

                // A damaged archive can produce fewer bytes than its header claims.
                if (written != file.Length)
                    destination.SetLength(written);
            }

            File.SetLastWriteTime(target, file.LastWrite);
            completed = true;
        }
        finally
        {
            // Never leave a half-written file behind (cancel or error).
            if (created && !completed)
            {
                try { File.Delete(target); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            }
        }
    }
}
