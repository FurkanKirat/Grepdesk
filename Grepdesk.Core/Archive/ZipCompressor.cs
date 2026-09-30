using System.Buffers;
using System.Collections.Concurrent;
using System.IO.Compression;
using System.IO.Hashing;
using System.Threading.Channels;
using Grepdesk.Core.Jobs;

namespace Grepdesk.Core.Archive;

/// <summary>
/// Parallel zip creation. Workers compress files independently — small ones
/// into memory, large ones into a temp file — and a single writer appends the
/// finished entries to the archive with <see cref="ZipWriter"/>. This is the
/// same model 7-Zip uses for zip (parallel across files): one huge file still
/// compresses on one core, but a folder of many files uses all of them.
/// </summary>
public static class ZipCompressor
{
    // Up to this size a file is read whole and compressed in memory.
    private const long InMemoryLimit = 32L * 1024 * 1024;
    // Bytes of raw + compressed data allowed to wait in memory for the writer.
    private const long MemoryBudget = 512L * 1024 * 1024;
    private const int BufferSize = 1 << 20;
    // A large file is cut into chunks of this size and the chunks are
    // compressed on several cores (see PrepareInTempFile).
    private const int ChunkSize = 4 << 20;
    // A large file whose first chunk doesn't shrink by at least this much is
    // stored instead of compressed (random data, encrypted or media files
    // with an extension missing from the list above).
    private const double WorthCompressingRatio = 0.97;

    // Already compressed formats: deflating them again burns CPU for ~0% gain,
    // so they are stored as-is. This is where most of the time goes in a
    // typical photos/videos/downloads folder.
    private static readonly HashSet<string> Incompressible = new(StringComparer.OrdinalIgnoreCase)
    {
        ".zip", ".7z", ".rar", ".gz", ".tgz", ".bz2", ".xz", ".zst", ".lz4", ".cab", ".jar", ".apk", ".nupkg", ".whl",
        ".jpg", ".jpeg", ".png", ".gif", ".webp", ".heic", ".heif", ".avif", ".jxl",
        ".mp3", ".aac", ".m4a", ".ogg", ".opus", ".flac", ".wma",
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv",
        ".docx", ".xlsx", ".pptx", ".odt", ".ods", ".odp", ".epub",
        ".pdf", ".woff", ".woff2", ".msi", ".iso", ".vhdx", ".dmg"
    };

    private sealed record Item(string FullPath, string EntryName, long Length, DateTime LastWrite, FileAttributes Attributes);

    private abstract record Prepared(Item Item);
    private sealed record InMemory(Item Item, ushort Method, uint Crc, byte[] Data, int Length, byte[]? Pooled, long Budget) : Prepared(Item);
    private sealed record InTempFile(Item Item, uint Crc, long CompressedLength, string TempPath) : Prepared(Item);
    private sealed record StoreFromSource(Item Item) : Prepared(Item);

