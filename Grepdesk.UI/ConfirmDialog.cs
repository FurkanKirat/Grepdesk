using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Grepdesk.UI;

/// <summary>A small yes/no dialog styled like the rest of the app.</summary>
internal sealed class ConfirmDialog : Window
{
    private ConfirmDialog(string title, string message, string detail, string confirmText, string cancelText)
    {
        Title = title;
        Width = 440;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#1e1e2e");

        var confirm = new Button
        {
            Content = confirmText,
            Background = Brush.Parse("#f38ba8"),
            Foreground = Brush.Parse("#11111b"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6)
        };
        var cancel = new Button
        {
            Content = cancelText,
            Background = Brush.Parse("#313244"),
            Foreground = Brush.Parse("#cdd6f4"),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(14, 6),
            IsCancel = true
        };
        confirm.Click += (_, _) => Close(true);
        cancel.Click += (_, _) => Close(false);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = message, Foreground = Brush.Parse("#cdd6f4"), FontSize = 14, TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = detail, Foreground = Brush.Parse("#7f849c"), FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Spacing = 8,
                    Margin = new Thickness(0, 10, 0, 0),
                    Children = { cancel, confirm }
                }
            }
        };

        // Destructive action: Cancel has the focus, so a stray Enter keeps the file.
        Opened += (_, _) => cancel.Focus();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(false); };
    }

    public static Task<bool> ShowAsync(Window owner, string title, string message, string detail, string confirmText, string cancelText) =>
        new ConfirmDialog(title, message, detail, confirmText, cancelText).ShowDialog<bool>(owner);
}
