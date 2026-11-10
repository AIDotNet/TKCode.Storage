using Avalonia.Controls;
using Avalonia.LogicalTree;
using Island.Platform;
using Island.Plugins.Host;
using Island.UI.Console;

namespace Island.UI.Tests;

/// <summary>The plugin list groups by category and nests data sources; 插件市场 offers what is off.</summary>
public sealed class PluginsPageTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("o_o-pluginspage-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    private void Plugin(string id, string name, string extra = "")
    {
        var dir = Path.Combine(_root, "plugins", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{id}}", "name": "{{name}}", "version": "1.0.0", "engines": { "island": "^2.0.0" }, "main": "main.js", "permissions": [] {{extra}} }""");
        File.WriteAllText(Path.Combine(dir, "main.js"), "export function activate() {}");
    }

    private PluginHost Host()
    {
        Plugin("com.o_o.ai-usage", "AI 用量");
        Plugin("com.o_o.ai.claude", "Claude 用量", """, "requires": ["com.o_o.ai-usage"]""");
        Plugin("com.o_o.todo", "待办");
        Plugin("com.o_o.focus", "专注", """, "optional": true""");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        var host = new PluginHost(new PluginServices(data, new InMemorySecretStore()));
        host.Discover(Path.Combine(_root, "plugins"), bundled: true);
        return host;
    }

    private static List<string> Texts(Control root) =>
        root.GetLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "").ToList();

    private static List<TKCode.Arc.Card> Cards(Control root) => root.GetLogicalDescendants().OfType<TKCode.Arc.Card>().ToList();

    /// <summary>The market's cards, by name (each card carries its plugin's name for screen readers).</summary>
    private static List<string> MarketCards(Control root) =>
        [.. root.GetLogicalDescendants().OfType<Border>().Select(b => Avalonia.Automation.AutomationProperties.GetName(b)).OfType<string>()];

    [Fact]
    public void A_source_sits_in_the_card_of_the_plugin_it_feeds_and_a_plugin_turned_off_stays() => Headless.Run(() =>
    {
        var host = Host();
        host.SetEnabled("com.o_o.ai-usage", false);   // the user's state that hid Claude 用量 before
        var page = new PluginsPage(host);
        new Window { Width = 900, Height = 700, Content = new ScrollViewer { Content = page } }.Show();

        var texts = Texts(page);
        Assert.Contains("AI", texts);                              // grouped by category
        Assert.Contains("效率", texts);
        Assert.Contains("数据来源", texts);
        Assert.Contains("Claude 用量", texts);                     // inside AI 用量's card, not a card of its own
        Assert.DoesNotContain(Cards(page), c => (c.Title as string) == "Claude 用量");
        Assert.Contains(texts, t => t.StartsWith("AI 用量已关闭", StringComparison.Ordinal));
        Assert.Contains(Cards(page), c => (c.Title as string) == "待办");
        Assert.DoesNotContain(Cards(page), c => (c.Title as string) == "专注"); // optional, never added: in the market
    });

    [Fact]
    public void The_market_offers_the_plugins_that_are_off_and_filters_them() => Headless.Run(() =>
    {
        var host = Host();
        host.SetEnabled("com.o_o.ai-usage", false);
        var page = new PluginsPage(host);
        new Window { Width = 900, Height = 700, Content = new ScrollViewer { Content = page } }.Show();
        page.ShowAdd();

        var titles = MarketCards(page);
        Assert.Contains("专注", titles);
        Assert.Contains("AI 用量", titles);
        Assert.DoesNotContain("待办", titles);                     // on: it is in the list, not the market

        var search = page.GetLogicalDescendants().OfType<TKCode.Arc.SearchField>().Single();
        search.Text = "专注";
        Assert.Equal(["专注"], MarketCards(page));

        search.Text = "";
        var filter = page.GetLogicalDescendants().OfType<TKCode.Arc.SegmentedControl>().Single();
        filter.SelectedValue = "ai";
        Assert.Equal(["AI 用量"], MarketCards(page));
    });

    [Theory]
    [InlineData("com.o_o.ai.deepseek", "com.o_o.ai-usage", "ai")]
    [InlineData("com.example.ics", "com.o_o.calendar", "calendar")]
    [InlineData("com.o_o.theme.glass", "", "look")]
    [InlineData("com.o_o.netease", "", "media")]
    [InlineData("com.o_o.clipboard", "", "productivity")]
    [InlineData("com.example.thing", "", "other")]
    public void Categories_come_from_what_a_plugin_is(string id, string requires, string expected) =>
        Assert.Equal(expected, PluginCategories.Of(id, requires.Length == 0 ? [] : [requires], []));
}

/// <summary>岛体 → 活动 (docs/design/09-island-v2.md §5): the user's say over activities, saved in the shared store.</summary>
public sealed class IslandPageActivityTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("o_o-islandpage-").FullName;

    public void Dispose() => Directory.Delete(_root, recursive: true);

    [Fact]
    public void Activities_can_be_turned_alert_only_and_pinned() => Headless.Run(() =>
    {
        var dir = Path.Combine(_root, "plugins", "com.example.music");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"), """
            { "id": "com.example.music", "name": "音乐", "version": "1.0.0", "engines": { "island": "^2.0.0" }, "main": "main.js",
              "permissions": [], "contributes": { "activities": [{ "id": "playing", "title": "播放中" }, { "id": "timer", "title": "计时" }] } }
            """);
        File.WriteAllText(Path.Combine(dir, "main.js"), "export function activate() {}");
        var data = Path.Combine(_root, "data");
        Directory.CreateDirectory(data);
        using var host = new Island.Plugins.Host.PluginHost(new Island.Plugins.Host.PluginServices(data, new Island.Platform.InMemorySecretStore()));
        host.Discover(Path.Combine(_root, "plugins"), bundled: true);
        var activities = new Island.Core.Activities.ActivityPreferencesStore(Path.Combine(data, "activities.json"));
        var page = new Island.UI.Console.IslandPage(
            new Island.Core.Layout.IslandAppearanceStore(Path.Combine(data, "island.json")),
            new Island.Core.Pages.PagePreferencesStore(Path.Combine(data, "pages.json")),
            new Island.Core.Pages.ChipPreferencesStore(Path.Combine(data, "chips.json")),
            host, activities);
        var window = new Window { Width = 1200, Height = 900, Content = new ScrollViewer { Content = page } };
        window.Show();

        var policies = page.GetLogicalDescendants().OfType<TKCode.Arc.SegmentedControl>()
            .Where(s => s.Items.OfType<TKCode.Arc.SegmentedOption>().Any(o => (o.Value as string) == "alertOnly")).ToList();
        Assert.Equal(2, policies.Count);
        policies[0].SelectedValue = "alertOnly";
        Assert.Equal(Island.Core.Activities.ActivityPolicy.AlertOnly, activities.Current.PolicyOf("com.example.music/playing"));

        var pin = page.GetLogicalDescendants().OfType<TKCode.Arc.Button>().First(b => (b.Content as string) == "置顶");
        pin.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(TKCode.Arc.Button.ClickEvent));
        Assert.Equal("com.example.music/playing", activities.Current.Pinned);
        window.UpdateLayout();                                                 // the list is drawn again
        Assert.Contains(page.GetLogicalDescendants().OfType<TKCode.Arc.Button>(), b => (b.Content as string) == "已置顶");
    });
}
