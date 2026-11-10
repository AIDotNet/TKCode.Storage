using Avalonia;
using Island.UI.Plugins;

namespace Island.UI.Tests;

public sealed class SatelliteTests
{
    private static readonly PixelRect Screen = new(0, 0, 2000, 1200);

    [Fact]
    public void Auto_goes_right_of_the_island_when_there_is_room()
    {
        var island = new PixelRect(900, 0, 200, 60);
        Assert.Equal("right", SatellitePlacement.ChooseSide("auto", island, Screen, 300));
        var at = SatellitePlacement.Choose("auto", island, Screen, false, 300, 60);
        Assert.Equal(new PixelPoint(island.Right + SatellitePlacement.Gap, 0), at);
    }

    [Fact]
    public void Auto_flips_left_when_the_right_side_would_leave_the_screen()
    {
        var island = new PixelRect(1700, 0, 200, 60);
        Assert.Equal("left", SatellitePlacement.ChooseSide("auto", island, Screen, 300));
        var at = SatellitePlacement.Choose(null, island, Screen, false, 300, 60);
        Assert.Equal(1700 - SatellitePlacement.Gap - 300, at.X);
    }

    [Fact]
    public void A_fixed_side_is_kept_but_never_leaves_the_screen()
    {
        var island = new PixelRect(1900, 0, 90, 60);
        var at = SatellitePlacement.Choose("right", island, Screen, false, 300, 60);
        Assert.Equal(1700, at.X);
        Assert.Equal("left", SatellitePlacement.ChooseSide("left", new PixelRect(10, 0, 100, 60), Screen, 300));
    }

    [Fact]
    public void An_island_on_a_side_edge_gets_its_satellite_below()
    {
        var island = new PixelRect(0, 500, 40, 200);
        var at = SatellitePlacement.Choose("auto", island, Screen, true, 300, 60);
        Assert.Equal(0, at.X);
        Assert.Equal(island.Bottom + SatellitePlacement.Gap, at.Y);
        Assert.Equal(0, SatellitePlacement.SlideDirection("auto", island, Screen, true, 300));
    }

    [Fact]
    public void App_icon_fallback_letter_comes_from_the_last_segment()
    {
        Assert.Equal("X", AppIconView.Letter("com.tencent.xinWeChat"));
        Assert.Equal("Q", AppIconView.Letter("com.tencent.qq"));
        Assert.Equal("?", AppIconView.Letter(""));
        Assert.Equal("?", AppIconView.Letter(null));
        Assert.Equal("A", AppIconView.Letter("app."));
    }

    [Fact]
    public void App_icon_size_is_clamped_and_a_broken_resolver_means_no_icon()
    {
        Assert.Equal(20, AppIconView.ClampSize(null));
        Assert.Equal(8, AppIconView.ClampSize(1));
        Assert.Equal(64, AppIconView.ClampSize(500));
        var before = VNodeRenderer.AppIconResolver;
        try
        {
            AppIconView.ClearCache();
            VNodeRenderer.AppIconResolver = _ => throw new InvalidOperationException();
            Assert.Null(AppIconView.IconFor("com.example.one"));
        }
        finally
        {
            VNodeRenderer.AppIconResolver = before;
        }
    }

    [Fact]
    public void Satellite_preferences_round_trip_and_drop_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"sat-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SatellitePreferencesStore(path);
            Assert.True(store.IsEnabled("p/message"));
            store.SetEnabled("p/message", false);
            store.SetSide("p/message", "left");
            store.SetSide("p/other", "bogus");
            var again = new SatellitePreferencesStore(path);
            Assert.False(again.IsEnabled("p/message"));
            Assert.Equal("left", again.SideOf("p/message"));
            Assert.Null(again.SideOf("p/other"));
            again.Set("p/message", new SatelliteSetting());
            Assert.DoesNotContain("p/message", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void App_icon_node_renders_a_letter_circle_when_the_app_has_no_icon()
    {
        Headless.Run(() =>
        {
            var before = VNodeRenderer.AppIconResolver;
            try
            {
                AppIconView.ClearCache();
                VNodeRenderer.AppIconResolver = _ => null;
                var node = new Island.Plugins.VNode("appIcon", null,
                    new Dictionary<string, object> { ["appId"] = "com.tencent.qq", ["size"] = 200.0 },
                    new Dictionary<string, int>(), []);
                var control = new VNodeRenderer((_, _) => { }).Render(node, null);
                var icon = Assert.IsType<AppIconView>(control);
                Assert.Equal(64, icon.Width);
                var text = Assert.IsType<Avalonia.Controls.TextBlock>(icon.Child);
                Assert.Equal("Q", text.Text);
            }
            finally
            {
                VNodeRenderer.AppIconResolver = before;
            }
        });
    }
}
