using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using UglyToad.PdfPig;

namespace FileSearch.Core.ContentSearch;

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

    public Task<string?> ExtractTextAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var sb = new StringBuilder();

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                sb.Append(page.Text).Append(' ');
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            // Corrupt file, unsupported encryption, scanned-image-only PDF, etc.
            return (string?)null;
        }
    }, ct);
}