using Island.Core.Pages;

namespace Island.Core.Tests;

public class ChipPreferencesTests
{
    private static readonly ChipInfo Mascot = new("clock/mascot", "吉祥物", DefaultSide: ChipSide.Left);
    private static readonly ChipInfo Time = new("clock/time", "时间");
    private static readonly ChipInfo Usage = new("ai/summary", "AI 用量");
    private static readonly ChipInfo Work = new("ai/account#claude:work", "Claude · work", EnabledByDefault: false);

    [Fact]
    public void Chips_go_to_their_side_in_order_and_hidden_ones_stay_out()
    {
        var prefs = new ChipPreferences { Order = ["ai/summary"] };
        var (left, right) = prefs.Visible([Mascot, Time, Usage, Work]);
        Assert.Equal(["clock/mascot"], left.Select(c => c.Key));
        Assert.Equal(["ai/summary", "clock/time"], right.Select(c => c.Key));

        prefs = prefs.WithEnabled(Work.Key, true).WithSide(Time.Key, ChipSide.Left);
        (left, right) = prefs.Visible([Mascot, Time, Usage, Work]);
        Assert.Equal(["clock/mascot", "clock/time"], left.Select(c => c.Key));
        Assert.Equal(["ai/summary", "ai/account#claude:work"], right.Select(c => c.Key));
    }

    [Fact]
    public void Each_side_is_capped()
    {
        var many = Enumerable.Range(0, 6).Select(i => new ChipInfo($"p/c{i}", $"c{i}")).ToList();
        Assert.Equal(ChipPreferences.MaxPerSide, new ChipPreferences().Visible(many).Right.Count);
    }

    [Fact]
    public void Preferences_survive_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-chips-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new ChipPreferences { Order = ["clock/time"] }.WithEnabled("clock/mascot", false).WithSide("clock/time", ChipSide.Left);
            new ChipPreferencesStore(path).Set(prefs);
            var back = new ChipPreferencesStore(path).Current;
            Assert.Equal(prefs.Order, back.Order);
            Assert.False(back.Enabled["clock/mascot"]);
            Assert.Equal(ChipSide.Left, back.Sides["clock/time"]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
