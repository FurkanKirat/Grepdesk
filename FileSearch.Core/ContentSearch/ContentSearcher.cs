using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace FileSearch.Core.ContentSearch;

public record ContentMatch(string FullPath, string Snippet);

public enum SkipReason { TooLarge, ExtractionFailed }

public record SkippedFile(string FullPath, SkipReason Reason);

public static class ContentSearcher
{
    private const long MaxFileSizeBytes = 20 * 1024 * 1024; // 20 MB safety cap
    private const int SnippetContextChars = 40;

    // Each file's extraction is largely I/O + one-off CPU work (unzip, regex,
    // decode). Running them one-at-a-time serializes all of that for no
    // reason; a handful of files in flight at once keeps the UI responsive
    // and finishes far faster without saturating disk I/O the way full
    // unbounded parallelism would.
    private static readonly int MaxConcurrency = Math.Clamp(Environment.ProcessorCount, 2, 8);

    /// <summary>
    /// Searches files concurrently (bounded) and streams matches to the caller
    /// as soon as each one is found, so the UI can update incrementally instead
    /// of freezing until the whole folder finishes.
    ///
    /// Files that couldn't actually be searched (too large, or the extractor
    /// failed — corrupt/password-protected/compressed-PDF/etc.) are reported
    /// via <paramref name="onSkipped"/> rather than silently dropped, so the
    /// caller can tell "searched and found nothing" apart from "couldn't read
    /// this one at all". Files with unsupported extensions are expected to
    /// already be filtered out by the caller before this runs.
    /// </summary>
    public static async IAsyncEnumerable<ContentMatch> SearchAsync(
        IEnumerable<string> filePaths,
        string query,
        Action<SkippedFile>? onSkipped = null,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query)) yield break;

        var channel = Channel.CreateUnbounded<ContentMatch>();
        using var semaphore = new SemaphoreSlim(MaxConcurrency);

        var producer = Task.Run(async () =>
        {
            var inFlight = new List<Task>();

            try
            {
                foreach (var path in filePaths)
                {
                    ct.ThrowIfCancellationRequested();
                    await semaphore.WaitAsync(ct);

                    inFlight.Add(Task.Run(async () =>
                    {
                        try
                        {
                            var result = await SearchSingleFileAsync(path, query, ct);
                            if (result.Match is not null)
                                await channel.Writer.WriteAsync(result.Match, ct);
                            else if (result.SkipReason is { } reason)
                                onSkipped?.Invoke(new SkippedFile(path, reason));
                        }
                        catch (OperationCanceledException) { /* expected on cancel */ }
                        catch
                        {
                            onSkipped?.Invoke(new SkippedFile(path, SkipReason.ExtractionFailed));
                        }
                        finally { semaphore.Release(); }
                    }, ct));
                }

                await Task.WhenAll(inFlight);
            }
            catch (OperationCanceledException) { /* expected on cancel */ }
            finally
            {
                channel.Writer.TryComplete();
            }
        }, ct);

        await foreach (var match in channel.Reader.ReadAllAsync(ct))
            yield return match;

        await producer; // surface any unexpected producer-level exception
    }

    private static async Task<(ContentMatch? Match, SkipReason? SkipReason)> SearchSingleFileAsync(
        string path, string query, CancellationToken ct)
    {
        var ext = Path.GetExtension(path);
        if (!ExtractorRegistry.TryGetExtractor(ext, out var extractor))
            return (null, null); // unsupported extension — caller already filters these, not a "skip"

        var info = new FileInfo(path);
        if (!info.Exists) return (null, null);
        if (info.Length > MaxFileSizeBytes) return (null, SkipReason.TooLarge);

        var content = await extractor.ExtractTextAsync(path, ct);
        if (content is null) return (null, SkipReason.ExtractionFailed);

        var idx = content.IndexOf(query, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return (null, null); // read fine, just no match — not a skip

        return (new ContentMatch(path, BuildSnippet(content, idx, query.Length)), null);
    }

    private static string BuildSnippet(string content, int matchIndex, int matchLength)
    {
        var start = Math.Max(0, matchIndex - SnippetContextChars);
        var end = Math.Min(content.Length, matchIndex + matchLength + SnippetContextChars);
        var snippet = content[start..end].Replace('\n', ' ').Replace('\r', ' ').Trim();
        return (start > 0 ? "…" : "") + snippet + (end < content.Length ? "…" : "");
    }
}


