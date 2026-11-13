using System.Collections.Generic;
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

    public Task<string?> ExtractTextAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var doc = XlsxDocument.Load(path, ct);
            var text = doc.ToPlainText();
            if (!string.IsNullOrWhiteSpace(text))
                return text;
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

            return any ? sb.ToString() : null;
        }
        catch
        {
            return (string?)null;
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
