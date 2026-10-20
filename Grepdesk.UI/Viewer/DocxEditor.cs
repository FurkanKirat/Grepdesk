using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Immutable;
using Avalonia.Media.TextFormatting;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI.Viewer;

/// <summary>What the toolbar shows for the text at the caret or in the selection.</summary>
internal sealed record DocxEditorState(
    bool Bold, bool Italic, bool Underline, bool Strike, double Size, string? Font,
    DocxAlignment Alignment, string? List, string? StyleId, bool CanUndo, bool CanRedo);

/// <summary>
/// Edits a Word document in a <see cref="DocxView"/>: the caret and selection,
/// typing, the keyboard and mouse, the clipboard, formatting, and undo. The
/// document itself (<see cref="DocxEditDocument"/>) does the edits; this keeps
/// the pages in step with them, refreshing only the paragraphs that changed.
/// </summary>
internal sealed class DocxEditor : IDisposable
{
    private const int MaxUndo = 200;

    private static LocalizationService Loc => LocalizationService.Instance;

    private readonly string _path;
    private readonly Dictionary<byte[], Bitmap> _images;
    private readonly Dictionary<DocxEditParagraph, EditableParagraph> _controls = [];
    private readonly Dictionary<DocxEditParagraph, Control> _wrappers = [];
    private readonly Dictionary<DocxRunStyle, GenericTextRunProperties> _runProperties = [];
    private readonly List<(DocxEditDocument.Snapshot Snapshot, DocxPosition Caret, DocxPosition Anchor)> _undo = [];
    private readonly List<(DocxEditDocument.Snapshot Snapshot, DocxPosition Caret, DocxPosition Anchor)> _redo = [];
    private readonly DispatcherTimer _blink;
    private DocxView? _view;
    private DocxPosition _caret, _anchor;
    private DocxFormat? _pending;
    private DocxEditDocument.Clip? _clip;
    private string? _undoGroup;
    private double? _desiredX;
    private bool _caretOn = true, _dragging;
    private EditableParagraph? _dragOrigin;

