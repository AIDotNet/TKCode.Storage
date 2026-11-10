using Island.Core.Themes;

namespace Island.Core.Tests;

public class ThemeTests
{
    [Fact]
    public void The_schedule_is_light_between_its_two_times_even_across_midnight()
    {
        var day = ThemePreferences.Default with { Schedule = true };
        Assert.True(day.IsLightAt(new TimeOnly(7, 0)));
        Assert.True(day.IsLightAt(new TimeOnly(18, 59)));
        Assert.False(day.IsLightAt(new TimeOnly(19, 0)));
        Assert.False(day.IsLightAt(new TimeOnly(3, 0)));

        var night = day with { LightAt = new TimeOnly(20, 0), DarkAt = new TimeOnly(6, 0) };
        Assert.True(night.IsLightAt(new TimeOnly(23, 0)));
        Assert.True(night.IsLightAt(new TimeOnly(1, 0)));
        Assert.False(night.IsLightAt(new TimeOnly(12, 0)));
    }

    [Fact]
    public void Theme_preferences_survive_a_round_trip()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-theme-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new ThemePreferences("com.o_o.theme.aurora/aurora", true, new TimeOnly(6, 30), new TimeOnly(20, 15), true);
            new ThemePreferencesStore(path).Set(prefs);
            Assert.Equal(prefs, new ThemePreferencesStore(path).Current);
            Assert.Equal(ThemePreferences.Default, new ThemePreferencesStore(path + ".missing").Current);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static byte[] Fill(int count, byte r, byte g, byte b)
    {
        var pixels = new byte[count * 4];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            (pixels[i], pixels[i + 1], pixels[i + 2], pixels[i + 3]) = (b, g, r, 255);
        }
        return pixels;
    }

    [Fact]
    public void The_wallpaper_accent_is_its_main_hue_made_vivid_and_greys_give_none()
    {
        // A mostly grey wallpaper with a strong blue area and a little red: blue wins.
        var pixels = Fill(1000, 120, 120, 120).Concat(Fill(300, 20, 60, 180)).Concat(Fill(50, 200, 30, 30)).ToArray();
        var (r, g, b) = WallpaperAccent.Pick(pixels)!.Value;
        Assert.True(b > r && b > g, $"blue expected, got {r},{g},{b}");

        Assert.Null(WallpaperAccent.Pick(Fill(1000, 128, 128, 128)));   // greys only
        Assert.Null(WallpaperAccent.Pick(Fill(1000, 250, 250, 252)));   // near white
        Assert.Null(WallpaperAccent.Pick([]));

        // A dull olive still gives a usable (lifted) accent.
        var (_, _, _) = WallpaperAccent.Pick(Fill(100, 90, 95, 50))!.Value;
    }
}
