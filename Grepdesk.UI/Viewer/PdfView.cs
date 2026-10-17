using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// PDF pages one under the other, fitted to the window's width until the user
/// zooms. Every page gets a placeholder of its size up front, but only the
/// pages on screen (and their neighbours) are drawn and kept in memory.
/// </summary>
internal sealed class PdfView : DockPanel, IViewerBody
{
    private const double PointsToDips = 96.0 / 72.0;
    private const double PageGap = 12, SideMargin = 24;
    private const double MinZoom = 0.1, MaxZoom = 8, Step = 1.25;

    /// <summary>Pages further than this from the visible ones give their bitmap back.</summary>
    private const int KeepAround = 4;

    /// <summary>
    /// All kept bitmaps together stay under this many pixels (~256 MB). A
    /// maximized window on a large, high-DPI screen draws each page several
    /// times bigger, and keeping 4 neighbours each way at that size runs a
    /// small machine out of memory; then fewer neighbours are kept.
    /// </summary>
    private const double KeptPixelsBudget = 64_000_000;

    /// <summary>Caps one page's bitmap (~64 MB) at high zoom on a big page.</summary>
    private const double MaxPagePixels = 16_000_000;

    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly PdfRenderer _renderer;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _pages = new() { Spacing = PageGap, Margin = new Thickness(SideMargin, PageGap) };
    private readonly Image[] _images;
    private readonly double[] _renderedZoom;
    private readonly TextBlock _pageText;
    private readonly TextBlock _zoomText;

    private bool _fit = true;
    private double _zoom = 1;
    private double _notedZoom;
    private bool _disposed;

    /// <summary>Pages to draw, most wanted first, at this zoom and screen scaling; updated on every scroll.</summary>
    private List<int> _wanted = [];
    private double _wantedZoom, _wantedScaling = 1;
    private bool _rendering;

    public PdfView(PdfRenderer renderer)
    {
        _renderer = renderer;
        var count = renderer.PageSizes.Count;
        _images = new Image[count];
        _renderedZoom = new double[count];

        for (var i = 0; i < count; i++)
        {
            _images[i] = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(_images[i], BitmapInterpolationMode.HighQuality);
            _pages.Children.Add(new Border
            {
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 2 8 0 #66000000"),
                Child = _images[i],
            });
        }

        _scroll = new ScrollViewer
        {
            Content = _pages,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Focusable = true,
        };
        _scroll.ScrollChanged += (_, _) => OnScrolled();
        _scroll.SizeChanged += (_, e) => { if (_fit && e.WidthChanged) ApplyZoom(); };
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);

        _pageText = ViewerBar.Label();
        _zoomText = ViewerBar.Label("", 52);
        var zoomOut = ViewerBar.Button("−", Loc.Get("ViewerZoomOut"));
        var zoomIn = ViewerBar.Button("+", Loc.Get("ViewerZoomIn"));
        var fit = ViewerBar.Button(Loc.Get("ViewerZoomFitWidth"));
        zoomOut.Click += (_, _) => SetZoom(_zoom / Step);
        zoomIn.Click += (_, _) => SetZoom(_zoom * Step);
        fit.Click += (_, _) => { _fit = true; ApplyZoom(); };

        var info = renderer.TotalPages > count
            ? ViewerBar.Label(Loc.Get("ViewerPdfFirstPages", count, renderer.TotalPages))
            : _pageText;
        var zoom = ViewerBar.Row(zoomOut, _zoomText, zoomIn, fit);
        zoom.HorizontalAlignment = HorizontalAlignment.Right;
        var bar = new DockPanel { Children = { info, zoom } };
        SetDock(info, Dock.Left);

