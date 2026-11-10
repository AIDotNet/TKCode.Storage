using System.Collections.Concurrent;

namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    private readonly ConcurrentQueue<ChipSnapshot> _chips = new();
    private readonly ConcurrentQueue<AlertSnapshot> _alerts = new();

    private PluginHost StartWithChips(string js)
    {
        _contributes = """{ "chips": [{ "id": "time", "title": "时间", "side": "right" }, { "id": "account", "title": "账户", "multiple": true, "enabled": false }] }""";
        WritePlugin("test.plugin", js);
        _host = new PluginHost(Services, FastRestart);
        _host.ChipUpdated += _chips.Enqueue;
        _host.AlertRaised += _alerts.Enqueue;
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
        return _host;
    }

    [Fact]
    public void Chips_render_per_instance_can_be_removed_and_alerts_are_bounded()
    {
        var host = StartWithChips("""
            import { island, ui } from '@island/api';
            export function activate() {
              island.chip('time').update(ui.text('09:41'));
              island.chip('account', 'claude:work').update(ui.text('41%'), { title: 'Claude · work' });
              island.chip('account', 'codex:me').update(ui.text('0%'));
              island.chip('account', 'codex:me').remove();
              island.alert({ leading: ui.icon('Check'), trailing: ui.text('已复制') }, { seconds: 60 });
              for (const bad of [() => island.chip('time', 'x').update(ui.text('')), () => island.chip('account').update(ui.text('')),
                                 () => island.chip('nope').update(ui.text(''))]) {
                try { bad(); } catch (e) { console.warn('rejected: ' + e.message); }
              }
            }
            """);

        var time = WaitFor(() => _chips.FirstOrDefault(c => c.Key == "test.plugin/time"), "time chip");
        Assert.Equal("09:41", Text(time.View));
        Assert.Equal("时间", time.Title);
        var work = WaitFor(() => _chips.FirstOrDefault(c => c.Key == "test.plugin/account#claude:work"), "account chip");
        Assert.Equal("Claude · work", work.Title);
        WaitFor(() => _chips.FirstOrDefault(c => c.Key == "test.plugin/account#codex:me" && c.View is null), "removed");
        var alert = WaitFor(() => _alerts.FirstOrDefault(), "alert");
        Assert.Equal("已复制", Text(alert.Trailing));
        Assert.Equal(TimeSpan.FromSeconds(10), alert.Duration); // capped
        WaitFor(() => _logs.Count(l => l.Message.StartsWith("rejected")) == 3 ? "ok" : null, "three rejections");
        Assert.Equal(["test.plugin/account#claude:work", "test.plugin/time"], host.LiveChips.Select(c => c.Key).Order());

        host.Dispose();
        Assert.Empty(host.LiveChips); // the chips go with the plugin
    }
}
