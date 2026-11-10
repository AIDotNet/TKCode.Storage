using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Island.Core.Pages;
using Island.Plugins;
using Island.UI.Plugins;
using Island.UI.Themes;
using Island.UI.Views;

namespace Island.UI.Tests;

/// <summary>The island layer of a theme (docs/design/09-island-v2.md §4).</summary>
public sealed class IslandThemeTests
{
    private static IslandThemeSpec Neon => new()
    {
        Surface = new IslandSurfaceSpec { Material = "gradient", Colors = ["#1A1033", "#3B0A45"], Angle = 0 },
        Shape = new IslandShapeSpec { Radius = 1.3 },
        Content = new IslandContentSpec { Accents = new() { ["music"] = "#FF2D95" } },
        Motion = new IslandMotionSpec { Spring = "bouncy" },
    };

    [Fact]
    public void Accents_come_from_the_theme_then_the_defaults_and_hex_is_taken_as_is() => Headless.Run(() =>
    {
        IslandTheme.Apply(Neon);
        try
        {
            Assert.Equal(Color.Parse("#FF2D95"), IslandTheme.Current.Accent("music"));
            Assert.Equal(IslandTheme.DefaultAccents["timer"], IslandTheme.Current.Accent("timer"));
            Assert.Equal(Color.Parse("#123456"), IslandTheme.Current.Accent("#123456"));
            Assert.Null(IslandTheme.Current.Accent("unknown"));
            Assert.Null(IslandTheme.Current.Accent(null));
            Assert.Equal(1.3, IslandTheme.Current.CornerScale);
            Assert.NotNull(IslandTheme.Current.Spring);

            var gradient = Assert.IsType<LinearGradientBrush>(IslandTheme.Current.Surface);
            Assert.Equal([Color.Parse("#1A1033"), Color.Parse("#3B0A45")], gradient.GradientStops.Select(g => g.Color));
            Assert.Same(IslandTheme.Current.Surface?.GetType(), Avalonia.Application.Current!.Resources[IslandTheme.SurfaceBrushKey]?.GetType());

            IslandTheme.Apply(new IslandThemeSpec { Surface = new IslandSurfaceSpec { Material = "translucent", Color = "#202030", Opacity = 0.6 } });
            var tint = Assert.IsType<SolidColorBrush>(IslandTheme.Current.Surface);
            Assert.Equal((byte)153, tint.Color.A);
        }
        finally
        {
            IslandTheme.Apply(null);
        }
        Assert.Null(IslandTheme.Current.Surface);
        Assert.False(Avalonia.Application.Current!.Resources.ContainsKey(IslandTheme.SurfaceBrushKey)); // the island's own black again
        Assert.Equal(1, IslandTheme.Current.CornerScale);
    });

    [Fact]
    public void An_activitys_accent_colours_its_accent_tone_on_the_island() => Headless.Run(() =>
    {
        IslandTheme.Apply(Neon);
        try
        {
            var view = new IslandView();
            var temp = Path.GetTempPath();
            var presenter = new IslandPresenter(view, (_, _, _) => { },
                new PagePreferencesStore(Path.Combine(temp, $"o_o-p-{Guid.NewGuid():N}.json")), s => new PageInfo(s.Key, s.Title),
                new ChipPreferencesStore(Path.Combine(temp, $"o_o-c-{Guid.NewGuid():N}.json")));
            new Window { Content = view }.Show();
            var icon = new VNode("icon", null, new Dictionary<string, object> { ["name"] = "Music", ["tone"] = "accent" }, new Dictionary<string, int>(), []);
            presenter.UpdateLive(new LiveActivitySnapshot("test.plugin", "playing", null, "播放中", LivePriority.Normal, "music", null,
                new LiveActivityView(new RegionView(icon), null, RegionView.Empty), 1));

            var drawn = view.CompactLeading.GetSelfAndLogicalDescendants().OfType<TKCode.Arc.Icons.Lucide.LucideIcon>().First();
            var brush = Assert.IsAssignableFrom<ISolidColorBrush>(drawn.Foreground);
            Assert.Equal(Color.Parse("#FF2D95"), brush.Color);

            presenter.EndLive("test.plugin/playing");                 // back to the app's accent
            Assert.False(view.CompactPanel.Resources.ContainsKey(TKCode.Arc.DesignTokens.ArcDesignTokenKind.Accent));
        }
        finally
        {
            IslandTheme.Apply(null);
        }
    });
}
