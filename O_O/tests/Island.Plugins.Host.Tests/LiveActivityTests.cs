using System.Collections.Concurrent;
using Island.Plugins;
using Island.Plugins.Host;

namespace Island.Plugins.Host.Tests;

/// <summary>The 2.0 island API (docs/design/09-island-v2.md §3): live activities, alerts, ambient items, home pages.</summary>
public sealed partial class PluginHostTests
{
    private readonly ConcurrentQueue<LiveActivitySnapshot> _live = new();
    private readonly ConcurrentQueue<string> _liveEnded = new();
    private readonly ConcurrentQueue<LiveAlertSnapshot> _liveAlerts = new();
    private readonly ConcurrentQueue<AmbientSnapshot> _ambientSeen = new();

    private PluginHost StartV2(string contributes, string js)
    {
        _engine = "^2.0.0";
        _contributes = contributes;
        var host = Start(js);
        host.LiveActivityUpdated += _live.Enqueue;
        host.LiveActivityEnded += (_, key) => _liveEnded.Enqueue(key);
        host.LiveAlertRaised += _liveAlerts.Enqueue;
        host.AmbientUpdated += _ambientSeen.Enqueue;
        return host;
    }

    [Fact]
    public void A_live_activity_fills_its_regions_and_updates_and_ends()
    {
        var host = StartV2("""{ "activities": [{ "id": "playing", "title": "播放中", "accent": "music" }] }""", """
            import { island, ui } from '@island/api';
            export function activate() {
              setTimeout(() => {
                const a = island.activity('playing', {
                  open: { app: 'com.netease.163music' },
                  compact: { leading: ui.icon('Music'), trailing: ui.text('晴天') },
                  minimal: ui.icon('Music'),
                  expanded: { center: ui.text('晴天'), bottom: ui.button('暂停', { onClick: () => { a.update({ priority: 'high' }); } }) },
                });
                setTimeout(() => a.update({ compact: { leading: ui.icon('Music'), trailing: ui.text('稻香') } }), 50);
                setTimeout(() => a.end(), 150);
              }, 10);
            }
            """);
        var first = WaitFor(() => _live.FirstOrDefault(), "activity");
        Assert.Equal("test.plugin/playing", first.Key);
        Assert.Equal("播放中", first.Title);
        Assert.Equal(LivePriority.Normal, first.Priority);
        Assert.Equal("music", first.Accent);
        Assert.Equal("com.netease.163music", first.Open!.App);
        Assert.Equal("Music", first.View.Compact.Leading!.GetString("name"));
        Assert.Equal("Music", first.View.Minimal!.GetString("name"));
        Assert.Null(first.View.Expanded.Leading);
        Assert.True(first.View.Expanded.Bottom!.Handlers.ContainsKey("onClick"));

        var second = WaitFor(() => _live.Skip(1).FirstOrDefault(), "update");
        Assert.Equal("稻香", second.View.Compact.Trailing!.GetString("text"));
        Assert.Equal("晴天", second.View.Expanded.Center!.GetString("text"));   // what the update did not give stays
        Assert.Equal("test.plugin/playing", WaitFor(() => _liveEnded.FirstOrDefault(), "end"));
        Assert.Empty(host.LiveActivities);
    }

    [Fact]
    public void Activities_must_be_declared_and_instances_match_multiple()
    {
        StartV2("""{ "activities": [{ "id": "usage", "multiple": true, "priority": "passive" }] }""", """
            import { island, ui } from '@island/api';
            export function activate() {
              const tryIt = (f) => { try { f(); return 'ok'; } catch (e) { return e.message; } };
              console.info(tryIt(() => island.activity('nope', { minimal: ui.text('x') })));
              console.info(tryIt(() => island.activity('usage', { minimal: ui.text('x') })));
              console.info(tryIt(() => island.activity('usage', {}, { instance: 'a' })));
              island.activity('usage', { minimal: ui.text('62%') }, { instance: 'claude' });
            }
            """);
        var instance = WaitFor(() => _live.FirstOrDefault(), "instance");
        Assert.Equal("test.plugin/usage#claude", instance.Key);
        Assert.Equal("test.plugin/usage", instance.Kind);
        Assert.Equal(LivePriority.Passive, instance.Priority);                    // the declared default
        var messages = _logs.Select(l => l.Message).ToList();
        Assert.Contains(messages, m => m.Contains("'nope' is not declared"));
        Assert.Contains(messages, m => m.Contains("declared multiple: give an instance"));
        Assert.Contains(messages, m => m.Contains("at least one of compact, minimal or expanded"));
    }

