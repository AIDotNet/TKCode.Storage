namespace Island.Plugins.Host.Tests;

/// <summary>The shipped system monitor (system.info) and the focus plugin's statistics tab, end to end.</summary>
public sealed partial class PluginHostTests
{
    [Fact]
    public void The_system_monitor_draws_gauges_once_the_host_reports_figures()
    {
        CopyPlugin(RepoPlugin("com.o_o.monitor"), Path.Combine(_root, "com.o_o.monitor"));
        StartWith(null, Services);
        // Memory is known from the first sample on macOS and Linux; the CPU needs a second one a few seconds later.
        var page = WaitForPage(s => s.PluginId == "com.o_o.monitor" && Nodes(s.View.Expanded).Any(n => n.Type == "gauge"), "a gauge");
        Assert.Contains(Nodes(page.View.Expanded), n => n.Type == "gauge" && (n.GetString("detail") ?? "").StartsWith("内存", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_the_permission_the_system_api_is_absent()
    {
        _permissions = "";
        _contributes = """{ "home": [{ "id": "usage", "title": "x" }] }""";
        StartWith("""
            import { system, island, ui } from '@island/api';
            export function activate() {
              island.home.page('usage').update(ui.text(system === undefined ? 'no-system' : 'has-system'));
            }
            """, Services);
        WaitForPage(s => Texts(s.View.Expanded).Contains("no-system"), "no system api");
    }

    [Fact]
    public void Focus_statistics_count_the_days_and_the_streak()
    {
        CopyPlugin(RepoPlugin("com.o_o.focus"), Path.Combine(_root, "com.o_o.focus"));
        string Day(int back) => DateTime.Today.AddDays(-back).ToString("yyyy-MM-dd");
        SeedStorage("com.o_o.focus", $$"""{ "days": { "{{Day(0)}}": 3, "{{Day(1)}}": 2, "{{Day(2)}}": 1, "{{Day(5)}}": 4 } }""");
        StartWith(null, Services);
        var page = WaitForPage(s => s.PluginId == "com.o_o.focus" && Nodes(s.View.Expanded).Any(n => n.Type == "tabs"), "tabs");
        var tabs = Nodes(page.View.Expanded).First(n => n.Type == "tabs");
        _host!.Dispatch("com.o_o.focus", tabs.Handlers["onChange"], "\"stats\"");
        page = WaitForPage(s => s.PluginId == "com.o_o.focus" && Texts(s.View.Expanded).Any(t => t.StartsWith("连续 3 天", StringComparison.Ordinal)), "stats view");
        Assert.Contains(Texts(page.View.Expanded), t => t.StartsWith("本周 10 个番茄", StringComparison.Ordinal));
        Assert.Contains(Texts(page.View.Expanded), t => t.Contains("近 30 天共 10 个", StringComparison.Ordinal));
    }
}
