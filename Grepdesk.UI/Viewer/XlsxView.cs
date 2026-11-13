using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Grepdesk.Core.Viewer;
using Grepdesk.UI.Preview;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// Spreadsheet viewer and editor for Excel (.xlsx) workbooks: sheet tabs,
/// column/row headers, cell selection, formula bar, search highlighting,
/// zoom controls, and in-place cell editing with undo/redo.
/// </summary>
internal sealed class XlsxView : DockPanel, IViewerBody
{
    private const double MinZoom = 0.5, MaxZoom = 3.0, ZoomStep = 1.15;
    private const double RowHeaderWidth = 46;
    private const double ColumnHeaderHeight = 26;
    private const double DefaultRowHeight = 26;
    private const int MinVisibleRows = 30;
    private const int MinVisibleCols = 12;
    private const int MaxRenderedRows = 500;
    private const int MaxRenderedCols = 60;

    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly IBrush HeaderBg = new ImmutableSolidColorBrush(Color.Parse("#f1f3f4"));
    private static readonly IBrush HeaderText = new ImmutableSolidColorBrush(Color.Parse("#4d5156"));
    private static readonly IBrush HeaderActiveBg = new ImmutableSolidColorBrush(Color.Parse("#d3e3fd"));
    private static readonly IBrush CellBg = new ImmutableSolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush AltCellBg = new ImmutableSolidColorBrush(Color.Parse("#ffffff"));
    private static readonly IBrush GridLineBrush = new ImmutableSolidColorBrush(Color.Parse("#d4d4d4"));
    private static readonly IBrush SelectedBorderBrush = new ImmutableSolidColorBrush(Color.Parse("#1a73e8"));
    private static readonly IBrush SelectedBgBrush = new ImmutableSolidColorBrush(Color.Parse("#1a73e8"), 0.12);
    private static readonly IBrush MatchBgBrush = new ImmutableSolidColorBrush(Color.Parse("#ffd400"), 0.55);
    private static readonly IBrush DefaultTextBrush = new ImmutableSolidColorBrush(Color.Parse("#1f1f1f"));
    private static readonly IBrush FormulaBadgeBrush = new ImmutableSolidColorBrush(Color.Parse("#a6e3a1"));

    private XlsxDocument _doc;
    private readonly XlsxEditDocument? _editDoc;
    private readonly string? _highlight;
    private readonly List<XlsxEditDocument.Snapshot> _undoStack = [];
    private readonly List<XlsxEditDocument.Snapshot> _redoStack = [];

    private int _sheetIndex;
    private int _selectedRow;
    private int _selectedCol;
    private double _zoom = 1.0;
    private bool _updatingFormulaBar;

    private readonly TextBlock _cellAddressText;
    private readonly TextBox _formulaBox;
    private readonly Button? _undoBtn, _redoBtn;
    private readonly ToggleButton? _boldBtn, _italicBtn, _underlineBtn, _alignLeftBtn, _alignCenterBtn, _alignRightBtn;
    private readonly ScrollViewer _scroll;
    private readonly LayoutTransformControl _zoomHost;
    private readonly StackPanel _sheetTabsPanel;
    private readonly TextBlock _zoomText;
    private readonly Dictionary<(int Row, int Col), Border> _cellBorders = new();
    private readonly Dictionary<int, Border> _rowHeaderBorders = new();
    private readonly Dictionary<int, Border> _colHeaderBorders = new();

    public event Action? Changed;

