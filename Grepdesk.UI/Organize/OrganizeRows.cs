using System.ComponentModel;
using Grepdesk.Core.Grouping;
using Grepdesk.UI.Jobs;

namespace Grepdesk.UI.Organize;

/// <summary>One group in the list on the left.</summary>
public sealed class GroupRow
{
    public required FileGroup Group { get; init; }
    public required string Title { get; init; }
    public required string Subtitle { get; init; }
    public string SizeText => Format.Size(Group.Size);
}

/// <summary>A checkable file or folder of the selected group.</summary>
public sealed class MemberRow : INotifyPropertyChanged
{
    private bool _isChecked;

    public required GroupMember Member { get; init; }
    public required string Detail { get; init; }
    public string Name => Member.Name;
    public string SizeText => Format.Size(Member.Size);
    public bool IsFolder => Member.IsDirectory;

    public string ShowTip => LocalizationService.Instance.Get("ContextMenuShowInFolder");
    public string OpenTip => LocalizationService.Instance.Get("ContextMenuOpen");

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

    public Action? CheckedChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;
}
