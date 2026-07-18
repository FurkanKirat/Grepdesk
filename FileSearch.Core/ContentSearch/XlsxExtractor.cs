using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace FileSearch.Core.ContentSearch;

/// <summary>
/// .xlsx is also a zip archive. Cell text mostly lives in xl/sharedStrings.xml
/// (deduplicated string table); numeric/formula cells live inline in each sheet
/// xml but we mainly care about text search, so shared strings covers the
/// common case. No external NuGet package required.
///
/// A single shared-string entry can be split into multiple &lt;r&gt; (rich
/// text) runs — e.g. if only part of a cell's text is bold. Concatenating
/// &lt;t&gt; node values directly (rather than a naive tag-strip-with-space)
/// avoids inserting a stray space in the middle of a word at a run boundary.
/// </summary>
public class XlsxExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions => [".xlsx"];

    public Task<string?> ExtractTextAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sb = new StringBuilder();
            var any = false;

            var sharedStrings = archive.GetEntry("xl/sharedStrings.xml");
            if (sharedStrings is not null && TryExtractSharedStrings(sharedStrings, sb))
                any = true;

            // Also sweep sheet XML for inline strings/values not in the shared table.
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

    // sharedStrings.xml structure: <sst><si><t>text</t></si> or, for rich text,
    // <si><r><t>part one</t></r><r><t>part two</t></r></si> — each <si> is one
    // logical string, so its runs concatenate with nothing between them, and
    // each <si> is separated from the next by a space (they're distinct cells).
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

    // Worksheet XML: each <c> (cell) may contain an inline <is><t>text</t></is>
    // or a numeric <v>. We only care about text here for search purposes.
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

