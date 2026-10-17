using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Grepdesk.UI.Viewer;

internal enum SaveChoice { Save, Discard, Cancel }

/// <summary>"Save changes to X?" with Save, Don't save and Cancel, styled like <see cref="ConfirmDialog"/>.</summary>
internal sealed class SaveChangesDialog : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private SaveChangesDialog(string fileName)
    {
        Title = Loc.Get("ViewerUnsavedTitle");
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#1e1e2e");

        var save = MakeButton(Loc.Get("ViewerSave"), "#89b4fa", "#11111b");
        var discard = MakeButton(Loc.Get("ViewerDontSave"), "#313244", "#cdd6f4");
        var cancel = MakeButton(Loc.Get("JobCancel"), "#313244", "#cdd6f4");
        cancel.IsCancel = true;
        save.IsDefault = true;
        save.Click += (_, _) => Close(SaveChoice.Save);
        discard.Click += (_, _) => Close(SaveChoice.Discard);
        cancel.Click += (_, _) => Close(SaveChoice.Cancel);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock
                {
                    Text = Loc.Get("ViewerUnsavedMessage", fileName),
                    Foreground = Brush.Parse("#cdd6f4"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { cancel, discard, save }
                }
            }
        };

        Opened += (_, _) => save.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(SaveChoice.Cancel); };
    }

    private static Button MakeButton(string text, string background, string foreground) => new()
    {
        Content = text,
        Background = Brush.Parse(background),
        Foreground = Brush.Parse(foreground),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 6)
    };

    public static async Task<SaveChoice> ShowAsync(Window owner, string fileName) =>
        await new SaveChangesDialog(fileName).ShowDialog<SaveChoice?>(owner) ?? SaveChoice.Cancel;
}
