namespace Island.Core.Activities;

/// <summary>An alert waiting to be shown: the island opens briefly for it (09 §2, 提醒).</summary>
/// <param name="Key">Identifies the alert; an alert with the same key replaces the waiting or shown one.</param>
/// <param name="Kind">The activity kind it belongs to (for the user's policies), or the plugin for a one-off alert.</param>
public sealed record IslandAlert(string Key, string Kind, IslandPriority Priority, TimeSpan Duration);

/// <summary>
/// One alert at a time, in order of priority, then arrival. A <see cref="IslandPriority.Critical"/> alert takes over at
/// once (the interrupted one waits at the front of the queue); others wait their turn. Time is given by the caller, so
/// it is deterministic: call <see cref="Advance"/> from the UI's timer.
/// </summary>
public sealed class AlertQueue
{
    /// <summary>The shortest and longest an alert stays.</summary>
    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxDuration = TimeSpan.FromSeconds(10);

    private readonly List<IslandAlert> _waiting = [];
    private long _arrivals;
    private readonly Dictionary<string, long> _arrived = new(StringComparer.Ordinal);
    private TimeSpan _shownUntil;

    /// <summary>The alert shown now, or null.</summary>
    public IslandAlert? Current { get; private set; }

    public int Waiting => _waiting.Count;

    /// <summary>Raised when <see cref="Current"/> changes (to another alert or to none).</summary>
    public event Action<IslandAlert?>? CurrentChanged;

    public void Enqueue(IslandAlert alert, TimeSpan now)
    {
        alert = alert with { Duration = Clamp(alert.Duration) };
        if (Current?.Key == alert.Key)
        {
            Show(alert, now); // the same alert again: replaced in place, its time starts over
            return;
        }
        _waiting.RemoveAll(a => a.Key == alert.Key);
        _arrived[alert.Key] = ++_arrivals;
        if (Current is { } shown && alert.Priority == IslandPriority.Critical && shown.Priority != IslandPriority.Critical)
        {
            // Interrupted: shown again in full after the critical one.
            _waiting.Insert(0, shown);
            Show(alert, now);
            return;
        }
        _waiting.Add(alert);
        Advance(now);
    }

    /// <summary>Removes an alert whether shown or waiting (its activity ended, the user dismissed it).</summary>
    public void Remove(string key, TimeSpan now)
    {
        _waiting.RemoveAll(a => a.Key == key);
        if (Current?.Key == key)
        {
            Current = null;
            Advance(now, changed: true);
        }
    }

    /// <summary>Ends the shown alert when its time is up and shows the next one.</summary>
    public void Advance(TimeSpan now) => Advance(now, changed: false);

    private void Advance(TimeSpan now, bool changed)
    {
        if (Current is not null && now < _shownUntil)
        {
            if (changed)
            {
                CurrentChanged?.Invoke(Current);
            }
            return;
        }
        var next = _waiting
            .OrderByDescending(a => a.Priority)
            .ThenBy(a => _arrived.TryGetValue(a.Key, out var n) ? n : 0)
            .FirstOrDefault();
        if (next is null)
        {
            if (Current is not null || changed)
            {
                Current = null;
                CurrentChanged?.Invoke(null);
            }
            return;
        }
        _waiting.Remove(next);
        Show(next, now);
    }

    private void Show(IslandAlert alert, TimeSpan now)
    {
        Current = alert;
        _shownUntil = now + alert.Duration;
        CurrentChanged?.Invoke(alert);
    }

    private static TimeSpan Clamp(TimeSpan d) => d < MinDuration ? MinDuration : d > MaxDuration ? MaxDuration : d;
}
