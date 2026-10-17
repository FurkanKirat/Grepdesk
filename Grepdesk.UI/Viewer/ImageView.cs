using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// An image, fitted to the window until the user zooms: Ctrl+wheel, + and -,
/// or the buttons. 0 fits again, 1 shows it at actual size.
/// </summary>
internal sealed class ImageView : DockPanel, IViewerBody
{
    private const double MinZoom = 0.05, MaxZoom = 16, Step = 1.25;

    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly Bitmap _bitmap;
    private readonly Image _image;
    private readonly ScrollViewer _scroll;
    private readonly TextBlock _zoomText;
    private bool _fit = true;
    private double _zoom = 1;

    public ImageView(Bitmap bitmap)
    {
        _bitmap = bitmap;
        _image = new Image
        {
            Source = bitmap,
            Stretch = Stretch.Fill,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        RenderOptions.SetBitmapInterpolationMode(_image, BitmapInterpolationMode.HighQuality);

        _scroll = new ScrollViewer
        {
            Content = _image,
            HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Focusable = true,
        };
        _scroll.SizeChanged += (_, _) => { if (_fit) ApplyZoom(); };
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, Avalonia.Interactivity.RoutingStrategies.Tunnel);

        var zoomOut = ViewerBar.Button("−", Loc.Get("ViewerZoomOut"));
        var zoomIn = ViewerBar.Button("+", Loc.Get("ViewerZoomIn"));
        var fit = ViewerBar.Button(Loc.Get("ViewerZoomFit"));
        var actual = ViewerBar.Button("1:1", Loc.Get("ViewerZoomActual"));
        _zoomText = ViewerBar.Label("", 52);
        zoomOut.Click += (_, _) => ZoomBy(1 / Step);
        zoomIn.Click += (_, _) => ZoomBy(Step);
        fit.Click += (_, _) => Fit();
        actual.Click += (_, _) => SetZoom(1);

        var size = ViewerBar.Label($"{bitmap.PixelSize.Width} × {bitmap.PixelSize.Height}");
        var zoom = ViewerBar.Row(zoomOut, _zoomText, zoomIn, fit, actual);
        zoom.HorizontalAlignment = HorizontalAlignment.Right;
        var bar = new DockPanel { Children = { size, zoom } };
        SetDock(size, Dock.Left);

        Children.Add(ViewerBar.Create(bar));
        Children.Add(_scroll);
        ApplyZoom();
    }

    public void FocusContent() => _scroll.Focus();

    private void Fit()
    {
        _fit = true;
        ApplyZoom();
    }

    private void ZoomBy(double factor) => SetZoom(_zoom * factor);

    private void SetZoom(double zoom)
    {
        _fit = false;
        _zoom = Math.Clamp(zoom, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        var size = _bitmap.Size;
        if (_fit)
        {
            var viewport = _scroll.Bounds.Size;
            // Fit, but never blow a small image up beyond its own size.
            _zoom = viewport.Width > 0 && viewport.Height > 0
                ? Math.Min(1, Math.Min(viewport.Width / size.Width, viewport.Height / size.Height))
                : 1;
        }
        _image.Width = Math.Max(1, size.Width * _zoom);
        _image.Height = Math.Max(1, size.Height * _zoom);
        _zoomText.Text = $"{_zoom * 100:0}%";
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        ZoomBy(e.Delta.Y > 0 ? Step : 1 / Step);
        e.Handled = true;
    }

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: ZoomBy(Step); return true;
            case Key.OemMinus or Key.Subtract: ZoomBy(1 / Step); return true;
            case Key.D0 or Key.NumPad0: Fit(); return true;
            case Key.D1 or Key.NumPad1: SetZoom(1); return true;
            default: return false;
        }
    }

    public void Dispose()
    {
        _image.Source = null;
        _bitmap.Dispose();
    }
}
