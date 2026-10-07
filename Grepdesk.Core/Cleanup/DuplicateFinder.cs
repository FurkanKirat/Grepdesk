using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.IO.Hashing;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Grepdesk.Core.Cleanup;

public sealed record DuplicateGroup(long FileSize, IReadOnlyList<SearchResult> Files)
{
    /// <summary>Space freed by keeping one copy.</summary>
    public long WastedBytes => FileSize * (Files.Count - 1);
}

/// <summary>
/// Finds files with identical contents in three narrowing passes, so most files
/// are never read: same size (from the index, free) → same first and last
/// 64 KB → same full XxHash128. Only the last pass reads whole files.
/// </summary>
public static class DuplicateFinder
{
    private const int SampleBytes = 64 * 1024;
    private const int BufferBytes = 1024 * 1024;

    /// <summary>
    /// The user's own files under <paramref name="root"/>: games, apps, the system
    /// and developer folders ship identical files on purpose, and deleting
    /// one of those copies would break something.
    /// </summary>
    public static List<SearchResult> UserFileCandidates(FileIndex index, string root, long minSize, CancellationToken ct)
    {
        var normalized = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var prefix = normalized.EndsWith(Path.DirectorySeparatorChar) ? normalized : normalized + Path.DirectorySeparatorChar;
        var result = new List<SearchResult>();
        var scanned = 0;

        foreach (var (path, e) in index.Entries)
        {
            if ((++scanned & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (e.IsDirectory || e.IsCloudOnly || e.Size < minSize) continue;
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            if (DiskUsage.DiskClassifier.ClassifyDetailed(path).GroupLength > 0) continue;

            result.Add(new SearchResult(path, false, e.Size, e.Modified));
        }
        return result;
    }

    /// <param name="progress">(bytes hashed, bytes to hash) during the full-hash pass.</param>
    public static async Task<List<DuplicateGroup>> FindAsync(
        IEnumerable<SearchResult> files, long minSize, IProgress<(long Done, long Total)>? progress, CancellationToken ct)
    {
        // Pass 1: size.
        var bySize = files
            .Where(f => !f.IsDirectory && f.Size >= minSize)
            .GroupBy(f => f.Size)
            .Where(g => g.Count() > 1)
            .ToList();

        // Pass 2: head + tail sample.
        var sampled = new ConcurrentBag<List<SearchResult>>();
        await Parallel.ForEachAsync(bySize, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct }, async (group, token) =>
        {
            var bySample = new Dictionary<UInt128, List<SearchResult>>();
            foreach (var file in group)
            {
                if (await SampleHashAsync(file.FullPath, file.Size, token) is not { } hash) continue;
                if (!bySample.TryGetValue(hash, out var list)) bySample[hash] = list = [];
                list.Add(file);
            }
            foreach (var list in bySample.Values.Where(l => l.Count > 1))
                sampled.Add(list);
        });

        // Small files: the sample already covered every byte.
        var groups = new ConcurrentBag<DuplicateGroup>();
        var needFullHash = new List<List<SearchResult>>();
        foreach (var list in sampled)
        {
            if (list[0].Size <= 2L * SampleBytes) groups.Add(new DuplicateGroup(list[0].Size, list));
            else needFullHash.Add(list);
        }

        // Pass 3: full hash.
        var total = needFullHash.Sum(l => l.Sum(f => f.Size));
        long done = 0;
        progress?.Report((0, total));

        await Parallel.ForEachAsync(needFullHash, new ParallelOptions { MaxDegreeOfParallelism = 2, CancellationToken = ct }, async (list, token) =>
        {
            var byHash = new Dictionary<UInt128, List<SearchResult>>();
            foreach (var file in list)
            {
                var hash = await FullHashAsync(file.FullPath, token, read =>
                {
                    var now = Interlocked.Add(ref done, read);
                    progress?.Report((now, total));
                });
                if (hash is null) continue;
                if (!byHash.TryGetValue(hash.Value, out var same)) byHash[hash.Value] = same = [];
                same.Add(file);
            }
            foreach (var same in byHash.Values.Where(l => l.Count > 1))
                groups.Add(new DuplicateGroup(same[0].Size, same));
        });

        return groups.OrderByDescending(g => g.WastedBytes).ToList();
    }

    private static async Task<UInt128?> SampleHashAsync(string path, long size, CancellationToken ct)
    {
        try
        {
            await using var stream = Open(path);
            var hasher = new XxHash128();
            var buffer = ArrayPool<byte>.Shared.Rent(SampleBytes);
            try
            {
                var read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, SampleBytes), SampleBytes, false, ct);
                hasher.Append(buffer.AsSpan(0, read));
                if (size > 2L * SampleBytes)
                {
                    stream.Seek(-SampleBytes, SeekOrigin.End);
                    read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, SampleBytes), SampleBytes, false, ct);
                    hasher.Append(buffer.AsSpan(0, read));
                }
                else if (size > SampleBytes)
                {
                    read = await stream.ReadAtLeastAsync(buffer.AsMemory(0, SampleBytes), SampleBytes, false, ct);
                    hasher.Append(buffer.AsSpan(0, read));
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return hasher.GetCurrentHashAsUInt128();
        }
        catch (IOException) { return null; }               // locked, gone
        catch (UnauthorizedAccessException) { return null; }
    }

    private static async Task<UInt128?> FullHashAsync(string path, CancellationToken ct, Action<int> onRead)
    {
        try
        {
            await using var stream = Open(path);
            var hasher = new XxHash128();
            var buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
            try
            {
                int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, BufferBytes), ct)) > 0)
                {
                    hasher.Append(buffer.AsSpan(0, read));
                    onRead(read);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return hasher.GetCurrentHashAsUInt128();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static FileStream Open(string path) =>
        new(path, new FileStreamOptions
        {
            Mode = FileMode.Open,
            Access = FileAccess.Read,
            Share = FileShare.ReadWrite | FileShare.Delete,
            Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            BufferSize = 0,
        });
}
