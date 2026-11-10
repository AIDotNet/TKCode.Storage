using Avalonia.Controls;
using Avalonia.LogicalTree;
using Island.Core.Pages;
using Island.Core.State;
using Island.Plugins;
using Island.UI.Plugins;
using Island.UI.Views;

namespace Island.UI.Tests;

/// <summary>The 2.0 island in the presenter (docs/design/09-island-v2.md §2).</summary>
public sealed class LivePresenterTests
{
    private static long s_sequence;

    private static VNode Text(string text) => new("text", null, new Dictionary<string, object> { ["text"] = text }, new Dictionary<string, int>(), []);

    private static LiveActivitySnapshot Live(string id, LivePriority priority, string compact, string? center = null) =>
        new("test.plugin", id, null, id, priority, null, null,
            new LiveActivityView(new RegionView(Text(compact + "-l"), Text(compact)), Text(compact + "-m"),
                center is null ? RegionView.Empty : new RegionView(Center: Text(center))),
            ++s_sequence);

    private static (IslandPresenter Presenter, IslandView View) Make()
    {
        var view = new IslandView();
        var temp = Path.GetTempPath();
        var presenter = new IslandPresenter(view, (_, _, _) => { },
            new PagePreferencesStore(Path.Combine(temp, $"o_o-p-{Guid.NewGuid():N}.json")),
            s => new PageInfo(s.Key, s.Title),
            new ChipPreferencesStore(Path.Combine(temp, $"o_o-c-{Guid.NewGuid():N}.json")));
        new Window { Content = view }.Show();
        return (presenter, view);
    }

    private static List<string> Texts(Control? c) =>
        c is null ? [] : [.. c.GetSelfAndLogicalDescendants().OfType<TextBlock>().Select(t => t.Text ?? "")];

    [Fact]
    public void The_highest_activity_takes_the_compact_place_and_the_second_is_the_bubble() => Headless.Run(() =>
    {
        var (presenter, view) = Make();
        LiveActivitySnapshot? bubble = null;
        presenter.BubbleChanged += s => bubble = s;

        presenter.UpdateLive(Live("music", LivePriority.Normal, "晴天"));
        Assert.Contains("晴天", Texts(view.CompactTrailing.Content as Control));
        Assert.Null(bubble);

        presenter.UpdateLive(Live("meeting", LivePriority.High, "5 分钟"));
        Assert.Contains("5 分钟", Texts(view.CompactTrailing.Content as Control));
        Assert.Equal("test.plugin/music", bubble!.Key);

        presenter.BringToFront("test.plugin/music");                     // the user clicked the bubble
        Assert.Contains("晴天", Texts(view.CompactTrailing.Content as Control));
        Assert.Equal("test.plugin/meeting", bubble!.Key);

        presenter.Cycle(1);
        Assert.Contains("5 分钟", Texts(view.CompactTrailing.Content as Control));

        presenter.EndLive("test.plugin/meeting");
        Assert.Contains("晴天", Texts(view.CompactTrailing.Content as Control));
        Assert.Null(bubble);
    });

    [Fact]
    public void Ambient_items_show_when_idle_and_make_way_for_an_activity() => Headless.Run(() =>
    {
        var (presenter, view) = Make();
        presenter.UpdateAmbient(new AmbientSnapshot("test.plugin", "temp", "left", Text("17°"), ++s_sequence));
        Assert.Contains("17°", Texts(view.CompactLeading.Content as Control));

        presenter.UpdateLive(Live("music", LivePriority.Normal, "晴天"));
        Assert.DoesNotContain("17°", Texts(view.CompactLeading.Content as Control));
        presenter.EndLive("test.plugin/music");
        Assert.Contains("17°", Texts(view.CompactLeading.Content as Control));

        presenter.UpdateAmbient(new AmbientSnapshot("test.plugin", "temp", "left", null, ++s_sequence));
        Assert.DoesNotContain("17°", Texts(view.CompactLeading.Content as Control));
    });

    [Fact]
    public void Opening_shows_the_activity_card_and_home_is_one_click_away() => Headless.Run(() =>
    {
        var (presenter, view) = Make();
        presenter.UpdatePage(new PageSnapshot("test.plugin", "home", "主页一", Text("页面内容"), ++s_sequence));
        presenter.UpdateLive(Live("music", LivePriority.Normal, "晴天", center: "晴天 · 周杰伦"));

        presenter.OnStateChanged(IslandState.Expanded);
        Assert.Contains("晴天 · 周杰伦", Texts(view.ExpandedContent.Content as Control));
        Assert.Equal("music", view.ExpandedTitle.Text);
        var home = Assert.IsType<TKCode.Arc.Button>(view.HeaderStatus.Content);
        home.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(TKCode.Arc.Button.ClickEvent));
        Assert.Contains("页面内容", Texts(view.ExpandedContent.Content as Control));

        presenter.OnStateChanged(IslandState.Compact);
        presenter.OpenHomeNext();                                          // the hotkey
        presenter.OnStateChanged(IslandState.Expanded);
        Assert.Contains("页面内容", Texts(view.ExpandedContent.Content as Control));
    });

    [Fact]
    public void An_alert_shows_over_the_activity_for_its_time() => Headless.Run(() =>
    {
        var (presenter, view) = Make();
        presenter.UpdateLive(Live("music", LivePriority.Normal, "晴天"));
        presenter.RaiseLiveAlert(new LiveAlertSnapshot("test.plugin", null, LivePriority.Normal,
            new RegionView(Center: Text("已复制")), TimeSpan.FromSeconds(1), ++s_sequence));
        Assert.Contains("已复制", Texts(view.CompactTrailing.Content as Control));
    });
}
