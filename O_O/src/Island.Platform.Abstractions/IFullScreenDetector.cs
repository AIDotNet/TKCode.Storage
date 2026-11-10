using Island.Core.Geometry;

namespace Island.Platform;

/// <summary>
/// Tells whether another application's window fills a screen (docs/design/07-home-and-layout.md §5, 全屏时): a video,
/// a game, a presentation. The island hides meanwhile when the user asks it to.
/// </summary>
public interface IFullScreenDetector
{
    /// <param name="screen">The screen, in desktop DIPs (top-left origin, as Avalonia reports screens).</param>
    /// <param name="scale">Pixels per DIP on that screen.</param>
    /// <returns>True when the frontmost window of another process covers the whole screen.</returns>
    bool IsFullScreen(RectD screen, double scale);
}

/// <summary>For platforms (and tools) that cannot tell: never full screen.</summary>
public sealed class NullFullScreenDetector : IFullScreenDetector
{
    public bool IsFullScreen(RectD screen, double scale) => false;
}
