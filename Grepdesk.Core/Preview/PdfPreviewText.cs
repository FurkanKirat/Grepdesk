using System;
using System.Text;
using System.Threading;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Grepdesk.Core.Preview;

/// <summary>
/// PDF text for reading, not for searching. <see cref="ContentSearch.PdfExtractor"/>
/// takes each page's raw text stream, which is fine for substring matching but
/// often has no spaces or line breaks between words. This rebuilds the reading
/// order from glyph positions instead — slower, so it only runs for the few
/// pages a preview shows.
/// </summary>
public static class PdfPreviewText
{
    public sealed record Result(string Text, int PagesRead, int PageCount);

    public static Result? Extract(string path, int maxPages, int maxChars, CancellationToken ct)
    {
        try
        {
            using var pdf = PdfDocument.Open(path);
            var sb = new StringBuilder();
            var pagesRead = 0;

            foreach (var page in pdf.GetPages())
            {
                ct.ThrowIfCancellationRequested();
                if (pagesRead == maxPages || sb.Length >= maxChars) break;

                if (pagesRead > 0) sb.Append("\n\n");
                sb.Append(ContentOrderTextExtractor.GetText(page, true).Trim());
                pagesRead++;
            }

            var text = sb.ToString().Trim();
            if (text.Length == 0) return null; // scanned PDF without a text layer
            if (text.Length > maxChars) text = text[..maxChars];
            return new Result(text, pagesRead, pdf.NumberOfPages);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null; // corrupt, encrypted, unsupported
        }
    }
}
