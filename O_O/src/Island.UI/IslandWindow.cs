using Avalonia.Controls;
using Avalonia.Media;
using Island.Core.Layout;
using Island.UI.Views;
using TKCode.Arc;

namespace Island.UI;

/// <summary>
/// The island's top-level window: borderless, transparent, never resized (docs/design/02-island-shell.md §1).
/// An Arc window, like every O_O window, so popups, menus and toasts get Arc's overlay layer; without its title bar
/// and without extending into system decorations, so nothing but the island's own shape is drawn.
/// <see cref="IslandController"/> positions it and drives its content.
/// </summary>
public sealed class IslandWindow : ArcWindow
{
    public IslandWindow(IslandMetrics metrics)
    {
        Title = "O_O";
        IsTitleBarVisible = false;
        TitleBar = null;
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        // On macOS the platform layer sets a level above the menu bar; Avalonia's Topmost would lower it to "floating".
        Topmost = !OperatingSystem.IsMacOS();
        WindowStartupLocation = WindowStartupLocation.Manual;
        Width = metrics.WindowWidth;
        Height = metrics.WindowHeight;
        _metrics = metrics;
        View = new IslandView();
        // The whole island scales as one (docs/design/07-home-and-layout.md §5): the view keeps its own size and
        // layout in island units and is drawn larger or smaller; the window grows with it.
        Content = _scaler = new LayoutTransformControl
        {
            Child = View,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Top,
        };
        View.Width = metrics.WindowWidth;
        View.Height = metrics.WindowHeight;
    }

    private readonly IslandMetrics _metrics;
    private readonly LayoutTransformControl _scaler;

    public IslandView View { get; }

    /// <summary>How much larger than its island units the island is drawn (1 = 100%).</summary>
    public double IslandScale { get; private set; } = 1;

    /// <summary>Sets the island's scale and its window's size in island units (upright when docked to a side).</summary>
    public void SetIslandSize(double scale, double width, double height)
    {
        if (Math.Abs(scale - IslandScale) < 0.001 && View.Width == width && View.Height == height)
        {
            return;
        }
        IslandScale = scale;
        _scaler.LayoutTransform = Math.Abs(scale - 1) < 0.001 ? null : new ScaleTransform(scale, scale);
        View.Width = width;
        View.Height = height;
        Width = width * scale;
        Height = height * scale;
    }
}
