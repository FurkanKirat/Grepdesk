using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Grepdesk.Core.ContentSearch;

public readonly record struct ExtractionResult(string? Text, SkipReason? FailureReason = null, string? Detail = null)
{
    public bool IsSuccess => Text is not null;
    public static ExtractionResult Ok(string text) => new(text);
    public static ExtractionResult Fail(SkipReason reason, string? detail = null) => new(null, reason, detail);
}

/// <summary>
/// A pluggable extractor that knows how to pull searchable plain text out of
/// one or more file extensions. Register new formats by implementing this
/// and adding an instance in <see cref="ExtractorRegistry"/> — no other
/// code needs to change.
/// </summary>
public interface IContentExtractor
{
    /// <summary>File extensions this extractor handles, e.g. ".docx". Case-insensitive.</summary>
    IEnumerable<string> SupportedExtensions { get; }

    /// <summary>
    /// Returns the extracted plain text, or null if the file could not be read
    /// or contained nothing usable. Should never throw for "expected" failures
    /// (corrupt file, locked file, unsupported sub-format) — return null instead
    /// so ContentSearcher can just skip it.
    /// </summary>
    Task<string?> ExtractTextAsync(string path, CancellationToken ct);

    /// <summary>
    /// Extracts text and returns a structured failure reason and optional detail
    /// when extraction fails.
    /// </summary>
    async Task<ExtractionResult> ExtractDetailedAsync(string path, CancellationToken ct)
    {
        var text = await ExtractTextAsync(path, ct);
        return text is not null ? ExtractionResult.Ok(text) : ExtractionResult.Fail(SkipReason.ExtractionFailed);
    }
}
