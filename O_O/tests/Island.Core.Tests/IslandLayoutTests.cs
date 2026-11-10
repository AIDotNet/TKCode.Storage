using Island.Core.Geometry;
using Island.Core.Layout;
using Island.Core.State;

namespace Island.Core.Tests;

public class IslandLayoutTests
{
    private static readonly IslandMetrics M = new();

    // A 14" MacBook Pro: 1512×982 points, notch 185 wide and 32 high, slightly off-centre to prove we use the notch.
    private static readonly ScreenTopology Notched =
        new(new RectD(0, 0, 1512, 982), 2, new NotchInfo(663, 185, 32), TopInset: 32);

    private static readonly ScreenTopology PlainWindows = new(new RectD(0, 0, 1920, 1080), 1.25, null, 0);
    private static readonly ScreenTopology MacExternal = new(new RectD(1512, -200, 2560, 1440), 1, null, 25);

    [Fact]
    public void Auto_wraps_the_notch()
    {
        Assert.Equal(ResolvedPlacement.WrapNotch, IslandLayout.Resolve(Notched, IslandPlacement.Auto));
        Assert.Equal(ResolvedPlacement.BelowMenuBar, IslandLayout.Resolve(MacExternal, IslandPlacement.Auto));
        Assert.Equal(ResolvedPlacement.Attached, IslandLayout.Resolve(PlainWindows, IslandPlacement.Auto));
    }

    [Fact]
    public void Floating_or_attached_on_a_notched_screen_still_wraps_the_notch()
    {
        Assert.Equal(ResolvedPlacement.WrapNotch, IslandLayout.Resolve(Notched, IslandPlacement.Floating));
        Assert.Equal(ResolvedPlacement.WrapNotch, IslandLayout.Resolve(Notched, IslandPlacement.Attached));
        Assert.Equal(ResolvedPlacement.BelowMenuBar, IslandLayout.Resolve(Notched, IslandPlacement.BelowMenuBar));
    }

    [Fact]
    public void Idle_around_a_notch_is_exactly_the_notch()
    {
        var f = IslandLayout.Compute(Notched, IslandPlacement.Auto, IslandState.Idle, M);
        var surfaceOnScreen = f.Surface with { X = f.Window.X + f.Surface.X, Y = f.Window.Y + f.Surface.Y };
        Assert.Equal(new RectD(663, 0, 185, 32), surfaceOnScreen);
    }

    [Fact]
    public void Compact_around_a_notch_keeps_the_notch_centred_with_a_wing_each_side()
    {
        var f = IslandLayout.Compute(Notched, IslandPlacement.Auto, IslandState.Compact, M);
        var left = f.Window.X + f.Surface.X;
        Assert.Equal(185 + 2 * M.WingWidth, f.Surface.Width);
        Assert.Equal(32, f.Surface.Height);
        Assert.Equal(663 - M.WingWidth, left);
        Assert.Equal(185, f.NotchGap);
        Assert.Equal(new Corners(0, 0, 16, 16), f.Corners);
    }

    [Fact]
    public void Expanded_around_a_notch_reserves_the_notch_height_for_the_top_bar()
    {
        var f = IslandLayout.Compute(Notched, IslandPlacement.Auto, IslandState.Expanded, M);
        Assert.Equal(32, f.ContentTopInset);
        Assert.Equal(M.ExpandedHeight + 32, f.Surface.Height);
        Assert.Equal(0, f.Surface.Y);
    }

    [Fact]
    public void Without_a_notch_attached_is_centred_on_the_screen_and_has_no_gap()
    {
        var f = IslandLayout.Compute(PlainWindows, IslandPlacement.Auto, IslandState.Compact, M);
        Assert.Equal(960, f.Window.X + f.Surface.X + f.Surface.Width / 2);
        Assert.Equal(0, f.NotchGap);
        Assert.Equal(0, f.Surface.Y);
    }

    [Fact]
    public void Below_menu_bar_starts_under_the_bar_on_a_secondary_screen()
    {
        var f = IslandLayout.Compute(MacExternal, IslandPlacement.Auto, IslandState.Compact, M);
        Assert.Equal(-200, f.Window.Y);
        Assert.Equal(25 + M.BelowBarGap, f.Surface.Y);
        Assert.Equal(1512 + 1280, f.Window.X + f.Surface.X + f.Surface.Width / 2);
        Assert.Equal(new Corners(17, 17, 17, 17), f.Corners);
    }

