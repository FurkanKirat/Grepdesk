using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using AvaloniaEdit;
using AvaloniaEdit.Editing;
using AvaloniaEdit.Rendering;

namespace Grepdesk.UI.Viewer;

/// <summary>
/// <see cref="TextEditor"/> subclass whose <see cref="TextView"/> clamps <see cref="TextView.MakeVisible"/>
/// requests to the valid scroll bounds, preventing the AvaloniaEdit #606 / #609 infinite scroll-clamp loop
/// when selecting or pasting near the end of long lines with TextMate active.
/// </summary>
public class GrepdeskTextEditor : TextEditor
{
    public GrepdeskTextEditor() : base(new GrepdeskTextArea()) { }

    protected override Type StyleKeyOverride => typeof(TextEditor);
}

internal sealed class GrepdeskTextArea : TextArea
{
    public GrepdeskTextArea() : base(new GrepdeskTextView())
    {
        AddHandler(KeyDownEvent, OnTunnelKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnTunnelKeyUp, RoutingStrategies.Tunnel);
    }

    protected override Type StyleKeyOverride => typeof(TextArea);

    private void OnTunnelKeyDown(object? sender, KeyEventArgs e)
    {
        foreach (var handler in StackedInputHandlers)
        {
            if (e.Handled) break;
            handler.OnPreviewKeyDown(e);
        }
    }

    private void OnTunnelKeyUp(object? sender, KeyEventArgs e)
    {
        foreach (var handler in StackedInputHandlers)
        {
            if (e.Handled) break;
            handler.OnPreviewKeyUp(e);
        }
    }
}

internal sealed class GrepdeskTextView : TextView
{
    protected override Type StyleKeyOverride => typeof(TextView);

    public override void MakeVisible(Rect rectangle)
    {
        var scrollable = (ILogicalScrollable)this;
        base.MakeVisible(ClampToScrollBounds(rectangle, scrollable.Extent, scrollable.Viewport));
    }

    internal static Rect ClampToScrollBounds(Rect rectangle, Size extent, Size viewport)
    {
        var left = rectangle.X;
        var top = rectangle.Y;
        var right = rectangle.Right;
        var bottom = rectangle.Bottom;

        if (viewport.Width > 0)
        {
            var maxRight = Math.Max(extent.Width, viewport.Width);
            left = Math.Clamp(left, 0, maxRight);
            right = Math.Clamp(right, left, maxRight);
        }
        if (viewport.Height > 0)
        {
            var maxBottom = Math.Max(extent.Height, viewport.Height);
            top = Math.Clamp(top, 0, maxBottom);
            bottom = Math.Clamp(bottom, top, maxBottom);
        }

        return new Rect(left, top, right - left, bottom - top);
    }
}
