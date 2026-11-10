using Island.Core.Geometry;
using Island.Core.Layout;

namespace Island.Core.Tests;

public class ShellLeftoversTests
{
    private static readonly (string? Name, RectD PixelBounds)[] Screens =
    [
        ("Built-in Retina Display", new RectD(0, 0, 2940, 1912)),
        ("LG HDR 4K", new RectD(2940, 0, 3840, 2160)),
        (null, new RectD(-1920, 0, 1920, 1080)),
    ];

    [Fact]
    public void A_chosen_screen_is_found_again_by_name_and_size()
    {
        var lg = ScreenIdentity.Of("LG HDR 4K", new RectD(2940, 0, 3840, 2160));
        Assert.Equal("LG HDR 4K|3840x2160", lg);
        Assert.Equal(1, ScreenIdentity.Find(lg, Screens));
        // Moved in the arrangement: still the same screen.
        Assert.Equal(1, ScreenIdentity.Find(lg, [Screens[0], ("LG HDR 4K", new RectD(-3840, 0, 3840, 2160))]));
        // Same monitor at another resolution: found by its name.
        Assert.Equal(1, ScreenIdentity.Find("LG HDR 4K|2560x1440", Screens));
        // An unnamed screen: found by its size when that is unambiguous.
        Assert.Equal(2, ScreenIdentity.Find(ScreenIdentity.Of(null, new RectD(0, 0, 1920, 1080)), Screens));
        // Not connected: the caller falls back to the primary screen.
        Assert.Equal(-1, ScreenIdentity.Find("Dell U2720Q|3840x2161", Screens));
        Assert.Equal(-1, ScreenIdentity.Find(null, Screens));
    }

    [Fact]
    public void Minimal_mode_the_chosen_screen_and_the_thin_line_are_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-island-{Guid.NewGuid():N}.json");
        try
        {
            var a = new IslandAppearance(Display: IslandDisplay.Specific, FullScreen: FullScreenBehavior.ThinLine,
                ScreenId: "LG HDR 4K|3840x2160", Minimal: true, Offset: -120);
            new IslandAppearanceStore(path).Set(a);
            Assert.Contains("\"display\": \"specific\"", File.ReadAllText(path));
            Assert.Contains("\"fullScreen\": \"thinLine\"", File.ReadAllText(path));
            Assert.Equal(a, new IslandAppearanceStore(path).Current);

            // Files written before these settings existed keep the defaults.
            File.WriteAllText(path, """{ "shape": "notch", "display": "followMouse", "fullScreen": "show" }""");
            var old = new IslandAppearanceStore(path).Current;
            Assert.Equal((IslandDisplay.FollowMouse, FullScreenBehavior.Show, false, (string?)null),
                (old.Display, old.FullScreen, old.Minimal, old.ScreenId));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
