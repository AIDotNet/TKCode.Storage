namespace Island.Plugins.Host.Tests;

/// <summary>Ambient items (docs/design/09-island-v2.md §3): one per id, or one per instance for items declared multiple.</summary>
public sealed partial class PluginHostTests
{
    [Fact]
    public void Ambient_items_render_per_instance_can_be_removed_and_go_with_the_plugin()
    {
        var host = StartV2("""
            { "ambient": [{ "id": "time", "title": "时间", "side": "left" }, { "id": "account", "title": "账户", "multiple": true, "enabled": false }] }
            """, """
            import { island, ui } from '@island/api';
            export function activate() {
              island.ambient('time').update(ui.text('09:41'));
              island.ambient('account', 'claude:work').update(ui.text('41%'), { title: 'Claude · work' });
              island.ambient('account', 'codex:me').update(ui.text('0%'));
              island.ambient('account', 'codex:me').remove();
              for (const bad of [() => island.ambient('time', 'x').update(ui.text('')), () => island.ambient('account').update(ui.text('')),
                                 () => island.ambient('nope').update(ui.text(''))]) {
                try { bad(); } catch (e) { console.warn('rejected: ' + e.message); }
              }
            }
            """);

        var time = WaitFor(() => _ambientSeen.FirstOrDefault(c => c.Key == "test.plugin/time"), "time item");
        Assert.Equal("09:41", Text(time.View));
        Assert.Equal("时间", time.Title);
        Assert.Equal("left", time.Side);
        Assert.True(time.EnabledByDefault);
        var work = WaitFor(() => _ambientSeen.FirstOrDefault(c => c.Key == "test.plugin/account#claude:work"), "account item");
        Assert.Equal("Claude · work", work.Title);
        Assert.False(work.EnabledByDefault);                         // declared enabled: false
        WaitFor(() => _ambientSeen.FirstOrDefault(c => c.Key == "test.plugin/account#codex:me" && c.View is null), "removed");
        WaitFor(() => _logs.Count(l => l.Message.StartsWith("rejected", StringComparison.Ordinal)) == 3 ? "ok" : null, "three rejections");
        Assert.Equal(["test.plugin/account#claude:work", "test.plugin/time"], host.LiveAmbient.Select(c => c.Key).Order());

        host.Dispose();
        Assert.Empty(host.LiveAmbient); // the items go with the plugin
    }
}
