using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Grepdesk.UI.Browse;

/// <summary>
/// An image whose resampling can be bound: smooth by default, hard pixels for a small picture enlarged
/// in a cell. (The render option is a code-only setter, so it can't be set from a style or binding directly.)
/// </summary>
public sealed class ThumbnailImage : Image
{
    public static readonly StyledProperty<bool> HardPixelsProperty =
        AvaloniaProperty.Register<ThumbnailImage, bool>(nameof(HardPixels));

    public bool HardPixels
    {
        get => GetValue(HardPixelsProperty);
        set => SetValue(HardPixelsProperty, value);
    }

    public ThumbnailImage() => Apply();

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == HardPixelsProperty) Apply();
    }

    private void Apply() =>
        RenderOptions.SetBitmapInterpolationMode(this, HardPixels ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality);
}