    public XlsxView(XlsxDocument document, string? highlight, XlsxEditDocument? editDocument = null)
    {
        _doc = document;
        _editDoc = editDocument;
        _highlight = string.IsNullOrEmpty(highlight) ? null : highlight;

        // Top formula & formatting bar
        var topBar = new DockPanel { LastChildFill = true };

        if (_editDoc is not null)
        {
            _undoBtn = ToolbarButton("↶", Loc.Get("DocxUndo"), Undo);
            _redoBtn = ToolbarButton("↷", Loc.Get("DocxRedo"), Redo);
            _boldBtn = ToolbarToggle(new TextBlock { Text = "B", FontWeight = FontWeight.Bold }, Loc.Get("DocxBold"),
                () => ApplyStyle(new XlsxStyleChange.Bold(_boldBtn!.IsChecked == true)));
            _italicBtn = ToolbarToggle(new TextBlock { Text = "I", FontStyle = FontStyle.Italic }, Loc.Get("DocxItalic"),
                () => ApplyStyle(new XlsxStyleChange.Italic(_italicBtn!.IsChecked == true)));
            _underlineBtn = ToolbarToggle(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, Loc.Get("DocxUnderline"),
                () => ApplyStyle(new XlsxStyleChange.Underline(_underlineBtn!.IsChecked == true)));
            _alignLeftBtn = ToolbarToggle(new TextBlock { Text = "⇤", FontSize = 11 }, Loc.Get("DocxAlignLeft"),
                () => ApplyStyle(new XlsxStyleChange.Alignment(DocxAlignment.Left)));
            _alignCenterBtn = ToolbarToggle(new TextBlock { Text = "↔", FontSize = 11 }, Loc.Get("DocxAlignCenter"),
                () => ApplyStyle(new XlsxStyleChange.Alignment(DocxAlignment.Center)));
            _alignRightBtn = ToolbarToggle(new TextBlock { Text = "⇥", FontSize = 11 }, Loc.Get("DocxAlignRight"),
                () => ApplyStyle(new XlsxStyleChange.Alignment(DocxAlignment.Right)));

            var editTools = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 4,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Children = { _undoBtn, _redoBtn, Separator(), _boldBtn, _italicBtn, _underlineBtn, Separator(), _alignLeftBtn, _alignCenterBtn, _alignRightBtn, Separator() }
            };
            SetDock(editTools, Dock.Left);
            topBar.Children.Add(editTools);
        }

        _cellAddressText = new TextBlock
        {
            Text = "A1",
            FontFamily = PreviewPane.Monospace,
            FontSize = 12,
            FontWeight = FontWeight.SemiBold,
            Foreground = ViewerBar.Text,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            MinWidth = 54,
        };
        var addressBadge = new Border
        {
            Background = Brush.Parse("#11111b"),
            BorderBrush = ViewerBar.Border,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(8, 4),
            Margin = new Thickness(0, 0, 8, 0),
            Child = _cellAddressText,
            [DockProperty] = Dock.Left,
        };
        topBar.Children.Add(addressBadge);

        var fxLabel = new TextBlock
        {
            Text = "fx",
            FontStyle = FontStyle.Italic,
            FontWeight = FontWeight.SemiBold,
            Foreground = ViewerBar.Muted,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
            [DockProperty] = Dock.Left,
        };
        topBar.Children.Add(fxLabel);

        _formulaBox = new TextBox
        {
            IsReadOnly = _editDoc is null,
            FontFamily = PreviewPane.Monospace,
            FontSize = 12,
            Watermark = _editDoc is not null ? Loc.Get("XlsxFormulaHint") : "",
            VerticalAlignment = VerticalAlignment.Center,
        };
        _formulaBox.KeyDown += OnFormulaBoxKeyDown;
        _formulaBox.LostFocus += (_, _) =>
        {
            if (_editDoc is not null && !_updatingFormulaBar)
                CommitFormulaBoxEdit();
        };
        topBar.Children.Add(_formulaBox);

        var topContainer = new Border
        {
            Background = ViewerBar.Background,
            BorderBrush = ViewerBar.Border,
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(10, 6),
            Child = topBar,
            [DockProperty] = Dock.Top,
        };
        Children.Add(topContainer);

