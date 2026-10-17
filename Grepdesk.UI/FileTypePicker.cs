using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Grepdesk.Core.Viewer;

namespace Grepdesk.UI;

/// <summary>
/// The file types "Open with Grepdesk" registers, one row per
/// <see cref="ViewerFormats.FileTypeCategories"/> group. A group's box ticks or
/// clears the whole group and shows a dash when only part of it is picked;
/// "Show types" opens the group to pick single extensions.
/// </summary>
public sealed class FileTypePicker : StackPanel
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private sealed record Row(
        FileTypeCategory Category, CheckBox Box, TextBlock Count, Button Toggle,
        WrapPanel Types, Dictionary<string, CheckBox> TypeBoxes);

    private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Row> _rows = [];
    private bool _syncing;

    /// <summary>Raised when the user changes the selection (not by <see cref="SetSelection"/>).</summary>
    public event Action? SelectionChanged;

    public IReadOnlyCollection<string> Selected => _selected;

    public FileTypePicker()
    {
        Spacing = 2;
        foreach (var category in ViewerFormats.FileTypeCategories)
            _rows.Add(AddRow(category));
    }

    public void SetSelection(IEnumerable<string> extensions)
    {
        _selected.Clear();
        _selected.UnionWith(extensions);
        Sync();
    }

    public void ApplyLanguage()
    {
        foreach (var row in _rows)
        {
            row.Box.Content = Loc.Get("FileTypes" + row.Category.Id);
            row.Toggle.Content = Loc.Get(row.Types.IsVisible ? "FileTypesHide" : "FileTypesShow");
        }
        Sync();
    }

    private Row AddRow(FileTypeCategory category)
    {
        var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center };
        var count = new TextBlock
        {
            FontSize = 11,
            Foreground = Brush.Parse("#6c7086"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(10, 0, 0, 0)
        };
        var toggle = new Button { FontSize = 11, Padding = new Thickness(8, 3), VerticalAlignment = VerticalAlignment.Center };
        var types = new WrapPanel { IsVisible = false, Margin = new Thickness(28, 0, 0, 8) };
        var typeBoxes = new Dictionary<string, CheckBox>(StringComparer.OrdinalIgnoreCase);

        foreach (var ext in category.Extensions)
        {
            var typeBox = new CheckBox { Content = ext, MinWidth = 96, FontSize = 12 };
            typeBox.IsCheckedChanged += (_, _) =>
            {
                if (_syncing) return;
                if (typeBox.IsChecked == true) _selected.Add(ext);
                else _selected.Remove(ext);
                Changed();
            };
            typeBoxes[ext] = typeBox;
            types.Children.Add(typeBox);
        }

        // Click comes after the box has toggled itself; the group's state is
        // decided from the selection instead, so a part-picked group fills up.
        box.Click += (_, _) =>
        {
            if (category.Extensions.All(_selected.Contains)) _selected.ExceptWith(category.Extensions);
            else _selected.UnionWith(category.Extensions);
            Changed();
        };

        toggle.Click += (_, _) =>
        {
            types.IsVisible = !types.IsVisible;
            toggle.Content = Loc.Get(types.IsVisible ? "FileTypesHide" : "FileTypesShow");
        };

        var header = new DockPanel();
        DockPanel.SetDock(toggle, Dock.Right);
        header.Children.Add(toggle);
        header.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { box, count } });

        Children.Add(header);
        Children.Add(types);
        return new Row(category, box, count, toggle, types, typeBoxes);
    }

    private void Changed()
    {
        Sync();
        SelectionChanged?.Invoke();
    }

    /// <summary>Shows the selection on every box without treating the update as a user change.</summary>
    private void Sync()
    {
        _syncing = true;
        try
        {
            foreach (var row in _rows)
            {
                var picked = row.Category.Extensions.Count(_selected.Contains);
                row.Box.IsChecked = picked == row.Category.Extensions.Count ? true : picked == 0 ? false : null;
                row.Count.Text = Loc.Get("FileTypesCount", picked, row.Category.Extensions.Count);
                foreach (var (ext, typeBox) in row.TypeBoxes)
                    typeBox.IsChecked = _selected.Contains(ext);
            }
        }
        finally
        {
            _syncing = false;
        }
    }
}
