using System;
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
/// .docx is a zip archive. The main body lives in word/document.xml, but
/// headers, footers, and footnotes/endnotes are each stored in their own
/// separate part — a document with a search term only in a footer or footnote
/// would otherwise silently not match. This reads all of them.
///
/// Text is pulled out by actually parsing the XML and reading only &lt;w:t&gt;
/// text nodes (plus w:tab/w:br as whitespace), rather than a "strip all tags"
/// regex. That distinction matters: Word frequently splits a single word
/// across multiple &lt;w:r&gt; runs — not just for partial bold/italic, but
/// also for spellcheck, language tags, or revision marks with no visible
/// formatting change at all. A naive tag-strip-with-space approach would
/// turn "Merhaba" split as "Merha"+"ba" into "Merha ba", silently breaking
/// the search. Reading only w:t content concatenates those runs back
/// together correctly.
/// </summary>
public class DocxExtractor : IContentExtractor
{
    public IEnumerable<string> SupportedExtensions => [".docx"];

    // Parts worth searching, beyond the main body. Word numbers header/footer
    // files (header1.xml, header2.xml, ...) depending on how many sections the
    // document has, so those are matched by prefix; the rest are exact names.
    private static readonly string[] ExactParts =
    [
        "word/document.xml",
        "word/footnotes.xml",
        "word/endnotes.xml",
    ];

    private static readonly string[] PrefixParts = ["word/header", "word/footer"];

    public Task<string?> ExtractTextAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            using var archive = ZipFile.OpenRead(path);
            var sb = new StringBuilder();

            var relevantEntries = archive.Entries.Where(e =>
                ExactParts.Contains(e.FullName, StringComparer.OrdinalIgnoreCase) ||
                (PrefixParts.Any(p => e.FullName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) &&
                 e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase)));

            var any = false;
            foreach (var entry in relevantEntries)
            {
                ct.ThrowIfCancellationRequested();

                var text = TryExtractPartText(entry);
                if (text is null) continue;

                sb.Append(text).Append('\n');
                any = true;
            }

            return any ? sb.ToString() : null;
        }
        catch
        {
            // Corrupt zip, password-protected, locked file, etc. — just skip it.
            return null;
        }
    }, ct);

    private static string? TryExtractPartText(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            var doc = XDocument.Load(stream);

            var sb = new StringBuilder();

            // Each <w:p> (paragraph) — including ones inside table cells,
            // which are just paragraphs nested in w:tc — becomes one line.
            // Joining paragraphs with a real newline keeps text from
            // different cells/paragraphs from fusing into one word, while
            // text WITHIN a paragraph (across run boundaries) is
            // concatenated with nothing in between, exactly as Word displays it.
            foreach (var paragraph in doc.Descendants().Where(e => e.Name.LocalName == "p"))
            {
                foreach (var node in paragraph.Descendants())
                {
                    switch (node.Name.LocalName)
                    {
                        case "t": sb.Append(node.Value); break;
                        case "tab": sb.Append('\t'); break;
                        case "br":
                        case "cr": sb.Append('\n'); break;
                    }
                }
                sb.Append('\n');
            }

            return sb.Length > 0 ? sb.ToString() : null;
        }
        catch
        {
            // Malformed part — skip just this one, other parts may still be fine.
            return null;
        }
    }
}


