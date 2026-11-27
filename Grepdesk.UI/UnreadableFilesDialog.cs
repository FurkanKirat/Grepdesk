using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Grepdesk.Core;
using Grepdesk.Core.ContentSearch;

namespace Grepdesk.UI;

/// <summary>
/// Lists files that Content Search could not read, showing why each was skipped
/// and its full location, with quick actions to reveal or copy paths.
/// </summary>
internal sealed class UnreadableFilesDialog : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private sealed record ReasonOption(SkipReason? Reason, string Label);

    private sealed record SkippedRow(
        string FileName,
        string FullPath,
        SkipReason Reason,
        string ReasonLabel,
        string? Detail,
        IBrush BadgeBackground,
        IBrush BadgeForeground);

    private readonly IReadOnlyList<SkippedRow> _allRows;
    private readonly IPlatformShell _shell;
    private readonly ListBox _list;
    private readonly ComboBox _reasonFilter;
    private readonly TextBox _searchBox;
    private readonly TextBlock _feedbackText;
    private List<SkippedRow> _filteredRows = [];

    private UnreadableFilesDialog(IReadOnlyList<SkippedFile> items, IPlatformShell shell)
    {
        _shell = shell;
        Title = Loc.Get("SkippedFilesTitle");
        Width = 780;
        Height = 520;
        MinWidth = 520;
        MinHeight = 340;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#1e1e2e");

        _allRows = items.Select(ToRow).ToList();

        var summary = new TextBlock
        {
            Text = Loc.Get("SkippedFilesSummary", _allRows.Count),
            Foreground = Brush.Parse("#cdd6f4"),
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            TextWrapping = TextWrapping.Wrap,
        };

        var options = new List<ReasonOption>
        {
            new(null, Loc.Get("SkippedFilesAllReasons", _allRows.Count))
        };
        foreach (var group in _allRows.GroupBy(r => r.Reason).OrderByDescending(g => g.Count()))
            options.Add(new(group.Key, Loc.Get("SkippedFilesReasonCount", ReasonLabel(group.Key), group.Count())));

        _reasonFilter = new ComboBox
        {
            ItemsSource = options,
            SelectedIndex = 0,
            MinWidth = 230,
            FontSize = 12,
            Background = Brush.Parse("#313244"),
            Foreground = Brush.Parse("#cdd6f4"),
            BorderBrush = Brush.Parse("#45475a"),
            CornerRadius = new CornerRadius(6),
            ItemTemplate = new FuncDataTemplate<ReasonOption>((opt, _) =>
                new TextBlock { Text = opt?.Label ?? "", Foreground = Brush.Parse("#cdd6f4"), FontSize = 12 }),
        };
        _reasonFilter.SelectionChanged += (_, _) => ApplyFilter();

        _searchBox = new TextBox
        {
            Watermark = Loc.Get("SkippedFilesFilterWatermark"),
            FontSize = 12,
            Background = Brush.Parse("#313244"),
            Foreground = Brushes.White,
            BorderBrush = Brush.Parse("#45475a"),
            CornerRadius = new CornerRadius(6),
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        _searchBox.TextChanged += (_, _) => ApplyFilter();

        var filterRow = new DockPanel { Margin = new Thickness(0, 8, 0, 10) };
        DockPanel.SetDock(_reasonFilter, Dock.Right);
        _reasonFilter.Margin = new Thickness(8, 0, 0, 0);
        filterRow.Children.Add(_reasonFilter);
        filterRow.Children.Add(_searchBox);

        var topPanel = new StackPanel { Spacing = 2, Children = { summary, filterRow } };

        _list = new ListBox
        {
            Background = Brush.Parse("#181825"),
            BorderBrush = Brush.Parse("#313244"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            ItemTemplate = new FuncDataTemplate<SkippedRow>((row, _) => BuildRowView(row)),
        };
        ScrollViewer.SetHorizontalScrollBarVisibility(_list, ScrollBarVisibility.Disabled);
        _list.DoubleTapped += (_, _) =>
        {
            if (_list.SelectedItem is SkippedRow row)
                RevealInFolder(row);
        };
        _list.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton != MouseButton.Right) return;
            var item = (e.Source as Control)?.DataContext as SkippedRow ?? _list.SelectedItem as SkippedRow;
            if (item is null) return;
            _list.SelectedItem = item;
            OpenRowContextMenu(item);
            e.Handled = true;
        };

        _feedbackText = new TextBlock
        {
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var copyAll = StyledButton(Loc.Get("SkippedFilesCopyAll"), "#313244", "#cdd6f4");
        copyAll.Click += async (_, _) => await CopyAllAsync();

        var close = StyledButton(Loc.Get("JobClose"), "#89b4fa", "#11111b");
        close.Click += (_, _) => Close();

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { copyAll, close } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(_feedbackText);

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(topPanel, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        layout.Children.Add(topPanel);
        layout.Children.Add(buttons);
        layout.Children.Add(_list);
        Content = layout;

        ApplyFilter();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    public static Task ShowAsync(Window owner, IReadOnlyList<SkippedFile> items, IPlatformShell shell) =>
        new UnreadableFilesDialog(items, shell).ShowDialog(owner);

    private Control BuildRowView(SkippedRow? row)
    {
        if (row is null) return new Panel();

        var showBtn = new Button
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(4),
            Content = IconOrFallback("ContextMenuShowInFolder", 14, "#a6adc8"),
        };
        ToolTip.SetTip(showBtn, Loc.Get("ContextMenuShowInFolder"));
        showBtn.Click += (_, e) =>
        {
            e.Handled = true;
            RevealInFolder(row);
        };

        var copyBtn = new Button
        {
            Background = Brushes.Transparent,
            Padding = new Thickness(5),
            CornerRadius = new CornerRadius(4),
            Content = IconOrFallback("ContextMenuCopyPath", 14, "#a6adc8"),
        };
        ToolTip.SetTip(copyBtn, Loc.Get("ContextMenuCopyPath"));
        copyBtn.Click += async (_, e) =>
        {
            e.Handled = true;
            await CopyPathAsync(row);
        };

        var badge = new Border
        {
            Background = row.BadgeBackground,
            BorderBrush = row.BadgeForeground,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(7, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = row.ReasonLabel,
                Foreground = row.BadgeForeground,
                FontSize = 11,
                FontWeight = FontWeight.SemiBold,
            },
        };

        var rightCluster = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Margin = new Thickness(10, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Children = { badge, showBtn, copyBtn },
        };

        var topRow = new DockPanel();
        DockPanel.SetDock(rightCluster, Dock.Right);
        topRow.Children.Add(rightCluster);
        topRow.Children.Add(new TextBlock
        {
            Text = row.FileName,
            Foreground = Brush.Parse("#cdd6f4"),
            FontSize = 13,
            FontWeight = FontWeight.Medium,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        var stack = new StackPanel
        {
            Margin = new Thickness(4, 5),
            Spacing = 2,
            Background = Brushes.Transparent,
            Children =
            {
                topRow,
                new TextBlock
                {
                    Text = row.FullPath,
                    Foreground = Brush.Parse("#7f849c"),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
            },
        };

        if (!string.IsNullOrWhiteSpace(row.Detail) &&
            !string.Equals(row.Detail, row.ReasonLabel, StringComparison.OrdinalIgnoreCase))
        {
            stack.Children.Add(new TextBlock
            {
                Text = row.Detail,
                Foreground = Brush.Parse("#6c7086"),
                FontSize = 11,
                TextWrapping = TextWrapping.Wrap,
            });
        }

        ToolTip.SetTip(stack, row.FullPath);
        return stack;
    }

    private void OpenRowContextMenu(SkippedRow row)
    {
        var showItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuShowInFolder"),
            Icon = MenuIcons.For("ContextMenuShowInFolder"),
        };
        showItem.Click += (_, _) => RevealInFolder(row);

        var copyItem = new MenuItem
        {
            Header = Loc.Get("ContextMenuCopyPath"),
            Icon = MenuIcons.For("ContextMenuCopyPath"),
        };
        copyItem.Click += async (_, _) => await CopyPathAsync(row);

        new ContextMenu { ItemsSource = new Control[] { showItem, copyItem } }.Open(_list);
    }

    private void ApplyFilter()
    {
        var selectedReason = (_reasonFilter.SelectedItem as ReasonOption)?.Reason;
        var text = _searchBox.Text?.Trim();

        IEnumerable<SkippedRow> query = _allRows;
        if (selectedReason is { } reason)
            query = query.Where(r => r.Reason == reason);
        if (!string.IsNullOrEmpty(text))
            query = query.Where(r => r.FullPath.Contains(text, StringComparison.OrdinalIgnoreCase));

        _filteredRows = query.ToList();
        _list.ItemsSource = _filteredRows;
    }

    private void RevealInFolder(SkippedRow row)
    {
        var result = _shell.ShowInFileManager(row.FullPath);
        if (!result.IsSuccess)
        {
            _feedbackText.Foreground = Brush.Parse("#f38ba8");
            _feedbackText.Text = result.Exception is not null
                ? $"{Loc.Get("ShowInFolderFailed")}: {result.Exception.Message}"
                : Loc.Get("ShowInFolderFailed");
        }
    }

    private async Task CopyPathAsync(SkippedRow row)
    {
        if (Clipboard is null) return;
        await Clipboard.SetTextAsync(row.FullPath);
        _feedbackText.Foreground = Brush.Parse("#a6e3a1");
        _feedbackText.Text = Loc.Get("PathCopied");
    }

    private async Task CopyAllAsync()
    {
        if (Clipboard is null || _filteredRows.Count == 0) return;
        var lines = _filteredRows.Select(r =>
            string.IsNullOrWhiteSpace(r.Detail)
                ? $"{r.FullPath}\t{r.ReasonLabel}"
                : $"{r.FullPath}\t{r.ReasonLabel}\t{r.Detail}");
        await Clipboard.SetTextAsync(string.Join(Environment.NewLine, lines));
        _feedbackText.Foreground = Brush.Parse("#a6e3a1");
        _feedbackText.Text = Loc.Get("SkippedFilesCopiedAll", _filteredRows.Count);
    }

    private static SkippedRow ToRow(SkippedFile item)
    {
        var fileName = Path.GetFileName(item.FullPath);
        if (string.IsNullOrEmpty(fileName)) fileName = item.FullPath;

        var reasonLabel = ReasonLabel(item.Reason);
        var detail = item.Reason == SkipReason.TooLarge && item.FileSizeBytes is { } size
            ? Loc.Get("SkipReasonTooLargeDetail", Jobs.Format.Size(size))
            : item.Detail;

        var (bg, fg) = item.Reason switch
        {
            SkipReason.AccessDenied or SkipReason.LockedOrInUse => ("#2e1e26", "#f38ba8"),
            SkipReason.PasswordProtected => ("#2b2438", "#cba6f7"),
            SkipReason.TooLarge => ("#1e293b", "#89b4fa"),
            _ => ("#2e271e", "#f9e2af"),
        };

        return new SkippedRow(
            fileName,
            item.FullPath,
            item.Reason,
            reasonLabel,
            detail,
            Brush.Parse(bg),
            Brush.Parse(fg));
    }

    private static string ReasonLabel(SkipReason reason) => reason switch
    {
        SkipReason.TooLarge => Loc.Get("SkipReasonTooLarge", Jobs.Format.Size(ContentSearcher.MaxFileSizeBytes)),
        SkipReason.AccessDenied => Loc.Get("SkipReasonAccessDenied"),
        SkipReason.LockedOrInUse => Loc.Get("SkipReasonLockedOrInUse"),
        SkipReason.PasswordProtected => Loc.Get("SkipReasonPasswordProtected"),
        SkipReason.CorruptOrUnsupported => Loc.Get("SkipReasonCorruptOrUnsupported"),
        SkipReason.NoExtractableText => Loc.Get("SkipReasonNoExtractableText"),
        _ => Loc.Get("SkipReasonExtractionFailed"),
    };

    private static Control IconOrFallback(string key, double size, string color)
    {
        if (MenuIcons.For(key) is { } icon)
        {
            icon.Width = size;
            icon.Height = size;
            icon.Foreground = Brush.Parse(color);
            return icon;
        }
        return new TextBlock { Text = "•", Foreground = Brush.Parse(color), FontSize = 12 };
    }

    private static Button StyledButton(string content, string background, string foreground) => new()
    {
        Content = content,
        Background = Brush.Parse(background),
        Foreground = Brush.Parse(foreground),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 6),
    };
}
