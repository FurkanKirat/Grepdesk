using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace Grepdesk.UI.Cleanup;

/// <summary>
/// Shows the "is it safe to delete?" prompt before it leaves the app. Nothing
/// is sent anywhere: the user reads (and can edit) the text, then copies it
/// into whichever AI chat they use.
/// </summary>
internal sealed class AskAiDialog : Window
{
    private static LocalizationService Loc => LocalizationService.Instance;

    private AskAiDialog(string prompt)
    {
        Title = Loc.Get("AskTitle");
        Width = 680;
        Height = 560;
        MinWidth = 460;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#1e1e2e");

        var text = new TextBox
        {
            Text = prompt,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Background = Brush.Parse("#181825"),
            Foreground = Brush.Parse("#cdd6f4"),
            BorderBrush = Brush.Parse("#313244"),
            CornerRadius = new CornerRadius(6),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(text, Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);

        var status = new TextBlock { Foreground = Brush.Parse("#a6e3a1"), FontSize = 12, VerticalAlignment = VerticalAlignment.Center };

        var copy = Button(Loc.Get("AskCopy"), "#89b4fa", "#11111b");
        copy.Click += async (_, _) =>
        {
            if (Clipboard is null) return;
            await Clipboard.SetTextAsync(text.Text ?? "");
            status.Text = Loc.Get("AskCopied");
        };
        var close = Button(Loc.Get("JobClose"), "#313244", "#cdd6f4");
        close.Click += (_, _) => Close();

        var privacy = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#89b4fa"), 0.08),
            BorderBrush = Brush.Parse("#89b4fa"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(10, 7),
            Child = new TextBlock
            {
                Text = Loc.Get("AskPrivacy"),
                Foreground = Brush.Parse("#89b4fa"),
                FontSize = 11.5,
                TextWrapping = TextWrapping.Wrap,
            },
        };

        var buttons = new DockPanel { Margin = new Thickness(0, 10, 0, 0) };
        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { close, copy } };
        DockPanel.SetDock(right, Dock.Right);
        buttons.Children.Add(right);
        buttons.Children.Add(status);

        var header = new TextBlock
        {
            Text = Loc.Get("AskIntro"),
            Foreground = Brush.Parse("#a6adc8"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8),
        };

        var layout = new DockPanel { Margin = new Thickness(16) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(privacy, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        privacy.Margin = new Thickness(0, 0, 0, 10);
        layout.Children.Add(header);
        layout.Children.Add(privacy);
        layout.Children.Add(buttons);
        layout.Children.Add(text);
        Content = layout;

        KeyDown += (_, e) => { if (e.Key == Key.Escape) Close(); };
    }

    private static Button Button(string content, string background, string foreground) => new()
    {
        Content = content,
        Background = Brush.Parse(background),
        Foreground = Brush.Parse(foreground),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(14, 6),
    };

    public static Task ShowAsync(Window owner, string prompt) => new AskAiDialog(prompt).ShowDialog(owner);
}