    /// <param name="images">Pictures decoded so far, which the editor now owns.</param>
    public DocxEditor(DocxEditDocument document, string path, Dictionary<byte[], Bitmap> images)
    {
        Document = document;
        _path = path;
        _images = images;
        Toolbar = new DocxToolbar(this);
        _blink = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(530) };
        _blink.Tick += (_, _) =>
        {
            _caretOn = !_caretOn;
            CaretControl()?.InvalidateVisual();
        };
    }

    public DocxEditDocument Document { get; }

    public DocxToolbar Toolbar { get; }

    public bool IsDirty => Document.IsDirty;

    /// <summary>Raised after every edit and save, for the window's title.</summary>
    public event Action? Changed;

    /// <summary>The first layout's pictures, decoded off the UI thread along with it.</summary>
    public DocxPreview Preview(DocxDocument layout, CancellationToken ct) =>
        DocxPreview.From(layout, Path.GetDirectoryName(_path) ?? "", _images, ct, ownsImages: false);

    public void Attach(DocxView view)
    {
        _view = view;
        view.AddHandler(InputElement.KeyDownEvent, OnKeyDown, RoutingStrategies.Tunnel);
        view.AddHandler(InputElement.TextInputEvent, OnTextInput, RoutingStrategies.Bubble);
        view.Scroll.GotFocus += (_, _) => ShowCaret();
        view.Scroll.LostFocus += (_, _) => CaretControl()?.InvalidateVisual();
        view.Scroll.ContextMenu = BuildContextMenu();
        _blink.Start();
        RaiseState();
    }

    public void Save()
    {
        Document.Save(_path);
        Changed?.Invoke();
    }

    // =====================================================================
    // Building and refreshing the pages
    // =====================================================================

    public void BeginBuild()
    {
        _controls.Clear();
        _wrappers.Clear();
    }

    public EditableParagraph CreateControl(DocxEditParagraph model, DocxParagraph layout) => new(this, model, layout);

    public void Register(DocxEditParagraph model, EditableParagraph control, Control wrapper)
    {
        _controls[model] = control;
        _wrappers[model] = wrapper;
    }

    private void Relayout()
    {
        if (_view is null) return;
        var layout = Document.Layout();
        _view.Rebuild(Preview(layout, default));
    }

    private void Apply(DocxEditResult result, bool keepSelection = false)
    {
        var relayout = result.Relayout || (result.Reformat?.Any(p => p.IsList) ?? false);
        if (!relayout)
        {
            foreach (var p in result.Removed ?? []) RemoveWrapper(p);
            foreach (var (p, after) in result.Inserted ?? [])
                if (!InsertWrapper(p, after)) { relayout = true; break; }
        }
        if (!relayout)
        {
            foreach (var p in result.Reformat ?? []) ReplaceWrapper(p);
            foreach (var p in result.Retext ?? []) if (_controls.TryGetValue(p, out var control)) control.Refresh();
        }
        if (relayout) Relayout();

        if (keepSelection) Select(Clamp(_anchor), Clamp(_caret), fromEdit: true);
        else Select(Clamp(result.Caret), Clamp(result.Caret), fromEdit: true);
        Changed?.Invoke();
    }

    private void RemoveWrapper(DocxEditParagraph p)
    {
        if (_wrappers.Remove(p, out var wrapper) && wrapper.Parent is Panel panel) panel.Children.Remove(wrapper);
        _controls.Remove(p);
    }

    private bool InsertWrapper(DocxEditParagraph p, DocxEditParagraph after)
    {
        if (_view is null || !_wrappers.TryGetValue(after, out var previous) || previous.Parent is not Panel panel) return false;
        var wrapper = _view.BuildParagraph(Document.LayoutParagraph(p));
        panel.Children.Insert(panel.Children.IndexOf(previous) + 1, wrapper);
        return true;
    }

    private void ReplaceWrapper(DocxEditParagraph p)
    {
        if (_view is null || !_wrappers.TryGetValue(p, out var old) || old.Parent is not Panel panel) return;
        var index = panel.Children.IndexOf(old);
        panel.Children.RemoveAt(index);
        panel.Children.Insert(index, _view.BuildParagraph(Document.LayoutParagraph(p)));
    }

    // =====================================================================
    // Text layout of an editable paragraph
    // =====================================================================

    internal TextLayout CreateLayout(DocxEditParagraph p, DocxAlignment alignment, DocxRunStyle mark, double firstLine, double width)
    {
        var runs = new List<(int Start, TextRun Run)>();
        var position = 0;
        if (firstLine > 0)
        {
            runs.Add((0, new SpacerRun(firstLine, Properties(mark))));
            position = 1;
        }
        foreach (var run in p.Runs)
        {
            var properties = Properties(run.Style);
            TextRun textRun = run.Atom is { } atom
                ? AtomRun(atom, run.Style, properties, width)
                : new TextCharacters(run.Text.Replace(DocxEditParagraph.LineBreakChar, '\n').AsMemory(), properties);
            runs.Add((position, textRun));
            position += run.Text.Length;
        }

        var paragraph = new GenericTextParagraphProperties(FlowDirection.LeftToRight,
            alignment switch
            {
                DocxAlignment.Center => TextAlignment.Center,
                DocxAlignment.Right => TextAlignment.Right,
                _ => TextAlignment.Left,
            },
            true, false, Properties(mark), TextWrapping.Wrap, double.NaN, 0, 0);
        var layout = new TextLayout(new RunSource(runs, position), paragraph, null, Math.Max(1, width));
        if (alignment == DocxAlignment.Justify) WordJustification.Apply(layout);
        return layout;
    }

    private GenericTextRunProperties Properties(DocxRunStyle style)
    {
        if (_runProperties.TryGetValue(style, out var known)) return known;
        var typeface = new Typeface(DocxView.Font(style.Font) ?? FontFamily.Default,
            style.Italic ? FontStyle.Italic : FontStyle.Normal, style.Bold ? FontWeight.Bold : FontWeight.Normal);
        TextDecorationCollection? decorations = null;
        if (style.Underline || style.Strike)
        {
            decorations = [];
            if (style.Underline) decorations.AddRange(TextDecorations.Underline);
            if (style.Strike) decorations.AddRange(TextDecorations.Strikethrough);
        }
        var size = style.Size * DocxView.Dip * (style.Baseline == DocxBaseline.Normal ? 1 : 0.65);
        var baseline = style.Baseline switch
        {
            DocxBaseline.Superscript => BaselineAlignment.Superscript,
            DocxBaseline.Subscript => BaselineAlignment.Subscript,
            _ => BaselineAlignment.Baseline,
        };
        return _runProperties[style] = new GenericTextRunProperties(typeface, Math.Max(1, size), decorations,
            DocxView.BrushOf(style.Color) ?? DocxView.Ink, DocxView.BrushOf(style.Highlight), baseline);
    }

    private TextRun AtomRun(DocxAtom atom, DocxRunStyle style, GenericTextRunProperties properties, double width)
    {
        if (atom is { Kind: DocxAtomKind.Image, Image: { } data } && DocxPreview.Decode(data, _images) is { } bitmap)
        {
            var size = atom.Width > 0 && atom.Height > 0
                ? new Size(atom.Width * DocxView.Dip, atom.Height * DocxView.Dip)
                : bitmap.Size;
            if (size.Width > width && width > 0) size = new Size(width, size.Height * width / size.Width);
            return new ImageRun(bitmap, size, properties);
        }
        var label = atom.Kind == DocxAtomKind.PageBreak ? Loc.Get("DocxPageBreak") : atom.Label;
        var brush = atom.Kind == DocxAtomKind.PageBreak ? Brush.Parse("#8a8a8a") : properties.ForegroundBrush;
        return new LabelRun(label, properties, brush ?? DocxView.Ink);
    }

    /// <summary>The paragraph's runs, served from any position as Avalonia's formatter asks.</summary>
    private sealed class RunSource(List<(int Start, TextRun Run)> runs, int length) : ITextSource
    {
        public TextRun? GetTextRun(int index)
        {
            if (index >= length) return new TextEndOfParagraph();
            var lo = 0;
            var hi = runs.Count - 1;
            while (lo < hi)
            {
                var mid = (lo + hi + 1) / 2;
                if (runs[mid].Start <= index) lo = mid;
                else hi = mid - 1;
            }
            var (start, run) = runs[lo];
            return run is TextCharacters text && index > start
                ? new TextCharacters(text.Text[(index - start)..], text.Properties)
                : run;
        }
    }

    private sealed class SpacerRun(double width, TextRunProperties properties) : DrawableTextRun
    {
        public override Size Size => new(width, 1);
        public override double Baseline => 1;
        public override TextRunProperties Properties => properties;
        public override void Draw(DrawingContext drawingContext, Point origin) { }
    }

    private sealed class ImageRun(Bitmap bitmap, Size size, TextRunProperties properties) : DrawableTextRun
    {
        public override Size Size => size;
        public override double Baseline => size.Height;
        public override TextRunProperties Properties => properties;
        public override void Draw(DrawingContext drawingContext, Point origin) =>
            drawingContext.DrawImage(bitmap, new Rect(origin, size));
    }

    private sealed class LabelRun : DrawableTextRun
    {
        private readonly FormattedText _text;
        private readonly TextRunProperties _properties;

        public LabelRun(string label, TextRunProperties properties, IBrush brush)
        {
            _properties = properties;
            _text = new FormattedText(label.Length > 0 ? label : " ", CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                properties.Typeface, properties.FontRenderingEmSize * (label.Length > 12 ? 0.8 : 1), brush);
        }

        public override Size Size => new(_text.WidthIncludingTrailingWhitespace + 2, _text.Height);
        public override double Baseline => _text.Baseline;
        public override TextRunProperties Properties => _properties;
        public override void Draw(DrawingContext drawingContext, Point origin) => drawingContext.DrawText(_text, origin + new Point(1, 0));
    }

    // =====================================================================
    // Caret and selection
    // =====================================================================

    private bool HasSelection => _caret != _anchor;

    private DocxPosition Start => _caret < _anchor ? _caret : _anchor;

    private DocxPosition End => _caret < _anchor ? _anchor : _caret;

    private DocxEditParagraph Paragraph(int index) => Document.Paragraphs[index];

    private DocxPosition Clamp(DocxPosition p)
    {
        if (Document.Paragraphs.Count == 0) return default;
        var index = Math.Clamp(p.Paragraph, 0, Document.Paragraphs.Count - 1);
        return new DocxPosition(index, Math.Clamp(p.Offset, 0, Paragraph(index).Length));
    }

    /// <summary>The selected range inside one paragraph, and whether the selection runs on past its end.</summary>
    internal (int From, int To, bool PastEnd) SelectionIn(DocxEditParagraph p)
    {
        if (!HasSelection || p.Index < Start.Paragraph || p.Index > End.Paragraph) return (0, 0, false);
        var from = p.Index == Start.Paragraph ? Start.Offset : 0;
        var to = p.Index == End.Paragraph ? End.Offset : p.Length;
        return (from, to, p.Index < End.Paragraph);
    }

    internal int? CaretIn(DocxEditParagraph p) =>
        _caretOn && !HasSelection && p.Index == _caret.Paragraph && _view?.Scroll.IsKeyboardFocusWithin == true ? _caret.Offset : null;

    private EditableParagraph? CaretControl() =>
        Document.Paragraphs.Count > _caret.Paragraph && _controls.TryGetValue(Paragraph(_caret.Paragraph), out var c) ? c : null;

    public void ShowCaret()
    {
        _caretOn = true;
        _blink.Stop();
        _blink.Start();
        CaretControl()?.InvalidateVisual();
    }

    private void MoveTo(DocxPosition position, bool extend) => Select(extend ? _anchor : position, position, fromEdit: false);

    private void Select(DocxPosition anchor, DocxPosition caret, bool fromEdit)
    {
        var low = Math.Min(Math.Min(_anchor.Paragraph, _caret.Paragraph), Math.Min(anchor.Paragraph, caret.Paragraph));
        var high = Math.Max(Math.Max(_anchor.Paragraph, _caret.Paragraph), Math.Max(anchor.Paragraph, caret.Paragraph));
        if (!fromEdit)
        {
            _undoGroup = null; // moving the caret ends a run of typing
            _pending = null;
        }
        _anchor = anchor;
        _caret = caret;
        for (var i = low; i <= high && i < Document.Paragraphs.Count; i++)
            if (_controls.TryGetValue(Paragraph(i), out var control)) control.InvalidateVisual();
        ShowCaret();
        BringCaretIntoView();
        RaiseState();
    }

    private void BringCaretIntoView()
    {
        if (CaretControl() is not { } control) return;
        // After layout: a paragraph just added has no size yet.
        Dispatcher.UIThread.Post(() =>
        {
            if (control.GetVisualRoot() is null) return;
            var rect = control.CaretRect(_caret.Offset);
            control.BringIntoView(rect.Inflate(new Thickness(0, 24)));
        }, DispatcherPriority.Background);
    }

    // =====================================================================
    // Edits
    // =====================================================================

    private void Record(string? group)
    {
        if (group is not null && group == _undoGroup) return;
        _undo.Add((Document.TakeSnapshot(), _caret, _anchor));
        if (_undo.Count > MaxUndo) _undo.RemoveAt(0);
        _redo.Clear();
        _undoGroup = group;
    }

    private void Edit(Func<DocxEditResult> edit, string? group = null, bool keepSelection = false)
    {
        Record(group);
        Apply(edit(), keepSelection);
    }

    /// <summary>Takes the selection away before typing over it; true when there was one.</summary>
    private bool DeleteSelection()
    {
        if (!HasSelection) return false;
        Edit(() => Document.Delete(Start, End));
        return true;
    }

    public void Type(string text)
    {
        var pending = _pending;
        DeleteSelection();
        Edit(() => Document.Insert(_caret, text, pending), "type");
        _pending = null;
        // A word typed is a step of its own to undo.
        if (text.Any(char.IsWhiteSpace)) _undoGroup = null;
    }

    private void Backspace(bool word)
    {
        if (DeleteSelection()) return;
        if (word && _caret.Offset > 0)
        {
            var from = WordLeft(_caret);
            Edit(() => Document.Delete(from, _caret));
            return;
        }
        Edit(() => Document.Backspace(_caret), "delete");
    }

    private void DeleteForward(bool word)
    {
        if (DeleteSelection()) return;
        if (word && _caret.Offset < Paragraph(_caret.Paragraph).Length)
        {
            var to = WordRight(_caret);
            Edit(() => Document.Delete(_caret, to));
            return;
        }
        Edit(() => Document.DeleteForward(_caret), "delete");
    }

    public void Undo()
    {
        if (_undo.Count == 0) return;
        var (snapshot, caret, anchor) = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add((Document.TakeSnapshot(), _caret, _anchor));
        Restore(snapshot, caret, anchor);
    }

    public void Redo()
    {
        if (_redo.Count == 0) return;
        var (snapshot, caret, anchor) = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add((Document.TakeSnapshot(), _caret, _anchor));
        Restore(snapshot, caret, anchor);
    }

    private void Restore(DocxEditDocument.Snapshot snapshot, DocxPosition caret, DocxPosition anchor)
    {
        Document.Restore(snapshot);
        Relayout();
        _undoGroup = null;
        Select(Clamp(anchor), Clamp(caret), fromEdit: true);
        Changed?.Invoke();
    }

    // ── Formatting ──────────────────────────────────────────────────────

    private DocxRunStyle CaretStyle() => Document.StyleOf(_caret, _pending ?? Document.FormatAt(_caret));

    private bool AllText(Func<DocxRunStyle, bool> test) =>
        HasSelection ? Document.All(Start, End, test) : test(CaretStyle());

    /// <summary>With a selection, formats it; without, sets what the next typed text looks like.</summary>
    public void FormatText(DocxRunChange change)
    {
        if (!HasSelection)
        {
            _pending = Document.Change(_pending ?? Document.FormatAt(_caret), change);
            RaiseState();
            return;
        }
        Edit(() => Document.FormatText(Start, End, change), keepSelection: true);
    }

    public void ToggleBold() => FormatText(new DocxRunChange.Bold(!AllText(s => s.Bold)));
    public void ToggleItalic() => FormatText(new DocxRunChange.Italic(!AllText(s => s.Italic)));
    public void ToggleUnderline() => FormatText(new DocxRunChange.Underline(!AllText(s => s.Underline)));
    public void ToggleStrike() => FormatText(new DocxRunChange.Strike(!AllText(s => s.Strike)));

    public void GrowFont(int direction)
    {
        double[] sizes = [8, 9, 10, 10.5, 11, 12, 14, 16, 18, 20, 22, 24, 26, 28, 36, 48, 72];
        var current = CaretStyle().Size;
        var next = direction > 0 ? sizes.FirstOrDefault(s => s > current + 0.01, current + 1) : sizes.LastOrDefault(s => s < current - 0.01, Math.Max(1, current - 1));
        FormatText(new DocxRunChange.Size(next));
    }

    public void SetAlignment(DocxAlignment alignment) =>
        Edit(() => Document.SetAlignment(Start.Paragraph, End.Paragraph, alignment), keepSelection: true);

    public void SetStyle(DocxStyleInfo style) =>
        Edit(() => Document.SetStyle(Start.Paragraph, End.Paragraph, style), keepSelection: true);

    public void ToggleList(bool numbered) =>
        Edit(() => Document.ToggleList(Start.Paragraph, End.Paragraph, numbered), keepSelection: true);

    public void Indent(int delta) =>
        Edit(() => Document.Indent(Start.Paragraph, End.Paragraph, delta), keepSelection: true);

    // ── Clipboard ───────────────────────────────────────────────────────

    private IClipboard? Clipboard => _view is null ? null : TopLevel.GetTopLevel(_view)?.Clipboard;

    public async void Copy(bool cut)
    {
        if (!HasSelection) return;
        _clip = Document.Copy(Start, End);
        try
        {
            if (Clipboard is { } clipboard) await clipboard.SetTextAsync(_clip.Text);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write("docx clipboard", e.ToString());
        }
        if (cut) DeleteSelection();
    }

    public async void Paste()
    {
        string? text;
        try
        {
            text = Clipboard is { } clipboard ? await clipboard.TryGetTextAsync() : null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            ErrorLog.Write("docx clipboard", e.ToString());
            return;
        }
        if (string.IsNullOrEmpty(text)) return;

        DeleteSelection();
        // Copied here: paste it back with its formatting.
        static string Plain(string s) => s.Replace("\r\n", "\n");
        var clip = _clip is not null && Plain(_clip.Text) == Plain(text) ? _clip : null;
        Edit(() => clip is not null ? Document.Paste(_caret, clip) : Document.Insert(_caret, text));
    }

    // =====================================================================
    // Keyboard
    // =====================================================================

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (_view is null || !_view.Scroll.IsKeyboardFocusWithin || Document.Paragraphs.Count == 0) return;
        var command = e.KeyModifiers.HasFlag(OperatingSystem.IsMacOS() ? KeyModifiers.Meta : KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var handled = true;

        switch (e.Key)
        {
            case Key.Left:
                if (HasSelection && !shift) MoveTo(Start, false);
                else MoveTo(command ? WordLeft(_caret) : CharLeft(_caret), shift);
                _desiredX = null;
                break;
            case Key.Right:
                if (HasSelection && !shift) MoveTo(End, false);
                else MoveTo(command ? WordRight(_caret) : CharRight(_caret), shift);
                _desiredX = null;
                break;
            case Key.Up or Key.Down:
                MoveVertically(e.Key == Key.Up ? -1 : 1, shift);
                break;
            case Key.PageUp or Key.PageDown:
                MovePage(e.Key == Key.PageUp ? -1 : 1, shift);
                break;
            case Key.Home:
                MoveTo(command ? new DocxPosition(0, 0) : LineEdge(_caret, start: true), shift);
                _desiredX = null;
                break;
            case Key.End:
                MoveTo(command ? Clamp(new DocxPosition(Document.Paragraphs.Count - 1, int.MaxValue)) : LineEdge(_caret, start: false), shift);
                _desiredX = null;
                break;
            case Key.Back:
                Backspace(command);
                break;
            case Key.Delete:
                DeleteForward(command);
                break;
            case Key.Enter when shift:
                Type(DocxEditParagraph.LineBreakChar.ToString());
                break;
            case Key.Enter when command:
                DeleteSelection();
                Edit(() => Document.PageBreak(_caret));
                break;
            case Key.Enter:
                DeleteSelection();
                Edit(() => Document.Enter(_caret));
                break;
            case Key.Tab when !command:
                // In a list, or over several paragraphs, Tab moves a level, as in Word.
                if (Start.Paragraph != End.Paragraph || (_caret.Offset == 0 && Paragraph(_caret.Paragraph).IsList)) Indent(shift ? -1 : 1);
                else if (!shift) Type("\t");
                break;
            case Key.Escape when HasSelection:
                MoveTo(_caret, false);
                break;
            case Key.A when command: Select(new DocxPosition(0, 0), Clamp(new DocxPosition(Document.Paragraphs.Count - 1, int.MaxValue)), false); break;
            case Key.C when command: Copy(cut: false); break;
            case Key.X when command: Copy(cut: true); break;
            case Key.V when command: Paste(); break;
            case Key.Z when command && shift: Redo(); break;
            case Key.Z when command: Undo(); break;
            case Key.Y when command: Redo(); break;
            case Key.B when command: ToggleBold(); break;
            case Key.I when command: ToggleItalic(); break;
            case Key.U when command: ToggleUnderline(); break;
            case Key.L when command: SetAlignment(DocxAlignment.Left); break;
            case Key.E when command: SetAlignment(DocxAlignment.Center); break;
            case Key.R when command: SetAlignment(DocxAlignment.Right); break;
            case Key.J when command: SetAlignment(DocxAlignment.Justify); break;
            case Key.Space when command: FormatText(new DocxRunChange.Clear()); break;
            case Key.OemCloseBrackets when command: GrowFont(1); break;
            case Key.OemOpenBrackets when command: GrowFont(-1); break;
            default:
                handled = false;
                break;
        }
        if (handled) e.Handled = true;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e)
    {
        if (_view is null || !_view.Scroll.IsKeyboardFocusWithin || Document.Paragraphs.Count == 0) return;
        var text = new string((e.Text ?? "").Where(c => !char.IsControl(c)).ToArray());
        if (text.Length == 0) return;
        Type(text);
        e.Handled = true;
    }

    private DocxPosition CharLeft(DocxPosition p)
    {
        if (p.Offset > 0)
        {
            return p with { Offset = DocxEditDocument.PreviousCharacter(Paragraph(p.Paragraph).Text, p.Offset) };
        }
        return p.Paragraph > 0 ? new DocxPosition(p.Paragraph - 1, Paragraph(p.Paragraph - 1).Length) : p;
    }

    private DocxPosition CharRight(DocxPosition p)
    {
        var text = Paragraph(p.Paragraph).Text;
        if (p.Offset < text.Length)
        {
            return p with { Offset = DocxEditDocument.NextCharacter(text, p.Offset) };
        }
        return p.Paragraph + 1 < Document.Paragraphs.Count ? new DocxPosition(p.Paragraph + 1, 0) : p;
    }

    private static bool IsWord(char c) => char.IsLetterOrDigit(c) || c is '_' or '\'';

    private DocxPosition WordLeft(DocxPosition p)
    {
        if (p.Offset == 0) return CharLeft(p);
        var text = Paragraph(p.Paragraph).Text;
        var i = p.Offset;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        if (i > 0 && IsWord(text[i - 1])) while (i > 0 && IsWord(text[i - 1])) i--;
        else if (i > 0) i--;
        return p with { Offset = i };
    }

    private DocxPosition WordRight(DocxPosition p)
    {
        var text = Paragraph(p.Paragraph).Text;
        if (p.Offset >= text.Length) return CharRight(p);
        var i = p.Offset;
        if (IsWord(text[i])) while (i < text.Length && IsWord(text[i])) i++;
        else i++;
        while (i < text.Length && char.IsWhiteSpace(text[i])) i++;
        return p with { Offset = i };
    }

    private (DocxPosition From, DocxPosition To) WordAt(DocxPosition p)
    {
        var text = Paragraph(p.Paragraph).Text;
        if (text.Length == 0) return (p, p);
        var i = Math.Min(p.Offset, text.Length - 1);
        if (!IsWord(text[i]) && i > 0 && IsWord(text[i - 1])) i--;
        if (!IsWord(text[i])) return (p with { Offset = i }, p with { Offset = i + 1 });
        int a = i, b = i;
        while (a > 0 && IsWord(text[a - 1])) a--;
        while (b < text.Length && IsWord(text[b])) b++;
        while (b < text.Length && text[b] == ' ') b++; // Word takes the space after a word too
        return (p with { Offset = a }, p with { Offset = b });
    }

    private DocxPosition LineEdge(DocxPosition p, bool start)
    {
        if (!_controls.TryGetValue(Paragraph(p.Paragraph), out var control)) return p with { Offset = start ? 0 : Paragraph(p.Paragraph).Length };
        var (a, b) = control.LineBounds(p.Offset);
        return p with { Offset = start ? a : b };
    }

    /// <summary>Up and Down keep to the column they started from, across lines and paragraphs.</summary>
    private void MoveVertically(int direction, bool extend)
    {
        if (_view is null || !_controls.TryGetValue(Paragraph(_caret.Paragraph), out var control)) return;
        var origin = control.TranslatePoint(default, _view.Pages) ?? default;
        _desiredX ??= origin.X + control.CaretRect(_caret.Offset).X;

        var desired = _desiredX.Value;
        if (control.LineMove(_caret.Offset, desired - origin.X, direction) is { } offset)
        {
            Keep(() => MoveTo(_caret with { Offset = offset }, extend));
            return;
        }
        for (var i = _caret.Paragraph + direction; i >= 0 && i < Document.Paragraphs.Count; i += direction)
        {
            if (!_controls.TryGetValue(Paragraph(i), out var next)) continue;
            var nextOrigin = next.TranslatePoint(default, _view.Pages) ?? default;
            var target = next.OffsetOnEdgeLine(first: direction > 0, desired - nextOrigin.X);
            Keep(() => MoveTo(new DocxPosition(i, target), extend));
            return;
        }
        Keep(() => MoveTo(direction < 0 ? new DocxPosition(0, 0) : Clamp(new DocxPosition(Document.Paragraphs.Count - 1, int.MaxValue)), extend));
    }

    private void MovePage(int direction, bool extend)
    {
        if (_view is null || !_controls.TryGetValue(Paragraph(_caret.Paragraph), out var control)) return;
        var pages = _view.Pages;
        var origin = control.TranslatePoint(default, pages) ?? default;
        var caret = control.CaretRect(_caret.Offset);
        _desiredX ??= origin.X + caret.X;
        var y = origin.Y + caret.Y + direction * _view.Scroll.Viewport.Height / Math.Max(0.1, _view.Zoom);

        DocxPosition? best = null;
        foreach (var (model, c) in _controls.OrderBy(x => x.Key.Index))
        {
            var top = c.TranslatePoint(default, pages) ?? default;
            if (top.Y > y && best is not null) break;
            best = new DocxPosition(model.Index, c.OffsetAt(new Point(_desiredX.Value - top.X, Math.Clamp(y - top.Y, 0, c.Bounds.Height - 1))));
        }
        if (best is { } position) Keep(() => MoveTo(position, extend));
    }

    /// <summary>Moves without forgetting the column Up and Down aim for.</summary>
    private void Keep(Action move)
    {
        var x = _desiredX;
        move();
        _desiredX = x;
    }

    // =====================================================================
    // Mouse
    // =====================================================================

    internal void OnPointerPressed(EditableParagraph control, PointerPressedEventArgs e)
    {
        if (_view is null) return;
        var point = e.GetCurrentPoint(control);
        _view.Scroll.Focus();
        if (!point.Properties.IsLeftButtonPressed)
        {
            // A right click inside the selection keeps it for the menu.
            var at = new DocxPosition(control.Model.Index, control.OffsetAt(point.Position));
            if (!HasSelection || at < Start || at > End) MoveTo(at, false);
            return;
        }

        var position = new DocxPosition(control.Model.Index, control.OffsetAt(point.Position));
        _desiredX = null;
        switch (e.ClickCount)
        {
            case 2:
                var (from, to) = WordAt(position);
                Select(from, to, false);
                break;
            case >= 3:
                Select(position with { Offset = 0 }, position with { Offset = control.Model.Length }, false);
                break;
            default:
                MoveTo(position, e.KeyModifiers.HasFlag(KeyModifiers.Shift));
                break;
        }
        _dragging = e.ClickCount == 1;
        _dragOrigin = control;
        e.Pointer.Capture(control);
        e.Handled = true;
    }

    internal void OnPointerMoved(EditableParagraph control, PointerEventArgs e)
    {
        if (!_dragging || _view is null || control != _dragOrigin) return;
        var pages = _view.Pages;
        var point = e.GetPosition(pages);
        var target = (pages.InputHitTest(point) as Visual)?.FindAncestorOfType<EditableParagraph>(includeSelf: true);
        if (target is null)
        {
            // Between paragraphs or past the text: the nearest edge of the one being dragged from.
            var local = e.GetPosition(control);
            if (local.Y < 0 || local.Y > control.Bounds.Height) return;
            target = control;
        }
        var offset = target.OffsetAt(e.GetPosition(target));
        Select(_anchor, new DocxPosition(target.Model.Index, offset), false);
    }

    internal void OnPointerReleased(EditableParagraph control, PointerReleasedEventArgs e)
    {
        _dragging = false;
        _dragOrigin = null;
        e.Pointer.Capture(null);
    }

    private ContextMenu BuildContextMenu()
    {
        MenuItem Item(string key, string gesture, Action action)
        {
            var item = new MenuItem { Header = Loc.Get(key), InputGesture = KeyGesture.Parse(gesture) };
            item.Click += (_, _) => action();
            return item;
        }
        return new ContextMenu
        {
            Items =
            {
                Item("DocxCut", "Ctrl+X", () => Copy(cut: true)),
                Item("DocxCopy", "Ctrl+C", () => Copy(cut: false)),
                Item("DocxPaste", "Ctrl+V", Paste),
                new Separator(),
                Item("DocxSelectAll", "Ctrl+A", () => Select(new DocxPosition(0, 0), Clamp(new DocxPosition(Document.Paragraphs.Count - 1, int.MaxValue)), false)),
            },
        };
    }

    // =====================================================================
    // Toolbar state
    // =====================================================================

    private void RaiseState()
    {
        if (Document.Paragraphs.Count == 0) return;
        var caret = Clamp(_caret);
        var style = CaretStyle();
        var paragraph = Paragraph(caret.Paragraph);
        Toolbar.Update(new DocxEditorState(
            AllText(s => s.Bold), AllText(s => s.Italic), AllText(s => s.Underline), AllText(s => s.Strike),
            style.Size, style.Font, Document.AlignmentOf(paragraph), Document.ListFormat(paragraph), Document.StyleIdOf(paragraph),
            _undo.Count > 0, _redo.Count > 0));
    }

    /// <summary>Gives the keyboard back to the page after a toolbar control took it.</summary>
    public void FocusText()
    {
        _view?.Scroll.Focus();
        ShowCaret();
    }

    public void Dispose()
    {
        _blink.Stop();
        foreach (var bitmap in _images.Values) bitmap.Dispose();
        _images.Clear();
    }
}

