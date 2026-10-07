using System.ComponentModel;
using System.Runtime.CompilerServices;
using Grepdesk.Core.Cleanup;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Cleanup;

/// <summary>One card in the list on the left: a way to get space back.</summary>
public sealed class SuggestionRow : INotifyPropertyChanged
{
    private string _sizeText = "";
    private string _summary = "";
    private string _title = "";

    public required CleanupKind Kind { get; init; }
    public required CleanupAction Action { get; init; }
    public string Title { get => _title; set => Set(ref _title, value); }
    public List<ItemRow> Items { get; set; } = [];

    /// <summary>Everything found, when the list shows only the largest part of it.</summary>
    public int TotalItems { get; set; }
    public long TotalBytes { get; set; }

    /// <summary>Total for cards whose size isn't the sum of items (the Recycle Bin).</summary>
    public long? FixedBytes { get; set; }

    public long Bytes => FixedBytes ?? Items.Where(i => !i.IsHeader).Sum(i => i.Item!.Bytes);
    public long SelectedBytes => Items.Where(i => i.IsChecked && !i.IsHeader).Sum(i => i.Item!.Bytes);

    public string SizeText { get => _sizeText; set => Set(ref _sizeText, value); }
    public string Summary { get => _summary; set => Set(ref _summary, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

/// <summary>A checkable item in the details list, or a group header (duplicates).</summary>
public sealed class ItemRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public CleanupItem? Item { get; init; }
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string SizeText { get; init; } = "";

    public bool IsHeader { get; init; }

    // Tooltips for the row buttons (bound from the template).
    public string ShowTip => LocalizationService.Instance.Get("ContextMenuShowInFolder");
    public string OpenTip => LocalizationService.Instance.Get("ContextMenuOpen");
    public bool IsItem => !IsHeader;
    public bool CanCheck { get; init; } = true;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            CheckedChanged?.Invoke();
        }
    }

    /// <summary>Lets the page update the action button total as boxes are ticked.</summary>
    public Action? CheckedChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Same row for an item that was partly deleted (files in use were left).</summary>
    public ItemRow WithItem(CleanupItem item) => new()
    {
        Item = item,
        Name = Name,
        Detail = Detail,
        SizeText = Format.Size(item.Bytes),
        IsChecked = false, // what's left is in use; don't offer it again by default
        CanCheck = CanCheck,
    };

    public static ItemRow For(CleanupItem item, bool canCheck, string? detail = null) => new()
    {
        Item = item,
        Name = Path.GetFileName(item.Path) is { Length: > 0 } name ? name : item.Path,
        Detail = detail ?? Path.GetDirectoryName(item.Path) ?? "",
        SizeText = Format.Size(item.Bytes),
        IsChecked = canCheck && item.SelectedByDefault,
        CanCheck = canCheck,
    };

    public static ItemRow Header(string text) => new() { IsHeader = true, Name = text, CanCheck = false };
}
