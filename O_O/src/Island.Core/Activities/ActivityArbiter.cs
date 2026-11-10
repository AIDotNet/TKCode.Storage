namespace Island.Core.Activities;

/// <summary>
/// How much an activity matters (docs/design/09-island-v2.md §2.1). A higher one takes the compact place from a lower
/// one; within a level the user's order, then the most recently updated, decides.
/// </summary>
public enum IslandPriority
{
    /// <summary>Background state, such as an unread count.</summary>
    Passive,

    /// <summary>Something the user started: playing, a focus timer.</summary>
    Normal,

    /// <summary>Needs attention soon: a meeting about to start, a usage limit nearly reached.</summary>
    High,

    /// <summary>Needs attention now: a call, a timer that ran out.</summary>
    Critical,
}

/// <summary>What the user lets an activity kind do (09 §5, 岛体 → 活动).</summary>
public enum ActivityPolicy
{
    /// <summary>Shown on the island and may alert.</summary>
    Allow,

    /// <summary>Never takes a place on the island, but its alerts still show.</summary>
    AlertOnly,

    /// <summary>Neither shown nor alerting.</summary>
    Off,
}

/// <summary>
/// One running activity as the arbiter sees it. <see cref="Key"/> is <c>plugin/activity</c> (one instance), <see cref="Kind"/>
/// the declared activity the user's preferences are about (the same for every instance of it).
/// </summary>
/// <param name="Updated">A monotonic stamp of its last update; larger is more recent.</param>
public sealed record ActivityEntry(string Key, string Kind, IslandPriority Priority, long Updated);

/// <summary>Where each activity goes: the compact place, the bubble beside the island, or the queue.</summary>
public sealed record ActivityArrangement(ActivityEntry? Primary, ActivityEntry? Secondary, IReadOnlyList<ActivityEntry> Queued)
{
    public static readonly ActivityArrangement Empty = new(null, null, []);

    public bool IsEmpty => Primary is null;

    /// <summary>Every shown or queued activity, in order.</summary>
    public IEnumerable<ActivityEntry> All =>
        new[] { Primary, Secondary }.OfType<ActivityEntry>().Concat(Queued);
}

/// <summary>
/// Decides which activities the island shows (09 §2.1): at most two at once, the first in the compact place, the second
/// as a bubble beside it, the rest queued. Pure: the inputs are the running activities, the user's preferences and what
/// the user just did on the island.
/// </summary>
public static class ActivityArbiter
{
    /// <param name="front">An activity the user brought to the front (clicking its bubble, scrolling to it); it takes the
    /// compact place unless a <see cref="IslandPriority.Critical"/> one is running and it is not one itself.</param>
    /// <param name="setAside">Activities the user scrolled away: they keep running but go to the end of the queue.</param>
    public static ActivityArrangement Arrange(
        IEnumerable<ActivityEntry> running,
        ActivityPreferences preferences,
        string? front = null,
        IReadOnlySet<string>? setAside = null)
    {
        var order = preferences.Order.Select((kind, i) => (kind, i)).ToDictionary(x => x.kind, x => x.i, StringComparer.Ordinal);
        var shown = running
            .Where(a => preferences.PolicyOf(a.Kind) == ActivityPolicy.Allow)
            .OrderBy(a => setAside?.Contains(a.Key) == true ? 1 : 0)
            .ThenBy(a => preferences.Pinned is { } pin && a.Kind == pin ? 0 : 1)
            .ThenByDescending(a => a.Priority)
            .ThenBy(a => order.TryGetValue(a.Kind, out var r) ? r : int.MaxValue)
            .ThenByDescending(a => a.Updated)
            .ThenBy(a => a.Key, StringComparer.Ordinal)
            .ToList();

        if (front is not null && shown.FindIndex(a => a.Key == front) is var i and > 0)
        {
            var chosen = shown[i];
            var critical = shown.Any(a => a.Priority == IslandPriority.Critical);
            if (!critical || chosen.Priority == IslandPriority.Critical)
            {
                shown.RemoveAt(i);
                shown.Insert(0, chosen);
            }
        }

        return shown.Count switch
        {
            0 => ActivityArrangement.Empty,
            1 => new ActivityArrangement(shown[0], null, []),
            _ => new ActivityArrangement(shown[0], shown[1], shown[2..]),
        };
    }
}