        // Bottom bar: sheet tabs + zoom
        _sheetTabsPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Center
        };
        var tabsScroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = _sheetTabsPanel
        };

        var zoomOut = ViewerBar.Button("−", Loc.Get("ViewerZoomOut"));
        var zoomIn = ViewerBar.Button("+", Loc.Get("ViewerZoomIn"));
        var actual = ViewerBar.Button("100%", Loc.Get("ViewerZoomActual"));
        _zoomText = ViewerBar.Label("100%", 48);
        zoomOut.Click += (_, _) => SetZoom(_zoom / ZoomStep);
        zoomIn.Click += (_, _) => SetZoom(_zoom * ZoomStep);
        actual.Click += (_, _) => SetZoom(1.0);

        var zoomRow = ViewerBar.Row(zoomOut, _zoomText, zoomIn, actual);
        SetDock(zoomRow, Dock.Right);

        var bottomDock = new DockPanel { LastChildFill = true };
        bottomDock.Children.Add(zoomRow);
        bottomDock.Children.Add(tabsScroll);
        Children.Add(ViewerBar.Create(bottomDock));

        // Main scrollable spreadsheet area
        _zoomHost = new LayoutTransformControl();
        _scroll = new ScrollViewer
        {
            Content = _zoomHost,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = Brush.Parse("#e8eaed"),
            Focusable = true,
        };
        _scroll.AddHandler(PointerWheelChangedEvent, OnWheel, RoutingStrategies.Tunnel);
        _scroll.KeyDown += OnGridKeyDown;
        Children.Add(_scroll);

        FindFirstHighlightMatch();
        RebuildSheetTabs();
        RebuildGrid();
        UpdateSelectionVisuals();
    }

    public bool IsEditing => _editDoc is not null;

    public void FocusContent() => _scroll.Focus();

    public bool HandleKey(KeyEventArgs e)
    {
        if (e.Key is Key.OemPlus or Key.Add) { SetZoom(_zoom * ZoomStep); return true; }
        if (e.Key is Key.OemMinus or Key.Subtract) { SetZoom(_zoom / ZoomStep); return true; }
        if (e.Key is Key.D0 or Key.NumPad0) { SetZoom(1.0); return true; }
        return false;
    }

    public void Dispose() { }

    private void FindFirstHighlightMatch()
    {
        if (_highlight is null) return;
        for (var s = 0; s < _doc.Sheets.Count; s++)
        {
            var sheet = _doc.Sheets[s];
            foreach (var ((r, c), cell) in sheet.Cells.OrderBy(kv => kv.Key.Row).ThenBy(kv => kv.Key.Column))
            {
                if (cell.DisplayText.Contains(_highlight, StringComparison.OrdinalIgnoreCase))
                {
                    _sheetIndex = s;
                    _selectedRow = r;
                    _selectedCol = c;
                    return;
                }
            }
        }
    }

    private void RebuildSheetTabs()
    {
        _sheetTabsPanel.Children.Clear();
        for (var i = 0; i < _doc.Sheets.Count; i++)
        {
            var idx = i;
            var sheet = _doc.Sheets[i];
            var isSelected = i == _sheetIndex;
            var btn = new Button
            {
                Content = sheet.Name,
                FontSize = 12,
                FontWeight = isSelected ? FontWeight.SemiBold : FontWeight.Normal,
                Background = isSelected ? Brush.Parse("#313244") : Brushes.Transparent,
                BorderBrush = isSelected ? SelectedBorderBrush : ViewerBar.Border,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 4),
            };
            btn.Click += (_, _) =>
            {
                if (_sheetIndex == idx) return;
                _sheetIndex = idx;
                _selectedRow = 0;
                _selectedCol = 0;
                RebuildSheetTabs();
                RebuildGrid();
                UpdateSelectionVisuals();
            };
            _sheetTabsPanel.Children.Add(btn);
        }
    }

    private void RebuildGrid()
    {
        _cellBorders.Clear();
        _rowHeaderBorders.Clear();
        _colHeaderBorders.Clear();

        if (_doc.Sheets.Count == 0) return;
        var sheet = _doc.Sheets[_sheetIndex];

        var rows = Math.Clamp(Math.Max(sheet.RowCount + (_editDoc is not null ? 10 : 2), MinVisibleRows), 1, MaxRenderedRows);
        var cols = Math.Clamp(Math.Max(sheet.ColumnCount + (_editDoc is not null ? 4 : 1), MinVisibleCols), 1, MaxRenderedCols);

        var grid = new Grid
        {
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            Background = GridLineBrush,
        };

        grid.ColumnDefinitions.Add(new ColumnDefinition(RowHeaderWidth, GridUnitType.Pixel));
        for (var c = 0; c < cols; c++)
        {
            var width = c < sheet.ColumnWidths.Count ? sheet.ColumnWidths[c] : 84.0;
            grid.ColumnDefinitions.Add(new ColumnDefinition(width, GridUnitType.Pixel));
        }

        grid.RowDefinitions.Add(new RowDefinition(ColumnHeaderHeight, GridUnitType.Pixel));
        for (var r = 0; r < rows; r++)
            grid.RowDefinitions.Add(new RowDefinition(DefaultRowHeight, GridUnitType.Pixel));

        // Top-left corner
        var corner = new Border
        {
            Background = HeaderBg,
            BorderBrush = GridLineBrush,
            BorderThickness = new Thickness(0, 0, 1, 1)
        };
        Grid.SetRow(corner, 0);
        Grid.SetColumn(corner, 0);
        grid.Children.Add(corner);

        // Column headers
        for (var c = 0; c < cols; c++)
        {
            var colHeader = new Border
            {
                Background = HeaderBg,
                BorderBrush = GridLineBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = XlsxDocument.ColumnName(c),
                    Foreground = HeaderText,
                    FontSize = 11.5,
                    FontWeight = FontWeight.SemiBold,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
            Grid.SetRow(colHeader, 0);
            Grid.SetColumn(colHeader, c + 1);
            grid.Children.Add(colHeader);
            _colHeaderBorders[c] = colHeader;
        }

        // Track cells covered by a merge so we don't draw duplicate borders over them
        var covered = new HashSet<(int Row, int Col)>();
        foreach (var merge in sheet.Merges)
        {
            for (var r = merge.StartRow; r <= merge.EndRow; r++)
                for (var c = merge.StartColumn; c <= merge.EndColumn; c++)
                    if (r != merge.StartRow || c != merge.StartColumn)
                        covered.Add((r, c));
        }

        // Row headers and data cells
        for (var r = 0; r < rows; r++)
        {
            var rowHeader = new Border
            {
                Background = HeaderBg,
                BorderBrush = GridLineBrush,
                BorderThickness = new Thickness(0, 0, 1, 1),
                Child = new TextBlock
                {
                    Text = (r + 1).ToString(CultureInfo.InvariantCulture),
                    Foreground = HeaderText,
                    FontSize = 11,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                }
            };
            Grid.SetRow(rowHeader, r + 1);
            Grid.SetColumn(rowHeader, 0);
            grid.Children.Add(rowHeader);
            _rowHeaderBorders[r] = rowHeader;

            for (var c = 0; c < cols; c++)
            {
                if (covered.Contains((r, c))) continue;

                var cell = sheet.GetCell(r, c);
                var border = CreateCellControl(r, c, cell);
                Grid.SetRow(border, r + 1);
                Grid.SetColumn(border, c + 1);
                if (cell is { RowSpan: > 1 })
                    Grid.SetRowSpan(border, Math.Min(cell.RowSpan, rows - r));
                if (cell is { ColumnSpan: > 1 })
                    Grid.SetColumnSpan(border, Math.Min(cell.ColumnSpan, cols - c));

                grid.Children.Add(border);
                _cellBorders[(r, c)] = border;
            }
        }

        _zoomHost.Child = grid;
    }

    private Border CreateCellControl(int row, int col, XlsxCell? cell)
    {
        var text = cell?.DisplayText ?? "";
        var isMatch = _highlight is not null && text.Length > 0 && text.Contains(_highlight, StringComparison.OrdinalIgnoreCase);

        IBrush bg = isMatch
            ? MatchBgBrush
            : cell?.Style.FillColor is { } fillHex && TryBrush(fillHex, out var fillBrush)
                ? fillBrush
                : (row % 2 == 0 ? CellBg : AltCellBg);

        IBrush fg = cell?.Style.FontColor is { } fontHex && TryBrush(fontHex, out var fontBrush)
            ? fontBrush
            : DefaultTextBrush;

        var hAlign = (cell?.Style.Alignment ?? DocxAlignment.Left) switch
        {
            DocxAlignment.Center => HorizontalAlignment.Center,
            DocxAlignment.Right => HorizontalAlignment.Right,
            _ => HorizontalAlignment.Left
        };

        var tb = new TextBlock
        {
            Text = text,
            Foreground = fg,
            FontSize = Math.Clamp(cell?.Style.FontSize ?? 11.5, 9, 24),
            FontWeight = cell?.Style.Bold == true ? FontWeight.Bold : FontWeight.Normal,
            FontStyle = cell?.Style.Italic == true ? FontStyle.Italic : FontStyle.Normal,
            TextDecorations = cell?.Style.Underline == true ? TextDecorations.Underline : null,
            HorizontalAlignment = hAlign,
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = cell?.Style.WrapText == true ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new Thickness(6, 2),
        };

        var border = new Border
        {
            Background = bg,
            BorderBrush = GridLineBrush,
            BorderThickness = new Thickness(0, 0, 1, 1),
            Child = tb,
            Tag = bg,
        };

        border.PointerPressed += (_, e) =>
        {
            SelectCell(row, col);
            if (e.ClickCount == 2 && _editDoc is not null)
            {
                _formulaBox.Focus();
                _formulaBox.SelectAll();
            }
            else
            {
                _scroll.Focus();
            }
            e.Handled = true;
        };

        return border;
    }

    private void SelectCell(int row, int col)
    {
        var sheet = _doc.Sheets[_sheetIndex];
        var maxR = Math.Clamp(Math.Max(sheet.RowCount + (_editDoc is not null ? 10 : 2), MinVisibleRows), 1, MaxRenderedRows) - 1;
        var maxC = Math.Clamp(Math.Max(sheet.ColumnCount + (_editDoc is not null ? 4 : 1), MinVisibleCols), 1, MaxRenderedCols) - 1;

        var prevRow = _selectedRow;
        var prevCol = _selectedCol;
        _selectedRow = Math.Clamp(row, 0, maxR);
        _selectedCol = Math.Clamp(col, 0, maxC);

        if (_cellBorders.TryGetValue((prevRow, prevCol), out var prevBorder))
        {
            prevBorder.BorderBrush = GridLineBrush;
            prevBorder.BorderThickness = new Thickness(0, 0, 1, 1);
            if (prevBorder.Tag is IBrush origBg) prevBorder.Background = origBg;
        }
        if (_rowHeaderBorders.TryGetValue(prevRow, out var prevRH)) prevRH.Background = HeaderBg;
        if (_colHeaderBorders.TryGetValue(prevCol, out var prevCH)) prevCH.Background = HeaderBg;

        UpdateSelectionVisuals();
    }

    private void UpdateSelectionVisuals()
    {
        if (_doc.Sheets.Count == 0) return;
        var sheet = _doc.Sheets[_sheetIndex];
        var cell = sheet.GetCell(_selectedRow, _selectedCol);

        _cellAddressText.Text = XlsxDocument.CellReference(_selectedRow, _selectedCol);
        _updatingFormulaBar = true;
        _formulaBox.Text = cell?.FormulaBarText ?? "";
        _updatingFormulaBar = false;

        if (_cellBorders.TryGetValue((_selectedRow, _selectedCol), out var curBorder))
        {
            curBorder.BorderBrush = SelectedBorderBrush;
            curBorder.BorderThickness = new Thickness(2);
            curBorder.Background = SelectedBgBrush;
        }
        if (_rowHeaderBorders.TryGetValue(_selectedRow, out var curRH)) curRH.Background = HeaderActiveBg;
        if (_colHeaderBorders.TryGetValue(_selectedCol, out var curCH)) curCH.Background = HeaderActiveBg;

        if (_editDoc is not null)
        {
            _updatingFormulaBar = true;
            _boldBtn!.IsChecked = cell?.Style.Bold == true;
            _italicBtn!.IsChecked = cell?.Style.Italic == true;
            _underlineBtn!.IsChecked = cell?.Style.Underline == true;
            var align = cell?.Style.Alignment ?? DocxAlignment.Left;
            _alignLeftBtn!.IsChecked = align == DocxAlignment.Left;
            _alignCenterBtn!.IsChecked = align == DocxAlignment.Center;
            _alignRightBtn!.IsChecked = align == DocxAlignment.Right;
            _undoBtn!.IsEnabled = _undoStack.Count > 0;
            _redoBtn!.IsEnabled = _redoStack.Count > 0;
            _updatingFormulaBar = false;
        }
    }

    private void CommitFormulaBoxEdit()
    {
        if (_editDoc is null || _doc.Sheets.Count == 0) return;
        var sheet = _doc.Sheets[_sheetIndex];
        var existing = sheet.GetCell(_selectedRow, _selectedCol)?.FormulaBarText ?? "";
        var next = _formulaBox.Text ?? "";
        if (string.Equals(existing, next, StringComparison.Ordinal)) return;

        PushUndoSnapshot();
        _doc = _editDoc.SetCell(_sheetIndex, _selectedRow, _selectedCol, next);
        RebuildGrid();
        UpdateSelectionVisuals();
        Changed?.Invoke();
    }

    private void ApplyStyle(XlsxStyleChange change)
    {
        if (_editDoc is null || _updatingFormulaBar) return;
        PushUndoSnapshot();
        _doc = _editDoc.FormatCells(_sheetIndex, _selectedRow, _selectedCol, _selectedRow, _selectedCol, change);
        RebuildGrid();
        UpdateSelectionVisuals();
        Changed?.Invoke();
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
        RebuildGrid();
        UpdateSelectionVisuals();
        Changed?.Invoke();
    }

    private void Redo()
    {
        if (_editDoc is null || _redoStack.Count == 0) return;
        _undoStack.Add(_editDoc.TakeSnapshot());
        var snap = _redoStack[^1];
        _redoStack.RemoveAt(_redoStack.Count - 1);
        _doc = _editDoc.Restore(snap);
        RebuildGrid();
        UpdateSelectionVisuals();
        Changed?.Invoke();
    }

    private void OnFormulaBoxKeyDown(object? sender, KeyEventArgs e)
    {
        if (_editDoc is null) return;
        if (e.Key == Key.Enter)
        {
            CommitFormulaBoxEdit();
            SelectCell(_selectedRow + 1, _selectedCol);
            _scroll.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Tab)
        {
            CommitFormulaBoxEdit();
            SelectCell(_selectedRow, _selectedCol + (e.KeyModifiers.HasFlag(KeyModifiers.Shift) ? -1 : 1));
            _scroll.Focus();
            e.Handled = true;
        }
        else if (e.Key == Key.Escape)
        {
            UpdateSelectionVisuals();
            _scroll.Focus();
            e.Handled = true;
        }
    }

    private async void OnGridKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);

        if (ctrl && e.Key == Key.C)
        {
            var text = _doc.Sheets[_sheetIndex].GetCell(_selectedRow, _selectedCol)?.DisplayText ?? "";
            if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard)
                await clipboard.SetTextAsync(text);
            e.Handled = true;
            return;
        }

        if (_editDoc is not null && ctrl && e.Key == Key.Z)
        {
            Undo();
            e.Handled = true;
            return;
        }
        if (_editDoc is not null && ctrl && e.Key == Key.Y)
        {
            Redo();
            e.Handled = true;
            return;
        }
        if (_editDoc is not null && ctrl && e.Key == Key.B)
        {
            var on = _doc.Sheets[_sheetIndex].GetCell(_selectedRow, _selectedCol)?.Style.Bold != true;
            ApplyStyle(new XlsxStyleChange.Bold(on));
            e.Handled = true;
            return;
        }
        if (_editDoc is not null && ctrl && e.Key == Key.I)
        {
            var on = _doc.Sheets[_sheetIndex].GetCell(_selectedRow, _selectedCol)?.Style.Italic != true;
            ApplyStyle(new XlsxStyleChange.Italic(on));
            e.Handled = true;
            return;
        }
        if (_editDoc is not null && ctrl && e.Key == Key.U)
        {
            var on = _doc.Sheets[_sheetIndex].GetCell(_selectedRow, _selectedCol)?.Style.Underline != true;
            ApplyStyle(new XlsxStyleChange.Underline(on));
            e.Handled = true;
            return;
        }

        switch (e.Key)
        {
            case Key.Up:
                SelectCell(_selectedRow - 1, _selectedCol);
                e.Handled = true;
                break;
            case Key.Down:
            case Key.Enter when _editDoc is null:
                SelectCell(_selectedRow + 1, _selectedCol);
                e.Handled = true;
                break;
            case Key.Enter when _editDoc is not null:
                _formulaBox.Focus();
                _formulaBox.SelectAll();
                e.Handled = true;
                break;
            case Key.Left:
                SelectCell(_selectedRow, _selectedCol - 1);
                e.Handled = true;
                break;
            case Key.Right:
                SelectCell(_selectedRow, _selectedCol + 1);
                e.Handled = true;
                break;
            case Key.Tab:
                SelectCell(_selectedRow, _selectedCol + (shift ? -1 : 1));
                e.Handled = true;
                break;
            case Key.F2 when _editDoc is not null:
                _formulaBox.Focus();
                _formulaBox.CaretIndex = _formulaBox.Text?.Length ?? 0;
                e.Handled = true;
                break;
            case Key.Delete or Key.Back when _editDoc is not null:
                _formulaBox.Text = "";
                CommitFormulaBoxEdit();
                e.Handled = true;
                break;
        }
    }

    private void OnWheel(object? sender, PointerWheelEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        SetZoom(e.Delta.Y > 0 ? _zoom * ZoomStep : _zoom / ZoomStep);
        e.Handled = true;
    }

    private void SetZoom(double value)
    {
        _zoom = Math.Clamp(value, MinZoom, MaxZoom);
        _zoomHost.LayoutTransform = Math.Abs(_zoom - 1.0) < 0.01 ? null : new ScaleTransform(_zoom, _zoom);
        _zoomText.Text = $"{Math.Round(_zoom * 100)}%";
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
