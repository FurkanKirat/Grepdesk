using System.Collections.Generic;
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

    public Task<string?> ExtractTextAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        try
        {
            var doc = PptxDocument.Load(path, ct);
            var text = doc.ToPlainText();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }
        catch
        {
            return null;
        }
    }, ct);
}
