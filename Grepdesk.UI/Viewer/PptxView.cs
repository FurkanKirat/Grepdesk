using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>A PowerPoint presentation read and its embedded images decoded off the UI thread.</summary>
internal sealed class PptxPreview : IDisposable
{
    public required PptxDocument Document { get; init; }
    public required IReadOnlyDictionary<byte[], Bitmap> Images { get; init; }
    public bool OwnsImages { get; init; } = true;

    public static PptxPreview Load(string path, CancellationToken ct)
    {
        var images = new Dictionary<byte[], Bitmap>(ReferenceEqualityComparer.Instance);
        try
        {
            var doc = PptxDocument.Load(path, ct);
            return From(doc, images, ct);
        }
        catch
        {
            foreach (var bmp in images.Values) bmp.Dispose();
            throw;
        }
    }

    public static PptxPreview From(PptxDocument doc, Dictionary<byte[], Bitmap> images, CancellationToken ct, bool ownsImages = true)
    {
        foreach (var img in doc.Slides.SelectMany(s => s.Backdrop.Concat(s.Shapes)).OfType<PptxImageShape>())
        {
            ct.ThrowIfCancellationRequested();
            DocxPreview.Decode(img.Data, images);
        }
        return new PptxPreview { Document = doc, Images = images, OwnsImages = ownsImages };
    }

    public void Dispose()
    {
        if (OwnsImages)
            foreach (var bmp in Images.Values) bmp.Dispose();
    }
}

/// <summary>
/// Slide viewer and editor for PowerPoint (.pptx) presentations: slide outline
/// sidebar, scaled slide canvas with positioned text boxes, tables and pictures,
/// speaker notes panel, keyboard slide navigation, zoom controls, and in-place
/// text/shape editing with undo/redo.
/// </summary>
internal sealed class PptxView : DockPanel, IViewerBody
{
    private const double MinZoom = 0.25, MaxZoom = 3.0, ZoomStep = 1.15;
    private const double Dip = 96.0 / 72.0;
    private const double StageMargin = 28.0;

    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly IBrush StageBg = new ImmutableSolidColorBrush(Color.Parse("#11111b"));
    private static readonly IBrush SidebarBg = new ImmutableSolidColorBrush(Color.Parse("#181825"));
    private static readonly IBrush ActiveThumbBg = new ImmutableSolidColorBrush(Color.Parse("#313244"));
    private static readonly IBrush SelectedBorder = new ImmutableSolidColorBrush(Color.Parse("#89b4fa"));
    private static readonly IBrush SubtleEditBorder = new ImmutableSolidColorBrush(Color.Parse("#89b4fa"), 0.45);
    private static readonly IBrush HighlightBrush = new ImmutableSolidColorBrush(Color.Parse("#ffd400"), 0.55);
    private static readonly IBrush DefaultSlideText = Brushes.Black;

    private PptxPreview _preview;
    private PptxDocument _doc;
    private readonly PptxEditDocument? _editDoc;
    private readonly string? _highlight;
    private readonly List<PptxEditDocument.Snapshot> _undoStack = [];
    private readonly List<PptxEditDocument.Snapshot> _redoStack = [];

    private int _slideIndex;
    private int _selectedEditableShape = -1;
    private int _activeEdit = -1;
    private bool _fit = true;
    private double _zoom = 1.0;
    private bool _updatingControls;

    private sealed class ThumbSlot
    {
        public required Border Card { get; init; }
        public required TextBlock Badge { get; init; }
        public required Border Host { get; init; }
        public required int Index { get; init; }
        public bool Realized { get; set; }
    }

    private const double ThumbWidth = 140;

    private readonly StackPanel _sidebarList;
    private readonly ScrollViewer _sidebarScroll;
    private readonly List<ThumbSlot> _thumbs = [];
    private readonly Button _prevOverlay, _nextOverlay;
    private readonly ScrollViewer _stageScroll;
    private readonly LayoutTransformControl _zoomHost;
    private readonly Border _notesPanel;
    private readonly SelectableTextBlock _notesReadBlock;
    private readonly TextBox _notesEditBox;
    private readonly Button _prevBtn, _nextBtn;
    private readonly TextBlock _slideIndicator, _zoomText;
    private readonly Button? _undoBtn, _redoBtn;
    private readonly ToggleButton? _boldBtn, _italicBtn, _underlineBtn, _alignLeftBtn, _alignCenterBtn, _alignRightBtn;

    public event Action? Changed;

