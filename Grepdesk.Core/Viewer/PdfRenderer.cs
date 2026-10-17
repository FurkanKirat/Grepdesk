using System;
using System.Collections.Generic;
using System.Threading;
using Docnet.Core;
using Docnet.Core.Models;
using Docnet.Core.Readers;

namespace Grepdesk.Core.Viewer;

/// <summary>One rendered page: BGRA pixels, not premultiplied, transparent where the page is blank.</summary>
public sealed record PdfPageImage(byte[] Pixels, int Width, int Height);

/// <summary>
/// Draws PDF pages with PDFium (through Docnet). PDFium isn't thread-safe, so
/// every call takes one process-wide lock; callers run it off the UI thread.
/// </summary>
public sealed class PdfRenderer : IDisposable
{
    /// <summary>Beyond this, only the first pages are listed: each page costs a placeholder control.</summary>
    public const int MaxPages = 3000;

    private static readonly object Gate = new();

    private readonly string _path;
    private IDocReader? _reader;
    private double _readerScale;
    private bool _disposed;

    /// <summary>Page sizes in points (1/72 inch).</summary>
    public IReadOnlyList<(double Width, double Height)> PageSizes { get; }

    public int TotalPages { get; }

    private PdfRenderer(string path, IReadOnlyList<(double, double)> sizes, int totalPages, IDocReader reader)
    {
        _path = path;
        PageSizes = sizes;
        TotalPages = totalPages;
        _reader = reader;
        _readerScale = 1;
    }

    /// <summary>Opens the file and measures its pages. Throws for files PDFium can't read (corrupt, encrypted).</summary>
    public static PdfRenderer Open(string path, CancellationToken ct)
    {
        lock (Gate)
        {
            var reader = DocLib.Instance.GetDocReader(path, new PageDimensions(1.0));
            try
            {
                var total = reader.GetPageCount();
                var sizes = new List<(double, double)>(Math.Min(total, MaxPages));
                for (var i = 0; i < total && i < MaxPages; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    using var page = reader.GetPageReader(i);
                    sizes.Add((page.GetPageWidth(), page.GetPageHeight()));
                }
                return new PdfRenderer(path, sizes, total, reader);
            }
            catch
            {
                reader.Dispose();
                throw;
            }
        }
    }

    /// <param name="scale">Pixels per point: 1 is 72 dpi.</param>
    public PdfPageImage? Render(int index, double scale)
    {
        lock (Gate)
        {
            if (_disposed) return null;
            if (_reader is null || Math.Abs(_readerScale - scale) > 1e-6)
            {
                // Docnet fixes the scale per document reader.
                _reader?.Dispose();
                _reader = DocLib.Instance.GetDocReader(_path, new PageDimensions(scale));
                _readerScale = scale;
            }

            using var page = _reader.GetPageReader(index);
            var width = page.GetPageWidth();
            var height = page.GetPageHeight();
            if (width <= 0 || height <= 0) return null;
            return new PdfPageImage(page.GetImage(RenderFlags.RenderAnnotations), width, height);
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            _disposed = true;
            _reader?.Dispose();
            _reader = null;
        }
    }
}
