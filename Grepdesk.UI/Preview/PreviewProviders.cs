using System.Text;
using Avalonia.Media.Imaging;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using Grepdesk.Core.Preview;

namespace Grepdesk.UI.Preview;

// The concrete previewers. Each one is self-contained: it decides what it can
// show and how much of the file it reads.

internal sealed class FolderPreviewProvider : IPreviewProvider
{
    private const int ListLimit = 200;
    private const int CountLimit = 10_000;

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) => item.IsDirectory;

    public Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        var count = 0;

        foreach (var info in new DirectoryInfo(item.FullPath).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (++count >= CountLimit) break;
            if (dirs.Count + files.Count >= ListLimit) continue;

            if (info is DirectoryInfo) dirs.Add(info.Name + Path.DirectorySeparatorChar);
            else files.Add(info.Name);
        }

        var countText = count >= CountLimit ? Loc.Get("PreviewItemsMore", CountLimit) : Loc.Get("PreviewItems", count);
        var details = new[] { (Loc.Get("PreviewContains"), countText) };

        if (count == 0)
            return Task.FromResult(new PreviewContent { Message = Loc.Get("PreviewFolderEmpty"), Details = details });

        dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return Task.FromResult(new PreviewContent
        {
            Text = string.Join('\n', dirs.Concat(files)),
            Monospace = true,
            Footer = count > ListLimit ? Loc.Get("PreviewFolderMore") : null,
            Details = details
        });
    }
}

internal sealed class ImagePreviewProvider : IPreviewProvider
{
    private const long MaxBytes = 50 * 1024 * 1024;
    private const int PreviewWidth = 800;
    private const long DecodeSmallerAbove = 2 * 1024 * 1024;

    private static readonly HashSet<string> Extensions =
        new([".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) => Extensions.Contains(Path.GetExtension(item.FullPath));

    public async Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        if (item.Size > MaxBytes) return PreviewContent.FromMessage(Loc.Get("PreviewTooLarge"));

        await using var stream = File.OpenRead(item.FullPath);

        // Big photos are decoded straight to preview width: a 24 MP image at
        // full size would take ~100 MB of memory for a 320 px wide panel.
        if (item.Size > DecodeSmallerAbove)
            return new PreviewContent { Image = Bitmap.DecodeToWidth(stream, PreviewWidth, BitmapInterpolationMode.MediumQuality) };

        var bitmap = new Bitmap(stream);
        return new PreviewContent
        {
            Image = bitmap,
            Details = [(Loc.Get("PreviewDimensions"), $"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}")]
        };
    }
}

/// <summary>PDFs get layout-aware text (spaces and line breaks rebuilt from glyph positions).</summary>
internal sealed class PdfPreviewProvider : IPreviewProvider
{
    private const int MaxPages = 5;
    private const int MaxChars = 12_000;

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) =>
        string.Equals(Path.GetExtension(item.FullPath), ".pdf", StringComparison.OrdinalIgnoreCase);

    public Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        var result = PdfPreviewText.Extract(item.FullPath, MaxPages, MaxChars, ct);
        if (result is null)
            return Task.FromResult(PreviewContent.FromMessage(Loc.Get("PreviewPdfNoText")));

        return Task.FromResult(new PreviewContent
        {
            Text = result.Text,
            Footer = result.PagesRead < result.PageCount ? Loc.Get("PreviewPdfPages", result.PagesRead, result.PageCount) : null,
            Details = [(Loc.Get("PreviewPages"), result.PageCount.ToString("N0"))]
        });
    }
}

/// <summary>Office documents: reuses the content-search extractors.</summary>
internal sealed class DocumentPreviewProvider : IPreviewProvider
{
    private const long MaxBytes = 20 * 1024 * 1024;
    private const int MaxChars = 6000;

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) =>
        ExtractorRegistry.TryGetExtractor(Path.GetExtension(item.FullPath), out var extractor)
        && extractor is not PlainTextExtractor and not PdfExtractor;

    public async Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        if (item.Size > MaxBytes) return PreviewContent.FromMessage(Loc.Get("PreviewTooLarge"));

        ExtractorRegistry.TryGetExtractor(Path.GetExtension(item.FullPath), out var extractor);
        var text = await extractor.ExtractTextAsync(item.FullPath, ct);
        if (string.IsNullOrWhiteSpace(text)) return PreviewContent.FromMessage(Loc.Get("PreviewFailed"));

        return text.Length > MaxChars
            ? new PreviewContent { Text = text[..MaxChars], Footer = Loc.Get("PreviewTruncated") }
            : new PreviewContent { Text = text };
    }
}

/// <summary>Fallback for anything else: show the start of the file if it looks like text.</summary>
internal sealed class TextPreviewProvider : IPreviewProvider
{
    private const int MaxBytes = 32 * 1024;

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) => !item.IsDirectory;

    public Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        var buffer = new byte[(int)Math.Min(item.Size, MaxBytes)];
        using (var stream = File.OpenRead(item.FullPath))
            buffer = buffer[..stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)];

        string text;
        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            text = Encoding.Unicode.GetString(buffer, 2, (buffer.Length - 2) & ~1);
        else if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(buffer, 2, (buffer.Length - 2) & ~1);
        else if (Array.IndexOf(buffer, (byte)0) >= 0)
            return Task.FromResult(new PreviewContent()); // binary: executables, media, databases...
        else
            text = new UTF8Encoding(false).GetString(buffer).TrimStart('﻿');

        return Task.FromResult(new PreviewContent
        {
            Text = text,
            Monospace = true,
            Footer = item.Size > buffer.Length ? Loc.Get("PreviewTruncated") : null
        });
    }
}