    /// <summary>
    /// Where Explorer's "Send to → Compressed folder" would put it: next to the
    /// selection, named after the single selected item, or after the
    /// containing folder when several are selected. Never overwrites.
    /// </summary>
    public static string DefaultArchivePath(IReadOnlyList<string> paths)
    {
        var first = Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0]));
        var folder = Path.GetDirectoryName(first) ?? first;

        var name = paths.Count == 1
            ? (Directory.Exists(first) ? Path.GetFileName(first) : Path.GetFileNameWithoutExtension(first))
            : Path.GetFileName(folder);
        if (string.IsNullOrEmpty(name))
            name = "Archive"; // a drive root like "D:\"

        var path = Path.Combine(folder, name + ".zip");
        return File.Exists(path) || Directory.Exists(path) ? ConflictPolicy.NextFreePath(path) : path;
    }

    public static async Task CompressAsync(IReadOnlyList<string> paths, string archivePath, CompressionLevel level,
        JobContext ctx, int workerCount)
    {
        archivePath = Path.GetFullPath(archivePath);
        var partialPath = archivePath + ".partial";
        var tempFiles = new ConcurrentBag<string>();

        var (items, emptyDirectories) = Plan(paths, archivePath, ctx);
        ctx.Progress.MarkTotalFinal();

        // Writer failure (disk full…) must also stop the workers, or they
        // would block forever on a full queue.
        using var abort = CancellationTokenSource.CreateLinkedTokenSource(ctx.Token);
        var queue = Channel.CreateBounded<Prepared>(Math.Max(4, workerCount * 2));
        var budget = new ByteBudget(MemoryBudget);
        // Chunks of large files in flight across all workers (each ~2 × 4 MB).
        using var chunkSlots = new SemaphoreSlim(Math.Max(2, workerCount * 2));
        var completed = false;

        try
        {
            await using (var output = new FileStream(partialPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, BufferSize))
            {
                var writer = new ZipWriter(output);

                var writerTask = Task.Factory.StartNew(() =>
                {
                    try { RunWriter(writer, queue.Reader, budget, ctx, abort.Token); }
                    catch { abort.Cancel(); throw; }
                }, abort.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

                var next = -1;
                var workers = Enumerable.Range(0, Math.Clamp(workerCount, 1, Math.Max(1, items.Length)))
                    .Select(_ => Task.Factory.StartNew(
                        () => RunWorker(items, ref next, archivePath, level, chunkSlots, queue.Writer, budget, tempFiles, ctx, abort.Token),
                        abort.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                    .ToArray();

                try { await Task.WhenAll(workers); }
                catch (OperationCanceledException) when (!ctx.Token.IsCancellationRequested)
                {
                    // The writer failed and stopped the workers; its own
                    // exception (disk full, …) is the one to report, below.
                }
                finally { queue.Writer.TryComplete(); }
                await writerTask;

                foreach (var (name, lastWrite) in emptyDirectories)
                    writer.AddDirectory(name, lastWrite);
                writer.Finish();
            }

            ctx.Token.ThrowIfCancellationRequested();
            var finalPath = File.Exists(archivePath) ? ConflictPolicy.NextFreePath(archivePath) : archivePath;
            File.Move(partialPath, finalPath);
            completed = true;
        }
        finally
        {
            foreach (var temp in tempFiles)
                TryDelete(temp);
            if (!completed)
                TryDelete(partialPath);
        }
    }

    // ---------------------------------------------------------------- plan

    private static (Item[] Files, List<(string Name, DateTime LastWrite)> EmptyDirectories) Plan(
        IReadOnlyList<string> paths, string archivePath, JobContext ctx)
    {
        var baseFolder = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(Path.GetFullPath(paths[0])))!;
        var files = new List<Item>();
        var emptyDirs = new List<(string, DateTime)>();

        string EntryName(string fullPath)
        {
            var relative = Path.GetRelativePath(baseFolder, fullPath);
            // Selection from another folder: never write "../" into an archive.
            if (relative.StartsWith("..", StringComparison.Ordinal) || Path.IsPathRooted(relative))
                relative = Path.GetFileName(fullPath);
            return relative.Replace('\\', '/');
        }

        foreach (var raw in paths)
        {
            ctx.Token.ThrowIfCancellationRequested();
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(raw));

            if (File.Exists(path))
            {
                var info = new FileInfo(path);
                files.Add(new Item(path, EntryName(path), info.Length, info.LastWriteTime, info.Attributes));
                ctx.Progress.AddToTotal(info.Length);
                continue;
            }
            if (!Directory.Exists(path))
            {
                ctx.ReportError(path, JobErrorKind.NotFound);
                continue;
            }

            var pending = new Stack<DirectoryInfo>();
            pending.Push(new DirectoryInfo(path));
            while (pending.Count > 0)
            {
                ctx.Token.ThrowIfCancellationRequested();
                var dir = pending.Pop();

                List<FileSystemInfo> entries;
                try { entries = dir.EnumerateFileSystemInfos().ToList(); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    ctx.ReportError(dir.FullName, ex);
                    continue;
                }

                if (entries.Count == 0)
                    emptyDirs.Add((EntryName(dir.FullName) + "/", dir.LastWriteTime));

                foreach (var entry in entries)
                {
                    switch (entry)
                    {
                        case DirectoryInfo sub when sub.LinkTarget is not null:
                            ctx.ReportError(sub.FullName, JobErrorKind.LinkSkipped);
                            break;
                        case DirectoryInfo sub:
                            pending.Push(sub);
                            break;
                        case FileInfo file when IsOwnOutput(file.FullName, archivePath):
                            break;
                        case FileInfo file:
                            files.Add(new Item(file.FullName, EntryName(file.FullName), file.Length, file.LastWriteTime, file.Attributes));
                            ctx.Progress.AddToTotal(file.Length);
                            break;
                    }
                }
            }
        }

        // Largest first, so the run doesn't end with one core on a big file.
        return (files.OrderByDescending(f => f.Length).ToArray(), emptyDirs);
    }

    private static bool IsOwnOutput(string path, string archivePath) =>
        string.Equals(path, archivePath, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(path, archivePath + ".partial", StringComparison.OrdinalIgnoreCase);

    // ---------------------------------------------------------------- workers

    private static void RunWorker(Item[] items, ref int next, string archivePath, CompressionLevel level, SemaphoreSlim chunkSlots,
        ChannelWriter<Prepared> queue, ByteBudget budget, ConcurrentBag<string> tempFiles, JobContext ctx, CancellationToken abort)
    {
        int i;
        while ((i = Interlocked.Increment(ref next)) < items.Length)
        {
            abort.ThrowIfCancellationRequested();
            ctx.Pause.WaitIfPaused(abort);
            var item = items[i];
            ctx.Progress.SetCurrent(item.EntryName);

            Prepared prepared;
            try
            {
                prepared = Prepare(item, archivePath, level, chunkSlots, budget, tempFiles, ctx, abort);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Unreadable file: leave it out of the archive and carry on.
                ctx.ReportError(item.FullPath, ex);
                ctx.Progress.AddDoneBytes(item.Length);
                ctx.Progress.FileDone();
                continue;
            }

            queue.WriteAsync(prepared, abort).AsTask().GetAwaiter().GetResult();
        }
    }

    private static Prepared Prepare(Item item, string archivePath, CompressionLevel level, SemaphoreSlim chunkSlots, ByteBudget budget,
        ConcurrentBag<string> tempFiles, JobContext ctx, CancellationToken abort)
    {
        var store = level == CompressionLevel.NoCompression || Incompressible.Contains(Path.GetExtension(item.FullPath));

        // Stored files are copied straight from the source by the writer:
        // no point staging them anywhere first.
        if (store && item.Length > InMemoryLimit)
            return new StoreFromSource(item);

        return item.Length <= InMemoryLimit
            ? PrepareInMemory(item, store, level, budget, ctx, abort)
            : PrepareInTempFile(item, archivePath, level, chunkSlots, tempFiles, ctx, abort);
    }

    private static Prepared PrepareInMemory(Item item, bool store, CompressionLevel level, ByteBudget budget, JobContext ctx, CancellationToken abort)
    {
        var length = (int)item.Length;
        var reserved = store ? item.Length : item.Length * 2;
        budget.Acquire(reserved, abort);

        var raw = ArrayPool<byte>.Shared.Rent(Math.Max(1, length));
        try
        {
            using (var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
            {
                if (input.Length != item.Length)
                    throw new IOException($"{item.FullPath} changed size while it was being compressed.");
                input.ReadExactly(raw, 0, length);
            }

            var crc = Crc32.HashToUInt32(raw.AsSpan(0, length));
            ctx.Progress.AddDoneBytes(length);

            if (!store && length > 0)
            {
                var compressed = new MemoryStream(length / 2 + 64);
                using (var deflate = new DeflateStream(compressed, level, leaveOpen: true))
                    deflate.Write(raw, 0, length);

                // Deflate made it bigger (random data): store the original instead.
                if (compressed.Length < length)
                {
                    ArrayPool<byte>.Shared.Return(raw);
                    budget.Release(reserved - compressed.Length);
                    return new InMemory(item, ZipWriter.MethodDeflate, crc, compressed.GetBuffer(), (int)compressed.Length, null, compressed.Length);
                }
            }

            budget.Release(reserved - length);
            return new InMemory(item, ZipWriter.MethodStored, crc, raw, length, raw, length);
        }
        catch
        {
            ArrayPool<byte>.Shared.Return(raw);
            budget.Release(reserved);
            throw;
        }
    }

    /// <summary>
    /// Large files. One file would otherwise compress on a single core (as in
    /// 7-Zip and Explorer), so it is cut into chunks that are deflated in
    /// parallel. Every chunk but the last ends with a sync flush, which closes
    /// it on a byte boundary without marking the stream finished; laid end to
    /// end the chunks form one valid deflate stream (the technique pigz uses).
    /// Each chunk starts without the previous chunk's 32 KB history, which
    /// costs well under 1% of size at 4 MB chunks.
    /// </summary>
    private static Prepared PrepareInTempFile(Item item, string archivePath, CompressionLevel level, SemaphoreSlim chunkSlots,
        ConcurrentBag<string> tempFiles, JobContext ctx, CancellationToken abort)
    {
        using var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        if (input.Length != item.Length)
            throw new IOException($"{item.FullPath} changed size while it was being compressed.");

        // Try the first chunk before committing to compress 100s of MB.
        var first = ArrayPool<byte>.Shared.Rent(ChunkSize);
        var firstLength = input.ReadAtLeast(first.AsSpan(0, ChunkSize), ChunkSize, throwOnEndOfStream: false);
        var (_, _, sampleCompressed) = CompressChunk(first, firstLength, CompressionLevel.Fastest, last: true);
        if (sampleCompressed.Length > firstLength * WorthCompressingRatio)
        {
            ArrayPool<byte>.Shared.Return(first);
            return new StoreFromSource(item);
        }

        // Same folder as the archive, so it's on the same disk and has space.
        var tempPath = Path.Combine(Path.GetDirectoryName(archivePath)!, $".{Path.GetFileName(archivePath)}.{Guid.NewGuid():N}.tmp");
        tempFiles.Add(tempPath);

        var crc = new Crc32();
        var pending = new Queue<Task<(byte[] Raw, int Length, MemoryStream Compressed)>>();
        long read = 0;

        using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        try
        {
            var chunk = first;
            var length = firstLength;
            while (true)
            {
                crc.Append(chunk.AsSpan(0, length));
                read += length;
                var last = read >= item.Length || length == 0;

                // A slot per chunk in flight, shared by every worker. With none
                // free, write out our own oldest chunk first: waiting while
                // holding slots could deadlock workers against each other.
                while (!chunkSlots.Wait(0))
                {
                    if (pending.Count > 0)
                        WriteChunk(pending.Dequeue(), temp, ctx, chunkSlots);
                    else
                    {
                        chunkSlots.Wait(abort);
                        break;
                    }
                }

                var (buffer, size, isLast) = (chunk, length, last);
                pending.Enqueue(Task.Run(() => CompressChunk(buffer, size, level, isLast), abort));

                if (last)
                {
                    while (pending.Count > 0)
                        WriteChunk(pending.Dequeue(), temp, ctx, chunkSlots);
                }

                if (last)
                    break;

                abort.ThrowIfCancellationRequested();
                ctx.Pause.WaitIfPaused(abort);
                chunk = ArrayPool<byte>.Shared.Rent(ChunkSize);
                length = input.ReadAtLeast(chunk.AsSpan(0, ChunkSize), ChunkSize, throwOnEndOfStream: false);
            }
        }
        finally
        {
            // On cancel or error, let the chunks in flight finish and give their buffers back.
            while (pending.Count > 0)
            {
                try { var (raw, _, _) = pending.Dequeue().GetAwaiter().GetResult(); ArrayPool<byte>.Shared.Return(raw); }
                catch (OperationCanceledException) { }
                finally { chunkSlots.Release(); }
            }
        }

        if (read != item.Length)
            throw new IOException($"{item.FullPath} changed size while it was being compressed.");

        return new InTempFile(item, crc.GetCurrentHashAsUInt32(), temp.Length, tempPath);
    }

    private static (byte[] Raw, int Length, MemoryStream Compressed) CompressChunk(byte[] raw, int length, CompressionLevel level, bool last)
    {
        var output = new MemoryStream(length / 2 + 1024);
        var deflate = new DeflateStream(output, level, leaveOpen: true);
        deflate.Write(raw, 0, length);
        if (last)
        {
            deflate.Dispose(); // final block: marks the end of the stream
            return (raw, length, output);
        }

        deflate.Flush(); // sync flush: byte-aligned, stream stays open
        var flushed = output.Length;
        deflate.Dispose(); // would append a final block; cut it off again
        output.SetLength(flushed);
        return (raw, length, output);
    }

    private static void WriteChunk(Task<(byte[] Raw, int Length, MemoryStream Compressed)> task, Stream temp, JobContext ctx, SemaphoreSlim chunkSlots)
    {
        try
        {
            var (raw, length, compressed) = task.GetAwaiter().GetResult();
            temp.Write(compressed.GetBuffer(), 0, (int)compressed.Length);
            ctx.Progress.AddDoneBytes(length);
            ArrayPool<byte>.Shared.Return(raw);
        }
        finally
        {
            chunkSlots.Release();
        }
    }

    // ---------------------------------------------------------------- writer

    private static void RunWriter(ZipWriter writer, ChannelReader<Prepared> queue, ByteBudget budget, JobContext ctx, CancellationToken abort)
    {
        while (queue.WaitToReadAsync(abort).AsTask().GetAwaiter().GetResult())
        {
            while (queue.TryRead(out var prepared))
            {
                var item = prepared.Item;
                try
                {
                    Write(writer, prepared, ctx, abort);
                }
                catch (IOException ex) when (prepared is StoreFromSource)
                {
                    // The source became unreadable; the writer rolled the entry back.
                    ctx.ReportError(item.FullPath, ex);
                }
                finally
                {
                    if (prepared is InMemory m)
                    {
                        if (m.Pooled is not null)
                            ArrayPool<byte>.Shared.Return(m.Pooled);
                        budget.Release(m.Budget);
                    }
                    else if (prepared is InTempFile t)
                    {
                        TryDelete(t.TempPath);
                    }
                }
                ctx.Progress.FileDone();
            }
        }
    }

    private static void Write(ZipWriter writer, Prepared prepared, JobContext ctx, CancellationToken abort)
    {
        var item = prepared.Item;
        switch (prepared)
        {
            case InMemory m:
                writer.AddPrepared(item.EntryName, item.LastWrite, item.Attributes, m.Method, m.Crc, item.Length, m.Length,
                    output => output.Write(m.Data, 0, m.Length));
                break;

            case InTempFile t:
                writer.AddPrepared(item.EntryName, item.LastWrite, item.Attributes, ZipWriter.MethodDeflate, t.Crc, item.Length, t.CompressedLength,
                    output =>
                    {
                        using var temp = new FileStream(t.TempPath, FileMode.Open, FileAccess.Read, FileShare.None, BufferSize, FileOptions.SequentialScan);
                        temp.CopyTo(output, BufferSize);
                    });
                break;

            case StoreFromSource:
                using (var input = new FileStream(item.FullPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan))
                {
                    writer.AddStoredStream(item.EntryName, item.LastWrite, item.Attributes, input, item.Length,
                        bytes => ctx.Progress.AddDoneBytes(bytes),
                        () =>
                        {
                            abort.ThrowIfCancellationRequested();
                            ctx.Pause.WaitIfPaused(abort);
                        });
                }
                break;
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>A counting limit on bytes held in memory; one oversize request is let through alone.</summary>
    private sealed class ByteBudget(long limit)
    {
        private readonly object _lock = new();
        private long _inUse;

        public void Acquire(long bytes, CancellationToken ct)
        {
            lock (_lock)
            {
                while (_inUse > 0 && _inUse + bytes > limit)
                {
                    ct.ThrowIfCancellationRequested();
                    Monitor.Wait(_lock, 100);
                }
                _inUse += bytes;
            }
        }

        public void Release(long bytes)
        {
            if (bytes == 0)
                return;
            lock (_lock)
            {
                _inUse -= bytes;
                Monitor.PulseAll(_lock);
            }
        }
    }
}