/// <summary>
/// A paragraph being edited: its own text layout (so pictures and labels sit in
/// the line), with the selection and caret drawn over it.
/// </summary>
internal sealed class EditableParagraph : Control
{
    private static readonly IBrush CaretBrush = Brushes.Black;

    private readonly DocxEditor _editor;
    private readonly DocxAlignment _alignment;
    private readonly DocxRunStyle _mark;
    private readonly double _firstLine;
    private TextLayout? _layout;
    private double _width = -1;
    private int _version = -1;

    public EditableParagraph(DocxEditor editor, DocxEditParagraph model, DocxParagraph layout)
    {
        _editor = editor;
        Model = model;
        _alignment = layout.Format.Alignment;
        _mark = layout.MarkStyle;
        // A first-line indent is a spacer at the start of the layout; positions shift by one past it.
        _firstLine = layout.Marker is null && layout.Format.FirstLine > 0 ? layout.Format.FirstLine * DocxView.Dip : 0;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        PointerPressed += (_, e) => _editor.OnPointerPressed(this, e);
        PointerMoved += (_, e) => _editor.OnPointerMoved(this, e);
        PointerReleased += (_, e) => _editor.OnPointerReleased(this, e);
    }

    public DocxEditParagraph Model { get; }

    private int Shift => _firstLine > 0 ? 1 : 0;

