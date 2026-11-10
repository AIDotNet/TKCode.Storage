using Island.Core.Geometry;
using Island.Core.State;

namespace Island.Core.Layout;

/// <summary>Corner radii of the island shape, clockwise from top-left.</summary>
public readonly record struct Corners(double TopLeft, double TopRight, double BottomRight, double BottomLeft);

/// <summary>Where the island window goes and what shape it draws inside it.</summary>
/// <param name="Window">The island window, in desktop DIPs.</param>
/// <param name="Surface">The visible shape, relative to the window.</param>
/// <param name="Corners">The shape's corner radii.</param>
/// <param name="Placement">The placement in effect.</param>
/// <param name="NotchGap">
/// Width of the band in the middle of the shape that the notch hides; content must stay out of it. 0 without a notch.
/// </param>
/// <param name="ContentTopInset">Height at the top of the shape that the notch hides in the expanded state.</param>
/// <param name="Shoulder">Radius of the concave curves joining the shape to the top edge; 0 when it floats.</param>
/// <param name="Dock">The screen edge the island lives on.</param>
public sealed record IslandFrame(
    RectD Window,
    RectD Surface,
    Corners Corners,
    ResolvedPlacement Placement,
    double NotchGap,
    double ContentTopInset,
    double Shoulder = 0,
    IslandDock Dock = IslandDock.Top);

