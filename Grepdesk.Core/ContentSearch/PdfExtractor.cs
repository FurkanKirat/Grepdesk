using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Exceptions;

namespace Grepdesk.Core.ContentSearch;

/// <summary>
/// PDF text extraction via PdfPig — handles standard PDF text encoding
/// including compressed (FlateDecode) content streams, which covers the vast
/// majority of real-world PDFs (Word/Google Docs exports, browser "print to
/// PDF", etc.).
///
/// LIMITATION: this only reads text that's actually embedded as text in the
/// PDF. Scanned/photographed documents with no text layer will return null
/// and be skipped — that needs OCR (e.g. Tesseract), which is a separate,
/// heavier addition if it turns out to matter for your files.
/// </summary>
public class PdfExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions => [".pdf"];

    public async Task<string?> ExtractTextAsync(string path, CancellationToken ct) =>
        (await ExtractDetailedAsync(path, ct)).Text;

    public Task<ExtractionResult> ExtractDetailedAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var sb = new StringBuilder();
            var hasText = false;

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                var pageText = page.Text;
                if (!string.IsNullOrWhiteSpace(pageText))
                    hasText = true;
                sb.Append(pageText).Append(' ');
            }

            return hasText
                ? ExtractionResult.Ok(sb.ToString())
                : ExtractionResult.Fail(SkipReason.NoExtractableText);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (PdfDocumentEncryptedException ex)
        {
            return ExtractionResult.Fail(SkipReason.PasswordProtected, ex.Message);
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
            return ex.GetType().Name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase)
                ? ExtractionResult.Fail(SkipReason.PasswordProtected, ex.Message)
                : ExtractionResult.Fail(SkipReason.CorruptOrUnsupported, ex.Message);
        }
    }, ct);
}