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
