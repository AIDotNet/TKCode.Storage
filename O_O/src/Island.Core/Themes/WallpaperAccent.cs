namespace Island.Core.Themes;

/// <summary>
/// The accent a wallpaper suggests (docs/design/05-builtin-plugins.md §3, "从壁纸取色"): the most prominent colourful
/// hue, ignoring greys, near-black and near-white, made vivid enough to work as an accent. Null for a wallpaper with
/// no real colour (then the accent stays as it is).
/// </summary>
public static class WallpaperAccent
{
    private const int HueBuckets = 24;
    private const double MinSaturation = 0.22;
    private const double MinValue = 0.2;
    private const double MaxValueForWhite = 0.97;

    /// <summary>
    /// Picks the accent from pixels in BGRA order (4 bytes each, as Avalonia's default bitmap format): the hue bucket
    /// with the most weight (saturation × brightness), averaged within the bucket.
    /// </summary>
    /// <returns>(R, G, B), or null when fewer than 2% of the pixels are colourful.</returns>
    public static (byte R, byte G, byte B)? Pick(ReadOnlySpan<byte> bgra)
    {
        var weight = new double[HueBuckets];
        var sumR = new double[HueBuckets];
        var sumG = new double[HueBuckets];
        var sumB = new double[HueBuckets];
        var pixels = bgra.Length / 4;
        var colourful = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            if (bgra[i + 3] < 128)
            {
                continue; // transparent
            }
            double b = bgra[i] / 255.0, g = bgra[i + 1] / 255.0, r = bgra[i + 2] / 255.0;
            var (h, s, v) = ToHsv(r, g, b);
            if (s < MinSaturation || v < MinValue || (v > MaxValueForWhite && s < 0.3))
            {
                continue;
            }
            colourful++;
            var bucket = (int)(h / 360 * HueBuckets) % HueBuckets;
            var w = s * v;
            weight[bucket] += w;
            sumR[bucket] += r * w;
            sumG[bucket] += g * w;
            sumB[bucket] += b * w;
        }
        if (pixels == 0 || colourful < pixels * 0.02)
        {
            return null;
        }
        var best = 0;
        for (var i = 1; i < HueBuckets; i++)
        {
            if (weight[i] > weight[best])
            {
                best = i;
            }
        }
        var (hue, sat, val) = ToHsv(sumR[best] / weight[best], sumG[best] / weight[best], sumB[best] / weight[best]);
        // An accent has to read on buttons and switches: keep the hue, lift saturation and brightness into range.
        var (rr, gg, bb) = FromHsv(hue, Math.Clamp(Math.Max(sat, 0.55), 0, 0.9), Math.Clamp(Math.Max(val, 0.6), 0, 0.9));
        return ((byte)Math.Round(rr * 255), (byte)Math.Round(gg * 255), (byte)Math.Round(bb * 255));
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 1e-9)
        {
            h = max == r ? 60 * (((g - b) / d) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
        }
        if (h < 0)
        {
            h += 360;
        }
        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0d),
            < 120 => (x, c, 0d),
            < 180 => (0d, c, x),
            < 240 => (0d, x, c),
            < 300 => (x, 0d, c),
            _ => (c, 0d, x),
        };
        return (r + m, g + m, b + m);
    }
}