    [Fact]
    public void Alerts_one_off_and_of_an_activity_and_the_rate_limit()
    {
        StartV2("""{ "activities": [{ "id": "meeting", "priority": "high" }] }""", """
            import { island, ui } from '@island/api';
            export function activate() {
              const m = island.activity('meeting', { compact: { leading: ui.icon('Video') } });
              m.alert({ leading: ui.icon('Video'), center: ui.text('5 分钟后 · 周会'), seconds: 60 });
              island.alert({ center: ui.text('已复制') });
              let accepted = 0;
              for (let i = 0; i < 40; i++) if (island.alert({ center: ui.text(String(i)) })) accepted++;
              console.info(`accepted ${accepted}`);
            }
            """);
        var meeting = WaitFor(() => _liveAlerts.FirstOrDefault(), "activity alert");
        Assert.Equal("test.plugin/meeting", meeting.Key);
        Assert.Equal("test.plugin/meeting", meeting.Kind);
        Assert.Equal(LivePriority.High, meeting.Priority);
        Assert.Equal(TimeSpan.FromSeconds(10), meeting.Duration);                  // clamped
        var once = WaitFor(() => _liveAlerts.Skip(1).FirstOrDefault(), "one-off alert");
        Assert.Equal("test.plugin/alert", once.Key);
        Assert.Equal("test.plugin", once.Kind);
        Assert.Equal("accepted 28", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("accepted", StringComparison.Ordinal)), "count").Message);
    }

    [Fact]
    public void Ambient_items_and_home_pages_and_everything_ends_when_the_plugin_stops()
    {
        var host = StartV2("""
            { "activities": [{ "id": "a" }], "ambient": [{ "id": "temp", "side": "left" }], "home": [{ "id": "now", "title": "天气" }] }
            """, """
            import { island, ui } from '@island/api';
            export function activate() {
              island.ambient('temp').update(ui.text('17°'));
              island.home.page('now').update(ui.text('晴'), { status: ui.text('刚刚') });
              island.activity('a', { minimal: ui.text('x') });
            }
            """);
        var ambient = WaitFor(() => _ambientSeen.FirstOrDefault(), "ambient");
        Assert.Equal("test.plugin/temp", ambient.Key);
        Assert.Equal("left", ambient.Side);
        Assert.Equal("17°", ambient.View!.GetString("text"));
        var page = WaitFor(() => host.LivePages.FirstOrDefault(p => p.PageId == "now"), "home page");
        Assert.Equal("天气", page.Title);
        WaitFor(() => _live.FirstOrDefault(), "activity");

        host.SetEnabled("test.plugin", false);
        WaitFor(() => _liveEnded.FirstOrDefault(), "activity ended");
        WaitFor(() => _ambientSeen.FirstOrDefault(a => a.View is null), "ambient removed");
        Assert.Empty(host.LiveAmbient);
    }

    [Fact]
    public void A_one_x_plugin_keeps_the_one_x_api()
    {
        _engine = null;
        Start("""
            import { island } from '@island/api';
            export function activate() { console.info(`${typeof island.chip} ${typeof island.ambient}`); }
            """);
        Assert.Equal("function undefined", WaitFor(() => _logs.FirstOrDefault(), "log").Message);
    }
}
