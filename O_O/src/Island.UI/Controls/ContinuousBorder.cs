using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace Island.UI.Controls;

/// <summary>
/// A Decorator that fills and clips its child to a <see cref="ContinuousShape"/>: continuous corners, and concave
/// shoulders when <see cref="Shoulder"/> is set and the top corners are square. The outline is rebuilt whenever the
/// size or the corners change, so it follows the island's spring animation frame by frame.
/// </summary>
public sealed class ContinuousBorder : Decorator
{
    public static readonly StyledProperty<IBrush?> BackgroundProperty =
        AvaloniaProperty.Register<ContinuousBorder, IBrush?>(nameof(Background));

    public static readonly StyledProperty<CornerRadius> CornerRadiusProperty =
        AvaloniaProperty.Register<ContinuousBorder, CornerRadius>(nameof(CornerRadius));

    public static readonly StyledProperty<double> SmoothingProperty =
        AvaloniaProperty.Register<ContinuousBorder, double>(nameof(Smoothing), ContinuousShape.DefaultSmoothing);

    public static readonly StyledProperty<double> ShoulderProperty =
        AvaloniaProperty.Register<ContinuousBorder, double>(nameof(Shoulder));

    private Geometry? _shape;

    static ContinuousBorder()
    {
        AffectsRender<ContinuousBorder>(BackgroundProperty, CornerRadiusProperty, SmoothingProperty, ShoulderProperty);
        CornerRadiusProperty.Changed.AddClassHandler<ContinuousBorder>((b, _) => b.Rebuild());
        SmoothingProperty.Changed.AddClassHandler<ContinuousBorder>((b, _) => b.Rebuild());
        ShoulderProperty.Changed.AddClassHandler<ContinuousBorder>((b, _) => b.Rebuild());
    }

    public IBrush? Background
    {
        get => GetValue(BackgroundProperty);
        set => SetValue(BackgroundProperty, value);
    }

    public CornerRadius CornerRadius
    {
        get => GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>0 draws circular corners; Apple's are about 0.6.</summary>
    public double Smoothing
    {
        get => GetValue(SmoothingProperty);
        set => SetValue(SmoothingProperty, value);
    }

    /// <summary>Radius of the concave curves joining a top-attached shape to the screen edge; 0 for none.</summary>
    public double Shoulder
    {
        get => GetValue(ShoulderProperty);
        set => SetValue(ShoulderProperty, value);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var arranged = base.ArrangeOverride(finalSize);
        Rebuild(arranged);
        return arranged;
    }

    private void Rebuild() => Rebuild(Bounds.Size);

    private void Rebuild(Size size)
    {
        _shape = ContinuousShape.Build(size, CornerRadius, Smoothing, Shoulder);
        if (Child is { } child)
        {
            child.Clip = _shape; // content never paints outside the island
        }
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (_shape is not null && Background is { } brush)
        {
            context.DrawGeometry(brush, null, _shape);
        }
    }
}
