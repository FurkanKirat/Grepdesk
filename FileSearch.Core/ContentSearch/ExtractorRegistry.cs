using System;
using System.Collections.Generic;

namespace FileSearch.Core.ContentSearch;

/// <summary>
/// Central place that knows which extractor handles which file extension.
/// To support a new format: write a class implementing IContentExtractor,
/// then add one line here. Nothing else in the app needs to change.
/// </summary>
public static class ExtractorRegistry
{
    private static readonly IContentExtractor[] Extractors =
    [
        new PlainTextExtractor(),
        new DocxExtractor(),
        new XlsxExtractor(),
        new PdfExtractor(),
    ];

    private static readonly Dictionary<string, IContentExtractor> ByExtension = BuildLookup();

    private static Dictionary<string, IContentExtractor> BuildLookup()
    {
        var map = new Dictionary<string, IContentExtractor>(StringComparer.OrdinalIgnoreCase);
        foreach (var extractor in Extractors)
            foreach (var ext in extractor.SupportedExtensions)
                map[ext] = extractor;
        return map;
    }

    public static bool TryGetExtractor(string extension, out IContentExtractor extractor) =>
        ByExtension.TryGetValue(extension, out extractor!);

    public static IEnumerable<string> AllSupportedExtensions => ByExtension.Keys;
}
