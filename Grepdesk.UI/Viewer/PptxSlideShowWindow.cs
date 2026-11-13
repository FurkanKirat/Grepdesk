using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;

namespace Grepdesk.UI.Viewer;

/// <summary>Full-screen slide show: one slide scaled to the screen, advanced with keys, clicks or the wheel.</summary>
internal sealed class PptxSlideShowWindow : Window
{
    private readonly int _count;
    private readonly Func<int, Control> _build;
    private readonly Viewbox _host;
    private readonly TextBlock _counter;

    public int Index { get; private set; }

    public PptxSlideShowWindow(int count, int startIndex, Func<int, Control> build)
    {
        _count = count;
        _build = build;
        Index = Math.Clamp(startIndex, 0, Math.Max(0, count - 1));

        WindowState = WindowState.FullScreen;
        SystemDecorations = SystemDecorations.None;
        Background = Brushes.Black;
        ShowInTaskbar = false;
        Title = "Grepdesk";
        Focusable = true;

        _host = new Viewbox { Stretch = Stretch.Uniform, IsHitTestVisible = false };
        _counter = new TextBlock
        {
            Foreground = Brushes.White,
            Opacity = 0.45,
            FontSize = 14,
            Margin = new Thickness(0, 0, 18, 12),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            IsHitTestVisible = false,
        };
        Content = new Grid { Background = Brushes.Black, Children = { _host, _counter } };

        ShowSlide(Index);
        Opened += (_, _) => Dispatcher.UIThread.Post(() => Focus(), DispatcherPriority.Input);
        KeyDown += OnKeyDown;
        PointerPressed += OnPointerPressed;
        PointerWheelChanged += (_, e) =>
        {
            if (e.Delta.Y < 0) Step(+1);
            else if (e.Delta.Y > 0) Step(-1);
            e.Handled = true;
        };
    }

    private void ShowSlide(int index)
    {
        Index = Math.Clamp(index, 0, _count - 1);
        _host.Child = _build(Index);
        _counter.Text = $"{Index + 1} / {_count}";
    }

    private void Step(int delta)
    {
        var next = Index + delta;
        if (next < 0 || next >= _count)
        {
            if (next >= _count) Close(); // running past the last slide ends the show
            return;
        }
        ShowSlide(next);
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Right or Key.Down or Key.PageDown or Key.Space or Key.Enter or Key.N:
                Step(+1);
                break;
            case Key.Left or Key.Up or Key.PageUp or Key.Back or Key.P:
                Step(-1);
                break;
            case Key.Home:
                ShowSlide(0);
                break;
            case Key.End:
                ShowSlide(_count - 1);
                break;
            case Key.Escape:
                Close();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (props.IsLeftButtonPressed) Step(+1);
        else if (props.IsRightButtonPressed) Step(-1);
        e.Handled = true;
    }
}