    private double LayoutWidth => _width > 0 ? _width : Math.Max(1, Bounds.Width);

    public void Refresh()
    {
        _version = -1;
        InvalidateMeasure();
        InvalidateVisual();
    }

    private TextLayout Layout(double width)
    {
        if (_layout is not null && Math.Abs(_width - width) < 0.01 && _version == Model.Version) return _layout;
        _layout?.Dispose();
        _layout = _editor.CreateLayout(Model, _alignment, _mark, _firstLine, width);
        _width = width;
        _version = Model.Version;
        return _layout;
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        var width = double.IsFinite(availableSize.Width) ? availableSize.Width : 600;
        return new Size(width, Math.Ceiling(Layout(width).Height));
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        Layout(finalSize.Width);
        return finalSize;
    }

    public override void Render(DrawingContext context)
    {
        var layout = Layout(LayoutWidth);
        // Hit-testable everywhere, not just over the glyphs.
        context.FillRectangle(Brushes.Transparent, new Rect(Bounds.Size));

        var (from, to, pastEnd) = _editor.SelectionIn(Model);
        if (to > from)
            foreach (var rect in layout.HitTestTextRange(from + Shift, to - from))
                context.FillRectangle(DocxView.SelectionBrush, rect);
        if (pastEnd)
        {
            var end = layout.HitTestTextPosition(Model.Length + Shift);
            context.FillRectangle(DocxView.SelectionBrush, new Rect(end.X, end.Y, 6, end.Height));
        }

        layout.Draw(context, default);

        if (_editor.CaretIn(Model) is { } caret)
        {
            var rect = layout.HitTestTextPosition(caret + Shift);
            context.FillRectangle(CaretBrush, new Rect(rect.X, rect.Y, 1.5, Math.Max(rect.Height, 8)));
        }
    }

