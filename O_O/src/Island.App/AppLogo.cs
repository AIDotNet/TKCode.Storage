using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Media;

namespace Island.App;

/// <summary>
/// The O_O logo: a purple blob with two upright eyes and a small dot above its right shoulder. Drawn from shapes on
/// a 100 x 100 design grid so the app icon (Tools/IconRenderer.cs) and the tray icon (ShellIntegration.cs) are the
/// same picture at any size.
/// </summary>
internal static class AppLogo
{
    private static readonly IBrush Body = new SolidColorBrush(Color.Parse("#7C4DFF"));
    private static readonly IBrush Shade = new SolidColorBrush(Color.Parse("#6A3BE6"));
    private static readonly IBrush Eye = new SolidColorBrush(Color.Parse("#0E0A2A"));

    /// <summary>The logo scaled to fit a <paramref name="size"/> square, on a transparent background.</summary>
    public static Control Create(double size)
    {
        var canvas = new Canvas { Width = 100, Height = 100 };
        // The body sits low-left in the grid; the dot takes the top-right corner.
        canvas.Children.Add(Shape(new Ellipse { Width = 92, Height = 80, Fill = Shade }, 0, 17));
        // A lighter body offset up and to the left leaves a thin darker crescent along the lower right edge.
        canvas.Children.Add(Shape(new Ellipse { Width = 89, Height = 76, Fill = Body }, 0, 17));
        canvas.Children.Add(Shape(new Rectangle { Width = 12.5, Height = 25, RadiusX = 6.25, RadiusY = 6.25, Fill = Eye }, 25, 43));
        canvas.Children.Add(Shape(new Rectangle { Width = 12.5, Height = 25, RadiusX = 6.25, RadiusY = 6.25, Fill = Eye }, 57.5, 43));
        canvas.Children.Add(Shape(new Ellipse { Width = 17.5, Height = 17.5, Fill = Body }, 82.5, 0));
        return new Viewbox { Width = size, Height = size, Child = canvas };
    }

    private static T Shape<T>(T shape, double x, double y) where T : Control
    {
        Canvas.SetLeft(shape, x);
        Canvas.SetTop(shape, y);
        return shape;
    }
}
