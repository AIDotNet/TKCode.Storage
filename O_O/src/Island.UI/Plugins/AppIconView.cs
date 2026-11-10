using System.Collections.Concurrent;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Island.UI.Plugins;

/// <summary>
/// <c>ui.appIcon</c> (docs/design/08 §3.2): another app's real icon, from the PNG the host gets from the system, or a
/// round badge with the app's first letter when none is available. Never throws.
/// </summary>
public sealed class AppIconView : Border
{
    private static readonly ConcurrentDictionary<string, Bitmap> Cache = new(StringComparer.Ordinal);

    /// <summary>Gives the PNG bytes of an app's icon (set by the app: <c>host.AppIcon</c>); null when there is none.</summary>
    public static Func<string, byte[]?>? AppIconResolver { get; set; }

    internal static void ClearCache() => Cache.Clear();

    /// <summary>The icon of <paramref name="appId"/>, decoded once; null when the resolver has none.</summary>
    public static Bitmap? IconFor(string appId)
    {
        if (Cache.TryGetValue(appId, out var cached))
        {
            return cached;
        }
        try
        {
            if (AppIconResolver?.Invoke(appId) is { Length: > 0 } png)
            {
                using var stream = new MemoryStream(png);
                var bitmap = new Bitmap(stream);
                Cache[appId] = bitmap;
                return bitmap;
            }
        }
        catch (Exception)
        {
            // A broken icon is the same as none.
        }
        return null;
    }

    /// <summary>The fallback letter: the first character of the id's last dot-separated segment, uppercased.</summary>
    public static string Letter(string? appId)
    {
        var segment = (appId ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries).LastOrDefault()?.Trim() ?? "";
        return segment.Length == 0 ? "?" : char.ToUpperInvariant(segment[0]).ToString();
    }

    public static double ClampSize(double? size) => Math.Clamp(size ?? 20, 8, 64);

    public void Update(string? appId, double? size)
    {
        var px = ClampSize(size);
        Width = Height = px;
        CornerRadius = new CornerRadius(px / 2);
        HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left;
        VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center;
        var bitmap = string.IsNullOrEmpty(appId) ? null : IconFor(appId);
        if (bitmap is not null)
        {
            Background = null;
            CornerRadius = new CornerRadius(px * 0.22);
            Child = new Image { Source = bitmap, Stretch = Stretch.Uniform };
            return;
        }
        Background = Resource("IslandSecondaryBrush") ?? Brushes.Gray;
        Child = new TextBlock
        {
            Text = Letter(appId),
            FontSize = px * 0.5,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brushes.Black,
            HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
            VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center,
        };
    }

    private static IBrush? Resource(string key) =>
        Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;
}
