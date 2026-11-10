using Island.Core.Pages;

namespace Island.Core.Tests;

public class PagePreferencesTests
{
    private static readonly PageInfo Usage = new("ai/usage", "AI 用量");
    private static readonly PageInfo Music = new("music/now", "音乐");
    private static readonly PageInfo Today = new("clock/today", "今日", EnabledByDefault: false);
    private static readonly PageInfo[] Offered = [Usage, Music, Today];

    [Fact]
    public void Untouched_pages_keep_their_default_and_follow_the_ordered_ones()
    {
        var prefs = new PagePreferences { Order = ["music/now"] };
        Assert.Equal(["music/now", "ai/usage", "clock/today"], prefs.Arrange(Offered).Select(p => p.Key));
        Assert.Equal(["music/now", "ai/usage"], prefs.Visible(Offered).Select(p => p.Key));
        Assert.Equal(["music/now", "ai/usage", "clock/today"],
            prefs.WithEnabled("clock/today", true).Visible(Offered).Select(p => p.Key));
    }

    [Fact]
    public void Home_is_the_users_choice_while_visible_else_the_first_page()
    {
        var visible = new PagePreferences().Visible(Offered);
        Assert.Equal("ai/usage", new PagePreferences().HomeOf(visible));
        Assert.Equal("music/now", new PagePreferences { Home = "music/now" }.HomeOf(visible));
        Assert.Equal("ai/usage", new PagePreferences { Home = "clock/today" }.HomeOf(visible)); // hidden
    }

    [Fact]
    public void Smart_home_prefers_a_recent_pick_then_an_urgent_activity_then_home()
    {
        var prefs = new PagePreferences { Home = "ai/usage" };
        var visible = prefs.Visible(Offered);
        var recently = TimeSpan.FromSeconds(5);
        var longAgo = TimeSpan.FromMinutes(5);

        Assert.Equal("music/now", SmartHome.Choose(visible, prefs, "music/now", recently, ["ai/usage"]));
        Assert.Equal("ai/usage", SmartHome.Choose(visible, prefs, "music/now", longAgo, ["ai/usage"]));
        Assert.Equal("music/now", SmartHome.Choose(visible, prefs, null, longAgo, ["clock/today", "music/now"])); // hidden skipped
        Assert.Equal("ai/usage", SmartHome.Choose(visible, prefs, null, longAgo, []));
        Assert.Null(SmartHome.Choose([], prefs, null, longAgo, []));
    }

    [Fact]
    public void Preferences_survive_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-pages-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new PagePreferences { Order = ["music/now", "ai/usage"], Home = "music/now" }.WithEnabled("ai/usage", false);
            new PagePreferencesStore(path).Set(prefs);
            var back = new PagePreferencesStore(path).Current;
            Assert.Equal(prefs.Order, back.Order);
            Assert.Equal(prefs.Home, back.Home);
            Assert.False(back.Enabled["ai/usage"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
