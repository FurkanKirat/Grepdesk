using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;

namespace Grepdesk.Core.Cleanup;

/// <summary>What happened to one item.</summary>
/// <param name="Freed">Bytes actually deleted (or moved to the bin).</param>
/// <param name="Skipped">Files left behind, usually because a program has them open.</param>
public sealed record ItemOutcome(CleanupItem Item, long Freed, int Skipped)
{
    public bool Complete => Skipped == 0;
}

/// <param name="Outcomes">One per item that was processed (fewer than asked if cancelled).</param>
public sealed record CleanupResult(IReadOnlyList<ItemOutcome> Outcomes, bool Cancelled)
{
    public long FreedBytes => Outcomes.Sum(o => o.Freed);
    public int SkippedFiles => Outcomes.Sum(o => o.Skipped);
    public IEnumerable<CleanupItem> Done => Outcomes.Where(o => o.Complete).Select(o => o.Item);
}

/// <param name="FreedBytes">Freed so far.</param>
/// <param name="ItemsDone">Items finished so far.</param>
public readonly record struct CleanupProgress(long FreedBytes, int ItemsDone, int ItemCount);

public sealed class CleanupExecutor(IPlatformShell shell)
{
    /// <summary>
    /// Deletes or trashes the items, reporting progress as it goes. Cancelling
    /// stops between files; what was already deleted is reported, not undone.
    /// </summary>
    public CleanupResult Run(CleanupAction action, IReadOnlyList<CleanupItem> items, CancellationToken ct,
        IProgress<CleanupProgress>? progress = null)
    {
        var outcomes = new List<ItemOutcome>();
        long freedSoFar = 0;

        try
        {
            foreach (var item in items)
            {
                ct.ThrowIfCancellationRequested();

                var outcome = action switch
                {
                    // Freed once the bin is emptied; the UI says so.
                    CleanupAction.MoveToTrash => shell.MoveToTrash(item.Path).IsSuccess
                        ? new ItemOutcome(item, item.Bytes, 0)
                        : new ItemOutcome(item, 0, 1),
                    CleanupAction.DeletePermanently => Delete(item, ct, bytes => progress?.Report(new(freedSoFar + bytes, outcomes.Count, items.Count))),
                    _ => throw new ArgumentOutOfRangeException(nameof(action), action, "Only delete and trash act on items."),
                };

                outcomes.Add(outcome);
                freedSoFar += outcome.Freed;
                progress?.Report(new CleanupProgress(freedSoFar, outcomes.Count, items.Count));
            }
        }
        catch (OperationCanceledException)
        {
            return new CleanupResult(outcomes, Cancelled: true);
        }

        return new CleanupResult(outcomes, Cancelled: false);
    }

    private static ItemOutcome Delete(CleanupItem item, CancellationToken ct, Action<long> onProgress)
    {
        var (bytes, failures) = item.IsDirectory
            ? DeleteTree(item.Path, deleteRoot: !item.ContentsOnly, ct, onProgress)
            : DeleteFile(item.Path);
        return new ItemOutcome(item, bytes, failures);
    }

    private static (long Bytes, int Failures) DeleteFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return (0, 0);
            var length = info.Length;
            if (info.IsReadOnly) info.IsReadOnly = false;
            info.Delete();
            return (length, 0);
        }
        catch (IOException) { return (0, 1); }               // in use
        catch (UnauthorizedAccessException) { return (0, 1); }
    }

    /// <summary>
    /// Deletes a folder's contents (and the folder, if asked), keeping going past
    /// files in use. Links inside are removed as links: their targets are never touched.
    /// </summary>
    private static (long Bytes, int Failures) DeleteTree(string path, bool deleteRoot, CancellationToken ct, Action<long> onProgress)
    {
        var lastReport = 0L;
        long bytes = 0;
        var failures = 0;

        DirectoryInfo root;
        try
        {
            root = new DirectoryInfo(path);
            if (!root.Exists) return (0, 0);
        }
        catch (IOException) { return (0, 1); }
        catch (UnauthorizedAccessException) { return (0, 1); }

        void Walk(DirectoryInfo dir, bool removeSelf)
        {
            ct.ThrowIfCancellationRequested();

            IEnumerable<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos(); }
            catch (IOException) { failures++; return; }
            catch (UnauthorizedAccessException) { failures++; return; }

            foreach (var entry in entries)
            {
                if (entry is DirectoryInfo sub)
                {
                    if (sub.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    {
                        try { sub.Delete(recursive: false); } // removes the link only
                        catch (IOException) { failures++; }
                        catch (UnauthorizedAccessException) { failures++; }
                    }
                    else
                    {
                        Walk(sub, removeSelf: true);
                    }
                }
                else
                {
                    var (b, f) = DeleteFile(entry.FullName);
                    bytes += b;
                    failures += f;

                    // A big cache is tens of thousands of files: report every 32 MB, not every file.
                    if (bytes - lastReport >= 32L * 1024 * 1024)
                    {
                        lastReport = bytes;
                        onProgress(bytes);
                    }
                }
            }

            if (!removeSelf) return;
            try { dir.Delete(recursive: false); }
            catch (IOException) { failures++; }               // not empty: something inside was in use
            catch (UnauthorizedAccessException) { failures++; }
        }

        Walk(root, deleteRoot);
        return (bytes, failures);
    }
}
