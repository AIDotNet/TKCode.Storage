using System.Collections.Concurrent;

namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    private readonly ConcurrentQueue<PageSnapshot> _pages = new();
    private readonly ConcurrentQueue<(string Plugin, string Page)> _pagesEnded = new();

    /// <summary>Pages as snapshots with only an expanded view, so view helpers work on them.</summary>
    private readonly ConcurrentQueue<PageShot> _pageViews = new();

    /// <summary>What a test sees of a home page: a plugin's view, or a test plugin's report (a home page as well).</summary>
    private sealed record PageShot(string PluginId, string ActivityId, string Title, PageShotView View, long Sequence);

    private sealed record PageShotView(VNode? Expanded, VNode? Status);

    private void MirrorPage(PageSnapshot p)
    {
        _pages.Enqueue(p);
        var shot = new PageShot(p.PluginId, p.PageId, p.Title, new PageShotView(p.View, p.Status), p.Sequence);
        _pageViews.Enqueue(shot);
        _updates.Enqueue(shot);
    }

    private PageShot WaitForPage(Func<PageShot, bool> match, string what) =>
        WaitFor(() => _pageViews.LastOrDefault(match), what);

    private PluginHost StartWithPages(string js)
    {
        _contributes = """{ "home": [{ "id": "usage", "title": "AI 用量", "icon": "Gauge" }, { "id": "today", "title": "今日", "enabled": false }] }""";
        WritePlugin("test.plugin", js);
        _host = new PluginHost(Services, FastRestart);
        _host.PageUpdated += MirrorPage;
        _host.PageUpdated += _pages.Enqueue;
        _host.PageEnded += (p, g) => _pagesEnded.Enqueue((p, g));
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
        return _host;
    }

    [Fact]
    public void A_declared_home_page_renders_with_its_manifest_title_and_its_handlers_work()
    {
        var host = StartWithPages("""
            import { island, ui } from '@island/api';
            export function activate() {
              let n = 0;
              const render = () => island.home.page('usage').update(
                ui.button(`n ${n}`, { onClick: () => { n++; render(); } }), { status: ui.text(`${n} 次`) });
              render();
            }
            """);

        var page = WaitFor(() => _pages.LastOrDefault(), "page");
        Assert.Equal("test.plugin/usage", page.Key);
        Assert.Equal("AI 用量", page.Title);
        Assert.Equal("n 0", page.View!.GetString("label") ?? page.View.GetString("text"));
        host.Dispatch("test.plugin", page.View.Handlers["onClick"]);
        WaitFor(() => _pages.LastOrDefault(p => (p.View!.GetString("label") ?? p.View.GetString("text")) == "n 1"), "click");

        Assert.Equal("1 次", Text(_pages.Last().Status));
        var manifest = host.Packages.Single().Manifest;
        Assert.False(manifest.Contributes.Home.Single(p => p.Id == "today").Enabled);
        Assert.Equal("Gauge", manifest.Contributes.Home[0].Icon);
    }

    [Fact]
    public void An_undeclared_home_page_is_an_error_and_pages_end_with_the_plugin()
    {
        var host = StartWithPages("""
            import { island, ui } from '@island/api';
            export function activate() {
              island.home.page('usage').update(ui.text('ok'));
              try { island.home.page('nope').update(ui.text('x')); } catch (e) { console.warn('rejected: ' + e.message); }
            }
            """);

        WaitFor(() => _pages.LastOrDefault(), "page");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.Contains("rejected") && l.Message.Contains("contributes.home")), "rejection");
        Assert.DoesNotContain(_pages, p => p.PageId == "nope");

        host.Dispose();
        WaitFor(() => _pagesEnded.FirstOrDefault(e => e.Page == "usage") is { Page: not null } e ? "ended" : null, "page ended");
    }
}
