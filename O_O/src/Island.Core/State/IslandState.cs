namespace Island.Core.State;

/// <summary>The island's visible states (docs/design/02-island-shell.md §2). Alert, DropTarget and Hidden come later.</summary>
public enum IslandState
{
    /// <summary>Nothing to show: the smallest shape (exactly the notch on notched Macs).</summary>
    Idle,

    /// <summary>An activity's leading and trailing content beside the notch.</summary>
    Compact,

    /// <summary>The full card.</summary>
    Expanded,
}