    public PptxView(PptxPreview preview, string? highlight, PptxEditDocument? editDocument = null)
    {
        _preview = preview;
        _doc = preview.Document;
        _editDoc = editDocument;
        _highlight = string.IsNullOrEmpty(highlight) ? null : highlight;

        // Top toolbar when editing
        if (_editDoc is not null)
        {
            _undoBtn = ToolbarButton("↶", Loc.Get("DocxUndo"), Undo);
            _redoBtn = ToolbarButton("↷", Loc.Get("DocxRedo"), Redo);
            _boldBtn = ToolbarToggle(new TextBlock { Text = "B", FontWeight = FontWeight.Bold }, Loc.Get("DocxBold"),
                () => FormatSelectedShape(new DocxRunChange.Bold(_boldBtn!.IsChecked == true)));
            _italicBtn = ToolbarToggle(new TextBlock { Text = "I", FontStyle = FontStyle.Italic }, Loc.Get("DocxItalic"),
                () => FormatSelectedShape(new DocxRunChange.Italic(_italicBtn!.IsChecked == true)));
            _underlineBtn = ToolbarToggle(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, Loc.Get("DocxUnderline"),
                () => FormatSelectedShape(new DocxRunChange.Underline(_underlineBtn!.IsChecked == true)));
            var sizeDown = ToolbarButton("A−", Loc.Get("DocxFontSize"), () => AdjustSelectedShapeFontSize(-2));
            var sizeUp = ToolbarButton("A+", Loc.Get("DocxFontSize"), () => AdjustSelectedShapeFontSize(+2));
            _alignLeftBtn = ToolbarToggle(new TextBlock { Text = "⇤", FontSize = 11 }, Loc.Get("DocxAlignLeft"),
                () => AlignSelectedShape(DocxAlignment.Left));
            _alignCenterBtn = ToolbarToggle(new TextBlock { Text = "↔", FontSize = 11 }, Loc.Get("DocxAlignCenter"),
                () => AlignSelectedShape(DocxAlignment.Center));
            _alignRightBtn = ToolbarToggle(new TextBlock { Text = "⇥", FontSize = 11 }, Loc.Get("DocxAlignRight"),
                () => AlignSelectedShape(DocxAlignment.Right));

            var bar = new Border
            {
                Background = ViewerBar.Background,
                BorderBrush = ViewerBar.Border,
                BorderThickness = new Thickness(0, 0, 0, 1),
                Padding = new Thickness(10, 6),
                Child = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 4,
                    Children =
                    {
                        _undoBtn, _redoBtn, Separator(),
                        _boldBtn, _italicBtn, _underlineBtn, sizeDown, sizeUp, Separator(),
                        _alignLeftBtn, _alignCenterBtn, _alignRightBtn
                    }
                },
                [DockProperty] = Dock.Top
            };
            Children.Add(bar);
        }

        // Bottom bar: slide navigation + zoom
        _prevBtn = ViewerBar.Button("‹", Loc.Get("PptxPrevSlide"));
        _nextBtn = ViewerBar.Button("›", Loc.Get("PptxNextSlide"));
        _slideIndicator = ViewerBar.Label("", 100);
        _prevBtn.Click += (_, _) => SelectSlide(_slideIndex - 1);
        _nextBtn.Click += (_, _) => SelectSlide(_slideIndex + 1);

        var showBtn = ViewerBar.Button("▶ " + Loc.Get("PptxSlideShow"), Loc.Get("PptxSlideShowTip"));
        showBtn.Click += (_, _) => StartSlideShow();
        var navRow = ViewerBar.Row(_prevBtn, _slideIndicator, _nextBtn, showBtn);
        SetDock(navRow, Dock.Left);

        var zoomOut = ViewerBar.Button("−", Loc.Get("ViewerZoomOut"));
        var zoomIn = ViewerBar.Button("+", Loc.Get("ViewerZoomIn"));
        var fitBtn = ViewerBar.Button(Loc.Get("ViewerZoomFit"));
        var actualBtn = ViewerBar.Button("100%", Loc.Get("ViewerZoomActual"));
        _zoomText = ViewerBar.Label("", 52);
        zoomOut.Click += (_, _) => SetZoom(_zoom / ZoomStep);
        zoomIn.Click += (_, _) => SetZoom(_zoom * ZoomStep);
        fitBtn.Click += (_, _) => { _fit = true; ApplyZoom(); };
        actualBtn.Click += (_, _) => SetZoom(1.0);

        var zoomRow = ViewerBar.Row(zoomOut, _zoomText, zoomIn, fitBtn, actualBtn);
        SetDock(zoomRow, Dock.Right);

        var bottomDock = new DockPanel { LastChildFill = false };
        bottomDock.Children.Add(navRow);
        bottomDock.Children.Add(zoomRow);
        Children.Add(ViewerBar.Create(bottomDock));

        // Left sidebar: slide outline list
        _sidebarList = new StackPanel { Spacing = 6, Margin = new Thickness(8) };
        _sidebarScroll = new ScrollViewer
        {
            Content = _sidebarList,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Width = 200,
        };
        _sidebarScroll.ScrollChanged += (_, _) => RealizeThumbs();
        var sidebarBorder = new Border
        {
            Background = SidebarBg,
            BorderBrush = ViewerBar.Border,
            BorderThickness = new Thickness(0, 0, 1, 0),
            Child = _sidebarScroll,
            [DockProperty] = Dock.Left,
        };
        Children.Add(sidebarBorder);

        // Speaker notes panel at bottom of stage
        _notesReadBlock = new SelectableTextBlock
        {
            Foreground = ViewerBar.Text,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        _notesEditBox = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            MinHeight = 48,
            MaxHeight = 96,
            Watermark = Loc.Get("PptxNotesWatermark"),
            IsVisible = _editDoc is not null,
        };
        _notesEditBox.LostFocus += (_, _) =>
        {
            if (_editDoc is not null && !_updatingControls)
                CommitNotesEdit();
        };

