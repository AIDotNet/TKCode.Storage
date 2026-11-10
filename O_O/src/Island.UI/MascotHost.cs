using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;
using TKCode.Arc;
using TKCode.Arc.Primitives;

namespace Island.UI;

/// <summary>
/// Shows the current <see cref="MascotStyle"/> mascot at <see cref="Size"/> and follows every change to it. Changing
/// the colour updates the mascot in place; changing its kind swaps it with Arc's blur transition.
/// </summary>
/// <remarks>
/// <see cref="Wander"/> makes a resting mascot (<see cref="Status"/> Idle) now and then do a random action (thinking,
/// writing or done) for a moment, then go back to rest. A mascot whose status a caller sets is never wandered.
/// </remarks>
public sealed class MascotHost : Border
{
    // The actions it may pick: poses every kind of mascot has (listening and error are left out on purpose).
    private static readonly AiMascotStatus[] Actions = [AiMascotStatus.Thinking, AiMascotStatus.Writing, AiMascotStatus.Done];

    private readonly ArcContentSwap _swap = new()
    {
        Mode = ContentSwapMode.BlurSwap,
        HorizontalContentAlignment = HorizontalAlignment.Center,
        VerticalContentAlignment = VerticalAlignment.Center,
    };

    private readonly DispatcherTimer _timer = new();
    private double _size = 28;
    private AiMascotStatus _status = AiMascotStatus.Idle;
    private AiMascotStatus? _action;
    private MascotKind? _kind;
    private Control? _mascot;
    private bool _wander;

    public MascotHost()
    {
        Child = _swap;
        HorizontalAlignment = HorizontalAlignment.Left;
        VerticalAlignment = VerticalAlignment.Center;
        _timer.Tick += OnTick;
    }

    /// <summary>The width and height of the mascot, in px.</summary>
    public double Size
    {
        get => _size;
        set
        {
            _size = value;
            Refresh();
        }
    }

    /// <summary>What the mascot is doing. Idle is the rest pose, and the only one <see cref="Wander"/> changes.</summary>
    public AiMascotStatus Status
    {
        get => _status;
        set
        {
            _status = value;
            Refresh();
        }
    }

    /// <summary>When true, a resting mascot does a random action every few seconds.</summary>
    public bool Wander
    {
        get => _wander;
        set
        {
            _wander = value;
            if (value && VisualRoot is not null)
            {
                ScheduleNext();
            }
            else
            {
                _timer.Stop();
                _action = null;
                Refresh();
            }
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        MascotStyle.Changed += OnStyleChanged;
        Refresh();
        if (_wander)
        {
            ScheduleNext();
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        MascotStyle.Changed -= OnStyleChanged;
        _timer.Stop();
        base.OnDetachedFromVisualTree(e);
    }

    private void OnStyleChanged(object? sender, EventArgs e) => Refresh();

    private void OnTick(object? sender, EventArgs e)
    {
        if (_action is not null)
        {
            // The action is over: rest again, and wait before the next one.
            _action = null;
            Refresh();
            ScheduleNext();
            return;
        }
        if (_status == AiMascotStatus.Idle)
        {
            _action = Actions[Random.Shared.Next(Actions.Length)];
            Refresh();
            _timer.Interval = TimeSpan.FromSeconds(2.4);
            _timer.Start();
            return;
        }
        ScheduleNext();
    }

    /// <summary>Waits a random 6 to 14 seconds before the next action, if the mascot wanders.</summary>
    private void ScheduleNext()
    {
        if (!_wander)
        {
            return;
        }
        _timer.Interval = TimeSpan.FromSeconds(6 + Random.Shared.NextDouble() * 8);
        _timer.Start();
    }

    private void Refresh()
    {
        var shown = _action ?? _status;
        if (_mascot is not null && _kind == MascotStyle.Kind)
        {
            MascotStyle.Apply(_mascot, Size, shown);
            return;
        }
        _kind = MascotStyle.Kind;
        _mascot = MascotStyle.Create(Size, shown);
        _swap.Content = _mascot;
    }
}
