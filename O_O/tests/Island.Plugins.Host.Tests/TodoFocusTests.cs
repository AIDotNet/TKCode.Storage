using System.Text.Json;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>The shipped to-do and focus plugins (plugins/com.o_o.todo, com.o_o.focus), end to end.</summary>
public sealed partial class PluginHostTests
{
    private const string Todo = "com.o_o.todo";
    private const string Focus = "com.o_o.focus";

    private static JsonElement Json(string value) => JsonSerializer.SerializeToElement(value);

    private static List<VNode> Rows(ActivitySnapshot page) => [.. Nodes(page.View.Expanded).Where(n => n.Type == "item")];

    private AmbientSnapshot WaitForAmbient(string key, Func<AmbientSnapshot, bool> match, string what) =>
        WaitFor(() => _ambientSeen.LastOrDefault(c => c.Key == key) is { } c && match(c) ? c : null, what);

    private LiveActivitySnapshot WaitForLive(string kind, Func<LiveActivitySnapshot, bool> match, string what) =>
        WaitFor(() => _host!.LiveActivities.FirstOrDefault(a => a.Kind == kind && match(a)), what);

    /// <summary>A plugin's latest page: handler ids change on every render, so events go to this one.</summary>
    private ActivitySnapshot Latest(string pluginId)
    {
        Thread.Sleep(200); // let renders already under way land
        return _pageViews.Last(s => s.PluginId == pluginId);
    }

    private static bool HasButton(ActivitySnapshot s, string label) =>
        Nodes(s.View.Expanded).Any(n => n.Type == "button" && n.GetString("label") == label);

    private void SeedStorage(string pluginId, string json)
    {
        Directory.CreateDirectory(Path.Combine(_data, pluginId));
        File.WriteAllText(Path.Combine(_data, pluginId, "storage.json"), json);
    }

    [Fact]
    public void To_dos_come_from_the_console_box_and_the_clipboard_and_are_ticked_off_on_the_island()
    {
        var clipboard = new FakeClipboard();
        CopyPlugin(RepoPlugin(Todo), Path.Combine(_root, Todo));
        StartWith(null, Services with { Clipboard = clipboard });
        WaitForPage(s => s.PluginId == Todo && Texts(s.View.Expanded).Contains("没有待办"), "empty list");

        var settings = _host!.GetSettings(Todo);
        settings.Set("add", Json("交周报\n!明天 下午3点 发版\n买牛奶"));
        var page = WaitForPage(s => s.PluginId == Todo && Rows(s).Count == 3, "three tasks");
        var release = Rows(page).Single(r => Texts(r).Contains("发版"));
        Assert.Contains("明天 15:00", Texts(release).ToList());                       // date and time understood
        Assert.Contains(Nodes(release), n => n.Type == "icon" && n.GetString("tone") == "accent"); // "!" = important
        Assert.Equal("发版", Texts(release).First());                                // and taken out of the title
        WaitForAmbient($"{Todo}/count", c => Texts(c.View).Contains("3 项待办"), "chip");

        // Keeping the box as it is and adding a line adds only the new line.
        settings.Set("add", Json("交周报\n!明天 下午3点 发版\n买牛奶\n周五 交房租"));
        page = WaitForPage(s => s.PluginId == Todo && Rows(s).Count == 4, "one more, not four");

        // Ticking a task off: it leaves the list (and the count).
        var milk = Rows(Latest(Todo)).Single(r => Texts(r).Contains("买牛奶"));
        _host.Dispatch(Todo, milk.Handlers["onClick"]);
        WaitForAmbient($"{Todo}/count", c => Texts(c.View).Contains("3 项待办"), "count after ticking off");

        // Copied lines become tasks.
        clipboard.Content = ClipboardEntry.FromText("回电话\n订机票", null, DateTimeOffset.Now);
        page = WaitForPage(s => s.PluginId == Todo && Rows(s).Count == 3, "list after ticking off");
        var paste = Nodes(Latest(Todo).View.Expanded).Single(n => n.Type == "iconButton" && n.GetString("icon") == "ClipboardPaste");
        _host.Dispatch(Todo, paste.Handlers["onClick"]);
        WaitForAmbient($"{Todo}/count", c => Texts(c.View).Contains("5 项待办"), "two pasted");
        WaitFor(() => _liveAlerts.FirstOrDefault(a => Text(a.View.Center) == "已添加 2 项"), "added alert");
    }

