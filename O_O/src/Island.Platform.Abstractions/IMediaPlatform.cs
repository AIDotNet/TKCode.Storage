namespace Island.Platform;

/// <summary>What is playing (docs/design/05-builtin-plugins.md §1). Times in seconds.</summary>
/// <param name="App">The player, e.g. "Music", "Spotify".</param>
/// <param name="PositionAt">When <paramref name="Position"/> was true, so callers can extrapolate while playing.</param>
public sealed record NowPlaying(
    string App,
    string? Title,
    string? Artist,
    string? Album,
    bool Playing,
    double? Position = null,
    double? Duration = null,
    DateTimeOffset? PositionAt = null);

public enum MediaCommand
{
    Play,
    Pause,
    Toggle,
    Next,
    Previous,
}

/// <summary>The native half of the media capability: who is playing what, and transport commands.</summary>
public interface IMediaPlatform
{
    /// <summary>True when <see cref="Changed"/> is raised by the system; otherwise callers poll <see cref="Query"/>.</summary>
    bool PushesChanges { get; }

    /// <summary>Raised (any thread) when the platform reports a change; <see cref="Query"/> then returns the new state.</summary>
    event Action? Changed;

    /// <summary>The current state, or null when nothing is playing or it cannot be known. May block briefly; call off the UI thread.</summary>
    NowPlaying? Query();

    /// <summary>Sends a transport command to the current player; false when the platform cannot.</summary>
    bool Send(MediaCommand command);
}

/// <summary>For tests and platforms without media support.</summary>
public sealed class NullMediaPlatform : IMediaPlatform
{
    public bool PushesChanges => false;
    public event Action? Changed { add { } remove { } }
    public NowPlaying? Query() => null;
    public bool Send(MediaCommand command) => false;
}
