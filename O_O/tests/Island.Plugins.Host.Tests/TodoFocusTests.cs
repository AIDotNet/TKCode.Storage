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

    private ChipSnapshot WaitForChip(string key, Func<ChipSnapshot, bool> match, string what) =>
        WaitFor(() => _chipViews.LastOrDefault(c => c.Key == key) is { } c && match(c) ? c : null, what);

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
        WaitForChip($"{Todo}/count", c => Texts(c.View).Contains("3 项待办"), "chip");

        // Keeping the box as it is and adding a line adds only the new line.
        settings.Set("add", Json("交周报\n!明天 下午3点 发版\n买牛奶\n周五 交房租"));
        page = WaitForPage(s => s.PluginId == Todo && Rows(s).Count == 4, "one more, not four");

        // Ticking a task off: it leaves the list (and the count).
        var milk = Rows(Latest(Todo)).Single(r => Texts(r).Contains("买牛奶"));
        _host.Dispatch(Todo, milk.Handlers["onClick"]);
        WaitForChip($"{Todo}/count", c => Texts(c.View).Contains("3 项待办"), "count after ticking off");

        // Copied lines become tasks.
        clipboard.Content = ClipboardEntry.FromText("回电话\n订机票", null, DateTimeOffset.Now);
        page = WaitForPage(s => s.PluginId == Todo && Rows(s).Count == 3, "list after ticking off");
        var paste = Nodes(Latest(Todo).View.Expanded).Single(n => n.Type == "iconButton" && n.GetString("icon") == "ClipboardPaste");
        _host.Dispatch(Todo, paste.Handlers["onClick"]);
        WaitForChip($"{Todo}/count", c => Texts(c.View).Contains("5 项待办"), "two pasted");
        WaitFor(() => _alertsSeen.FirstOrDefault(a => Text(a.Trailing) == "已添加 2 项"), "added alert");
    }

    [Fact]
    public void A_task_due_soon_shows_on_the_collapsed_island_and_opens_its_page()
    {
        CopyPlugin(RepoPlugin(Todo), Path.Combine(_root, Todo));
        var soon = DateTime.Now.AddMinutes(5);
        SeedStorage(Todo, $$"""{ "tasks": [{ "id": 1, "title": "开会", "due": "{{soon.ToUniversalTime():O}}", "allDay": false, "high": false, "done": false, "created": "2026-01-01T00:00:00Z", "pomodoros": 0 }] }""");
        StartWith(null, Services);
        var due = WaitForUpdate(s => s.PluginId == Todo && s.ActivityId == "due", "due activity");
        Assert.Equal(ActivityPriority.Passive, due.Priority);   // ahead of time it only waits on the collapsed island
        Assert.Equal("list", due.View.Page);
        Assert.Contains("分钟后", Text(due.View.CompactTrailing));
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
        WaitFor(() => _alertsSeen.FirstOrDefault(a => Text(a.Trailing).StartsWith("完成一个番茄")), "phase-end alert");
        WaitForPage(s => s.PluginId == Todo && Texts(s.View.Expanded).Contains("🍅1"), "task credited");

        // Start the break: a chip with the time left, and an active activity that opens this page.
        var start = Nodes(Latest(Focus).View.Expanded).Single(n => n.Type == "button" && n.GetString("label") == "开始");
        _host!.Dispatch(Focus, start.Handlers["onClick"]);
        WaitForChip($"{Focus}/remaining", c => c.View is not null && Texts(c.View).Any(t => t.StartsWith("04:5") || t == "05:00"), "time-left chip");
        var session = WaitForUpdate(s => s.PluginId == Focus && s.ActivityId == "session", "session activity");
        Assert.Equal(ActivityPriority.Active, session.Priority);
        Assert.Equal("timer", session.View.Page);

        // Skipping the break goes back to work, stops, and does not count.
        page = WaitForPage(s => s.PluginId == Focus && HasButton(s, "暂停"), "running");
        var skip = Nodes(Latest(Focus).View.Expanded).Single(n => n.Type == "iconButton" && n.GetString("icon") == "SkipForward");
        _host.Dispatch(Focus, skip.Handlers["onClick"]);
        page = WaitForPage(s => s.PluginId == Focus && Texts(s.View.Expanded).Contains("专注") && Texts(s.View.Expanded).Contains("25:00"), "back to work");
        Assert.Contains("今天完成 1 个番茄", Texts(page.View.Expanded).ToList());
        WaitForChip($"{Focus}/remaining", c => c.View is null, "chip gone when not under way");

        // The to-do list offers "专注此任务", which starts focus on that task.
        var list = WaitForPage(s => s.PluginId == Todo && Nodes(s.View.Expanded).Any(n => n.GetString("icon") == "Timer"), "focus button on tasks");
        var focusButton = Nodes(Latest(Todo).View.Expanded).First(n => n.Type == "iconButton" && n.GetString("icon") == "Timer");
        _host.Dispatch(Todo, focusButton.Handlers["onClick"]);
        WaitForPage(s => s.PluginId == Focus && HasButton(s, "暂停") && Texts(s.View.Expanded).Any(t => t.StartsWith("24:5") || t == "25:00"), "focus started from the task");
    }
}