    [Fact]
    public void A_task_due_soon_is_a_quiet_live_activity_with_a_done_button()
    {
        CopyPlugin(RepoPlugin(Todo), Path.Combine(_root, Todo));
        var soon = DateTime.Now.AddMinutes(5);
        SeedStorage(Todo, $$"""{ "tasks": [{ "id": 1, "title": "开会", "due": "{{soon.ToUniversalTime():O}}", "allDay": false, "high": false, "done": false, "created": "2026-01-01T00:00:00Z", "pomodoros": 0 }] }""");
        StartWith(null, Services);
        var due = WaitForLive($"{Todo}/due", _ => true, "due activity");
        Assert.Equal(LivePriority.Passive, due.Priority);       // ahead of time it waits its turn
        Assert.Contains("分钟后", Text(due.View.Compact.Trailing));
        Assert.Contains("开会", Texts(due.View.Expanded.Center!));
        Assert.Equal("完成", due.View.Expanded.Trailing!.GetString("label"));
    }

    [Fact]
    public void Focus_runs_pomodoros_counts_finished_ones_and_credits_the_bound_task()
    {
        CopyPlugin(RepoPlugin(Todo), Path.Combine(_root, Todo));
        CopyPlugin(RepoPlugin(Focus), Path.Combine(_root, Focus));
        SeedStorage(Todo, """{ "tasks": [{ "id": 7, "title": "写报告", "due": null, "allDay": false, "high": false, "done": false, "created": "2026-01-01T00:00:00Z", "pomodoros": 0 }] }""");
        // A work phase that ended while O_O was closed: it finishes on start.
        var ended = DateTimeOffset.Now.AddSeconds(-5).ToUnixTimeMilliseconds();
        SeedStorage(Focus, $$"""{ "state": { "phase": "work", "running": true, "endsAt": {{ended}}, "cycle": 0, "task": { "id": 7, "title": "写报告" } } }""");
        StartWith(null, Services);

        var page = WaitForPage(s => s.PluginId == Focus && Texts(s.View.Expanded).Contains("短休息"), "now a short break");
        Assert.Contains("今天完成 1 个番茄", Texts(page.View.Expanded).ToList());
        Assert.Contains("05:00", Texts(page.View.Expanded).ToList());
        WaitFor(() => _liveAlerts.FirstOrDefault(a => Text(a.View.Center).StartsWith("完成一个番茄")), "phase-end alert");
        WaitForPage(s => s.PluginId == Todo && Texts(s.View.Expanded).Contains("🍅1"), "task credited");

        // Start the break: a live activity with the time left, whose card has the controls.
        var start = Nodes(Latest(Focus).View.Expanded).Single(n => n.Type == "button" && n.GetString("label") == "开始");
        _host!.Dispatch(Focus, start.Handlers["onClick"]);
        var session = WaitForLive($"{Focus}/session", a => Text(a.View.Compact.Trailing) is var t && (t.StartsWith("04:5") || t == "05:00"), "session activity");
        Assert.Equal(LivePriority.Normal, session.Priority);
        Assert.Equal("timer", session.Accent);
        Assert.Contains("短休息", Texts(session.View.Expanded.Center!));
        Assert.Contains(Nodes(session.View.Expanded.Bottom!), n => n.Type == "button" && n.GetString("label") == "暂停");

        // Skipping the break goes back to work, stops, and does not count.
        page = WaitForPage(s => s.PluginId == Focus && HasButton(s, "暂停"), "running");
        var skip = Nodes(Latest(Focus).View.Expanded).Single(n => n.Type == "iconButton" && n.GetString("icon") == "SkipForward");
        _host.Dispatch(Focus, skip.Handlers["onClick"]);
        page = WaitForPage(s => s.PluginId == Focus && Texts(s.View.Expanded).Contains("专注") && Texts(s.View.Expanded).Contains("25:00"), "back to work");
        Assert.Contains("今天完成 1 个番茄", Texts(page.View.Expanded).ToList());
        WaitFor(() => _host.LiveActivities.Any(a => a.Kind == $"{Focus}/session") ? null : "ended", "activity gone when not under way");

        // The to-do list offers "专注此任务", which starts focus on that task.
        var list = WaitForPage(s => s.PluginId == Todo && Nodes(s.View.Expanded).Any(n => n.GetString("icon") == "Timer"), "focus button on tasks");
        var focusButton = Nodes(Latest(Todo).View.Expanded).First(n => n.Type == "iconButton" && n.GetString("icon") == "Timer");
        _host.Dispatch(Todo, focusButton.Handlers["onClick"]);
        WaitForPage(s => s.PluginId == Focus && HasButton(s, "暂停") && Texts(s.View.Expanded).Any(t => t.StartsWith("24:5") || t == "25:00"), "focus started from the task");
    }
}
