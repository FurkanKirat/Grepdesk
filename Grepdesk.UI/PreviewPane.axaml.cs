using System.Text;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI;

/// <summary>
/// Side panel showing the selected result: metadata plus a look inside —
/// a thumbnail for images, the first lines for text and code, extracted
/// text for documents, and the first entries for folders.
/// </summary>
public partial class PreviewPane : UserControl
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly HashSet<string> ImageExtensions =
        new([".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp"], StringComparer.OrdinalIgnoreCase);

    private const int TextPreviewBytes = 32 * 1024;
    private const int MaxExtractedChars = 6000;
    private const long MaxDocumentBytes = 20 * 1024 * 1024;
    private const long MaxImageBytes = 50 * 1024 * 1024;
    private const int FolderListLimit = 200;
    private const int FolderCountLimit = 10_000;

    private CancellationTokenSource _cts = new();
    private ResultItem? _item;
    private Bitmap? _bitmap;

    /// <summary>Raised with the full path when the user clicks Open / Show in folder.</summary>
    public event Action<string>? OpenRequested;
    public event Action<string>? ShowInFolderRequested;

    public PreviewPane()
    {
        InitializeComponent();

        OpenButton.Click += (_, _) => { if (_item is not null) OpenRequested?.Invoke(_item.FullPath); };
        ShowInFolderButton.Click += (_, _) => { if (_item is not null) ShowInFolderRequested?.Invoke(_item.FullPath); };

        ApplyLanguage();
    }

    public void ApplyLanguage()
    {
        EmptyText.Text = Loc.Get("PreviewEmpty");
        TypeLabel.Text = Loc.Get("PreviewType");
        ModifiedLabel.Text = Loc.Get("PreviewModified");
        OpenButton.Content = Loc.Get("ContextMenuOpen");
        ShowInFolderButton.Content = Loc.Get("ContextMenuShowInFolder");
        Show(_item);
    }

    public async void Show(ResultItem? item)
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        _item = item;

        SetBitmap(null);
        TextScroll.IsVisible = false;
        TextPreview.Text = "";

        EmptyText.IsVisible = item is null;
        Details.IsVisible = item is not null;
        if (item is null) return;

        var r = item.Result;
        NameText.Text = item.FileName;
        PathText.Text = item.Directory;

        FolderIcon.IsVisible = item.ShowFolderIcon;
        DocumentIcon.IsVisible = item.ShowDocumentIcon;
        Badge.IsVisible = item.ShowBadge;
        BadgeText.Text = item.Kind.Label;
        BadgeText.Foreground = Badge.BorderBrush = item.Kind.Foreground;
        Badge.Background = item.Kind.Background;

        TypeValue.Text = r.IsDirectory
            ? Loc.Get("PreviewFolder")
            : item.Kind.Label.Length > 0 ? Loc.Get("PreviewFileType", item.Kind.Label) : Loc.Get("PreviewFile");
        SizeLabel.Text = Loc.Get(r.IsDirectory ? "PreviewContains" : "PreviewSize");
        SizeValue.Text = r.IsDirectory ? "…" : $"{Format.Size(r.Size)} ({r.Size:N0} B)";
        ModifiedValue.Text = item.ModifiedText;

        BodyMessage.IsVisible = true;
        BodyMessage.Text = Loc.Get("PreviewLoading");

        PreviewContent content;
        try
        {
            content = await Task.Run(() => LoadAsync(r, token), token);
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch
        {
            content = new PreviewContent(Message: Loc.Get("PreviewFailed"));
        }

        if (token.IsCancellationRequested)
        {
            content.Image?.Dispose();
            return;
        }

        if (content.FolderCount is { } count)
            SizeValue.Text = count >= FolderCountLimit
                ? Loc.Get("PreviewItemsMore", FolderCountLimit)
                : Loc.Get("PreviewItems", count);

        if (content.Image is not null)
        {
            SetBitmap(content.Image);
            BodyMessage.IsVisible = false;
        }
        else if (content.Text is not null)
        {
            TextPreview.Text = content.Footer is not null
                ? content.Text + "\n\n" + content.Footer
                : content.Text;
            TextScroll.IsVisible = true;
            TextScroll.Offset = default;
            BodyMessage.IsVisible = false;
        }
        else
        {
            BodyMessage.Text = content.Message ?? Loc.Get("PreviewUnavailable");
        }
    }

    private void SetBitmap(Bitmap? bitmap)
    {
        ImagePreview.Source = bitmap;
        ImagePreview.IsVisible = bitmap is not null;
        _bitmap?.Dispose();
        _bitmap = bitmap;
    }

    private sealed record PreviewContent(
        Bitmap? Image = null, string? Text = null, string? Footer = null,
        string? Message = null, int? FolderCount = null);

    private static async Task<PreviewContent> LoadAsync(SearchResult r, CancellationToken ct)
    {
        if (r.IsDirectory)
            return ListFolder(r.FullPath, ct);

        var ext = Path.GetExtension(r.FullPath);

        if (ImageExtensions.Contains(ext))
        {
            if (r.Size > MaxImageBytes) return new(Message: Loc.Get("PreviewTooLarge"));
            await using var stream = File.OpenRead(r.FullPath);
            // Large photos are decoded at preview size, not full resolution.
            var bitmap = r.Size > 2 * 1024 * 1024
                ? Bitmap.DecodeToWidth(stream, 800, BitmapInterpolationMode.MediumQuality)
                : new Bitmap(stream);
            return new(Image: bitmap);
        }

        // Office documents and PDFs: reuse the content-search extractors.
        if (ExtractorRegistry.TryGetExtractor(ext, out var extractor) && extractor is not PlainTextExtractor)
        {
            if (r.Size > MaxDocumentBytes) return new(Message: Loc.Get("PreviewTooLarge"));
            var text = await extractor.ExtractTextAsync(r.FullPath, ct);
            if (string.IsNullOrWhiteSpace(text)) return new(Message: Loc.Get("PreviewFailed"));
            return text.Length > MaxExtractedChars
                ? new(Text: text[..MaxExtractedChars], Footer: Loc.Get("PreviewTruncated"))
                : new(Text: text);
        }

        return ReadTextHead(r.FullPath, r.Size);
    }

    /// <summary>First bytes of the file as text, or "no preview" when it looks binary.</summary>
    private static PreviewContent ReadTextHead(string path, long size)
    {
        var buffer = new byte[(int)Math.Min(size, TextPreviewBytes)];
        using (var stream = File.OpenRead(path))
            buffer = buffer[..stream.ReadAtLeast(buffer, buffer.Length, throwOnEndOfStream: false)];

        string text;
        if (buffer.Length >= 2 && buffer[0] == 0xFF && buffer[1] == 0xFE)
            text = Encoding.Unicode.GetString(buffer, 2, (buffer.Length - 2) & ~1);
        else if (buffer.Length >= 2 && buffer[0] == 0xFE && buffer[1] == 0xFF)
            text = Encoding.BigEndianUnicode.GetString(buffer, 2, (buffer.Length - 2) & ~1);
        else if (Array.IndexOf(buffer, (byte)0) >= 0)
            return new();  // binary: executables, media, databases...
        else
            text = new UTF8Encoding(false).GetString(buffer).TrimStart('﻿');

        return new(Text: text, Footer: size > buffer.Length ? Loc.Get("PreviewTruncated") : null);
    }

    private static PreviewContent ListFolder(string path, CancellationToken ct)
    {
        var dirs = new List<string>();
        var files = new List<string>();
        var count = 0;

        foreach (var info in new DirectoryInfo(path).EnumerateFileSystemInfos())
        {
            ct.ThrowIfCancellationRequested();
            if (++count >= FolderCountLimit) break;
            if (dirs.Count + files.Count >= FolderListLimit) continue;

            if (info is DirectoryInfo) dirs.Add(info.Name + Path.DirectorySeparatorChar);
            else files.Add(info.Name);
        }

        if (count == 0) return new(Message: Loc.Get("PreviewFolderEmpty"), FolderCount: 0);

        dirs.Sort(StringComparer.CurrentCultureIgnoreCase);
        files.Sort(StringComparer.CurrentCultureIgnoreCase);
        return new(Text: string.Join('\n', dirs.Concat(files)), Footer: count > FolderListLimit ? Loc.Get("PreviewFolderMore") : null, FolderCount: count);
    }
}
