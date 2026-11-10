using Avalonia;
using Avalonia.Media;
using Island.Plugins;
using TKCode.Arc.Motion;

namespace Island.UI.Themes;

/// <summary>
/// The selected theme's island layer (docs/design/09-island-v2.md §4), as the island draws it: the surface brush away
/// from a notch, the activity accents, the corner scale and the spring of the shape. <see cref="ThemeService"/> sets it;
/// the island reads <see cref="Current"/> and listens to <see cref="Changed"/>.
/// </summary>
public sealed class IslandTheme
{
    /// <summary>Accent colours when the theme gives none (Apple's system colours, dark variants).</summary>
    public static readonly IReadOnlyDictionary<string, Color> DefaultAccents = new Dictionary<string, Color>(StringComparer.Ordinal)
    {
        ["music"] = Color.Parse("#FF375F"),
        ["timer"] = Color.Parse("#FF9F0A"),
        ["calendar"] = Color.Parse("#0A84FF"),
        ["message"] = Color.Parse("#30D158"),
        ["reminder"] = Color.Parse("#FF9F0A"),
        ["warning"] = Color.Parse("#FFD60A"),
    };

    /// <summary>The key the island surface's brush is published under; around a notch the surface ignores it.</summary>
    public const string SurfaceBrushKey = "IslandSurfaceBrush";

    public static IslandTheme Current { get; private set; } = new(null, null);

    /// <summary>Raised on the UI thread after <see cref="Current"/> changed.</summary>
    public static event Action? Changed;

    private readonly IslandThemeSpec? _spec;
    private readonly string? _directory;

    private IslandTheme(IslandThemeSpec? spec, string? directory)
    {
        _spec = spec;
        _directory = directory;
    }

    /// <summary>The activity card's layout: standard, stacked or media.</summary>
    public string ExpandedLayout => _spec?.Layout?.Expanded ?? "standard";

    /// <summary>How the bubble leaves the island: slide or droplet.</summary>
    public string Split => _spec?.Motion?.Split ?? "slide";

    /// <summary>The theme's alert sound (a file in its plugin folder), or null.</summary>
    public string? AlertSound =>
        _spec?.Sounds?.Alert is { } alert && _directory is not null && Path.GetFullPath(Path.Combine(_directory, alert)) is var path &&
        path.StartsWith(Path.GetFullPath(_directory) + Path.DirectorySeparatorChar, StringComparison.Ordinal) && File.Exists(path)
            ? path
            : null;

    /// <summary>The surface away from a notch; null: the default black.</summary>
    public IBrush? Surface => SurfaceOf(_spec?.Surface);

    /// <summary>An animated surface: its preset, colours and speed; null when the surface does not move.</summary>
    public (string Preset, IReadOnlyList<Color> Colors, double Speed)? Animated =>
        _spec?.Surface is { Material: "animated", Animation: { } preset, Colors: { Count: >= 2 } colors } s
            ? (preset, [.. colors.Select(Color.Parse)], s.Speed ?? 1)
            : null;

    /// <summary>How much the island's corners are scaled (1: as designed).</summary>
    public double CornerScale => _spec?.Shape?.Radius ?? 1;

    /// <summary>The spring the shape moves with; null: the island's default (Arc's Morph).</summary>
    public ArcSpring? Spring => _spec?.Motion?.Spring switch
    {
        "calm" => ArcSpring.Gentle,
        "snappy" => ArcSpring.Snappy,
        "bouncy" => ArcSpring.Responsive,
        _ => null,
    };

    /// <summary>
    /// An activity's accent: <c>#RRGGBB</c> as given, a name the theme colours, a name with a default colour; null for
    /// none (the app's accent then applies).
    /// </summary>
    public Color? Accent(string? accent)
    {
        if (string.IsNullOrEmpty(accent))
        {
            return null;
        }
        if (accent.StartsWith('#'))
        {
            return Color.TryParse(accent, out var direct) ? direct : null;
        }
        if (_spec?.Content?.Accents?.TryGetValue(accent, out var themed) == true && Color.TryParse(themed, out var c))
        {
            return c;
        }
        return DefaultAccents.TryGetValue(accent, out var fallback) ? fallback : null;
    }

    /// <summary>Makes <paramref name="spec"/> (from the plugin in <paramref name="directory"/>) the island's look and publishes its surface brush.</summary>
    public static void Apply(IslandThemeSpec? spec, string? directory = null)
    {
        Current = new IslandTheme(spec, directory);
        if (Application.Current?.Resources is { } resources)
        {
            if (Current.Surface is { } brush)
            {
                resources[SurfaceBrushKey] = brush;
            }
            else
            {
                resources.Remove(SurfaceBrushKey); // back to the island's own default
            }
        }
        Changed?.Invoke();
    }

    private static IBrush? SurfaceOf(IslandSurfaceSpec? s)
    {
        if (s is null)
        {
            return null;
        }
        switch (s.Material)
        {
            case "gradient" when s.Colors is { Count: >= 2 } colors:
            {
                // The angle turns the gradient's axis around the centre (0°: left to right, 90°: top to bottom).
                var radians = (s.Angle ?? 135) * Math.PI / 180;
                var dx = Math.Cos(radians) / 2;
                var dy = Math.Sin(radians) / 2;
                var brush = new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0.5 - dx, 0.5 - dy, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0.5 + dx, 0.5 + dy, RelativeUnit.Relative),
                };
                for (var i = 0; i < colors.Count; i++)
                {
                    brush.GradientStops.Add(new GradientStop(Color.Parse(colors[i]), (double)i / (colors.Count - 1)));
                }
                return brush;
            }
            case "animated" when s.Colors is { Count: >= 1 } colors:
                // The plain colour behind the animation (and of the bubble, which does not animate).
                return new SolidColorBrush(Color.Parse(colors[0]));
            case "translucent":
            {
                var c = Color.Parse(s.Color ?? "#000000");
                return new SolidColorBrush(Color.FromArgb((byte)Math.Round(Math.Clamp(s.Opacity ?? 0.85, 0.5, 1) * 255), c.R, c.G, c.B));
            }
            default:
                return s.Color is { } color ? new SolidColorBrush(Color.Parse(color)) : null;
        }
    }
}
