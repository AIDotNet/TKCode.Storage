using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;

namespace Island.UI.Console;

/// <summary>
/// An invisible strip that moves its window, for windows without a title bar (the unified, macOS-style console):
/// drag to move, double-click to zoom, like a native title bar.
/// </summary>
public sealed class WindowDragArea : Border
{
    public WindowDragArea()
    {
        Background = Brushes.Transparent; // hit-testable, but draws nothing
        PointerPressed += (_, e) =>
        {
            if (TopLevel.GetTopLevel(this) is not Window window || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (e.ClickCount == 2)
            {
                window.WindowState = window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
            }
            else
            {
                window.BeginMoveDrag(e);
            }
        };
    }
}
