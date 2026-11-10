namespace Island.Core.Layout;

/// <summary>Sizes of the island in each state, in DIPs. Defaults follow docs/design/02-island-shell.md.</summary>
public sealed record IslandMetrics
{
    /// <summary>
    /// The island window never resizes (resizing a top-level window per animation frame flickers on every platform);
    /// it is created once at this size and the visible shape animates inside it.
    /// </summary>
    public double WindowWidth { get; init; } = 760;
    public double WindowHeight { get; init; } = 460;

    public double IdleWidth { get; init; } = 150;
    public double IdleHeight { get; init; } = 30;

    public double CompactWidth { get; init; } = 230;
    public double CompactHeight { get; init; } = 34;

    /// <summary>Width of each content "wing" beside the notch in the compact state.</summary>
    public double WingWidth { get; init; } = 86;

    /// <summary>How far the idle capsule reaches past each side of a notch, so it reads as a pill rather than the notch.</summary>
    public double CapsuleNotchMargin { get; init; } = 18;

    public double ExpandedWidth { get; init; } = 640;
    /// <summary>Height of the expanded card's content area; the UI sets it from the content, within the limits below.</summary>
    public double ExpandedHeight { get; init; } = 240;
    public double MinExpandedHeight { get; init; } = 120;
    public double MaxExpandedHeight { get; init; } = 400;

    /// <summary>Gap above a floating island.</summary>
    public double FloatingGap { get; init; } = 8;

    /// <summary>Gap between the system top bar and an island placed below it.</summary>
    public double BelowBarGap { get; init; } = 6;

    /// <summary>Continuous corner of the expanded card, in the proportions of Apple's Dynamic Island.</summary>
    public double ExpandedCornerRadius { get; init; } = 40;

    /// <summary>Largest corner of the collapsed island: a full round end curves in on the values at its ends, so it is capped.</summary>
    public double CollapsedCornerRadius { get; init; } = 10;

    /// <summary>Concave curves joining a top-attached island to the screen edge, as a MacBook notch does.</summary>
    public double ShoulderRadius { get; init; } = 8;

    /// <summary>The idle island docked to a side edge: an upright pill (docs 07 §5).</summary>
    public double SideIdleWidth { get; init; } = 10;
    public double SideIdleHeight { get; init; } = 72;

    /// <summary>The collapsed side island; the UI sets both from its stacked chips.</summary>
    public double SideCompactWidth { get; init; } = 44;
    public double SideCompactHeight { get; init; } = 120;

    /// <summary>
    /// The expanded side island: a drawer, narrower and taller than the top card, growing out of the edge (docs 07 §5).
    /// Its height comes from the content, up to <see cref="SideMaxExpandedHeight"/>.
    /// </summary>
    public double SideExpandedWidth { get; init; } = 380;
    public double SideMaxExpandedHeight { get; init; } = 600;

    /// <summary>The window at a side edge is upright: the top window turned on its side.</summary>
    public double SideWindowWidth => WindowHeight;
    public double SideWindowHeight => WindowWidth;

    /// <summary>Extra margin around the shape that still counts as "pointer on the island".</summary>
    public double HitSlop { get; init; } = 4;
}
