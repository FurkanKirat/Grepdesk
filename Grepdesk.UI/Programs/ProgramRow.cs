using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Grepdesk.Core.Programs;

namespace Grepdesk.UI.Programs;

/// <summary>One program row in the Programs list.</summary>
public sealed class ProgramRow : INotifyPropertyChanged
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private static readonly IBrush StarGold = Brush.Parse("#f9e2af");
    private static readonly IBrush StarMuted = Brush.Parse("#6c7086");

    private bool _isFavorite;
    private Bitmap? _icon;

    public ProgramRow(ProgramEntry entry, bool isFavorite, string? highlight)
    {
        Entry = entry;
        _isFavorite = isFavorite;

        if (!string.IsNullOrWhiteSpace(highlight))
        {
            var idx = entry.Name.IndexOf(highlight, StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                NameBefore = entry.Name[..idx];
                NameMatch = entry.Name.Substring(idx, highlight.Length);
                NameAfter = entry.Name[(idx + highlight.Length)..];
            }
            else
            {
                NameBefore = entry.Name;
            }
        }
        else
        {
            NameBefore = entry.Name;
        }

        Subtitle = BuildSubtitle(entry);
    }

    public ProgramEntry Entry { get; }
    public string Name => Entry.Name;
    public string NameBefore { get; } = "";
    public string NameMatch { get; } = "";
    public string NameAfter { get; } = "";
    public string Subtitle { get; }
    public string TooltipText => string.Equals(Entry.LaunchPath, Entry.TargetPath, StringComparison.OrdinalIgnoreCase)
        ? Entry.LaunchPath
        : $"{Entry.Name}\n{Entry.TargetPath}";

    public bool IsFavorite
    {
        get => _isFavorite;
        set
        {
            if (_isFavorite == value) return;
            _isFavorite = value;
            Changed();
            Changed(nameof(IsNotFavorite));
            Changed(nameof(StarForeground));
            Changed(nameof(FavoriteTip));
        }
    }

    public bool IsNotFavorite => !_isFavorite;
    public IBrush StarForeground => _isFavorite ? StarGold : StarMuted;
    public string FavoriteTip => Loc.Get(_isFavorite ? "ProgramsRemoveFavoriteTip" : "ProgramsAddFavoriteTip");
    public string OpenTip => Loc.Get("ProgramsLaunchTip");
    public string ShowTip => Loc.Get("ContextMenuShowInFolder");

    public Bitmap? Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            Changed();
            Changed(nameof(ShowFallbackIcon));
        }
    }

    public bool ShowFallbackIcon => _icon is null;

    private static string BuildSubtitle(ProgramEntry entry)
    {
        var sourceLabel = entry.Source switch
        {
            ProgramSource.Desktop => Loc.Get("ProgramsSourceDesktop"),
            ProgramSource.Custom => Loc.Get("ProgramsSourceCustom"),
            _ => entry.Category,
        };

        var targetDisplay = entry.TargetPath;
        if (string.IsNullOrWhiteSpace(sourceLabel))
            return targetDisplay;

        return $"{sourceLabel}  ·  {targetDisplay}";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Changed([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
