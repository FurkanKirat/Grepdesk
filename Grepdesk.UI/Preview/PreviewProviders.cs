using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using Grepdesk.Core.Preview;
using Grepdesk.UI.Helpers;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

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

/// <summary>Markdown is shown rendered: headings, lists, tables, code, links and local images.</summary>
internal sealed class MarkdownPreviewProvider : IPreviewProvider
{
    private const int MaxChars = 128 * 1024;
    private const int MaxImages = 12;
    private const long MaxImageBytes = 20 * 1024 * 1024;
    private const int ImageWidth = 600;

    private static readonly HashSet<string> Extensions =
        new([".md", ".markdown", ".mdown", ".mkd"], StringComparer.OrdinalIgnoreCase);

    private static readonly HashSet<string> ImageExtensions =
        new([".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    public bool CanPreview(SearchResult item) => Extensions.Contains(Path.GetExtension(item.FullPath));

    public async Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        string text;
        var truncated = false;
        using (var reader = new StreamReader(item.FullPath, detectEncodingFromByteOrderMarks: true))
        {
            var buffer = new char[MaxChars];
            var read = await reader.ReadBlockAsync(buffer, ct);
            text = new string(buffer, 0, read);
            if (reader.Peek() >= 0)
            {
                // Cut at a line end, so a half-read table or list item isn't rendered.
                var lastLine = text.LastIndexOf('\n');
                if (lastLine > 0) text = text[..lastLine];
                truncated = true;
            }
        }

        return new PreviewContent
        {
            Markdown = Prepare(text, Path.GetDirectoryName(item.FullPath) ?? "", ct),
            Footer = truncated ? LocalizationService.Instance.Get("PreviewTruncated") : null
        };
    }

    /// <summary>Parses and decodes the local images, off the UI thread. Also used by the viewer window.</summary>
    public static MarkdownPreview Prepare(string text, string baseDirectory, CancellationToken ct)
    {
        var document = MarkdownView.Parse(text);
        return new MarkdownPreview(document, baseDirectory, LoadImages(document, baseDirectory, ct));
    }

    /// <summary>Images next to the file (README screenshots); web images show their alt text instead.</summary>
    private static Dictionary<string, Bitmap> LoadImages(MarkdownDocument document, string baseDirectory, CancellationToken ct)
    {
        var images = new Dictionary<string, Bitmap>();
        foreach (var link in document.Descendants<LinkInline>())
        {
            if (images.Count >= MaxImages) break;
            ct.ThrowIfCancellationRequested();

            if (!link.IsImage || link.Url is not { Length: > 0 } url || images.ContainsKey(url)) continue;
            var path = MarkdownView.ResolveLocalPath(url, baseDirectory);
            if (path is null || !ImageExtensions.Contains(Path.GetExtension(path))) continue;

            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > MaxImageBytes) continue;
                using var stream = info.OpenRead();
                images[url] = Bitmap.DecodeToWidth(stream, ImageWidth, BitmapInterpolationMode.MediumQuality);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
            {
                // Unreadable or not really an image: the alt text is shown.
            }
        }
        return images;
    }
}

/// <summary>
/// Video, audio, presentations and image formats we can't decode ourselves:
/// the file manager's thumbnail (a video frame, album art, the first slide)
/// plus what it knows about the file (duration, resolution, artist...).
/// </summary>
internal sealed class ShellPreviewProvider : IPreviewProvider
{
    private const int ThumbnailSize = 512;

    private static readonly IShellPreview? Shell = PlatformShellFactory.CreateShellPreview();

    private static readonly HashSet<string> Extensions = new(
    [
        // video
        ".mp4", ".m4v", ".mov", ".mkv", ".avi", ".wmv", ".webm", ".flv", ".mpg", ".mpeg", ".m2ts", ".mts", ".3gp",
        // audio
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".wma", ".aiff",
        // presentations
        ".pptx", ".ppt", ".ppsx", ".pps", ".odp",
        // images Avalonia can't decode, when a codec is installed
        ".heic", ".heif", ".avif", ".jxl", ".tif", ".tiff", ".ico", ".psd", ".svg",
        ".dng", ".cr2", ".cr3", ".nef", ".arw", ".orf", ".rw2",
    ], StringComparer.OrdinalIgnoreCase);

    private static LocalizationService Loc => LocalizationService.Instance;

    public bool CanPreview(SearchResult item) => Shell is not null && Extensions.Contains(Path.GetExtension(item.FullPath));

    public Task<PreviewContent> LoadAsync(SearchResult item, CancellationToken ct)
    {
        var info = Shell!.GetMediaInfo(item.FullPath);
        ct.ThrowIfCancellationRequested();
        var thumbnail = Shell.GetThumbnail(item.FullPath, ThumbnailSize);
        ct.ThrowIfCancellationRequested();

        var details = Details(info);
        return Task.FromResult(thumbnail is null
            ? new PreviewContent { Message = Loc.Get("PreviewUnavailable"), Details = details }
            : new PreviewContent { Image = ToBitmap(thumbnail), Details = details });
    }

    private static List<(string, string)> Details(ShellMediaInfo info)
    {
        var rows = new List<(string, string)>();
        if (info.Title is not null) rows.Add((Loc.Get("PreviewTitle"), info.Title));
        if (info.Artist is not null) rows.Add((Loc.Get("PreviewArtist"), info.Artist));
        if (info.Album is not null) rows.Add((Loc.Get("PreviewAlbum"), info.Album));
        if (info.Duration is { } d)
            rows.Add((Loc.Get("PreviewDuration"), d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"m\:ss")));
        if (info is { Width: { } w, Height: { } h }) rows.Add((Loc.Get("PreviewDimensions"), $"{w} × {h}"));
        if (info.FrameRate is { } fps)
            rows.Add((Loc.Get("PreviewFrameRate"), $"{fps.ToString("0.##", CultureInfo.CurrentCulture)} fps"));
        if (info.Bitrate is { } bps)
            rows.Add((Loc.Get("PreviewBitrate"), bps >= 1_000_000
                ? $"{(bps / 1_000_000.0).ToString("0.#", CultureInfo.CurrentCulture)} Mbps"
                : $"{bps / 1000} kbps"));
        if (info.Slides is { } slides) rows.Add((Loc.Get("PreviewSlides"), slides.ToString("N0")));
        return rows;
    }

    private static Bitmap ToBitmap(ShellThumbnail thumbnail)
    {
        var handle = GCHandle.Alloc(thumbnail.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, thumbnail.HasAlpha ? AlphaFormat.Premul : AlphaFormat.Opaque,
                handle.AddrOfPinnedObject(), new PixelSize(thumbnail.Width, thumbnail.Height), new Vector(96, 96),
                thumbnail.Width * 4);
        }
        finally
        {
            handle.Free();
        }
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
