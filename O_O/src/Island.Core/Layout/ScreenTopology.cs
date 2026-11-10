using Island.Core.Geometry;

namespace Island.Core.Layout;

/// <summary>
/// The camera housing at the top of a screen (MacBook notch), in DIPs relative to the screen's left edge.
/// </summary>
/// <param name="X">Distance from the screen's left edge to the notch's left edge.</param>
/// <param name="Width">Notch width.</param>
/// <param name="Height">Notch height; equals the menu bar height on notched Macs.</param>
public sealed record NotchInfo(double X, double Width, double Height)
{
    public double CenterX => X + Width / 2;
}

/// <summary>What the layout needs to know about the screen the island lives on.</summary>
/// <param name="Bounds">Whole screen, in DIPs, in the desktop's coordinate space.</param>
/// <param name="Scale">Pixels per DIP.</param>
/// <param name="Notch">The notch, or null when the screen has none.</param>
/// <param name="TopInset">Height of a system bar at the top (macOS menu bar, GNOME top bar); 0 when none.</param>
public sealed record ScreenTopology(RectD Bounds, double Scale, NotchInfo? Notch, double TopInset)
{
    public bool HasNotch => Notch is not null;
}
