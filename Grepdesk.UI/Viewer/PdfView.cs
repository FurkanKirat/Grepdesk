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

    private static readonly Cursor TextCursor = new(StandardCursorType.Ibeam);

    /// <summary>Each page's characters, loaded with its bitmap and dropped with it once far away.</summary>
    private readonly PdfPageText?[] _texts;
    private readonly PdfSelectionLayer[] _layers;

    /// <summary>Where the selection started and where it reaches now, as (page, caret position).</summary>
    private (int Page, int Caret) _anchor, _focus;
    private bool _dragging;

    private bool HasSelection => _anchor != _focus;

    public PdfView(PdfRenderer renderer)
    {
        _renderer = renderer;
        var count = renderer.PageSizes.Count;
        _images = new Image[count];
        _renderedZoom = new double[count];
        _texts = new PdfPageText?[count];
        _layers = new PdfSelectionLayer[count];

        for (var i = 0; i < count; i++)
        {
            _images[i] = new Image { Stretch = Stretch.Fill };
            RenderOptions.SetBitmapInterpolationMode(_images[i], BitmapInterpolationMode.HighQuality);
            _layers[i] = new PdfSelectionLayer(renderer.PageSizes[i].Width, renderer.PageSizes[i].Height);
            _pages.Children.Add(new Border
            {
                Background = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                BoxShadow = BoxShadows.Parse("0 2 8 0 #66000000"),
                Child = new Panel { Children = { _images[i], _layers[i] } },
            });
        }

        // Transparent, so a drag can also start in the margins and the gaps between pages.
        _pages.Background = Brushes.Transparent;
        _pages.PointerPressed += OnPagesPressed;
        _pages.PointerMoved += OnPagesMoved;
        _pages.PointerReleased += (_, _) => _dragging = false;
        _pages.PointerCaptureLost += (_, _) => _dragging = false;
        _pages.ContextMenu = BuildContextMenu();
        AddHandler(KeyDownEvent, OnSelectionKey);

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
            if (_texts[i] is null) continue;
            _texts[i] = null;
            _layers[i].Text = null;
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
                PdfPageText? text;
                var needText = _texts[index] is null;
                try
                {
                    (bitmap, text) = await Task.Run(() =>
                        (ToBitmap(_renderer.Render(index, scale)), needText ? LoadText(index) : null));
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
                if (text is not null && _texts[index] is null)
                {
                    _texts[index] = text;
                    _layers[index].Text = text;
                }
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

    // =====================================================================
    // Text selection: drag over the page to highlight, Ctrl+C to copy
    // =====================================================================

    /// <summary>Reads a page's characters; a page PDFium can't read just can't be selected.</summary>
    private PdfPageText? LoadText(int index)
    {
        try
        {
            return _renderer.GetText(index);
        }
        catch (Exception e)
        {
            ErrorLog.Write(e, $"pdf text, page {index + 1}");
            return PdfPageText.Empty;
        }
    }

    /// <summary>
    /// The page under a point of <see cref="_pages"/> and the point on it in
    /// points. Outside every page, the nearest one when <paramref name="nearest"/>.
    /// </summary>
    private (int Page, double X, double Y)? PageAt(Point p, bool nearest)
    {
        int found = -1;
        var bestDistance = double.MaxValue;
        for (var i = 0; i < _images.Length; i++)
        {
            var b = _pages.Children[i].Bounds;
            if (b.Width <= 0 || b.Height <= 0) continue;
            if (b.Contains(p)) { found = i; break; }
            if (!nearest) continue;
            var dy = p.Y < b.Top ? b.Top - p.Y : p.Y > b.Bottom ? p.Y - b.Bottom : 0;
            if (dy < bestDistance) { bestDistance = dy; found = i; }
            else if (p.Y < b.Top) break; // the pages below are only further away
        }
        if (found < 0) return null;
        var bounds = _pages.Children[found].Bounds;
        var (width, height) = _renderer.PageSizes[found];
        return (found, (p.X - bounds.X) / bounds.Width * width, (p.Y - bounds.Y) / bounds.Height * height);
    }

    private void OnPagesPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(_pages);
        if (!point.Properties.IsLeftButtonPressed) return; // a right click keeps the selection for the menu
        _scroll.Focus();

        if (PageAt(point.Position, nearest: false) is not { } hit || _texts[hit.Page] is not { } text)
        {
            SetSelection((0, 0), (0, 0));
            return;
        }

        if (e.ClickCount == 2)
        {
            var (start, end) = text.WordAt(text.CharAt(hit.X, hit.Y));
            SetSelection((hit.Page, start), (hit.Page, end));
        }
        else if (e.ClickCount >= 3)
        {
            SetSelection((hit.Page, 0), (hit.Page, text.Length));
        }
        else
        {
            var caret = (hit.Page, text.CaretAt(hit.X, hit.Y));
            SetSelection(e.KeyModifiers.HasFlag(KeyModifiers.Shift) && HasSelection ? _anchor : caret, caret);
            _dragging = true;
            e.Pointer.Capture(_pages);
        }
        e.Handled = true;
    }

    private void OnPagesMoved(object? sender, PointerEventArgs e)
    {
        var position = e.GetPosition(_pages);
        if (!_dragging)
        {
            var overText = PageAt(position, nearest: false) is { } over &&
                           _texts[over.Page]?.IsOverText(over.X, over.Y) == true;
            var cursor = overText ? TextCursor : null;
            if (_pages.Cursor != cursor) _pages.Cursor = cursor;
            return;
        }

        // Dragging past the top or bottom edge scrolls, faster the further out.
        var inView = e.GetPosition(_scroll);
        if (inView.Y < 0) _scroll.Offset = _scroll.Offset.WithY(Math.Max(0, _scroll.Offset.Y + inView.Y / 2));
        else if (inView.Y > _scroll.Viewport.Height)
            _scroll.Offset = _scroll.Offset.WithY(_scroll.Offset.Y + (inView.Y - _scroll.Viewport.Height) / 2);

        if (PageAt(position, nearest: true) is not { } hit || _texts[hit.Page] is not { } text) return;
        SetSelection(_anchor, (hit.Page, text.CaretAt(hit.X, hit.Y)));
    }

    private void SetSelection((int Page, int Caret) anchor, (int Page, int Caret) focus)
    {
        _anchor = anchor;
        _focus = focus;
        var (start, end) = Ordered();
        for (var i = 0; i < _layers.Length; i++)
        {
            if (!HasSelection || i < start.Page || i > end.Page) _layers[i].SetRange(0, 0);
            else _layers[i].SetRange(i == start.Page ? start.Caret : 0, i == end.Page ? end.Caret : int.MaxValue);
        }
    }

    private ((int Page, int Caret) Start, (int Page, int Caret) End) Ordered() =>
        _anchor.Page < _focus.Page || (_anchor.Page == _focus.Page && _anchor.Caret <= _focus.Caret)
            ? (_anchor, _focus)
            : (_focus, _anchor);

    private void SelectAll()
    {
        if (_layers.Length == 0) return;
        SetSelection((0, 0), (_layers.Length - 1, int.MaxValue));
    }

    private async Task CopyAsync()
    {
        if (!HasSelection) return;
        var (start, end) = Ordered();
        var known = _texts[start.Page..(end.Page + 1)];
        try
        {
            // Pages scrolled far away gave their text back; read them again off the UI thread.
            var copied = await Task.Run(() =>
            {
                var sb = new System.Text.StringBuilder();
                for (var page = start.Page; page <= end.Page; page++)
                {
                    var text = known[page - start.Page] ?? LoadText(page);
                    if (text is null) break; // closed meanwhile
                    if (page > start.Page && sb.Length > 0 && sb[^1] != '\n') sb.AppendLine();
                    text.AppendText(sb, page == start.Page ? start.Caret : 0, page == end.Page ? end.Caret : int.MaxValue);
                }
                return sb.ToString();
            });
            if (_disposed || copied.Length == 0) return;
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(copied);
            SessionTrail.Note($"pdf: copied {copied.Length:N0} chars from pages {start.Page + 1}-{end.Page + 1}");
        }
        catch (Exception e)
        {
            ErrorLog.Write(e, "pdf copy");
        }
    }

    private void OnSelectionKey(object? sender, KeyEventArgs e)
    {
        if (e.KeyModifiers != KeyModifiers.Control) return;
        switch (e.Key)
        {
            case Key.C or Key.Insert when HasSelection:
                _ = CopyAsync();
                e.Handled = true;
                break;
            case Key.A:
                SelectAll();
                e.Handled = true;
                break;
        }
    }

    private ContextMenu BuildContextMenu()
    {
        var copy = new MenuItem { Header = Loc.Get("PdfCopy"), InputGesture = new KeyGesture(Key.C, KeyModifiers.Control) };
        var selectAll = new MenuItem { Header = Loc.Get("PdfSelectAll"), InputGesture = new KeyGesture(Key.A, KeyModifiers.Control) };
        copy.Click += (_, _) => _ = CopyAsync();
        selectAll.Click += (_, _) => SelectAll();
        var menu = new ContextMenu { Items = { copy, selectAll } };
        menu.Opening += (_, _) => copy.IsEnabled = HasSelection;
        return menu;
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