/// <summary>Computes <see cref="IslandFrame"/>s. Pure: no platform or UI state.</summary>
public static class IslandLayout
{
    public static ResolvedPlacement Resolve(ScreenTopology screen, IslandPlacement placement) => placement switch
    {
        IslandPlacement.Auto when screen.HasNotch => ResolvedPlacement.WrapNotch,
        IslandPlacement.Auto when screen.TopInset > 0 => ResolvedPlacement.BelowMenuBar,
        IslandPlacement.Auto => ResolvedPlacement.Attached,
        // A notch hides whatever is drawn over it, so every placement except "below the bar" wraps it.
        IslandPlacement.Attached or IslandPlacement.Floating when screen.HasNotch => ResolvedPlacement.WrapNotch,
        IslandPlacement.Attached => ResolvedPlacement.Attached,
        IslandPlacement.Floating => ResolvedPlacement.Floating,
        IslandPlacement.BelowMenuBar when screen.TopInset > 0 || screen.HasNotch => ResolvedPlacement.BelowMenuBar,
        IslandPlacement.BelowMenuBar => ResolvedPlacement.Floating,
        IslandPlacement.Capsule when screen.HasNotch => ResolvedPlacement.NotchCapsule,
        IslandPlacement.Capsule => ResolvedPlacement.Floating,
        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null),
    };

    public static IslandFrame Compute(
        ScreenTopology screen,
        IslandPlacement placement,
        IslandState state,
        IslandMetrics metrics,
        double horizontalOffset = 0)
    {
        var resolved = Resolve(screen, placement);
        var notch = AroundNotch(resolved) ? screen.Notch : null;

        // The window is centred on the notch when wrapping it, otherwise on the screen.
        var centerX = (notch is not null ? screen.Bounds.X + notch.CenterX : screen.Bounds.CenterX)
                      + (notch is null ? horizontalOffset : 0);
        // Only the window origin is snapped; the shape is placed from the exact centre so that, around a notch, it
        // lines up with the physical notch to the point (rounding both would drift by one).
        var window = new RectD(
            Math.Floor(centerX - metrics.WindowWidth / 2),
            screen.Bounds.Y,
            metrics.WindowWidth,
            metrics.WindowHeight);

        var (width, height) = SizeFor(state, metrics, notch, resolved == ResolvedPlacement.NotchCapsule);
        var top = resolved switch
        {
            ResolvedPlacement.Floating => metrics.FloatingGap,
            ResolvedPlacement.BelowMenuBar => Math.Max(screen.TopInset, screen.Notch?.Height ?? 0) + metrics.BelowBarGap,
            _ => 0,
        };
        // Keep the expanded shape inside the window whatever the metrics say.
        width = Math.Min(width, metrics.WindowWidth);
        height = Math.Min(height, metrics.WindowHeight - top);
        // Unequal wings: the notch stays where it is, so the shape's centre moves by half the difference.
        var shift = notch is not null && state == IslandState.Compact && metrics.RightWingWidth is { } rightWing
            ? (rightWing - metrics.WingWidth) / 2
            : 0;
        var surface = new RectD(centerX + shift - width / 2 - window.X, top, width, height);

        var attachedToTop = resolved is ResolvedPlacement.WrapNotch or ResolvedPlacement.Attached;
        // Attached to the top, the collapsed island's bottom corners are capped: a full round end curved in on its values.
        var radius = state == IslandState.Expanded ? metrics.ExpandedCornerRadius
            : resolved == ResolvedPlacement.Attached ? Math.Min(height / 2, metrics.CollapsedCornerRadius)
            : height / 2;
        var corners = attachedToTop
            ? new Corners(0, 0, radius, radius)
            : new Corners(radius, radius, radius, radius);

        return new IslandFrame(
            window,
            surface,
            corners,
            resolved,
            NotchGap: notch?.Width ?? 0,
            ContentTopInset: notch is not null && state == IslandState.Expanded ? notch.Height : 0,
            Shoulder: attachedToTop ? metrics.ShoulderRadius : 0);
    }

    /// <summary>
    /// <see cref="Compute(ScreenTopology, IslandPlacement, IslandState, IslandMetrics, double)"/> for an island drawn at
    /// <paramref name="scale"/> (the UI applies one scale transform to the whole island). The layout runs in island units
    /// on a screen scaled down by the same factor, so <see cref="IslandFrame.Surface"/> stays in island units while
    /// <see cref="IslandFrame.Window"/> comes back in real screen DIPs. Around a physical notch the scale is 1: the
    /// shape must match the hardware exactly.
    /// </summary>
    public static IslandFrame Compute(
        ScreenTopology screen,
        IslandPlacement placement,
        IslandState state,
        IslandMetrics metrics,
        double horizontalOffset,
        double scale) => Compute(screen, placement, IslandDock.Top, state, metrics, horizontalOffset, scale);

    /// <summary>
    /// The island on any edge (docs 07 §5), at <paramref name="scale"/>. <paramref name="offset"/> runs along the edge:
    /// rightwards at the top, downwards at the sides.
    /// </summary>
    public static IslandFrame Compute(
        ScreenTopology screen,
        IslandPlacement placement,
        IslandDock dock,
        IslandState state,
        IslandMetrics metrics,
        double offset,
        double scale)
    {
        if (dock == IslandDock.Top && (AroundNotch(Resolve(screen, placement)) || Math.Abs(scale - 1) < 0.001))
        {
            return Compute(screen, placement, state, metrics, offset);
        }
        if (dock != IslandDock.Top && Math.Abs(scale - 1) < 0.001)
        {
            return ComputeSide(screen, placement, dock, state, metrics, offset);
        }
        var horizontalOffset = offset;
        var b = screen.Bounds;
        var local = screen with
        {
            Bounds = new RectD(b.X / scale, b.Y / scale, b.Width / scale, b.Height / scale),
            Notch = screen.Notch is { } n ? new NotchInfo(n.X / scale, n.Width / scale, n.Height / scale) : null,
            TopInset = screen.TopInset / scale,
        };
        var f = dock == IslandDock.Top
            ? Compute(local, placement, state, metrics, horizontalOffset / scale)
            : ComputeSide(local, placement, dock, state, metrics, horizontalOffset / scale);
        var w = f.Window;
        return f with { Window = new RectD(w.X * scale, w.Y * scale, w.Width * scale, w.Height * scale) };
    }

    /// <summary>
    /// Docked to the left or right edge: centred on the edge's free height (below a top bar), moved along it by
    /// <paramref name="verticalOffset"/>. Collapsed it is an upright pill; expanded, the card grows out of the edge.
    /// The notch shape sits flush with the edge, square on that side; the capsule floats a little off it. Notches do
    /// not matter here, they are at the top.
    /// </summary>
    public static IslandFrame ComputeSide(
        ScreenTopology screen,
        IslandPlacement placement,
        IslandDock dock,
        IslandState state,
        IslandMetrics metrics,
        double verticalOffset = 0)
    {
        var attached = placement == IslandPlacement.Attached;
        var b = screen.Bounds;
        var (width, height) = state switch
        {
            IslandState.Idle => (metrics.SideIdleWidth, metrics.SideIdleHeight),
            IslandState.Compact => (metrics.SideCompactWidth, metrics.SideCompactHeight),
            IslandState.Expanded => (metrics.SideExpandedWidth, Math.Min(metrics.ExpandedHeight, metrics.SideMaxExpandedHeight)),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
        var (windowWidth, windowHeight) = WindowSize(IslandDock.Left, metrics);
        width = Math.Min(width, windowWidth - metrics.FloatingGap);
        height = Math.Min(height, Math.Min(windowHeight, b.Height - screen.TopInset));

        // The window hugs the edge and is centred on the island's centre line, kept on the screen below any top bar.
        var top = b.Y + screen.TopInset;
        var centerY = Math.Clamp((top + b.Bottom) / 2 + verticalOffset, top + height / 2, b.Bottom - height / 2);
        var windowY = Math.Floor(Math.Clamp(centerY - windowHeight / 2, top, Math.Max(top, b.Bottom - windowHeight)));
        var windowX = dock == IslandDock.Left ? b.X : b.Right - windowWidth;
        var window = new RectD(windowX, windowY, windowWidth, windowHeight);

        var gap = attached ? 0 : metrics.FloatingGap;
        var x = dock == IslandDock.Left ? gap : windowWidth - gap - width;
        var y = Math.Clamp(centerY - height / 2 - windowY, 0, windowHeight - height);
        var radius = state == IslandState.Expanded ? metrics.ExpandedCornerRadius : width / 2;
        var corners = !attached
            ? new Corners(radius, radius, radius, radius)
            : dock == IslandDock.Left
                ? new Corners(0, radius, radius, 0)
                : new Corners(radius, 0, 0, radius);
        return new IslandFrame(
            window,
            new RectD(x, y, width, height),
            corners,
            attached ? ResolvedPlacement.Attached : ResolvedPlacement.Floating,
            NotchGap: 0,
            ContentTopInset: 0,
            Shoulder: 0,
            Dock: dock);
    }

    /// <summary>The island window's size (before scaling) on <paramref name="dock"/>: wide at the top, upright at the sides.</summary>
    public static (double Width, double Height) WindowSize(IslandDock dock, IslandMetrics metrics) =>
        dock == IslandDock.Top ? (metrics.WindowWidth, metrics.WindowHeight) : (metrics.SideWindowWidth, metrics.SideWindowHeight);

    /// <summary>The scale actually used: 1 around a physical notch.</summary>
    public static double EffectiveScale(ScreenTopology screen, IslandPlacement placement, double scale) =>
        AroundNotch(Resolve(screen, placement)) ? 1 : scale;

    /// <summary>The scale actually used on <paramref name="dock"/>: at the sides there is no notch to match.</summary>
    public static double EffectiveScale(ScreenTopology screen, IslandPlacement placement, IslandDock dock, double scale) =>
        dock == IslandDock.Top ? EffectiveScale(screen, placement, scale) : scale;

    /// <summary>The shape surrounds a physical notch: it cannot move or scale, and content keeps out of the notch.</summary>
    public static bool AroundNotch(ResolvedPlacement placement) =>
        placement is ResolvedPlacement.WrapNotch or ResolvedPlacement.NotchCapsule;

    private static (double Width, double Height) SizeFor(IslandState state, IslandMetrics m, NotchInfo? notch, bool capsule)
    {
        if (notch is not null)
        {
            // Around a notch the compact height is the notch height so the shape reads as the notch growing sideways.
            var compactWidth = notch.Width + m.WingWidth + (m.RightWingWidth ?? m.WingWidth);
            return state switch
            {
                // The capsule is visibly a pill even at rest; wrapping, the idle shape is the notch itself.
                IslandState.Idle => (notch.Width + (capsule ? m.CapsuleNotchMargin * 2 : 0), notch.Height),
                IslandState.Compact => (compactWidth, notch.Height),
                IslandState.Expanded => (Math.Max(m.ExpandedWidth, compactWidth), m.ExpandedHeight + notch.Height),
                _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
            };
        }
        return state switch
        {
            IslandState.Idle => (m.IdleWidth, m.IdleHeight),
            IslandState.Compact => (m.CompactWidth, m.CompactHeight),
            IslandState.Expanded => (m.ExpandedWidth, m.ExpandedHeight),
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, null),
        };
    }
}