        Children.Add(ViewerBar.Create(bar));
        Children.Add(_scroll);
    }

    public void FocusContent() => _scroll.Focus();

    private void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        var sizes = _renderer.PageSizes;
        if (sizes.Count == 0) return;

        if (_fit)
        {
            var available = _scroll.Bounds.Width - 2 * SideMargin - 16; // room for the scroll bar
            var widest = sizes.Max(s => s.Width);
            if (available <= 0 || widest <= 0) return;
            _zoom = Math.Clamp(available / (widest * PointsToDips), MinZoom, MaxZoom);
        }

        // Keep the same spot of the document in view across the change.
        var oldHeight = _pages.Bounds.Height;
        var anchor = oldHeight > 0 ? _scroll.Offset.Y / oldHeight : 0;

        for (var i = 0; i < sizes.Count; i++)
        {
            var page = (Border)_pages.Children[i];
            page.Width = sizes[i].Width * PointsToDips * _zoom;
            page.Height = sizes[i].Height * PointsToDips * _zoom;
        }
        _zoomText.Text = $"{_zoom * 100:0}%";
        if (Math.Abs(_zoom - _notedZoom) > _notedZoom * 0.01)
        {
            _notedZoom = _zoom;
            SessionTrail.Note($"pdf: zoom {_zoom * 100:0}%{(_fit ? " (fit)" : "")}, view {_scroll.Bounds.Width:0}×{_scroll.Bounds.Height:0}");
        }

        Dispatcher.UIThread.Post(() =>
        {
            if (oldHeight > 0) _scroll.Offset = _scroll.Offset.WithY(anchor * _pages.Bounds.Height);
            OnScrolled();
        }, DispatcherPriority.Loaded);
    }

    private (int First, int Last) VisiblePages()
    {
        var top = _scroll.Offset.Y;
        var bottom = top + _scroll.Viewport.Height;
        int first = -1, last = -1;
        for (var i = 0; i < _images.Length; i++)
        {
            var bounds = _pages.Children[i].Bounds;
            var pageTop = bounds.Y + _pages.Margin.Top;
            if (pageTop + bounds.Height < top) continue;
            if (pageTop > bottom) break;
            if (first < 0) first = i;
            last = i;
        }
        return first < 0 ? (0, 0) : (first, last);
    }

    private void OnScrolled()
    {
        if (_disposed || _images.Length == 0) return;
        var (first, last) = VisiblePages();
        _pageText.Text = Loc.Get("ViewerPage", first + 1, _images.Length);
        var renderScaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        var around = PagesAround(first, last, renderScaling);

        // Drop what scrolled far away, then draw what's on screen first, its neighbours next.
        for (var i = 0; i < _images.Length; i++)
        {
            if (i >= first - around && i <= last + around) continue;
            Release(i);
        }

        var ahead = Math.Min(2, around);
        _wanted = Enumerable.Range(first, last - first + 1)
            .Concat(Enumerable.Range(last + 1, ahead)).Concat(Enumerable.Range(first - ahead, ahead).Reverse())
            .Where(i => i >= 0 && i < _images.Length)
            .ToList();
        _wantedZoom = _zoom;
        _wantedScaling = renderScaling;
        if (!_rendering) _ = RenderAsync();
    }

    /// <summary>How many pages each side of the visible ones keep their bitmap within <see cref="KeptPixelsBudget"/>.</summary>
    private int PagesAround(int first, int last, double renderScaling)
    {
        var (width, height) = _renderer.PageSizes[first];
        var scale = _zoom * PointsToDips * renderScaling;
        var pagePixels = Math.Clamp(width * height * scale * scale, 1, MaxPagePixels);
        var spare = KeptPixelsBudget / pagePixels - (last - first + 1);
        return (int)Math.Clamp(spare / 2, 0, KeepAround);
    }

    /// <summary>
    /// Draws the wanted pages one at a time, re-reading what is wanted after
    /// each: scrolling only changes the list, so a page is drawn once however
    /// many scroll events ask for it, and pages scrolled past are skipped.
    /// </summary>
    private async Task RenderAsync()
    {
        _rendering = true;
        try
        {
            while (!_disposed && NextWanted() is { } index)
            {
                var zoom = _wantedZoom;
                var (width, height) = _renderer.PageSizes[index];
                var scale = zoom * PointsToDips * _wantedScaling;
                scale = Math.Min(scale, Math.Sqrt(MaxPagePixels / Math.Max(1, width * height)));

                SessionTrail.Note($"pdf: page {index + 1} at {scale:0.##} px/pt, {width * scale:0}×{height * scale:0} px");
                Bitmap? bitmap;
                try
                {
                    bitmap = await Task.Run(() => ToBitmap(_renderer.Render(index, scale)));
                }
                catch (Exception e)
                {
                    // A page PDFium can't draw (or that doesn't fit in memory) stays blank
                    // until the zoom changes, instead of being retried on every scroll.
                    ErrorLog.Write(e, $"pdf render, page {index + 1} at {scale:0.##} px/pt");
                    SessionTrail.Note($"pdf: page {index + 1} failed: {e.GetType().Name}");
                    _renderedZoom[index] = zoom;
                    if (e is OutOfMemoryException) return;
                    continue;
                }

                // Zoomed or scrolled away meanwhile: drawn for nothing.
                if (_disposed || zoom != _wantedZoom || !_wanted.Contains(index))
                {
                    bitmap?.Dispose();
                    continue;
                }
                Release(index);
                _images[index].Source = bitmap;
                _renderedZoom[index] = zoom;
            }
        }
        finally
        {
            _rendering = false;
        }
    }

    private int? NextWanted()
    {
        foreach (var i in _wanted)
            if (Math.Abs(_renderedZoom[i] - _wantedZoom) > 1e-9) return i;
        return null;
    }

    private static Bitmap? ToBitmap(PdfPageImage? page)
    {
        if (page is null) return null;
        var handle = GCHandle.Alloc(page.Pixels, GCHandleType.Pinned);
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Unpremul, handle.AddrOfPinnedObject(),
                new PixelSize(page.Width, page.Height), new Vector(96, 96), page.Width * 4);
        }
        finally
        {
            handle.Free();
        }
    }

    private void Release(int index)
    {
        if (_images[index].Source is not Bitmap old) return;
        _images[index].Source = null;
        _renderedZoom[index] = 0;
        old.Dispose();
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * Step : _zoom / Step);
        e.Handled = true;
    }

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: SetZoom(_zoom * Step); return true;
            case Key.OemMinus or Key.Subtract: SetZoom(_zoom / Step); return true;
            case Key.D0 or Key.NumPad0: _fit = true; ApplyZoom(); return true;
            case Key.Home: _scroll.ScrollToHome(); return true;
            case Key.End: _scroll.ScrollToEnd(); return true;
            default: return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        for (var i = 0; i < _images.Length; i++) Release(i);
        _renderer.Dispose();
    }
}
