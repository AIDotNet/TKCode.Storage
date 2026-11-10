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
    public void The_bundled_theme_packs_are_valid_and_only_the_default_is_on()
    {
        foreach (var dir in Directory.GetDirectories(RepoPlugins(), "com.o_o.theme.*"))
        {
            Assert.Empty(PluginPackages.Validate(dir, out var manifest));
            Assert.NotEmpty(manifest!.Contributes.Themes);
            Assert.Equal(manifest.Id != "com.o_o.theme.default", manifest.Optional);
        }
    }
}
