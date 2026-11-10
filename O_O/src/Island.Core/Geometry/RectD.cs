namespace Island.Core.Geometry;

/// <summary>A point in device-independent pixels (points on macOS).</summary>
public readonly record struct PointD(double X, double Y);

/// <summary>A rectangle in device-independent pixels (points on macOS). Y grows downwards.</summary>
public readonly record struct RectD(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double CenterX => X + Width / 2;

    public bool Contains(PointD p) => p.X >= X && p.X < Right && p.Y >= Y && p.Y < Bottom;

    public RectD Inflate(double by) => new(X - by, Y - by, Width + by * 2, Height + by * 2);

    public RectD Union(RectD other)
    {
        var x = Math.Min(X, other.X);
        var y = Math.Min(Y, other.Y);
        return new RectD(x, y, Math.Max(Right, other.Right) - x, Math.Max(Bottom, other.Bottom) - y);
    }

    public override string ToString() => $"({X:0.#}, {Y:0.#}, {Width:0.#}×{Height:0.#})";
}
