using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using Grepdesk.Core.Viewer;

namespace Grepdesk.Core.ContentSearch;

/// <summary>
/// Extracts structured rows and cells from Excel (.xlsx) workbooks in sheet order,
/// resolving shared strings, inline strings, numbers, dates, and formulas.
/// </summary>
public class XlsxExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions => [".xlsx"];

    public async Task<string?> ExtractTextAsync(string path, CancellationToken ct) =>
        (await ExtractDetailedAsync(path, ct)).Text;

    public Task<ExtractionResult> ExtractDetailedAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var doc = XlsxDocument.Load(path, ct);
            var text = doc.ToPlainText();
            if (!string.IsNullOrWhiteSpace(text))
                return ExtractionResult.Ok(text);
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
        catch
        {
            // Fall back to raw XML extraction if workbook structure is incomplete.
        }

        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sb = new StringBuilder();
            var any = false;

            var sharedStrings = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStrings is not null && TryExtractSharedStrings(sharedStrings, sb))
                any = true;

            foreach (var sheetEntry in archive.Entries.Where(e =>
                         e.FullName.StartsWith("xl/worksheets/") && e.FullName.EndsWith(".xml")))
            {
                ct.ThrowIfCancellationRequested();
                if (TryExtractSheetText(sheetEntry, sb))
                    any = true;
            }

            return any
                ? ExtractionResult.Ok(sb.ToString())
                : ExtractionResult.Fail(SkipReason.NoExtractableText);
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
        catch (InvalidDataException ex)
        {
            return SkipClassifier.IsOle2Container(path)
                ? ExtractionResult.Fail(SkipReason.PasswordProtected)
                : ExtractionResult.Fail(SkipReason.CorruptOrUnsupported, ex.Message);
        }
        catch (Exception ex)
        {
            return ExtractionResult.Fail(SkipReason.CorruptOrUnsupported, ex.Message);
        }
    }, ct);

    private static bool TryExtractSharedStrings(ZipArchiveEntry entry, StringBuilder sb)
    {
        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var any = false;

            foreach (var si in doc.Descendants().Where(e => e.Name.LocalName == "si"))
            {
                foreach (var t in si.Descendants().Where(e => e.Name.LocalName == "t"))
                    sb.Append(t.Value);
                sb.Append(' ');
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryExtractSheetText(ZipArchiveEntry entry, StringBuilder sb)
    {
        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);
            var any = false;

            foreach (var cell in doc.Descendants().Where(e => e.Name.LocalName == "c"))
            {
                foreach (var t in cell.Descendants().Where(e => e.Name.LocalName == "t"))
                    sb.Append(t.Value);
                sb.Append(' ');
                any = true;
            }

            return any;
        }
        catch
        {
            return false;
        }
    }
}
