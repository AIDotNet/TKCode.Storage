using Avalonia;
using Avalonia.Controls;

namespace Island.UI.Controls;

/// <summary>
/// Cards in equal columns: as many columns as fit at <see cref="MinItemWidth"/>, every child as wide as its column, and
/// every child in a row as tall as the tallest one, so a grid of cards lines up whatever their text.
/// </summary>
public sealed class ColumnsPanel : Panel
{
    public static readonly StyledProperty<double> MinItemWidthProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(MinItemWidth), 280);

    public static readonly StyledProperty<double> SpacingProperty =
        AvaloniaProperty.Register<ColumnsPanel, double>(nameof(Spacing), 12);

    static ColumnsPanel() => AffectsMeasure<ColumnsPanel>(MinItemWidthProperty, SpacingProperty);

    public double MinItemWidth
    {
        get => GetValue(MinItemWidthProperty);
        set => SetValue(MinItemWidthProperty, value);
    }

    public double Spacing
    {
        get => GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int Columns(double width) =>
        double.IsInfinity(width) || width <= 0 ? 1 : Math.Max(1, (int)((width + Spacing) / (MinItemWidth + Spacing)));

    private double ItemWidth(double width, int columns) =>
        double.IsInfinity(width) ? MinItemWidth : Math.Max(0, (width - Spacing * (columns - 1)) / columns);

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = Columns(availableSize.Width);
        var itemWidth = ItemWidth(availableSize.Width, columns);
        double height = 0, rowHeight = 0;
        var visible = Children.Where(c => c.IsVisible).ToList();
        for (var i = 0; i < visible.Count; i++)
        {
            visible[i].Measure(new Size(itemWidth, double.PositiveInfinity));
            rowHeight = Math.Max(rowHeight, visible[i].DesiredSize.Height);
            if (i % columns == columns - 1 || i == visible.Count - 1)
            {
                height += rowHeight + (i == visible.Count - 1 ? 0 : Spacing);
                rowHeight = 0;
            }
        }
        var width = double.IsInfinity(availableSize.Width) ? itemWidth * Math.Min(columns, Math.Max(1, visible.Count)) : availableSize.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var columns = Columns(finalSize.Width);
        var itemWidth = ItemWidth(finalSize.Width, columns);
        var visible = Children.Where(c => c.IsVisible).ToList();
        double y = 0;
        for (var start = 0; start < visible.Count; start += columns)
        {
            var row = visible.Skip(start).Take(columns).ToList();
            var rowHeight = row.Max(c => c.DesiredSize.Height);
            for (var i = 0; i < row.Count; i++)
            {
                row[i].Arrange(new Rect(i * (itemWidth + Spacing), y, itemWidth, rowHeight));
            }
            y += rowHeight + Spacing;
        }
        return finalSize;
    }
}
