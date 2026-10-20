using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// The Word editor's formatting bar: undo, paragraph style, font and size,
/// bold/italic/underline/strikethrough, colors, alignment, lists and indent.
/// Buttons don't take the keyboard, so typing goes on where it was.
/// </summary>
internal sealed class DocxToolbar : Border
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly double[] Sizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];

    private static readonly (string Hex, string Name)[] TextColors =
    [
        ("#000000", "Black"), ("#7F7F7F", "Gray"), ("#C00000", "Dark red"), ("#FF0000", "Red"), ("#FFC000", "Orange"),
        ("#FFFF00", "Yellow"), ("#92D050", "Light green"), ("#00B050", "Green"), ("#00B0F0", "Light blue"),
        ("#0070C0", "Blue"), ("#002060", "Dark blue"), ("#7030A0", "Purple"),
    ];

    private static readonly (string Name, string Hex)[] Highlights =
    [
        ("yellow", "#FFFF00"), ("green", "#00FF00"), ("cyan", "#00FFFF"), ("magenta", "#FF00FF"), ("blue", "#0000FF"),
        ("red", "#FF0000"), ("darkBlue", "#000080"), ("darkCyan", "#008080"), ("darkGreen", "#008000"),
        ("darkMagenta", "#800080"), ("darkRed", "#800000"), ("darkYellow", "#808000"), ("darkGray", "#808080"),
        ("lightGray", "#C0C0C0"), ("black", "#000000"),
    ];

    private readonly DocxEditor _editor;
    private readonly Button _undo, _redo;
    private readonly ComboBox _style, _font, _size;
    private readonly ToggleButton _bold, _italic, _underline, _strike, _left, _center, _right, _justify, _bullets, _numbers;
    private List<DocxStyleInfo> _styles = [];
    private List<string> _fonts = [];
    private bool _updating;

    public DocxToolbar(DocxEditor editor)
    {
        _editor = editor;
        Background = Brush.Parse("#181825");
        BorderBrush = Brush.Parse("#313244");
        BorderThickness = new Thickness(0, 0, 0, 1);
        Padding = new Thickness(10, 6);

        _undo = IconButton("↶", "DocxUndo", editor.Undo);
        _redo = IconButton("↷", "DocxRedo", editor.Redo);

        _style = Combo(170, "DocxStyle");
        _style.SelectionChanged += (_, _) =>
        {
            if (_updating || _style.SelectedIndex < 0 || _style.SelectedIndex >= _styles.Count) return;
            editor.SetStyle(_styles[_style.SelectedIndex]);
            editor.FocusText();
        };
        _font = Combo(150, "DocxFont");
        _font.SelectionChanged += (_, _) =>
        {
            if (_updating || _font.SelectedItem is not string name) return;
            editor.FormatText(new DocxRunChange.Font(name));
            editor.FocusText();
        };
        _size = Combo(66, "DocxFontSize");
        _size.ItemsSource = Sizes.Select(Label).ToList();
        _size.SelectionChanged += (_, _) =>
        {
            if (_updating || _size.SelectedItem is not string text ||
                !double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var size)) return;
            editor.FormatText(new DocxRunChange.Size(size));
            editor.FocusText();
        };

        _bold = Toggle(new TextBlock { Text = "B", FontWeight = FontWeight.Bold }, "DocxBold", editor.ToggleBold);
        _italic = Toggle(new TextBlock { Text = "I", FontStyle = FontStyle.Italic, FontFamily = new FontFamily("Times New Roman, serif") }, "DocxItalic", editor.ToggleItalic);
        _underline = Toggle(new TextBlock { Text = "U", TextDecorations = TextDecorations.Underline }, "DocxUnderline", editor.ToggleUnderline);
        _strike = Toggle(new TextBlock { Text = "S", TextDecorations = TextDecorations.Strikethrough }, "DocxStrike", editor.ToggleStrike);

        var color = ColorButton("A", "DocxTextColor",
            [(Loc.Get("DocxAutomatic"), null), .. TextColors.Select(c => ((string, string?))(c.Name, c.Hex))],
            hex => editor.FormatText(new DocxRunChange.Color(hex)));
        var highlight = ColorButton("ab", "DocxHighlight",
            [(Loc.Get("DocxNoHighlight"), null), .. Highlights.Select(h => ((string, string?))(h.Name, h.Hex))],
            hex => editor.FormatText(new DocxRunChange.Highlight(hex is null ? null : Highlights.First(h => h.Hex == hex).Name)),
            highlightSwatch: true);

        _left = Toggle(Icon("M1,2h14v1.6H1z M1,6h9v1.6H1z M1,10h14v1.6H1z M1,14h9v1.6H1z"), "DocxAlignLeft", () => editor.SetAlignment(DocxAlignment.Left));
        _center = Toggle(Icon("M1,2h14v1.6H1z M3.5,6h9v1.6h-9z M1,10h14v1.6H1z M3.5,14h9v1.6h-9z"), "DocxAlignCenter", () => editor.SetAlignment(DocxAlignment.Center));
        _right = Toggle(Icon("M1,2h14v1.6H1z M6,6h9v1.6H6z M1,10h14v1.6H1z M6,14h9v1.6H6z"), "DocxAlignRight", () => editor.SetAlignment(DocxAlignment.Right));
        _justify = Toggle(Icon("M1,2h14v1.6H1z M1,6h14v1.6H1z M1,10h14v1.6H1z M1,14h14v1.6H1z"), "DocxJustify", () => editor.SetAlignment(DocxAlignment.Justify));

        _bullets = Toggle(Icon("M1,2.8a1.3,1.3 0 1 0 2.6,0a1.3,1.3 0 1 0 -2.6,0z M5.5,2h9.5v1.6H5.5z M1,8a1.3,1.3 0 1 0 2.6,0a1.3,1.3 0 1 0 -2.6,0z M5.5,7.2h9.5v1.6H5.5z M1,13.2a1.3,1.3 0 1 0 2.6,0a1.3,1.3 0 1 0 -2.6,0z M5.5,12.4h9.5v1.6H5.5z"),
            "DocxBullets", () => editor.ToggleList(numbered: false));
        _numbers = Toggle(new TextBlock { Text = "1≡", FontSize = 12 }, "DocxNumbering", () => editor.ToggleList(numbered: true));
        var outdent = IconButton(Icon("M1,2h14v1.6H1z M7,6h8v1.6H7z M7,10h8v1.6H7z M1,14h14v1.6H1z M5,6.3v4.4L1.6,8.5z"), "DocxIndentLess", () => editor.Indent(-1));
        var indent = IconButton(Icon("M1,2h14v1.6H1z M7,6h8v1.6H7z M7,10h8v1.6H7z M1,14h14v1.6H1z M1.6,6.3v4.4L5,8.5z"), "DocxIndentMore", () => editor.Indent(1));
        var clear = IconButton(new TextBlock { Text = "A✕", FontSize = 11 }, "DocxClearFormatting", () => editor.FormatText(new DocxRunChange.Clear()));

        Child = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Group(_undo, _redo),
                Group(_style),
                Group(_font, _size),
                Group(_bold, _italic, _underline, _strike, color, highlight, clear),
                Group(_left, _center, _right, _justify),
                Group(_bullets, _numbers, outdent, indent),
            },
        };
    }

    private static string Label(double size) => size.ToString("0.#", CultureInfo.CurrentCulture);

    // =====================================================================
    // State
    // =====================================================================

    public void Update(DocxEditorState state)
    {
        _updating = true;
        try
        {
            _undo.IsEnabled = state.CanUndo;
            _redo.IsEnabled = state.CanRedo;
            _bold.IsChecked = state.Bold;
            _italic.IsChecked = state.Italic;
            _underline.IsChecked = state.Underline;
            _strike.IsChecked = state.Strike;
            _left.IsChecked = state.Alignment == DocxAlignment.Left;
            _center.IsChecked = state.Alignment == DocxAlignment.Center;
            _right.IsChecked = state.Alignment == DocxAlignment.Right;
            _justify.IsChecked = state.Alignment == DocxAlignment.Justify;
            _bullets.IsChecked = state.List == "bullet";
            _numbers.IsChecked = state.List is { } list && list != "bullet";

            if (_styles.Count == 0 || (state.StyleId is { } id && _styles.All(s => s.Id != id)))
            {
                _styles = _editor.Document.ParagraphStyles.ToList();
                _style.ItemsSource = _styles.Select(StyleName).ToList();
            }
            _style.SelectedIndex = _styles.FindIndex(s => s.Id == state.StyleId);

            if (_fonts.Count == 0) _fonts = _editor.Document.Fonts.ToList();
            if (state.Font is { } font && !_fonts.Contains(font, StringComparer.OrdinalIgnoreCase))
                _fonts.Insert(0, font);
            _font.ItemsSource = null;
            _font.ItemsSource = _fonts;
            _font.SelectedItem = state.Font is null ? null : _fonts.First(f => f.Equals(state.Font, StringComparison.OrdinalIgnoreCase));

            var sizes = Sizes.Select(Label).ToList();
            var size = Label(state.Size);
            if (!sizes.Contains(size)) sizes.Insert(0, size);
            _size.ItemsSource = sizes;
            _size.SelectedItem = size;
        }
        finally
        {
            _updating = false;
        }
    }

    /// <summary>Word's built-in style names, as Word shows them in the user's language.</summary>
    private static string StyleName(DocxStyleInfo style)
    {
        var name = style.Name.Trim();
        var lower = name.ToLowerInvariant();
        if (lower.StartsWith("heading ", StringComparison.Ordinal) && int.TryParse(lower[8..], out var level))
            return Loc.Get("DocxStyleHeading", level);
        return lower switch
        {
            "normal" => Loc.Get("DocxStyleNormal"),
            "title" => Loc.Get("DocxStyleTitle"),
            "subtitle" => Loc.Get("DocxStyleSubtitle"),
            "quote" => Loc.Get("DocxStyleQuote"),
            "list paragraph" => Loc.Get("DocxStyleListParagraph"),
            "no spacing" => Loc.Get("DocxStyleNoSpacing"),
            _ => name.Length > 0 ? char.ToUpper(name[0], CultureInfo.CurrentCulture) + name[1..] : style.Id ?? "",
        };
    }

    // =====================================================================
    // Controls
    // =====================================================================

    private static StackPanel Group(params Control[] children)
    {
        var group = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3, Margin = new Thickness(0, 2, 14, 2), VerticalAlignment = VerticalAlignment.Center };
        group.Children.AddRange(children);
        return group;
    }

    private static PathIcon Icon(string data) => new() { Data = Geometry.Parse(data), Width = 14, Height = 14 };

    private static void Compact(ContentControl control, string tipKey)
    {
        control.Padding = new Thickness(7, 4);
        control.MinWidth = 30;
        control.Height = 28;
        control.Focusable = false;
        control.HorizontalContentAlignment = HorizontalAlignment.Center;
        control.VerticalContentAlignment = VerticalAlignment.Center;
        ToolTip.SetTip(control, Loc.Get(tipKey));
    }

    private static Button IconButton(object content, string tipKey, Action action)
    {
        var button = new Button { Content = content };
        Compact(button, tipKey);
        button.Click += (_, _) => action();
        return button;
    }

    private ToggleButton Toggle(object content, string tipKey, Action action)
    {
        var toggle = new ToggleButton { Content = content };
        Compact(toggle, tipKey);
        // The state comes from the text, not the click: undo the toggle's own flip.
        toggle.Click += (_, _) =>
        {
            if (_updating) return;
            action();
        };
        return toggle;
    }

    private static ComboBox Combo(double width, string tipKey)
    {
        var combo = new ComboBox { Width = width, Height = 28, FontSize = 12, MaxDropDownHeight = 420, VerticalAlignment = VerticalAlignment.Center };
        ToolTip.SetTip(combo, Loc.Get(tipKey));
        return combo;
    }

    private Button ColorButton(string glyph, string tipKey, (string Name, string? Hex)[] colors, Action<string?> apply, bool highlightSwatch = false)
    {
        var bar = new Border { Height = 3, Width = 16, Background = Brush.Parse(highlightSwatch ? "#FFFF00" : "#C00000"), Margin = new Thickness(0, 1, 0, 0) };
        var content = new StackPanel { Children = { new TextBlock { Text = glyph, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Center }, bar } };
        var button = new Button { Content = content };
        Compact(button, tipKey);

        var palette = new WrapPanel { Width = 6 * 28, Margin = new Thickness(4) };
        var flyout = new Flyout { Content = palette, Placement = PlacementMode.BottomEdgeAlignedLeft };
        foreach (var (name, hex) in colors)
        {
            var swatch = new Button
            {
                Width = 24,
                Height = 24,
                Margin = new Thickness(2),
                Padding = default,
                Focusable = false,
                Background = hex is null ? Brushes.Transparent : Brush.Parse(hex),
                BorderBrush = Brush.Parse("#45475a"),
                BorderThickness = new Thickness(1),
                Content = hex is null ? new TextBlock { Text = "∅", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center } : null,
            };
            ToolTip.SetTip(swatch, name);
            swatch.Click += (_, _) =>
            {
                flyout.Hide();
                if (hex is not null) bar.Background = Brush.Parse(hex);
                apply(hex);
                _editor.FocusText();
            };
            palette.Children.Add(swatch);
        }
        button.Flyout = flyout;
        return button;
    }
}