    public int OffsetAt(Point point) =>
        Math.Clamp(Layout(LayoutWidth).HitTestPoint(point).TextPosition - Shift, 0, Model.Length);

    public Rect CaretRect(int offset) => Layout(LayoutWidth).HitTestTextPosition(offset + Shift);

    private int LineOf(int offset) =>
        Math.Clamp(Layout(LayoutWidth).GetLineIndexFromCharacterIndex(offset + Shift, false), 0, Layout(LayoutWidth).TextLines.Count - 1);

    private double LineTop(int line)
    {
        var top = 0.0;
        var lines = Layout(LayoutWidth).TextLines;
        for (var i = 0; i < line; i++) top += lines[i].Height;
        return top;
    }

    /// <summary>The offset one line up or down at <paramref name="x"/>, or null past the first or last line.</summary>
    public int? LineMove(int offset, double x, int direction)
    {
        var target = LineOf(offset) + direction;
        var lines = Layout(LayoutWidth).TextLines;
        if (target < 0 || target >= lines.Count) return null;
        return OffsetAt(new Point(x, LineTop(target) + lines[target].Height / 2));
    }

    public int OffsetOnEdgeLine(bool first, double x)
    {
        var lines = Layout(LayoutWidth).TextLines;
        var line = first ? 0 : lines.Count - 1;
        return OffsetAt(new Point(x, LineTop(line) + lines[line].Height / 2));
    }

    /// <summary>Where the caret's line starts and ends, for Home and End.</summary>
    public (int Start, int End) LineBounds(int offset)
    {
        var lines = Layout(LayoutWidth).TextLines;
        var index = LineOf(offset);
        var line = lines[index];
        var start = line.FirstTextSourceIndex;
        var end = start + line.Length - line.NewLineLength;
        // A wrapped line ends before the space it wrapped at, so the caret stays on it.
        if (index < lines.Count - 1 && line.NewLineLength == 0) end -= line.TrailingWhitespaceLength;
        return (Math.Clamp(start - Shift, 0, Model.Length), Math.Clamp(end - Shift, 0, Model.Length));
    }
}
