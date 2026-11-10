using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Media;

namespace Island.UI.Controls;

/// <summary>
/// The outline of Apple-style shapes: rounded rectangles with continuous ("squircle") corners, whose curvature eases in
/// instead of starting abruptly as a circular arc does, optionally with concave "shoulders" where the shape meets the
/// top edge of the screen, like a MacBook notch.
/// The corner construction is the widely used Figma corner-smoothing one: each corner is a short circular arc between
/// two cubic curves, and smoothing trades arc for curve. When a radius is so large that the curves do not fit (a pill),
/// smoothing is reduced automatically, so a capsule stays a capsule.
/// </summary>
public static class ContinuousShape
{
    /// <summary>Apple's own corner smoothing is close to this.</summary>
    public const double DefaultSmoothing = 0.6;

    private readonly record struct Corner(double A, double B, double C, double D, double P, double Arc, double R);

    public static Geometry Build(Size size, CornerRadius radii, double smoothing, double shoulder)
    {
        var w = size.Width;
        var h = size.Height;
        if (w <= 0 || h <= 0)
        {
            return new RectangleGeometry(default);
        }
        var budget = Math.Min(w, h) / 2;
        var tl = Corner_(radii.TopLeft, smoothing, budget);
        var tr = Corner_(radii.TopRight, smoothing, budget);
        var br = Corner_(radii.BottomRight, smoothing, budget);
        var bl = Corner_(radii.BottomLeft, smoothing, budget);
        // Shoulders only make sense on square top corners (the shape hangs from the top edge).
        var s = radii.TopLeft == 0 && radii.TopRight == 0 ? Math.Max(0, shoulder) : 0;

        var p = new StringBuilder();
        void Cmd(string format, params double[] values) =>
            p.Append(CultureInfo.InvariantCulture, $"{string.Format(CultureInfo.InvariantCulture, format, values.Cast<object>().ToArray())} ");

        if (s > 0)
        {
            // Along the top edge, out over the left shoulder, then down into the shape's left side.
            Cmd("M {0},{1}", -s, 0);
            Cmd("L {0},{1}", w + s, 0);
            Cmd("A {0},{0} 0 0 0 {1},{2}", s, w, s);
        }
        else
        {
            Cmd("M {0},{1}", w - tr.P, 0);
            if (tr.R > 0)
            {
                Cmd("c {0},0 {1},0 {2},{3}", tr.A, tr.A + tr.B, tr.A + tr.B + tr.C, tr.D);
                Cmd("a {0},{0} 0 0 1 {1},{1}", tr.R, tr.Arc);
                Cmd("c {0},{1} {0},{2} {0},{3}", tr.D, tr.C, tr.B + tr.C, tr.A + tr.B + tr.C);
            }
        }

        Cmd("L {0},{1}", w, h - br.P);
        if (br.R > 0)
        {
            Cmd("c 0,{0} 0,{1} {2},{3}", br.A, br.A + br.B, -br.D, br.A + br.B + br.C);
            Cmd("a {0},{0} 0 0 1 {1},{2}", br.R, -br.Arc, br.Arc);
            Cmd("c {0},{1} {2},{1} {3},{1}", -br.C, br.D, -(br.B + br.C), -(br.A + br.B + br.C));
        }

        Cmd("L {0},{1}", bl.P, h);
        if (bl.R > 0)
        {
            Cmd("c {0},0 {1},0 {2},{3}", -bl.A, -(bl.A + bl.B), -(bl.A + bl.B + bl.C), -bl.D);
            Cmd("a {0},{0} 0 0 1 {1},{1}", bl.R, -bl.Arc);
            Cmd("c {0},{1} {0},{2} {0},{3}", -bl.D, -bl.C, -(bl.B + bl.C), -(bl.A + bl.B + bl.C));
        }

        if (s > 0)
        {
            Cmd("L {0},{1}", 0, s);
            Cmd("A {0},{0} 0 0 0 {1},{2}", s, -s, 0);
        }
        else
        {
            Cmd("L {0},{1}", 0, tl.P);
            if (tl.R > 0)
            {
                Cmd("c 0,{0} 0,{1} {2},{3}", -tl.A, -(tl.A + tl.B), tl.D, -(tl.A + tl.B + tl.C));
                Cmd("a {0},{0} 0 0 1 {1},{2}", tl.R, tl.Arc, -tl.Arc);
                Cmd("c {0},{1} {2},{1} {3},{1}", tl.C, -tl.D, tl.B + tl.C, tl.A + tl.B + tl.C);
            }
            Cmd("L {0},{1}", w - tr.P, 0);
        }
        p.Append('Z');
        return StreamGeometry.Parse(p.ToString());
    }

    private static Corner Corner_(double radius, double smoothing, double budget)
    {
        var r = Math.Clamp(radius, 0, budget);
        if (r <= 0)
        {
            return default;
        }
        // Less smoothing when the full curve would not fit in half the shorter side.
        smoothing = Math.Clamp(Math.Min(smoothing, budget / r - 1), 0, 1);
        var p = Math.Min((1 + smoothing) * r, budget);
        var arcMeasure = 90 * (1 - smoothing);
        var arc = Math.Sin(Rad(arcMeasure / 2)) * r * Math.Sqrt(2);
        var alpha = (90 - arcMeasure) / 2;
        var p3ToP4 = r * Math.Tan(Rad(alpha / 2));
        var beta = 45 * smoothing;
        var c = p3ToP4 * Math.Cos(Rad(beta));
        var d = c * Math.Tan(Rad(beta));
        var b = (p - arc - c - d) / 3;
        return new Corner(2 * b, b, c, d, p, arc, r);
    }

    private static double Rad(double degrees) => degrees * Math.PI / 180;
}
