namespace Island.Core.Layout;

/// <summary>Where the island sits on a screen (docs/design/02-island-shell.md §3.3).</summary>
public enum IslandPlacement
{
    /// <summary>Wrap the notch when there is one, below the top bar when there is one, attached to the top edge otherwise.</summary>
    Auto,

    /// <summary>Top edge flush with the screen's top edge: square top corners, round bottom corners.</summary>
    Attached,

    /// <summary>A free-standing pill a few pixels below the top edge.</summary>
    Floating,

    /// <summary>A free-standing pill just below the system top bar.</summary>
    BelowMenuBar,

    /// <summary>
    /// The capsule shape (docs/design/07-home-and-layout.md §5): a free-standing pill a few pixels below the top edge;
    /// on a notched screen a pill at the top edge, wider than the notch, with content either side of it.
    /// </summary>
    Capsule,
}

/// <summary>The placement actually used after <see cref="IslandPlacement.Auto"/> is resolved.</summary>
public enum ResolvedPlacement
{
    WrapNotch,

    /// <summary>A pill around the notch: rounded on all four corners, no shoulders, content in the wings.</summary>
    NotchCapsule,
    Attached,
    Floating,
    BelowMenuBar,
}
