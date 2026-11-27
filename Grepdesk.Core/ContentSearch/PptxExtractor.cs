using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Core.ContentSearch;

/// <summary>
/// Extracts structured slide text, table contents, and speaker notes from
/// PowerPoint (.pptx and .ppt) presentations in presentation order.
/// </summary>
public class PptxExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions => [".pptx", ".ppt"];

    public async Task<string?> ExtractTextAsync(string path, CancellationToken ct) =>
        (await ExtractDetailedAsync(path, ct)).Text;

    public Task<ExtractionResult> ExtractDetailedAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var doc = PptxDocument.Load(path, ct);
            var text = doc.ToPlainText();
            return string.IsNullOrWhiteSpace(text)
                ? ExtractionResult.Fail(SkipReason.NoExtractableText)
                : ExtractionResult.Ok(text);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (UnauthorizedAccessException ex)
        {
            return ExtractionResult.Fail(SkipReason.AccessDenied, ex.Message);
        }
        catch (IOException ex)
        {
            return ExtractionResult.Fail(SkipClassifier.FromIoException(ex), ex.Message);
        }
        catch (Exception ex)
        {
            var isPptx = string.Equals(Path.GetExtension(path), ".pptx", StringComparison.OrdinalIgnoreCase);
            if ((isPptx && SkipClassifier.IsOle2Container(path)) ||
                ex.Message.Contains("password", StringComparison.OrdinalIgnoreCase) ||
                ex.Message.Contains("encrypt", StringComparison.OrdinalIgnoreCase))
                return ExtractionResult.Fail(SkipReason.PasswordProtected, ex.Message);

            return ExtractionResult.Fail(SkipReason.CorruptOrUnsupported, ex.Message);
        }
    }, ct);
}
