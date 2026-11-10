using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using SkiaSharp;

namespace Island.UI.Controls;

/// <summary>
/// A theme's animated surface (docs/design/09-island-v2.md §4): one of a few built-in shader presets, drawn behind the
/// island's content and clipped to its shape by <see cref="ContinuousBorder"/>. Themes choose a preset and its colours;
/// they ship no shader code. At most 30 frames a second, only while it can be seen; a still frame with reduced motion;
/// nothing around a real notch (<see cref="Suppressed"/>).
/// </summary>
public sealed class AnimatedSurface : Control
{
    /// <summary>The presets themes may name.</summary>
    public static readonly IReadOnlySet<string> Presets = new HashSet<string>(StringComparer.Ordinal) { "aurora", "pulse", "waves" };

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(1000.0 / 30);
    private static readonly Dictionary<string, SKRuntimeEffect?> Effects = new(StringComparer.Ordinal);

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly DispatcherTimer _timer;
    private string? _preset;
    private Color[] _colors = [];
    private double _speed = 1;
    private bool _suppressed;
    private bool _still;

    public AnimatedSurface()
    {
        IsHitTestVisible = false;
        _timer = new DispatcherTimer(Frame, DispatcherPriority.Render, (_, _) => InvalidateVisual());
        AttachedToVisualTree += (_, _) => UpdateTimer();
        DetachedFromVisualTree += (_, _) => _timer.Stop();
        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                UpdateTimer();
            }
        };
    }

    /// <summary>Shows <paramref name="preset"/> in <paramref name="colors"/> (2 or 3), or nothing for a null preset.</summary>
    public void Show(string? preset, IReadOnlyList<Color> colors, double speed, bool still)
    {
        _preset = preset is not null && Presets.Contains(preset) && colors.Count >= 2 ? preset : null;
        _colors = [.. colors.Take(3)];
        if (_colors.Length == 2)
        {
            _colors = [_colors[0], _colors[1], _colors[0]];
        }
        _speed = Math.Clamp(speed, 0.2, 2);
        _still = still;
        IsVisible = _preset is not null && !_suppressed;
        UpdateTimer();
        InvalidateVisual();
    }

    /// <summary>Around a real notch the island is the notch's black: the animation is not drawn.</summary>
    public bool Suppressed
    {
        get => _suppressed;
        set
        {
            _suppressed = value;
            IsVisible = _preset is not null && !value;
            UpdateTimer();
        }
    }

    private void UpdateTimer()
    {
        var run = _preset is not null && !_still && IsVisible && TopLevel.GetTopLevel(this) is not null;
        if (run && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!run && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    public override void Render(DrawingContext context)
    {
        if (_preset is not { } preset || Bounds.Width <= 0 || Bounds.Height <= 0 || Effect(preset) is not { } effect)
        {
            return;
        }
        var time = _still ? 2.0 : _clock.Elapsed.TotalSeconds * _speed;
        context.Custom(new Draw(new Rect(Bounds.Size), effect, (float)time, _colors));
    }

    private static SKRuntimeEffect? Effect(string preset)
    {
        lock (Effects)
        {
            if (!Effects.TryGetValue(preset, out var effect))
            {
                effect = SKRuntimeEffect.CreateShader(Source(preset), out _);
                Effects[preset] = effect; // null when the GPU backend refuses it: then nothing is drawn
            }
            return effect;
        }
    }

    /// <summary>Whether a preset's shader compiles: null when it does, Skia's message otherwise (for tests).</summary>
    public static string? CompileErrors(string preset)
    {
        using var effect = SKRuntimeEffect.CreateShader(Source(preset), out var errors);
        return effect is null ? errors ?? "no effect" : null;
    }

    /// <summary>The presets, in SkSL. Uniforms: size, time (seconds × speed), three colours.</summary>
    private static string Source(string preset) => Header + preset switch
    {
        // Slow bands of light flowing sideways.
        "aurora" => """
            half4 main(float2 p) {
              float2 uv = p / size;
              float a = sin(uv.x * 3.0 + time * 0.6) * 0.5 + 0.5;
              float b = sin(uv.x * 5.0 - time * 0.4 + uv.y * 4.0) * 0.5 + 0.5;
              half4 base = mix(c0, c1, half(a));
              return mix(base, c2, half(b * (0.25 + 0.5 * uv.y)));
            }
            """,
        // A soft glow breathing in the middle.
        "pulse" => """
            half4 main(float2 p) {
              float2 uv = (p - size * 0.5) / size.y;
              float d = length(uv * float2(0.5, 1.0));
              float glow = (0.55 + 0.45 * sin(time * 1.6)) * exp(-d * 3.0);
              return mix(mix(c0, c1, half(clamp(glow, 0.0, 1.0))), c2, half(0.15 * glow));
            }
            """,
        // Waves rolling across.
        _ => """
            half4 main(float2 p) {
              float2 uv = p / size;
              float w = sin(uv.x * 9.0 + time * 1.2 + sin(uv.y * 3.0 + time) * 1.5) * 0.5 + 0.5;
              return mix(mix(c0, c1, half(uv.x)), c2, half(w * 0.35));
            }
            """,
    };

    private const string Header = """
        uniform float2 size;
        uniform float time;
        uniform half4 c0;
        uniform half4 c1;
        uniform half4 c2;

        """;

    private sealed class Draw(Rect bounds, SKRuntimeEffect effect, float time, Color[] colors) : ICustomDrawOperation
    {
        public Rect Bounds => bounds;

        public bool HitTest(Point p) => false;

        public bool Equals(ICustomDrawOperation? other) => false;

        public void Dispose()
        {
        }

        public void Render(ImmediateDrawingContext context)
        {
            if (context.TryGetFeature<ISkiaSharpApiLeaseFeature>() is not { } feature)
            {
                return;
            }
            using var lease = feature.Lease();
            static float[] Rgba(Color c) => [c.R / 255f, c.G / 255f, c.B / 255f, c.A / 255f];
            var uniforms = new SKRuntimeEffectUniforms(effect)
            {
                ["size"] = new[] { (float)bounds.Width, (float)bounds.Height },
                ["time"] = time,
                ["c0"] = Rgba(colors[0]),
                ["c1"] = Rgba(colors[1]),
                ["c2"] = Rgba(colors[2]),
            };
            using var shader = effect.ToShader(uniforms);
            using var paint = new SKPaint { Shader = shader };
            lease.SkCanvas.DrawRect(SKRect.Create((float)bounds.Width, (float)bounds.Height), paint);
        }
    }
}