        var notesStack = new StackPanel
        {
            Spacing = 4,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.Get("PptxSpeakerNotes"),
                    Foreground = ViewerBar.Muted,
                    FontSize = 11,
                    FontWeight = FontWeight.SemiBold
                },
                _notesReadBlock,
                _notesEditBox
            }
        };
        _notesPanel = new Border
        {
            Background = SidebarBg,
            BorderBrush = ViewerBar.Border,
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 8),
            Child = notesStack,
            [DockProperty] = Dock.Bottom,
        };
        Children.Add(_notesPanel);

        // Slide stage
        _zoomHost = new LayoutTransformControl
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(StageMargin)
        };
        _stageScroll = new ScrollViewer
        {
            Content = _zoomHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = StageBg,
            Focusable = true,
        };
        _stageScroll.SizeChanged += (_, _) => { if (_fit) ApplyZoom(); };
        _stageScroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _stageScroll.KeyDown += OnStageKeyDown;

        _prevOverlay = OverlayArrow("‹", HorizontalAlignment.Left, new Thickness(12, 0, 0, 0), Loc.Get("PptxPrevSlide"), () => SelectSlide(_slideIndex - 1));
        _nextOverlay = OverlayArrow("›", HorizontalAlignment.Right, new Thickness(0, 0, 26, 0), Loc.Get("PptxNextSlide"), () => SelectSlide(_slideIndex + 1));
        Children.Add(new Grid { Children = { _stageScroll, _prevOverlay, _nextOverlay } });

        FindFirstHighlightSlide();
        RebuildSidebar();
        RebuildSlideStage();
        ApplyZoom();
    }

    public bool IsEditing => _editDoc is not null;

    public void FocusContent() => _stageScroll.Focus();

    public bool HandleKey(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Left or Key.Up or Key.PageUp:
                SelectSlide(_slideIndex - 1);
                return true;
            case Key.Right or Key.Down or Key.PageDown:
                SelectSlide(_slideIndex + 1);
                return true;
            case Key.F5:
                StartSlideShow();
                return true;
            case Key.Home:
                SelectSlide(0);
                return true;
            case Key.End:
                SelectSlide(_doc.Slides.Count - 1);
                return true;
            case Key.OemPlus or Key.Add:
                SetZoom(_zoom * ZoomStep);
                return true;
            case Key.OemMinus or Key.Subtract:
                SetZoom(_zoom / ZoomStep);
                return true;
            case Key.D0 or Key.NumPad0:
                _fit = true;
                ApplyZoom();
                return true;
            default:
                return false;
        }
    }

    public void Dispose()
    {
        _preview.Dispose();
        foreach (var bmp in _lateImages.Values) bmp.Dispose();
    }

    // After an edit the document is re-read, so its picture byte arrays are new objects: decode those on demand.
    private readonly Dictionary<byte[], Bitmap> _lateImages = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, FontFamily> _fonts = new(StringComparer.OrdinalIgnoreCase);

    private FontFamily? Font(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return null;
        if (_fonts.TryGetValue(name, out var cached)) return cached;
        try { return _fonts[name] = new FontFamily(name); }
        catch { return null; }
    }

    private Bitmap? ImageFor(byte[] data)
    {
        if (_preview.Images.TryGetValue(data, out var bmp)) return bmp;
        if (_lateImages.TryGetValue(data, out bmp)) return bmp;
        DocxPreview.Decode(data, _lateImages);
        return _lateImages.GetValueOrDefault(data);
    }

    private void FindFirstHighlightSlide()
    {
        if (_highlight is null) return;
        for (var i = 0; i < _doc.Slides.Count; i++)
        {
            var slideText = _doc.ToPlainText(slideRange: (i + 1, i + 1));
            if (slideText.Contains(_highlight, StringComparison.OrdinalIgnoreCase))
            {
                _slideIndex = i;
                return;
            }
        }
    }

    private void SelectSlide(int index)
    {
        if (_doc.Slides.Count == 0) return;
        var clamped = Math.Clamp(index, 0, _doc.Slides.Count - 1);
        if (clamped == _slideIndex) return;
        _slideIndex = clamped;
        _selectedEditableShape = -1;
        _activeEdit = -1;
        UpdateSidebarSelection();
        RebuildSlideStage();
    }

    private void StartSlideShow()
    {
        if (_doc.Slides.Count == 0) return;
        var win = new PptxSlideShowWindow(_doc.Slides.Count, _slideIndex, i => BuildSlideCanvas(_doc.Slides[i], interactive: false));
        win.Closed += (_, _) =>
        {
            SelectSlide(win.Index);
            Dispatcher.UIThread.Post(FocusContent);
        };
        SessionTrail.Note($"pptx slide show started");
        if (TopLevel.GetTopLevel(this) is Window owner) win.Show(owner);
        else win.Show();
    }

    private static Button OverlayArrow(string glyph, HorizontalAlignment side, Thickness margin, string tip, Action onClick)
    {
        var btn = new Button
        {
            Content = glyph,
            FontSize = 28,
            Width = 40,
            Height = 64,
            Padding = new Thickness(0, 0, 0, 4),
            CornerRadius = new CornerRadius(8),
            Background = new ImmutableSolidColorBrush(Colors.Black, 0.45),
            Foreground = Brushes.White,
            Opacity = 0.8,
            Focusable = false,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Margin = margin,
        };
        ToolTip.SetTip(btn, tip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private void UpdateSidebarSelection()
    {
        foreach (var slot in _thumbs)
        {
            var isSelected = slot.Index == _slideIndex;
            slot.Card.Background = isSelected ? ActiveThumbBg : Brushes.Transparent;
            slot.Card.BorderBrush = isSelected ? SelectedBorder : ViewerBar.Border;
            slot.Card.BorderThickness = new Thickness(isSelected ? 2 : 1);
            slot.Badge.Foreground = isSelected ? SelectedBorder : ViewerBar.Muted;
        }
        if (_slideIndex >= 0 && _slideIndex < _thumbs.Count)
            _thumbs[_slideIndex].Card.BringIntoView();
    }

    /// <summary>Builds the slide pictures for the cards at (or near) the visible part of the sidebar only.</summary>
    private void RealizeThumbs()
    {
        var top = _sidebarScroll.Offset.Y - 400;
        var bottom = _sidebarScroll.Offset.Y + _sidebarScroll.Viewport.Height + 400;
        foreach (var slot in _thumbs)
        {
            if (slot.Realized) continue;
            var b = slot.Card.Bounds;
            if (b.Height <= 0 || b.Bottom < top || b.Top > bottom) continue;
            slot.Realized = true;
            slot.Host.Child = new Viewbox
            {
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false,
                Child = BuildSlideCanvas(_doc.Slides[slot.Index], interactive: false),
            };
        }
    }

    private void RebuildSidebar()
    {
        _sidebarList.Children.Clear();
        _thumbs.Clear();
        for (var i = 0; i < _doc.Slides.Count; i++)
        {
            var idx = i;
            var slide = _doc.Slides[i];
            var title = slide.Title;
            if (string.IsNullOrWhiteSpace(title))
                title = Loc.Get("PptxSlideNumber", slide.Number);

            var badge = new TextBlock
            {
                Text = slide.Number.ToString(CultureInfo.InvariantCulture),
                Foreground = ViewerBar.Muted,
                FontWeight = FontWeight.Bold,
                FontSize = 11.5,
                Width = 22,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var host = new Border
            {
                Width = ThumbWidth,
                Height = ThumbWidth * Math.Max(1, slide.Height) / Math.Max(1, slide.Width),
                Background = TryBrush(slide.BackgroundColor, out var thumbBg) ? thumbBg : Brushes.White,
                ClipToBounds = true,
            };

            var row = new DockPanel { LastChildFill = true };
            SetDock(badge, Dock.Left);
            row.Children.Add(badge);
            row.Children.Add(host);

            var card = new Border
            {
                CornerRadius = new CornerRadius(5),
                Padding = new Thickness(6, 6),
                Cursor = new Cursor(StandardCursorType.Hand),
                Child = row
            };
            ToolTip.SetTip(card, title);
            card.PointerPressed += (_, e) =>
            {
                SelectSlide(idx);
                e.Handled = true;
            };
            _thumbs.Add(new ThumbSlot { Card = card, Badge = badge, Host = host, Index = i });
            _sidebarList.Children.Add(card);
        }
        UpdateSidebarSelection();
        Dispatcher.UIThread.Post(RealizeThumbs, DispatcherPriority.Background);
    }

    private void RebuildSlideStage()
    {
        if (_doc.Slides.Count == 0) return;
        var slide = _doc.Slides[_slideIndex];

        _prevBtn.IsEnabled = _slideIndex > 0;
        _nextBtn.IsEnabled = _slideIndex < _doc.Slides.Count - 1;
        _prevOverlay.IsEnabled = _prevBtn.IsEnabled;
        _nextOverlay.IsEnabled = _nextBtn.IsEnabled;
        _slideIndicator.Text = Loc.Get("PptxSlideOf", slide.Number, _doc.Slides.Count);

        var canvas = BuildSlideCanvas(slide, interactive: true);

        _zoomHost.Child = new Border
        {
            BorderBrush = ViewerBar.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            BoxShadow = new BoxShadows(new BoxShadow { Blur = 18, Color = Color.FromArgb(120, 0, 0, 0), OffsetY = 4 }),
            Child = canvas
        };

        // Update speaker notes
        _updatingControls = true;
        var hasNotes = !string.IsNullOrWhiteSpace(slide.Notes);
        var canEditNotes = _editDoc is not null && slide.NotesPartPath is not null;
        _notesPanel.IsVisible = hasNotes || canEditNotes;
        _notesReadBlock.IsVisible = _editDoc is null;
        _notesReadBlock.Text = slide.Notes;
        _notesEditBox.IsVisible = canEditNotes;
        _notesEditBox.Text = slide.Notes;
        UpdateToolbarState();
        _updatingControls = false;
    }

    private Canvas BuildSlideCanvas(PptxSlide slide, bool interactive)
    {
        var bgBrush = TryBrush(slide.BackgroundColor, out var parsedBg) ? parsedBg : Brushes.White;
        var defaultFg = IsDarkHex(slide.BackgroundColor) ? Brushes.White : DefaultSlideText;

        var canvas = new Canvas
        {
            Width = slide.Width * Dip,
            Height = slide.Height * Dip,
            Background = bgBrush,
            ClipToBounds = true,
        };

        var backdrop = new HashSet<PptxShape>(slide.Backdrop, ReferenceEqualityComparer.Instance);
        foreach (var shape in slide.Backdrop.Concat(slide.Shapes))
        {
            var control = BuildShapeControl(shape, defaultFg, interactive);
            if (control is null) continue;
            if (backdrop.Contains(shape)) control.IsHitTestVisible = false;
            Canvas.SetLeft(control, shape.X * Dip);
            Canvas.SetTop(control, shape.Y * Dip);
            canvas.Children.Add(control);
        }
        return canvas;
    }

    private Control? BuildShapeControl(PptxShape shape, IBrush defaultFg, bool interactive)
    {
        switch (shape)
        {
            case PptxImageShape imgShape:
                if (ImageFor(imgShape.Data) is not { } bmp) return BuildPicturePlaceholder(imgShape);
                return new Image
                {
                    Source = bmp,
                    Width = imgShape.Width * Dip,
                    Height = imgShape.Height * Dip,
                    Stretch = Stretch.Fill,
                };

            case PptxTableShape tableShape:
                return BuildTableControl(tableShape, defaultFg);

            case PptxLineShape lineShape:
                return BuildLineControl(lineShape);

            case PptxTextShape textShape:
                if (!interactive || _editDoc is null || textShape.EditableIndex < 0)
                    return BuildReadOnlyTextShape(textShape, defaultFg);
                return textShape.EditableIndex == _activeEdit
                    ? BuildEditableTextShape(textShape, defaultFg)
                    : BuildClickToEditShape(textShape, defaultFg);

            default:
                return null;
        }
    }

    /// <summary>Pictures Skia can't decode (EMF/WMF charts and drawings) are marked, not silently dropped.</summary>
    private static Control BuildPicturePlaceholder(PptxImageShape shape) => new Border
    {
        Width = shape.Width * Dip,
        Height = shape.Height * Dip,
        Background = new ImmutableSolidColorBrush(Color.Parse("#f2f3f7")),
        BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#c9ccd6")),
        BorderThickness = new Thickness(1),
        Child = new TextBlock
        {
            Text = Loc.Get("PptxPictureUnsupported"),
            Foreground = new ImmutableSolidColorBrush(Color.Parse("#6c7086")),
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(12),
        }
    };

    /// <summary>An editable shape at rest looks like the slide (bullets and all); clicking it swaps in a text box.</summary>
    private Control BuildClickToEditShape(PptxTextShape shape, IBrush defaultFg)
    {
        var visual = BuildReadOnlyTextShape(shape, defaultFg);
        visual.IsHitTestVisible = false;
        var selected = shape.EditableIndex == _selectedEditableShape;
        var wrapper = new Border
        {
            Background = Brushes.Transparent,
            BorderBrush = selected ? SelectedBorder : SubtleEditBorder,
            BorderThickness = new Thickness(selected ? 2 : 1),
            Cursor = new Cursor(StandardCursorType.Ibeam),
            Child = visual,
        };
        wrapper.PointerPressed += (_, e) =>
        {
            _selectedEditableShape = shape.EditableIndex;
            _activeEdit = shape.EditableIndex;
            RebuildSlideStage();
            e.Handled = true;
        };
        return wrapper;
    }

    private static Control BuildLineControl(PptxLineShape line)
    {
        var thickness = Math.Max(1.0, line.Thickness * Dip);
        var w = line.Width * Dip;
        var h = line.Height * Dip;
        var cw = Math.Max(w, thickness);
        var ch = Math.Max(h, thickness);
        var (x1, x2) = w < thickness ? (cw / 2, cw / 2) : (line.FlipH ? w : 0, line.FlipH ? 0 : w);
        var (y1, y2) = h < thickness ? (ch / 2, ch / 2) : (line.FlipV ? h : 0, line.FlipV ? 0 : h);
        return new Avalonia.Controls.Shapes.Line
        {
            StartPoint = new Point(x1, y1),
            EndPoint = new Point(x2, y2),
            Width = cw,
            Height = ch,
            Stroke = TryBrush(line.Color, out var b) ? b : Brushes.Black,
            StrokeThickness = thickness,
        };
    }

    private Control BuildReadOnlyTextShape(PptxTextShape shape, IBrush defaultFg)
    {
        var stack = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = shape.Anchor switch
            {
                PptxAnchor.Middle => VerticalAlignment.Center,
                PptxAnchor.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top
            }
        };
        foreach (var para in shape.Paragraphs)
        {
            if (para.Runs.Count == 0)
            {
                stack.Children.Add(new Border { Height = 12 });
                continue;
            }

            var tb = new SelectableTextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = para.Alignment switch
                {
                    DocxAlignment.Center => TextAlignment.Center,
                    DocxAlignment.Right => TextAlignment.Right,
                    DocxAlignment.Justify => TextAlignment.Justify,
                    _ => TextAlignment.Left
                },
            };
            var inlines = new InlineCollection();
            foreach (var run in para.Runs)
                AppendHighlightedRuns(inlines, run, defaultFg);
            tb.Inlines = inlines;

            var marL = para.MarginLeft ?? para.Level * 13.5;
            if (para.Bullet is { Length: > 0 } bullet)
            {
                // The bullet hangs in its own column left of the text, so wrapped lines align with the first one.
                var firstRun = para.Runs[0];
                var bulletLeft = Math.Max(0, marL + Math.Min(para.Indent ?? -18, 0));
                var hang = marL - bulletLeft;
                if (hang < firstRun.FontSize * 0.6) hang = firstRun.FontSize * 0.9;
                var bulletBlock = new TextBlock
                {
                    Text = bullet,
                    FontSize = firstRun.FontSize * Dip,
                    FontFamily = Font(firstRun.FontFamily),
                    FontWeight = bullet.Length > 1 ? FontWeight.Normal : FontWeight.SemiBold,
                    Foreground = para.BulletColor is { } bc && TryBrush(bc, out var bulletBrush) ? bulletBrush
                        : firstRun.Color is { } c && TryBrush(c, out var rb) ? rb : defaultFg,
                    VerticalAlignment = VerticalAlignment.Top,
                };
                var line = new Grid
                {
                    Margin = new Thickness(bulletLeft * Dip, 0, 0, 2),
                    ColumnDefinitions = { new ColumnDefinition(hang * Dip, GridUnitType.Pixel), new ColumnDefinition(1, GridUnitType.Star) },
                };
                Grid.SetColumn(tb, 1);
                line.Children.Add(bulletBlock);
                line.Children.Add(tb);
                stack.Children.Add(line);
            }
            else
            {
                tb.Margin = new Thickness(marL * Dip, 0, 0, 2);
                stack.Children.Add(tb);
            }
        }

        return new Border
        {
            Width = shape.Width * Dip,
            MinHeight = shape.Height * Dip,
            Background = shape.FillColor is { } fill && TryBrush(fill, out var fb) ? fb : Brushes.Transparent,
            BorderBrush = shape.BorderColor is { } border && TryBrush(border, out var bb) ? bb : null,
            BorderThickness = shape.BorderColor is not null ? new Thickness(1) : default,
            Padding = new Thickness(shape.InsetLeft * Dip, shape.InsetTop * Dip, shape.InsetRight * Dip, shape.InsetBottom * Dip),
            Child = stack
        };
    }

    private void AppendHighlightedRuns(InlineCollection inlines, PptxRun run, IBrush defaultFg)
    {
        var fg = run.Color is { } c && TryBrush(c, out var b) ? b : defaultFg;
        var fontSize = run.FontSize * Dip;
        var weight = run.Bold ? FontWeight.Bold : FontWeight.Normal;
        var style = run.Italic ? FontStyle.Italic : FontStyle.Normal;
        var decor = run.Underline ? TextDecorations.Underline : null;
        var family = Font(run.FontFamily);

        if (_highlight is null || !run.Text.Contains(_highlight, StringComparison.OrdinalIgnoreCase))
        {
            inlines.Add(new Run(run.Text)
            {
                FontSize = fontSize,
                FontFamily = family,
                Foreground = fg,
                FontWeight = weight,
                FontStyle = style,
                TextDecorations = decor
            });
            return;
        }

        var text = run.Text;
        var start = 0;
        int idx;
        while ((idx = text.IndexOf(_highlight, start, StringComparison.OrdinalIgnoreCase)) >= 0)
        {
            if (idx > start)
            {
                inlines.Add(new Run(text[start..idx])
                {
                    FontSize = fontSize,
                    FontFamily = family,
                    Foreground = fg,
                    FontWeight = weight,
                    FontStyle = style,
                    TextDecorations = decor
                });
            }
            inlines.Add(new Run(text.Substring(idx, _highlight.Length))
            {
                FontSize = fontSize,
                FontFamily = family,
                Foreground = Brushes.Black,
                Background = HighlightBrush,
                FontWeight = FontWeight.Bold,
                FontStyle = style,
                TextDecorations = decor
            });
            start = idx + _highlight.Length;
        }
        if (start < text.Length)
        {
            inlines.Add(new Run(text[start..])
            {
                FontSize = fontSize,
                FontFamily = family,
                Foreground = fg,
                FontWeight = weight,
                FontStyle = style,
                TextDecorations = decor
            });
        }
    }

    private Control BuildEditableTextShape(PptxTextShape shape, IBrush defaultFg)
    {
        var firstPara = shape.Paragraphs.FirstOrDefault();
        var firstRun = shape.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault();
        var fg = firstRun?.Color is { } c && TryBrush(c, out var b) ? b : defaultFg;
        var fontSize = (firstRun?.FontSize ?? (shape.Placeholder is PptxPlaceholderKind.Title or PptxPlaceholderKind.CenterTitle ? 32.0 : 18.0)) * Dip;

        var isSelected = shape.EditableIndex == _selectedEditableShape;
        var box = new TextBox
        {
            Text = shape.PlainText,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Width = shape.Width * Dip,
            MinHeight = Math.Max(shape.Height * Dip, 36),
            Foreground = fg,
            Background = shape.FillColor is { } fill && TryBrush(fill, out var fb) ? fb : Brushes.Transparent,
            BorderBrush = isSelected ? SelectedBorder : SubtleEditBorder,
            BorderThickness = new Thickness(isSelected ? 2 : 1),
            FontSize = fontSize,
            FontFamily = Font(firstRun?.FontFamily) ?? FontFamily.Default,
            VerticalContentAlignment = shape.Anchor switch
            {
                PptxAnchor.Middle => VerticalAlignment.Center,
                PptxAnchor.Bottom => VerticalAlignment.Bottom,
                _ => VerticalAlignment.Top
            },
            FontWeight = firstRun?.Bold == true ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = firstRun?.Italic == true ? FontStyle.Italic : FontStyle.Normal,
            TextAlignment = (firstPara?.Alignment ?? DocxAlignment.Left) switch
            {
                DocxAlignment.Center => TextAlignment.Center,
                DocxAlignment.Right => TextAlignment.Right,
                DocxAlignment.Justify => TextAlignment.Justify,
                _ => TextAlignment.Left
            },
            Padding = new Thickness(shape.InsetLeft * Dip, shape.InsetTop * Dip, shape.InsetRight * Dip, shape.InsetBottom * Dip),
        };

        // The Fluent TextBox theme swaps in its own (dark) focused/hover colors, which hides
        // the slide's text; pin every state to the slide's own colors.
        var bgBrush = box.Background ?? Brushes.Transparent;
        foreach (var state in new[] { "", "PointerOver", "Focused", "Disabled" })
        {
            box.Resources["TextControlBackground" + state] = bgBrush;
            box.Resources["TextControlForeground" + state] = fg;
        }
        foreach (var state in new[] { "PointerOver", "Focused" })
            box.Resources["TextControlBorderBrush" + state] = SubtleEditBorder;
        box.Resources["TextControlBorderBrushFocused"] = SelectedBorder;
        box.Resources["TextControlBorderThemeThicknessFocused"] = new Thickness(2);
        box.CaretBrush = fg;
        box.SelectionBrush = new ImmutableSolidColorBrush(Color.Parse("#1a73e8"), 0.35);
        box.SelectionForegroundBrush = fg;

        box.AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            box.CaretIndex = box.Text?.Length ?? 0;
        }, DispatcherPriority.Input);

        box.GotFocus += (_, _) =>
        {
            _selectedEditableShape = shape.EditableIndex;
            box.BorderBrush = SelectedBorder;
            box.BorderThickness = new Thickness(2);
            UpdateToolbarState();
        };

        box.LostFocus += (_, _) =>
        {
            box.BorderBrush = SubtleEditBorder;
            box.BorderThickness = new Thickness(1);
            var leaving = _activeEdit == shape.EditableIndex;
            if (leaving) _activeEdit = -1;
            if (_editDoc is not null && !_updatingControls)
                CommitShapeTextEdit(shape.EditableIndex, shape.PlainText, box.Text ?? "");
            if (leaving) Dispatcher.UIThread.Post(RebuildSlideStage, DispatcherPriority.Background);
        };

        return box;
    }

    private Control BuildTableControl(PptxTableShape tableShape, IBrush defaultFg)
    {
        var grid = new Grid
        {
            Width = tableShape.Width * Dip,
            MinHeight = tableShape.Height * Dip,
            Background = Brushes.Transparent,
        };

        foreach (var colW in tableShape.ColumnWidths)
            grid.ColumnDefinitions.Add(new ColumnDefinition(colW * Dip, GridUnitType.Pixel));
        for (var r = 0; r < tableShape.RowCount; r++)
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));

        foreach (var cell in tableShape.Cells)
        {
            var bg = cell.FillColor is { } fill && TryBrush(fill, out var fb)
                ? fb
                : (cell.Row == 0 ? new ImmutableSolidColorBrush(Color.Parse("#e6e9ef")) : Brushes.Transparent);

            var tb = new SelectableTextBlock
            {
                Text = cell.Text,
                Foreground = defaultFg,
                FontSize = 13 * Dip,
                FontWeight = cell.Bold ? FontWeight.Bold : FontWeight.Normal,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(6, 4),
            };

            var border = new Border
            {
                Background = bg,
                BorderBrush = new ImmutableSolidColorBrush(Color.Parse("#9ca0b0")),
                BorderThickness = new Thickness(0.5),
                Child = tb,
            };
            Grid.SetRow(border, cell.Row);
            Grid.SetColumn(border, cell.Column);
            if (cell.RowSpan > 1) Grid.SetRowSpan(border, cell.RowSpan);
            if (cell.ColumnSpan > 1) Grid.SetColumnSpan(border, cell.ColumnSpan);
            grid.Children.Add(border);
        }

        return grid;
    }

    private void CommitShapeTextEdit(int editableIndex, string oldText, string newText)
    {
        if (_editDoc is null || string.Equals(oldText, newText, StringComparison.Ordinal))
            return;

        PushUndoSnapshot();
        _doc = _editDoc.SetShapeText(_slideIndex, editableIndex, newText);
        RebuildSidebar();
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private void CommitNotesEdit()
    {
        if (_editDoc is null || _doc.Slides.Count == 0) return;
        var slide = _doc.Slides[_slideIndex];
        var next = _notesEditBox.Text ?? "";
        if (string.Equals(slide.Notes, next, StringComparison.Ordinal)) return;

        PushUndoSnapshot();
        _doc = _editDoc.SetSlideNotes(_slideIndex, next);
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private void FormatSelectedShape(DocxRunChange change)
    {
        if (_editDoc is null || _updatingControls || _doc.Slides.Count == 0) return;
        var targetShape = ResolveTargetEditableShape();
        if (targetShape is null) return;

        PushUndoSnapshot();
        _doc = _editDoc.FormatShape(_slideIndex, targetShape.EditableIndex, change);
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private void AdjustSelectedShapeFontSize(double deltaPoints)
    {
        if (_editDoc is null || _doc.Slides.Count == 0) return;
        var targetShape = ResolveTargetEditableShape();
        if (targetShape is null) return;

        var currentSize = targetShape.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault()?.FontSize ?? 18.0;
        var nextSize = Math.Clamp(currentSize + deltaPoints, 8.0, 96.0);
        FormatSelectedShape(new DocxRunChange.Size(nextSize));
    }

    private void AlignSelectedShape(DocxAlignment alignment)
    {
        if (_editDoc is null || _updatingControls || _doc.Slides.Count == 0) return;
        var targetShape = ResolveTargetEditableShape();
        if (targetShape is null) return;

        PushUndoSnapshot();
        _doc = _editDoc.SetShapeAlignment(_slideIndex, targetShape.EditableIndex, alignment);
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private PptxTextShape? ResolveTargetEditableShape()
    {
        var slide = _doc.Slides[_slideIndex];
        var shapes = slide.Shapes.OfType<PptxTextShape>().Where(s => s.EditableIndex >= 0).ToList();
        return shapes.FirstOrDefault(s => s.EditableIndex == _selectedEditableShape) ?? shapes.FirstOrDefault();
    }

    private void UpdateToolbarState()
    {
        if (_editDoc is null) return;
        _updatingControls = true;
        var shape = ResolveTargetEditableShape();
        var run = shape?.Paragraphs.SelectMany(p => p.Runs).FirstOrDefault();
        var align = shape?.Paragraphs.FirstOrDefault()?.Alignment ?? DocxAlignment.Left;

        _undoBtn!.IsEnabled = _undoStack.Count > 0;
        _redoBtn!.IsEnabled = _redoStack.Count > 0;
        _boldBtn!.IsChecked = run?.Bold == true;
        _italicBtn!.IsChecked = run?.Italic == true;
        _underlineBtn!.IsChecked = run?.Underline == true;
        _alignLeftBtn!.IsChecked = align == DocxAlignment.Left;
        _alignCenterBtn!.IsChecked = align == DocxAlignment.Center;
        _alignRightBtn!.IsChecked = align == DocxAlignment.Right;
        _updatingControls = false;
    }

    private void PushUndoSnapshot()
    {
        if (_editDoc is null) return;
        _undoStack.Add(_editDoc.TakeSnapshot());
        _redoStack.Clear();
    }

    private void Undo()
    {
        if (_editDoc is null || _undoStack.Count == 0) return;
        _redoStack.Add(_editDoc.TakeSnapshot());
        var snap = _undoStack[^1];
        _undoStack.RemoveAt(_undoStack.Count - 1);
        _doc = _editDoc.Restore(snap);
        RebuildSidebar();
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private void Redo()
    {
        if (_editDoc is null || _redoStack.Count == 0) return;
        _undoStack.Add(_editDoc.TakeSnapshot());
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _doc = _editDoc.Restore(snap);
        RebuildSidebar();
        RebuildSlideStage();
        Changed?.Invoke();
    }

    private void OnStageKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editDoc is null) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (ctrl && e.Key == Key.Z) { Undo(); e.Handled = true; }
        else if (ctrl && e.Key == Key.Y) { Redo(); e.Handled = true; }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * ZoomStep : _zoom / ZoomStep);
        e.Handled = true;
    }

    private void SetZoom(double value)
    {
        _fit = false;
        _zoom = Math.Clamp(value, MinZoom, MaxZoom);
        ApplyZoom();
    }

    private void ApplyZoom()
    {
        if (_fit && _doc.Slides.Count > 0)
        {
            var slide = _doc.Slides[_slideIndex];
            var availW = _stageScroll.Bounds.Width - StageMargin * 2;
            var availH = _stageScroll.Bounds.Height - StageMargin * 2;
            var slideDipW = slide.Width * Dip;
            var slideDipH = slide.Height * Dip;
            if (availW > 80 && availH > 80 && slideDipW > 0 && slideDipH > 0)
                _zoom = Math.Clamp(Math.Min(availW / slideDipW, availH / slideDipH), MinZoom, 1.5);
        }
        _zoomHost.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.01 ? null : new ScaleTransform(_zoom, _zoom);
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
    }

    private static bool IsDarkHex(string hex)
    {
        if (!Color.TryParse(hex, out var c)) return false;
        var lum = 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
        return lum < 128;
    }

    private static bool TryBrush(string hex, out IBrush brush)
    {
        if (Color.TryParse(hex, out var color))
        {
            brush = new ImmutableSolidColorBrush(color);
            return true;
        }
        brush = Brushes.Transparent;
        return false;
    }

    private static Button ToolbarButton(string text, string tip, Action onClick)
    {
        var btn = new Button
        {
            Content = text,
            MinWidth = 28,
            Height = 28,
            Padding = new Thickness(6, 2),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(btn, tip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static ToggleButton ToolbarToggle(Control content, string tip, Action onClick)
    {
        var btn = new ToggleButton
        {
            Content = content,
            MinWidth = 28,
            Height = 28,
            Padding = new Thickness(6, 2),
            Focusable = false,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center
        };
        ToolTip.SetTip(btn, tip);
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static Border Separator() => new()
    {
        Width = 1,
        Height = 18,
        Background = ViewerBar.Border,
        Margin = new Thickness(3, 0)
    };
}
