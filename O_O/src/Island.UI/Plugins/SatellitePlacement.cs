using Avalonia;

namespace Island.UI.Plugins;

/// <summary>Where a satellite goes beside the island (docs/design/08 §4.3). Pure, so it can be tested.</summary>
public static class SatellitePlacement
{
    public const int Gap = 8;

    /// <summary>The side a satellite really takes: "left" or "right" as asked; "auto" is right unless that leaves the screen.</summary>
    /// <param name="side">The user's pin if any, else the declared side: auto, left or right.</param>
    public static string ChooseSide(string? side, PixelRect island, PixelRect screen, int width, int gap = Gap)
    {
        switch (side)
        {
            case "left":
                return "left";
            case "right":
                return "right";
        }
        return island.Right + gap + width <= screen.Right ? "right" : "left";
    }

    /// <summary>
    /// The satellite's top-left in pixels. For an island on the top edge it sits beside it, top-aligned; for one on a
    /// side edge it goes below it. <paramref name="width"/> and <paramref name="height"/> are the satellite's pixels.
    /// </summary>
    public static PixelPoint Choose(string? side, PixelRect island, PixelRect screen, bool dockedToSide, int width, int height, int gap = Gap)
    {
        if (dockedToSide)
        {
            var x = Math.Clamp(island.X + (island.Width - width) / 2, screen.X, Math.Max(screen.X, screen.Right - width));
            return new PixelPoint(x, island.Bottom + gap);
        }
        var chosen = ChooseSide(side, island, screen, width, gap);
        var left = chosen == "right" ? island.Right + gap : island.X - gap - width;
        return new PixelPoint(Math.Clamp(left, screen.X, Math.Max(screen.X, screen.Right - width)), island.Y);
    }

    /// <summary>The direction the satellite slides out from the island: +1 rightwards, -1 leftwards, 0 downwards.</summary>
    public static int SlideDirection(string? side, PixelRect island, PixelRect screen, bool dockedToSide, int width) =>
        dockedToSide ? 0 : ChooseSide(side, island, screen, width) == "right" ? 1 : -1;
}