    [Fact]
    public void Expanded_never_leaves_the_window()
    {
        var huge = M with { ExpandedWidth = 5000, ExpandedHeight = 5000 };
        var f = IslandLayout.Compute(PlainWindows, IslandPlacement.Floating, IslandState.Expanded, huge);
        Assert.True(f.Surface.Right <= huge.WindowWidth);
        Assert.True(f.Surface.Bottom <= huge.WindowHeight);
    }
}

public class IslandAppearanceTests
{
    private static readonly IslandMetrics M = new();
    private static readonly ScreenTopology Notched =
        new(new RectD(0, 0, 1470, 956), 2, new NotchInfo(646, 179, 32), TopInset: 33);
    private static readonly ScreenTopology Plain = new(new RectD(0, 0, 1920, 1080), 1, null, 0);

    [Fact]
    public void Capsule_is_the_default_and_sits_on_the_notch_of_a_notched_mac()
    {
        var a = new IslandAppearance();
        Assert.Equal(IslandShape.Capsule, a.Shape);
        Assert.Equal(ResolvedPlacement.NotchCapsule, IslandLayout.Resolve(Notched, a.Placement));
        Assert.Equal(ResolvedPlacement.Floating, IslandLayout.Resolve(Plain, a.Placement));
    }

    [Fact]
    public void A_notch_capsule_is_a_pill_wider_than_the_notch_that_keeps_content_out_of_it()
    {
        foreach (var state in new[] { IslandState.Idle, IslandState.Compact, IslandState.Expanded })
        {
            var f = IslandLayout.Compute(Notched, IslandPlacement.Capsule, state, M, 300, 1.4);
            var left = f.Window.X + f.Surface.X;
            Assert.Equal(0, f.Window.Y + f.Surface.Y);                                     // at the top edge, over the notch
            Assert.True(left < 646 && left + f.Surface.Width > 646 + 179, $"{state} covers the notch");
            Assert.Equal(735.5, left + f.Surface.Width / 2, 3);                             // centred on it, offset ignored
            Assert.Equal(179, f.NotchGap);
            Assert.Equal(0, f.Shoulder);
            Assert.Equal(f.Corners.BottomLeft, f.Corners.TopLeft);                          // rounded all round
        }
        Assert.Equal(179 + 2 * M.CapsuleNotchMargin,
            IslandLayout.Compute(Notched, IslandPlacement.Capsule, IslandState.Idle, M).Surface.Width);
    }

    [Fact]
    public void Notch_shape_wraps_a_real_notch_and_hangs_from_the_edge_elsewhere()
    {
        var a = new IslandAppearance(IslandShape.Notch);
        Assert.Equal(ResolvedPlacement.WrapNotch, IslandLayout.Resolve(Notched, a.Placement));
        Assert.Equal(ResolvedPlacement.Attached, IslandLayout.Resolve(Plain, a.Placement));
    }

    [Fact]
    public void A_scaled_island_keeps_its_centre_and_its_island_units()
    {
        var normal = IslandLayout.Compute(Plain, IslandPlacement.Floating, IslandState.Compact, M, 0, 1);
        var big = IslandLayout.Compute(Plain, IslandPlacement.Floating, IslandState.Compact, M, 0, 1.4);
        Assert.Equal(normal.Surface.Width, big.Surface.Width);                         // island units unchanged
        Assert.Equal(M.WindowWidth * 1.4, big.Window.Width, 3);                         // the window grows
        Assert.Equal(960, big.Window.X + (big.Surface.X + big.Surface.Width / 2) * 1.4, 0); // still centred on screen
        Assert.Equal(M.FloatingGap * 1.4, big.Window.Y + big.Surface.Y * 1.4, 3);         // the gap scales too
    }

    [Fact]
    public void The_offset_moves_a_capsule_but_never_a_notch_wrap()
    {
        var moved = IslandLayout.Compute(Plain, IslandPlacement.Floating, IslandState.Compact, M, 200, 1);
        Assert.Equal(960 + 200, moved.Window.X + moved.Surface.X + moved.Surface.Width / 2);
        var wrap = IslandLayout.Compute(Notched, IslandPlacement.Attached, IslandState.Idle, M, 200, 1.4);
        Assert.Equal(646, wrap.Window.X + wrap.Surface.X);                               // exactly on the notch
        Assert.Equal(179, wrap.Surface.Width);
    }

