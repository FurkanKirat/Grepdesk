using System.Buffers;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Core.Transfer;

/// <summary>
/// Copies or moves files and folders into a destination folder. Unlike
/// Explorer it doesn't stop to "calculate" first: one task walks the source
/// tree and feeds files to the workers as it finds them, so copying starts
/// right away and the total grows while the walk runs.
/// </summary>
public sealed class TransferEngine(IFileCopier copier)
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    private static readonly StringComparison PathComparison =
        OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    private sealed record FileWork(string Source, string Destination, long Length, DateTime LastWrite, bool Rename);

    public async Task RunAsync(IReadOnlyList<string> sources, string destinationDir, bool move, JobContext ctx, int workerCount)
    {
        destinationDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(destinationDir));
        var queue = Channel.CreateBounded<FileWork>(new BoundedChannelOptions(4096) { SingleWriter = true });
        var movedDirectories = new ConcurrentBag<string>();

        var producer = Task.Run(async () =>
        {
            try { await ProduceAsync(sources, destinationDir, move, ctx, queue.Writer, movedDirectories); }
            finally
            {
                queue.Writer.TryComplete();
                ctx.Progress.MarkTotalFinal();
            }
        });

        var workers = Enumerable.Range(0, Math.Max(1, workerCount))
            .Select(_ => Task.Factory.StartNew(
                () => RunWorker(queue.Reader, move, ctx),
                ctx.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default))
            .ToList();

        await Task.WhenAll([producer, .. workers]);

        // A moved folder's files are gone; remove the emptied folder tree.
        // Folders that still hold skipped files are kept (Delete fails on them).
        foreach (var dir in movedDirectories)
            DeleteEmptyTree(dir);
    }

    // ---------------------------------------------------------------- walk

    private async Task ProduceAsync(IReadOnlyList<string> sources, string destinationDir, bool move, JobContext ctx,
        ChannelWriter<FileWork> writer, ConcurrentBag<string> movedDirectories)
    {
        foreach (var raw in sources)
        {
            ctx.Token.ThrowIfCancellationRequested();

            var source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));
            var name = Path.GetFileName(source);
            var sameFolder = string.Equals(Path.GetDirectoryName(source), destinationDir, PathComparison);

            // Cut and pasted back into the same folder: nothing to do.
            if (sameFolder && move)
                continue;

            var target = Path.Combine(destinationDir, name);
            // Copy into the same folder: Explorer's "name - Copy" behaviour, as "name (2)".
            if (sameFolder)
                target = ConflictPolicy.NextFreePath(target);

            var rename = move && SameVolume(source, destinationDir);

            if (Directory.Exists(source))
            {
                if (IsSameOrInside(destinationDir, source))
                {
                    ctx.ReportError(source, JobErrorKind.IntoItself);
                    continue;
                }

                // Same drive and nothing in the way: a move is a single rename.
                if (rename && !Directory.Exists(target) && !File.Exists(target))
                {
                    try
                    {
                        Directory.Move(source, target);
                        continue;
                    }
                    catch (IOException)
                    {
                        // Something inside is locked; fall back to file by file.
                    }
                }

                await WalkDirectoryAsync(new DirectoryInfo(source), target, rename, ctx, writer);
                if (move)
                    movedDirectories.Add(source);
            }
            else if (File.Exists(source))
            {
                var info = new FileInfo(source);
                ctx.Progress.AddToTotal(info.Length);
                await writer.WriteAsync(new FileWork(source, target, info.Length, info.LastWriteTime, rename), ctx.Token);
            }
            else
            {
                ctx.ReportError(source, JobErrorKind.NotFound);
            }
        }
    }

    private static async Task WalkDirectoryAsync(DirectoryInfo root, string rootTarget, bool rename, JobContext ctx, ChannelWriter<FileWork> writer)
    {
        var pending = new Stack<(DirectoryInfo Dir, string Target)>();
        pending.Push((root, rootTarget));

        while (pending.Count > 0)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var (dir, target) = pending.Pop();

            IEnumerable<FileSystemInfo> entries;
            try
            {
                // Created here, before any of its files are queued, so workers
                // never race to create parent folders. Empty folders are copied too.
                Directory.CreateDirectory(target);
                entries = dir.EnumerateFileSystemInfos().ToList();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ctx.ReportError(dir.FullName, ex);
                continue;
            }

            foreach (var entry in entries)
            {
                var entryTarget = Path.Combine(target, entry.Name);
                switch (entry)
                {
                    case DirectoryInfo sub when sub.LinkTarget is not null:
                        // Symlinks and junctions can point back up the tree.
                        ctx.ReportError(sub.FullName, JobErrorKind.LinkSkipped);
                        break;
                    case DirectoryInfo sub:
                        pending.Push((sub, entryTarget));
                        break;
                    case FileInfo file:
                        ctx.Progress.AddToTotal(file.Length);
                        await writer.WriteAsync(new FileWork(file.FullName, entryTarget, file.Length, file.LastWriteTime, rename), ctx.Token);
                        break;
                }
            }
        }
    }

    // ---------------------------------------------------------------- workers

    private void RunWorker(ChannelReader<FileWork> reader, bool move, JobContext ctx)
    {
        while (reader.WaitToReadAsync(ctx.Token).AsTask().GetAwaiter().GetResult())
        {
            while (reader.TryRead(out var work))
            {
                ctx.Checkpoint();
                ctx.Progress.SetCurrent(work.Source);

                long done = 0;
                try
                {
                    Transfer(work, move, ctx, bytes =>
                    {
                        done += bytes;
                        ctx.Progress.AddDoneBytes(bytes);
                    });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (IOException ex) when (IsLocked(ex))
                {
                    ctx.ReportError(work.Source, JobErrorKind.Locked);
                }
                catch (Exception ex)
                {
                    // Any failure is per-file: a dead worker could leave the
                    // walk blocked on a full queue.
                    ctx.ReportError(work.Source, ex);
                }

                // Skipped or failed files still count as done so the bar reaches 100%.
                ctx.Progress.AddDoneBytes(work.Length - done);
                ctx.Progress.FileDone();
            }
        }
    }

    private void Transfer(FileWork work, bool move, JobContext ctx, Action<long> onBytes)
    {
        var target = work.Destination;
        var overwrite = false;

        if (File.Exists(target))
        {
            var existing = new FileInfo(target);
            var conflict = new ConflictInfo(Path.GetFileName(work.Source), work.Length, work.LastWrite, target, existing.Length, existing.LastWriteTime);

            if (ConflictPolicy.LooksIdentical(conflict))
            {
                // Copy: the file is already there. Move: the source may only be
                // deleted if the contents really match, not just size and date.
                if (move && ContentsEqual(work.Source, target, ctx))
                    DeleteFile(work.Source);
                return;
            }

            switch (ctx.Resolve(conflict))
            {
                case ConflictChoice.Skip:
                    return;
                case ConflictChoice.KeepBoth:
                    target = ConflictPolicy.NextFreePath(target);
                    break;
                case ConflictChoice.Overwrite:
                    overwrite = true;
                    break;
            }
        }

        if (work.Rename)
        {
            // Same volume: the move is a rename, no data is copied.
            File.Move(work.Source, target, overwrite);
            onBytes(work.Length);
            return;
        }

        CopyWithRetry(work.Source, target, overwrite, ctx, onBytes);
        if (move)
            DeleteFile(work.Source);
    }

    private void CopyWithRetry(string source, string target, bool overwrite, JobContext ctx, Action<long> onBytes)
    {
        for (var attempt = 1; ; attempt++)
        {
            long attemptBytes = 0;
            try
            {
                copier.Copy(source, target, overwrite, ctx, bytes =>
                {
                    attemptBytes += bytes;
                    onBytes(bytes);
                });
                return;
            }
            catch (IOException ex) when (IsLocked(ex) && attempt < MaxAttempts)
            {
                // Undo this attempt's progress, wait for the other program, retry.
                onBytes(-attemptBytes);
                ctx.Token.WaitHandle.WaitOne(RetryDelay);
                ctx.Checkpoint();
            }
        }
    }

    // ---------------------------------------------------------------- helpers

    private static bool SameVolume(string a, string b) =>
        string.Equals(Path.GetPathRoot(a), Path.GetPathRoot(b), StringComparison.OrdinalIgnoreCase);

    private static bool IsSameOrInside(string path, string folder) =>
        string.Equals(path, folder, PathComparison) ||
        path.StartsWith(folder + Path.DirectorySeparatorChar, PathComparison);

    // Windows: ERROR_SHARING_VIOLATION (32), ERROR_LOCK_VIOLATION (33).
    // Unix: .NET emulates FileShare with flock and reports a conflict with the
    // raw errno EWOULDBLOCK as HResult: 11 on Linux, 35 on macOS.
    private static bool IsLocked(IOException ex) =>
        OperatingSystem.IsWindows() ? (ex.HResult & 0xFFFF) is 32 or 33
        : OperatingSystem.IsMacOS() ? ex.HResult == 35
        : ex.HResult == 11;

    private static void DeleteFile(string path)
    {
        var attributes = File.GetAttributes(path);
        if (attributes.HasFlag(FileAttributes.ReadOnly))
            File.SetAttributes(path, attributes & ~FileAttributes.ReadOnly);
        File.Delete(path);
    }

    private static bool ContentsEqual(string a, string b, JobContext ctx)
    {
        const int size = 1 << 20;
        var bufferA = ArrayPool<byte>.Shared.Rent(size);
        var bufferB = ArrayPool<byte>.Shared.Rent(size);
        try
        {
            using var streamA = new FileStream(a, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            using var streamB = new FileStream(b, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
            if (streamA.Length != streamB.Length)
                return false;

            int read;
            while ((read = streamA.ReadAtLeast(bufferA.AsSpan(0, size), size, throwOnEndOfStream: false)) > 0)
            {
                ctx.Checkpoint();
                if (streamB.ReadAtLeast(bufferB.AsSpan(0, read), read, throwOnEndOfStream: false) != read ||
                    !bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                    return false;
            }
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bufferA);
            ArrayPool<byte>.Shared.Return(bufferB);
        }
    }

    private static void DeleteEmptyTree(string dir)
    {
        try
        {
            // Links were skipped during the walk and are left in place;
            // Directory.Delete below then fails on their parent, as intended.
            foreach (var sub in new DirectoryInfo(dir).EnumerateDirectories())
                if (sub.LinkTarget is null)
                    DeleteEmptyTree(sub.FullName);

            Directory.Delete(dir); // non-recursive: only succeeds when empty
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
