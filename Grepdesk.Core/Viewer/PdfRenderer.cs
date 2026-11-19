using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
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
    private GCHandle? _formInfoHandle;
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
                DisposeReader();
                _reader = DocLib.Instance.GetDocReader(_path, new PageDimensions(scale));
                _readerScale = scale;
            }

            // Pin FPDF_FORMFILLINFO before RenderAnnotations initializes PDFium's form environment,
            // so GC compaction cannot move it while PDFium holds the raw pointer until Dispose.
            _formInfoHandle ??= FormInfoPinning.Pin(_reader);

            using var page = _reader.GetPageReader(index);
            var width = page.GetPageWidth();
            var height = page.GetPageHeight();
            if (width <= 0 || height <= 0) return null;
            return new PdfPageImage(page.GetImage(RenderFlags.RenderAnnotations), width, height);
        }
    }

    /// <summary>The page's characters and their boxes in points, for text selection; null once disposed.</summary>
    public PdfPageText? GetText(int index)
    {
        lock (Gate)
        {
            if (_disposed || _reader is null) return null;
            var (pointsWidth, pointsHeight) = PageSizes[index];

            using var page = _reader.GetPageReader(index);
            // Docnet reports boxes in pixels of a page drawn at the reader's scale.
            double pixelWidth = page.GetPageWidth(), pixelHeight = page.GetPageHeight();
            if (pixelWidth <= 0 || pixelHeight <= 0) return PdfPageText.Empty;
            var sx = pointsWidth / pixelWidth;
            var sy = pointsHeight / pixelHeight;

            var text = new System.Text.StringBuilder();
            var boxes = new List<PdfCharBox>();
            foreach (var c in page.GetCharacters())
            {
                var b = c.Box;
                text.Append(c.Char);
                boxes.Add(new PdfCharBox(
                    Math.Min(b.Left, b.Right) * sx, Math.Min(b.Top, b.Bottom) * sy,
                    Math.Max(b.Left, b.Right) * sx, Math.Max(b.Top, b.Bottom) * sy));
            }
            return new PdfPageText(text.ToString(), boxes.ToArray());
        }
    }

    public void Dispose()
    {
        lock (Gate)
        {
            _disposed = true;
            DisposeReader();
        }
    }

    private void DisposeReader()
    {
        try
        {
            _reader?.Dispose();
        }
        finally
        {
            _reader = null;
            if (_formInfoHandle is { IsAllocated: true } handle)
            {
                handle.Free();
                _formInfoHandle = null;
            }
        }
    }

    /// <summary>
    /// Docnet 2.6.0 passes an unpinned managed FPDF_FORMFILLINFO instance to
    /// FPDFDOCInitFormFillEnvironment; PDFium retains that pointer until
    /// FPDF_ExitFormFillEnvironment during DocumentWrapper.Dispose. Pre-allocating
    /// and pinning the struct prevents GC compaction from moving it out from under PDFium.
    /// </summary>
    private static class FormInfoPinning
    {
        private static readonly Assembly DocnetAsm = typeof(DocLib).Assembly;
        private static readonly FieldInfo? DocWrapperField =
            DocnetAsm.GetType("Docnet.Core.Readers.DocReader")
                ?.GetField("_docWrapper", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type? DocWrapperType =
            DocnetAsm.GetType("Docnet.Core.Bindings.DocumentWrapper");
        private static readonly PropertyInfo? InstanceProp =
            DocWrapperType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? FormInfoField =
            DocWrapperType?.GetField("_formInfo", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly FieldInfo? FormHandleField =
            DocWrapperType?.GetField("_formHandle", BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly Type? FormFillInfoType =
            DocnetAsm.GetType("Docnet.Core.Bindings.FPDF_FORMFILLINFO");
        private static readonly FieldInfo? VersionField =
            FormFillInfoType?.GetField("version", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        private static readonly MethodInfo? InitFormFillMethod =
            DocnetAsm.GetType("Docnet.Core.Bindings.fpdf_view")
                ?.GetMethod("FPDFDOCInitFormFillEnvironment", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);

        public static GCHandle? Pin(IDocReader reader)
        {
            if (DocWrapperField is null || InstanceProp is null || FormInfoField is null ||
                FormHandleField is null || FormFillInfoType is null || VersionField is null ||
                InitFormFillMethod is null)
                return null;

            var docWrapper = DocWrapperField.GetValue(reader);
            if (docWrapper is null || FormHandleField.GetValue(docWrapper) is not null)
                return null;

            var docInstance = InstanceProp.GetValue(docWrapper);
            if (docInstance is null) return null;

            var formInfo = Activator.CreateInstance(FormFillInfoType);
            if (formInfo is null) return null;

            var handle = GCHandle.Alloc(formInfo, GCHandleType.Pinned);
            try
            {
                object? formHandle = null;
                for (var version = 1; version <= 2; version++)
                {
                    VersionField.SetValue(formInfo, version);
                    formHandle = InitFormFillMethod.Invoke(null, [docInstance, formInfo]);
                    if (formHandle is not null) break;
                }
                if (formHandle is null)
                {
                    handle.Free();
                    return null;
                }
                FormInfoField.SetValue(docWrapper, formInfo);
                FormHandleField.SetValue(docWrapper, formHandle);
                return handle;
            }
            catch
            {
                handle.Free();
                throw;
            }
        }
    }
}