    [Fact]
    public void Appearance_is_clamped_and_survives_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-island-{Guid.NewGuid():N}.json");
        try
        {
            var store = new IslandAppearanceStore(path);
            IslandAppearance? seen = null;
            store.Changed += a => seen = a;
            store.Set(new IslandAppearance(IslandShape.Notch, IslandDock.Top, Scale: 9, Offset: -5000));
            Assert.Equal(new IslandAppearance(IslandShape.Notch, IslandDock.Top, 1.4, -600), seen);
            Assert.Equal(seen, new IslandAppearanceStore(path).Current);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

public class SideDockTests
{
    private static readonly IslandMetrics M = new();
    private static readonly ScreenTopology Mac = new(new RectD(0, 0, 1470, 956), 2, new NotchInfo(646, 179, 32), TopInset: 33);

    private static RectD OnScreen(IslandFrame f) =>
        new(f.Window.X + f.Surface.X, f.Window.Y + f.Surface.Y, f.Surface.Width, f.Surface.Height);

    [Fact]
    public void A_left_capsule_floats_off_the_edge_centred_on_the_free_height()
    {
        foreach (var state in new[] { IslandState.Idle, IslandState.Compact, IslandState.Expanded })
        {
            var f = IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Left, state, M, 0, 1);
            var s = OnScreen(f);
            Assert.Equal(IslandDock.Left, f.Dock);
            Assert.Equal(M.FloatingGap, s.X);                                   // off the edge, notch ignored
            Assert.Equal((33 + 956) / 2.0, s.Y + s.Height / 2, 0);             // centred below the menu bar
            Assert.Equal(0, f.Shoulder);
        }
        var compact = IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Left, IslandState.Compact, M, 0, 1);
        Assert.True(compact.Surface.Height > compact.Surface.Width, "collapsed it is upright");
    }

    [Fact]
    public void A_right_notch_shape_is_flush_and_square_on_the_edge_side()
    {
        var f = IslandLayout.Compute(Mac, IslandPlacement.Attached, IslandDock.Right, IslandState.Expanded, M, 0, 1);
        var s = OnScreen(f);
        Assert.Equal(1470, s.Right);
        Assert.Equal(0, f.Corners.TopRight);
        Assert.Equal(0, f.Corners.BottomRight);
        Assert.True(f.Corners.TopLeft > 0);
    }

    [Fact]
    public void The_offset_moves_along_the_edge_and_the_island_stays_on_screen()
    {
        var down = OnScreen(IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Left, IslandState.Compact, M, 200, 1));
        Assert.Equal((33 + 956) / 2.0 + 200, down.Y + down.Height / 2, 0);
        var far = IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Right, IslandState.Expanded, M, 600, 1);
        Assert.True(OnScreen(far).Bottom <= 956 && far.Window.Bottom <= 956);
        var up = IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Right, IslandState.Expanded, M, -600, 1);
        Assert.True(OnScreen(up).Y >= 33 && up.Window.Y >= 33);
    }

    [Fact]
    public void A_scaled_side_island_keeps_its_edge_gap_and_centre()
    {
        var f = IslandLayout.Compute(Mac, IslandPlacement.Capsule, IslandDock.Right, IslandState.Compact, M, 0, 1.4);
        Assert.Equal(1.4, IslandLayout.EffectiveScale(Mac, IslandPlacement.Capsule, IslandDock.Right, 1.4));
        var right = f.Window.X + (f.Surface.X + f.Surface.Width) * 1.4;
        Assert.Equal(1470 - M.FloatingGap * 1.4, right, 3);
        Assert.Equal((33 + 956) / 2.0, f.Window.Y + (f.Surface.Y + f.Surface.Height / 2) * 1.4, 0);
    }

    [Fact]
    public void Unequal_wings_keep_the_notch_where_it_is_and_size_each_side_on_its_own()
    {
        var m = M with { WingWidth = 150, RightWingWidth = 40 };
        var f = IslandLayout.Compute(Mac, IslandPlacement.Auto, IslandState.Compact, m);
        var notchLeft = Mac.Notch!.X;
        var notchRight = Mac.Notch.X + Mac.Notch.Width;
        var left = f.Window.X + f.Surface.X;
        var right = f.Window.X + f.Surface.X + f.Surface.Width;
        Assert.Equal(150, notchLeft - left, 0);            // the left wing
        Assert.Equal(40, right - notchRight, 0);           // the right wing hugs its content
        Assert.Equal(Mac.Notch.Width + 190, f.Surface.Width, 3);
        // Without a separate right wing both are as wide as WingWidth, centred on the notch, as before.
        var even = IslandLayout.Compute(Mac, IslandPlacement.Auto, IslandState.Compact, M);
        Assert.Equal(notchLeft - (even.Window.X + even.Surface.X), even.Window.X + even.Surface.X + even.Surface.Width - notchRight, 0);
    }
}
