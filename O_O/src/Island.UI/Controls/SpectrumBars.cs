using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Island.Platform;

namespace Island.UI.Controls;

/// <summary>Where the island's visualisers get real loudness from; set once by the app (the platform's audio source).</summary>
public static class AudioLevels
{
    public static IAudioLevelSource Source { get; set; } = new NullAudioLevelSource();
}

/// <summary>
/// A few bars that dance to the music: real band levels of one application's audio when the platform gives them
/// (<see cref="Source"/> names the process), otherwise a smooth made-up motion. Draws only while <see cref="Active"/>
/// (and settles to resting bars within a moment of it going false), 30+ fps on the animation clock, so it costs nothing
/// when the music is paused or the island is closed.
/// </summary>
public sealed class SpectrumBars : TemplatedControl
{
    public static readonly StyledProperty<bool> ActiveProperty = AvaloniaProperty.Register<SpectrumBars, bool>(nameof(Active));
    public static readonly StyledProperty<int> BarsProperty = AvaloniaProperty.Register<SpectrumBars, int>(nameof(Bars), 5);
    public static readonly StyledProperty<string?> SourceProperty = AvaloniaProperty.Register<SpectrumBars, string?>(nameof(Source));

    private const double BarWidth = 3, Gap = 2, Height_ = 16, Rest = 0.16;
    private static readonly TimeSpan RealTimeout = TimeSpan.FromMilliseconds(700);

    private float[] _shown = new float[5];
    private volatile float[]? _real;
    private long _realAtTicks;
    private IDisposable? _listening;
    private bool _animating;
    private long _lastFrame;
    private double _time;
    private readonly Random _random = new(7);
    private long _lastTry;

    static SpectrumBars()
    {
        AffectsRender<SpectrumBars>(ActiveProperty, BarsProperty);
        AffectsMeasure<SpectrumBars>(BarsProperty);
        ActiveProperty.Changed.AddClassHandler<SpectrumBars>((s, _) => s.Refresh());
        SourceProperty.Changed.AddClassHandler<SpectrumBars>((s, _) => s.Refresh());
        BarsProperty.Changed.AddClassHandler<SpectrumBars>((s, _) => s._shown = new float[Math.Clamp(s.Bars, 1, 16)]);
    }

    public bool Active { get => GetValue(ActiveProperty); set => SetValue(ActiveProperty, value); }

    /// <summary>1–16 bars.</summary>
    public int Bars { get => GetValue(BarsProperty); set => SetValue(BarsProperty, Math.Clamp(value, 1, 16)); }

    /// <summary>The process (name, or part of it) whose audio drives the bars; null: made-up motion.</summary>
    public string? Source { get => GetValue(SourceProperty); set => SetValue(SourceProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Bars * BarWidth + (Bars - 1) * Gap, Height_);

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        StopListening();
        _animating = false;
    }

    /// <summary>Starts or stops the listening and the frame loop to match <see cref="Active"/> and <see cref="Source"/>.</summary>
    private void Refresh()
    {
        StopListening();
        _lastTry = Environment.TickCount64;
        TryListen();
        if (VisualRoot is not null && !_animating && (Active || _shown.Any(v => v > Rest + 0.01f)))
        {
            _animating = true;
            _lastFrame = Environment.TickCount64;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
    }

    private void TryListen()
    {
        if (Active && Source is { Length: > 0 } source && VisualRoot is not null)
        {
            _listening = AudioLevels.Source.Listen(source, Math.Clamp(Bars, 1, 16), levels =>
            {
                _real = levels;
                Interlocked.Exchange(ref _realAtTicks, Environment.TickCount64);
            });
        }
    }

    private void StopListening()
    {
        _listening?.Dispose();
        _listening = null;
        _real = null;
    }

    private void OnFrame(TimeSpan _)
    {
        if (VisualRoot is null)
        {
            _animating = false;
            return;
        }
        var now = Environment.TickCount64;
        // The app may start (or the permission be given) after the island showed: look again every few seconds.
        if (Active && _listening is null && Source is { Length: > 0 } && now - _lastTry > 3000)
        {
            _lastTry = now;
            TryListen();
        }
        var dt = Math.Clamp((now - _lastFrame) / 1000.0, 0.001, 0.1);
        _lastFrame = now;
        _time += dt;

        var real = _real;
        var fresh = real is not null && now - Interlocked.Read(ref _realAtTicks) < RealTimeout.TotalMilliseconds;
        var bars = _shown.Length;
        var moving = false;
        for (var i = 0; i < bars; i++)
        {
            float target;
            if (!Active)
            {
                target = (float)Rest;
            }
            else if (fresh)
            {
                // The levels come in more bands than bars only if Bars changed meanwhile; stretch to fit.
                target = real![Math.Min(real.Length - 1, i * real.Length / bars)];
                target = (float)(Rest + (1 - Rest) * target);
            }
            else
            {
                var phase = i * 1.7;
                var wave = 0.5 + 0.28 * Math.Sin(_time * (2.3 + 0.55 * i) + phase) + 0.14 * Math.Sin(_time * (5.1 + 0.4 * i) + phase * 2);
                target = (float)Math.Clamp(Rest + (1 - Rest) * wave * (0.75 + 0.25 * _random.NextDouble()), Rest, 1);
            }
            // Fast attack, slower release, frame-rate independent.
            var rate = target > _shown[i] ? 18.0 : 7.0;
            _shown[i] += (float)((target - _shown[i]) * (1 - Math.Exp(-rate * dt)));
            moving |= Math.Abs(target - _shown[i]) > 0.004;
        }
        InvalidateVisual();
        if (Active || moving)
        {
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
        else
        {
            _animating = false;
        }
    }

    public override void Render(DrawingContext context)
    {
        var brush = Foreground ?? Brushes.White;
        var bars = _shown.Length;
        for (var i = 0; i < bars; i++)
        {
            var h = Math.Max(BarWidth, _shown[i] * Height_);
            var x = i * (BarWidth + Gap);
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, (Height_ - h) / 2, BarWidth, h), BarWidth / 2));
        }
    }
}
