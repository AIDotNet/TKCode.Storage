using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.VisualTree;
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
    private float[] _hold = new float[5];   // seconds a bar stays at its peak before it may fall
    private double _gainPeak = 0.3;         // the loudest recent level, so quiet music still fills the bars
    private double _beat;                   // a shared pulse (made-up motion): jumps to 1 on a beat, then decays
    private double _nextBeat;
    private long _lastTry;
    private IslandStatePanel? _panel;        // the island state this sits in; an inactive one is faded out, not removed
    private readonly IBrush?[] _brushes = new IBrush?[9];
    private Color _brushColor;
    private double _brushOpacity = -1;

    static SpectrumBars()
    {
        AffectsRender<SpectrumBars>(ActiveProperty, BarsProperty);
        AffectsMeasure<SpectrumBars>(BarsProperty);
        ActiveProperty.Changed.AddClassHandler<SpectrumBars>((s, _) => s.Refresh());
        SourceProperty.Changed.AddClassHandler<SpectrumBars>((s, _) => s.Refresh());
        BarsProperty.Changed.AddClassHandler<SpectrumBars>((s, _) =>
        {
            s._shown = new float[Math.Clamp(s.Bars, 1, 16)];
            s._hold = new float[s._shown.Length];
        });
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
        _panel = this.FindAncestorOfType<IslandStatePanel>();
        if (_panel is not null)
        {
            _panel.PropertyChanged += OnPanelChanged;
        }
        Refresh();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        if (_panel is not null)
        {
            _panel.PropertyChanged -= OnPanelChanged;
            _panel = null;
        }
        StopListening();
        _animating = false;
    }

    private void OnPanelChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property == IslandStatePanel.IsActiveProperty)
        {
            Refresh();
        }
    }

    /// <summary>
    /// On screen: attached, visible, and in the island state that is showing. A collapsed island keeps its expanded
    /// panel in the tree (faded out); animating there redrew the window every frame and kept the audio tap open.
    /// </summary>
    private bool Shown => VisualRoot is not null && IsEffectivelyVisible && (_panel?.IsActive ?? true);

    /// <summary>Starts or stops the listening and the frame loop to match <see cref="Active"/> and <see cref="Source"/>.</summary>
    private void Refresh()
    {
        StopListening();
        _lastTry = Environment.TickCount64;
        if (!Shown)
        {
            // Come back from rest instead of resuming a frozen frame.
            Array.Fill(_shown, (float)Rest);
            return;
        }
        TryListen();
        if (!_animating && (Active || _shown.Any(v => v > Rest + 0.01f)))
        {
            _animating = true;
            _lastFrame = Environment.TickCount64;
            TopLevel.GetTopLevel(this)?.RequestAnimationFrame(OnFrame);
        }
    }

    private void TryListen()
    {
        if (Active && Source is { Length: > 0 } source && Shown)
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
        if (!Shown)
        {
            _animating = false;
            StopListening();
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

        // Real levels: follow the loudest recent level (slowly forgetting it) so quiet passages still move the bars, then
        // lift the low end a little (a square root), which is closer to how loudness is heard.
        if (fresh)
        {
            var loudest = 0.0;
            foreach (var v in real!)
            {
                loudest = Math.Max(loudest, v);
            }
            _gainPeak = Math.Max(Math.Max(loudest, 0.25), _gainPeak * Math.Exp(-0.25 * dt));
        }
        // Made-up motion: a shared beat that decays, travelling along the bars as a ripple, over a slow organic wave.
        if (Active && !fresh)
        {
            if (_time >= _nextBeat)
            {
                _beat = 1;
                _nextBeat = _time + 0.42 + 0.16 * (0.5 + 0.5 * Math.Sin(_time * 0.7));
            }
            _beat *= Math.Exp(-5.5 * dt);
        }

        for (var i = 0; i < bars; i++)
        {
            float target;
            if (!Active)
            {
                target = (float)Rest;
            }
            else if (fresh)
            {
                // Neighbouring bands blend a little so the bars read as one shape, not as independent needles.
                float Level(int k) => real![Math.Min(real.Length - 1, Math.Clamp(k, 0, bars - 1) * real.Length / bars)];
                var blended = 0.25 * Level(i - 1) + 0.5 * Level(i) + 0.25 * Level(i + 1);
                var level = Math.Sqrt(Math.Clamp(blended / _gainPeak, 0, 1));
                target = (float)(Rest + (1 - Rest) * level);
            }
            else
            {
                var phase = i * 1.7;
                var wave = 0.42 + 0.22 * Math.Sin(_time * (1.9 + 0.35 * i) + phase) + 0.12 * Math.Sin(_time * (4.3 + 0.3 * i) + phase * 2);
                // The beat reaches the bars one after another, the middle ones highest.
                var ripple = _beat * Math.Exp(-0.18 * i) * (0.55 + 0.45 * Math.Sin(Math.PI * (i + 0.5) / bars));
                target = (float)Math.Clamp(Rest + (1 - Rest) * Math.Clamp(wave + 0.45 * ripple, 0, 1), Rest, 1);
            }
            // Snap up, hold the peak for a beat, then fall: frame-rate independent.
            if (target > _shown[i])
            {
                _shown[i] += (float)((target - _shown[i]) * (1 - Math.Exp(-26.0 * dt)));
                _hold[i] = 0.07f;
            }
            else if (_hold[i] > 0)
            {
                _hold[i] -= (float)dt;
            }
            else
            {
                _shown[i] += (float)((target - _shown[i]) * (1 - Math.Exp(-8.0 * dt)));
            }
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
        var baseBrush = Foreground ?? Brushes.White;
        var bars = _shown.Length;
        for (var i = 0; i < bars; i++)
        {
            // Heights on half points (crisp edges), and quieter bars a touch dimmer so a peak stands out.
            var h = Math.Round(Math.Max(BarWidth, _shown[i] * Height_) * 2) / 2;
            var x = i * (BarWidth + Gap);
            var brush = baseBrush;
            if (baseBrush is ISolidColorBrush solid)
            {
                brush = Dimmed(solid, Math.Clamp((_shown[i] - Rest) / (1 - Rest), 0, 1));
            }
            context.DrawRectangle(brush, null, new RoundedRect(new Rect(x, (Height_ - h) / 2, BarWidth, h), BarWidth / 2));
        }
    }

    /// <summary>The bar colour at 60–100 % of its opacity by loudness, in nine cached steps (was a new brush per bar per frame).</summary>
    private IBrush Dimmed(ISolidColorBrush solid, double loudness)
    {
        if (solid.Color != _brushColor || solid.Opacity != _brushOpacity)
        {
            Array.Clear(_brushes);
            _brushColor = solid.Color;
            _brushOpacity = solid.Opacity;
        }
        var step = (int)Math.Round(loudness * (_brushes.Length - 1));
        return _brushes[step] ??= new ImmutableSolidColorBrush(solid.Color, solid.Opacity * (0.6 + 0.4 * step / (_brushes.Length - 1)));
    }
}
