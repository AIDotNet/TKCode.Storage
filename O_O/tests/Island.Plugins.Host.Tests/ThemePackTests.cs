namespace Island.Plugins.Host.Tests;

/// <summary>Theme packs (docs 03 §9): declared in plugin.json, checked when the plugin is loaded.</summary>
public sealed partial class PluginHostTests
{
    [Fact]
    public void Theme_packs_are_read_and_bad_themes_are_refused()
    {
        WritePluginWithManifest("com.example.theme", """
            , "contributes": { "themes": [
              { "id": "sunset", "title": "Sunset", "accent": "#FF6B35", "appearance": "dark", "corners": 0.8 }
            ] }
            """, "", "export function activate() {}");
        WritePluginWithManifest("com.example.badtheme", """
            , "contributes": { "themes": [
              { "id": "x", "accent": "orange", "appearance": "dim", "corners": 5 },
              { "id": "x" }
            ] }
            """, "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("accent must be #RRGGBB", problem);
        Assert.Contains("appearance must be system, light or dark", problem);
        Assert.Contains("corners must be between 0.2 and 2", problem);
        Assert.Contains("used twice", problem);

        var theme = Assert.Single(_host.Packages).Manifest.Contributes.Themes.Single();
        Assert.Equal(("sunset", "#FF6B35", "dark", 0.8), (theme.Id, theme.Accent, theme.Appearance, theme.Corners));
    }

    [Fact]
    public void Theme_tokens_may_recolour_the_island_but_not_the_notch()
    {
        WritePluginWithManifest("com.example.tokens", """
            , "contributes": { "themes": [
              { "id": "ok", "tokens": { "IslandSecondaryBrush": "#B8A9FF", "IslandCardBrush": "#221A3A" } }
            ] }
            """, "", "export function activate() {}");
        WritePluginWithManifest("com.example.badtokens", """
            , "contributes": { "themes": [
              { "id": "bad", "tokens": { "IslandNotchSurfaceBrush": "#FFFFFF", "IslandForegroundBrush": "white" } }
            ] }
            """, "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("token 'IslandNotchSurfaceBrush' cannot be overridden", problem);
        Assert.Contains("token 'IslandForegroundBrush' must be #RRGGBB", problem);
        var theme = Assert.Single(_host.Packages).Manifest.Contributes.Themes.Single();
        Assert.Equal("#B8A9FF", theme.Tokens!["IslandSecondaryBrush"]);
    }

    [Fact]
    public void The_island_layer_of_a_theme_is_read_and_its_limits_are_checked()
    {
        WritePluginWithManifest("com.example.neon", """
            , "contributes": { "themes": [
              { "id": "neon", "island": {
                  "surface": { "material": "gradient", "colors": ["#1A1033", "#3B0A45"], "angle": 90 },
                  "shape": { "radius": 1.3 },
                  "content": { "accents": { "music": "#FF2D95" } },
                  "motion": { "spring": "bouncy" } } }
            ] }
            """, "", "export function activate() {}");
        WritePluginWithManifest("com.example.badneon", """
            , "contributes": { "themes": [
              { "id": "x", "island": {
                  "surface": { "material": "lava", "colors": ["#123456"], "opacity": 0.2 },
                  "shape": { "radius": 3 },
                  "content": { "accents": { "music": "pink" } },
                  "motion": { "spring": "wobbly" } } },
              { "id": "y", "island": { "surface": { "material": "gradient" } } }
            ] }
            """, "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("island.surface.material must be solid, gradient, translucent or animated", problem);
        Assert.Contains("island.surface.colors must be 2 to 4 colours", problem);
        Assert.Contains("island.surface.opacity must be between 0.5 and 1", problem);
        Assert.Contains("island.shape.radius must be between 0.5 and 1.5", problem);
        Assert.Contains("island.content.accents.music must be #RRGGBB", problem);
        Assert.Contains("island.motion.spring must be calm, default, snappy or bouncy", problem);
        Assert.Contains("a gradient surface needs island.surface.colors", problem);

        var island = Assert.Single(_host.Packages).Manifest.Contributes.Themes.Single().Island!;
        Assert.Equal(["#1A1033", "#3B0A45"], island.Surface!.Colors);
        Assert.Equal(1.3, island.Shape!.Radius);
        Assert.Equal("#FF2D95", island.Content!.Accents!["music"]);
        Assert.Equal("bouncy", island.Motion!.Spring);
    }

    [Fact]
    public void A_themes_mascot_sounds_layout_and_split_are_checked_and_its_sound_must_be_in_its_folder()
    {
        var dir = Path.Combine(_root, "com.example.chime");
        Directory.CreateDirectory(Path.Combine(dir, "sounds"));
        File.WriteAllBytes(Path.Combine(dir, "sounds", "ding.wav"), new byte[64]);
        WritePluginWithManifest("com.example.chime", """
            , "contributes": { "themes": [
              { "id": "chime", "island": { "mascot": { "kind": "ghost", "color": "#7747FF" }, "sounds": { "alert": "sounds/ding.wav" },
                "layout": { "expanded": "media" }, "motion": { "split": "droplet" } } }
            ] }
            """, "", "export function activate() {}");
        WritePluginWithManifest("com.example.badchime", """
            , "contributes": { "themes": [
              { "id": "x", "island": { "mascot": { "kind": "dragon", "color": "red" }, "sounds": { "alert": "../../secret.wav" },
                "layout": { "expanded": "grid" }, "motion": { "split": "explode" } } },
              { "id": "y", "island": { "sounds": { "alert": "missing.mp3" } } }
            ] }
            """, "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("island.mascot.kind must be classic, pixel, ghost or cube", problem);
        Assert.Contains("island.mascot.color must be #RRGGBB", problem);
        Assert.Contains("island.sounds.alert must be a .wav, .mp3 or .aiff file inside the plugin folder", problem);
        Assert.Contains("island.layout.expanded must be standard, stacked or media", problem);
        Assert.Contains("island.motion.split must be slide or droplet", problem);
        Assert.Contains("island.sounds.alert 'missing.mp3' is not in the plugin folder", problem);

        var island = Assert.Single(_host.Packages).Manifest.Contributes.Themes.Single().Island!;
        Assert.Equal(("ghost", "media", "droplet"), (island.Mascot!.Kind, island.Layout!.Expanded, island.Motion!.Split));
    }

    [Fact]
    public void The_bundled_theme_packs_are_valid_and_only_the_default_is_on()
    {
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot(), "plugins"), "com.o_o.theme.*"))
        {
            Assert.Empty(PluginPackages.Validate(dir, out var manifest));
            Assert.NotEmpty(manifest!.Contributes.Themes);
            Assert.Equal(manifest.Id != "com.o_o.theme.default", manifest.Optional);
        }
    }

    [Fact]
    public void Layout_presets_are_checked_and_the_bundled_one_is_valid()
    {
        WritePluginWithManifest("com.example.badlayout", """
            , "contributes": { "layouts": [
              { "id": "x", "shape": "round", "dock": "bottom", "scale": 3, "pages": ["nopluginkey"], "chips": [{ "key": "a/b", "side": "middle" }] },
              { "id": "x" }
            ] }
            """, "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("shape must be capsule or notch", problem);
        Assert.Contains("dock must be top, left or right", problem);
        Assert.Contains("scale must be between 0.8 and 1.4", problem);
        Assert.Contains("pages are written plugin/page", problem);
        Assert.Contains("chip side must be left or right", problem);
        Assert.Contains("used twice", problem);

        Assert.Empty(PluginPackages.Validate(RepoPlugin("com.o_o.layout.presets"), out var manifest));
        Assert.Equal(3, manifest!.Contributes.Layouts.Count);
    }
}
