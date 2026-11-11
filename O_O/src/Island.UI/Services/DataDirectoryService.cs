namespace Island.UI.Services;

/// <summary>
/// The data directory as the console's 关于 page shows it, and the way to move it. Set by the app; null where the data
/// cannot be moved (offscreen rendering, tests).
/// </summary>
public sealed class DataDirectoryService
{
    public static DataDirectoryService? Shared { get; set; }

    /// <summary>Where the data is this run.</summary>
    public required string Current { get; init; }

    /// <summary>The default folder (where the settings file always stays).</summary>
    public required string Default { get; init; }

    /// <summary>What happened to the data directory at this start (a move done or failed), or null.</summary>
    public string? Notice { get; init; }

    /// <summary>Moves the data to the chosen folder and restarts O_O; returns why it cannot, or null when restarting.</summary>
    public required Func<string, string?> MoveAndRestart { get; init; }
}
