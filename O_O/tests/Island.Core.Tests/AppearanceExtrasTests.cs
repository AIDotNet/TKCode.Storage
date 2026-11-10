using Island.Core.Layout;

namespace Island.Core.Tests;

public class AppearanceExtrasTests
{
    [Fact]
    public void Defaults_are_the_primary_screen_and_hiding_for_full_screen_apps()
    {
        var a = new IslandAppearance();
        Assert.Equal(IslandDisplay.Primary, a.Display);
        Assert.Equal(FullScreenBehavior.Hide, a.FullScreen);
    }

    [Fact]
    public void Display_and_full_screen_survive_a_round_trip_and_old_files_keep_the_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-island-extras-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """{ "shape": "notch", "dock": "top", "scale": 1, "offset": 0 }""");
            var old = new IslandAppearanceStore(path).Current;
            Assert.Equal(IslandShape.Notch, old.Shape);
            Assert.Equal(IslandDisplay.Primary, old.Display);
            Assert.Equal(FullScreenBehavior.Hide, old.FullScreen);

            var store = new IslandAppearanceStore(path);
            store.Set(old with { Display = IslandDisplay.FollowMouse, FullScreen = FullScreenBehavior.Show });
            var back = new IslandAppearanceStore(path).Current;
            Assert.Equal(IslandDisplay.FollowMouse, back.Display);
            Assert.Equal(FullScreenBehavior.Show, back.FullScreen);
            Assert.Contains("\"followMouse\"", File.ReadAllText(path));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
