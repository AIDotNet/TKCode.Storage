namespace Island.Core.State;

/// <summary>Timing of pointer-driven transitions.</summary>
public sealed record HoverOptions
{
    /// <summary>How long the pointer rests on the island before it expands. Null: only a click expands it.</summary>
    public TimeSpan? ExpandDelay { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How long the pointer is away before an expanded island collapses.</summary>
    public TimeSpan CollapseDelay { get; init; } = TimeSpan.FromMilliseconds(600);
}

/// <summary>
/// Pointer-driven state of the island. Fed with "is the pointer on the island" samples and a monotonic clock, so it is
/// deterministic and testable; the UI layer owns the timer.
/// </summary>
public sealed class IslandStateMachine(HoverOptions options)
{
    private TimeSpan? _insideSince;
    private TimeSpan? _outsideSince;
    private bool _pinned;

    /// <summary>The state shown when the island is not expanded: Compact while an activity runs, Idle otherwise.</summary>
    public IslandState RestingState { get; private set; } = IslandState.Idle;

    public IslandState State { get; private set; } = IslandState.Idle;

    public event Action<IslandState>? StateChanged;

    public void SetRestingState(IslandState state)
    {
        if (state == IslandState.Expanded)
        {
            throw new ArgumentException("The resting state cannot be Expanded.", nameof(state));
        }
        RestingState = state;
        if (State != IslandState.Expanded)
        {
            Set(state);
        }
    }

    /// <summary>Feeds one pointer sample.</summary>
    public void Update(bool pointerInside, TimeSpan now)
    {
        if (pointerInside)
        {
            _outsideSince = null;
            _insideSince ??= now;
            if (State != IslandState.Expanded && options.ExpandDelay is { } delay && now - _insideSince >= delay)
            {
                Set(IslandState.Expanded);
            }
        }
        else
        {
            _insideSince = null;
            _outsideSince ??= now;
            if (State == IslandState.Expanded && !_pinned && now - _outsideSince >= options.CollapseDelay)
            {
                Set(RestingState);
            }
        }
    }

    /// <summary>A click on the island: expands it; a click on an expanded island is left to its content.</summary>
    public void Click()
    {
        if (State != IslandState.Expanded)
        {
            Set(IslandState.Expanded);
        }
    }

    /// <summary>Keeps the island expanded until <see cref="Collapse"/>, e.g. while a text field in it has focus.</summary>
    public void Pin(bool pinned) => _pinned = pinned;

    /// <summary>Escape, or focus leaving the island.</summary>
    public void Collapse()
    {
        _pinned = false;
        Set(RestingState);
    }

    private void Set(IslandState state)
    {
        if (State == state)
        {
            return;
        }
        State = state;
        StateChanged?.Invoke(state);
    }
}
