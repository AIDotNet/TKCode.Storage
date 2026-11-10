using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Path = Avalonia.Controls.Shapes.Path;

namespace Island.UI.Console;

/// <summary>
/// Close, minimize and zoom in the Apple style for Windows and Linux (docs/design/README.md §6); macOS shows its own.
/// The glyphs appear while the pointer is over the group, and the lights turn grey while the window is inactive, as on
/// macOS. Each light is a 12 px circle in a 20 px hit target.
/// </summary>
public sealed class TrafficLights : StackPanel
{
    private readonly Window _window;
    private readonly List<(Ellipse Light, Path Glyph, string Brush)> _lights = [];

    public TrafficLights(Window window)
    {
        _window = window;
        Orientation = Orientation.Horizontal;
        Spacing = 0;
        Margin = new Thickness(6, 0, 6, 0);
        VerticalAlignment = VerticalAlignment.Center;
        Add("TrafficCloseBrush", "M 3,3 L 9,9 M 9,3 L 3,9", "关闭", () => _window.Close());
        Add("TrafficMinimizeBrush", "M 2.5,6 L 9.5,6", "最小化", () => _window.WindowState = WindowState.Minimized);
        Add("TrafficZoomBrush", "M 3,6 L 9,6 M 6,3 L 6,9", "缩放", () =>
            _window.WindowState = _window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);

        PointerEntered += (_, _) => ShowGlyphs(true);
        PointerExited += (_, _) => ShowGlyphs(false);
        window.Activated += (_, _) => Refresh();
        window.Deactivated += (_, _) => Refresh();
    }

    private void Add(string brush, string glyph, string name, Action action)
    {
        var light = new Ellipse { Width = 12, Height = 12 };
        var path = new Path
        {
            Data = Geometry.Parse(glyph),
            StrokeThickness = 1.4,
            StrokeLineCap = PenLineCap.Round,
            Width = 12,
            Height = 12,
            IsVisible = false,
        };
        path.Bind(Shape.StrokeProperty, path.GetResourceObservable("TrafficGlyphBrush"));
        var target = new Border
        {
            Width = 20,
            Height = 20,
            Background = Brushes.Transparent,
            Cursor = new Cursor(StandardCursorType.Arrow),
            Child = new Panel
            {
                Width = 12,
                Height = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Children = { light, path },
            },
        };
        Avalonia.Automation.AutomationProperties.SetName(target, name);
        target.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left)
            {
                action();
            }
        };
        _lights.Add((light, path, brush));
        Children.Add(target);
        Refresh();
    }

    private void ShowGlyphs(bool show)
    {
        foreach (var (_, glyph, _) in _lights)
        {
            glyph.IsVisible = show;
        }
        Refresh();
    }

    private void Refresh()
    {
        // Inactive windows show grey lights unless the pointer is over them, as on macOS.
        var active = _window.IsActive || IsPointerOver;
        foreach (var (light, _, brush) in _lights)
        {
            light.Bind(Shape.FillProperty, light.GetResourceObservable(active ? brush : "TrafficInactiveBrush"));
        }
    }
}
