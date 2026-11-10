namespace Island.Core.Activities;

/// <summary>What the island presents (09 §2).</summary>
public enum PresentationKind
{
    /// <summary>No activity: the island alone, with the ambient items when there are any.</summary>
    Idle,

    /// <summary>The first activity beside the notch; a second one as a bubble beside the island (minimal).</summary>
    Compact,

    /// <summary>The first activity's card, opened by the user.</summary>
    Expanded,

    /// <summary>An alert, briefly.</summary>
    Alert,

    /// <summary>The home dashboard (pages with tabs), opened by the user.</summary>
    Home,
}

/// <summary>What the user asked of the island.</summary>
public enum IslandIntent
{
    /// <summary>Nothing: the island shows what the activities and alerts call for.</summary>
    None,

    /// <summary>Open: the first activity's card when there is one, otherwise home (a click on the island).</summary>
    Open,

    /// <summary>Open home whatever is running (the hotkey, 主页 in the card or menu).</summary>
    Home,
}

/// <summary>The island's presentation at one moment: what to draw and with which activities.</summary>
/// <param name="Bubble">The second activity, drawn as the bubble beside the island (only while compact).</param>
/// <param name="ShowAmbient">Whether the ambient items show (only while idle).</param>
public sealed record IslandPresentation(
    PresentationKind Kind,
    ActivityEntry? Activity,
    ActivityEntry? Bubble,
    IslandAlert? Alert,
    bool ShowAmbient)
{
    /// <summary>
    /// Decides the presentation (09 §2): an alert shows over everything but what the user opened; what the user opened
    /// shows over the resting state; resting is compact with activities, idle without.
    /// </summary>
    public static IslandPresentation Decide(ActivityArrangement arrangement, IslandAlert? alert, IslandIntent intent, bool hasAmbient)
    {
        switch (intent)
        {
            case IslandIntent.Home:
                return new(PresentationKind.Home, null, null, null, false);
            case IslandIntent.Open when arrangement.Primary is { } open:
                return new(PresentationKind.Expanded, open, null, null, false);
            case IslandIntent.Open:
                return new(PresentationKind.Home, null, null, null, false);
        }
        if (alert is not null)
        {
            return new(PresentationKind.Alert, null, null, alert, false);
        }
        return arrangement.Primary is { } primary
            ? new(PresentationKind.Compact, primary, arrangement.Secondary, null, false)
            : new(PresentationKind.Idle, null, null, null, hasAmbient);
    }
}
