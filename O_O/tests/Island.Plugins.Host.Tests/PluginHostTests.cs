using System.Collections.Concurrent;

namespace Island.Plugins.Host.Tests;

/// <summary>End-to-end: real plugin folders on disk, real Jaunt engines, real runner threads.</summary>
public sealed partial class PluginHostTests : IDisposable
{
    // Generous: the first test in the class pays the cold start (JIT of Jaunt, engine and ICU initialisation), which
    // under a loaded CI machine can take several seconds. Waits return as soon as the condition holds.
    // On shared CI machines (GitHub sets CI=true) timers and engines run several times slower: wait longer there.
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(Environment.GetEnvironmentVariable("CI") == "true" ? 60 : 20);

    private readonly string _root = Directory.CreateTempSubdirectory("o_o-plugins-").FullName;
    private readonly string _data = Directory.CreateTempSubdirectory("o_o-data-").FullName;
    private readonly Island.Platform.InMemorySecretStore _secrets = new();
    // Tests never show real notifications: they are captured here.
    private readonly CapturingNotifier _notifier = new();
    private Island.Platform.ICalendarPlatform? _systemCalendar;
    private PluginServices Services => new(_data, _secrets) { Notifier = _notifier, SystemCalendar = _systemCalendar };

    private sealed class CapturingNotifier : Island.Plugins.Host.Services.IPluginNotifier
    {
        public readonly ConcurrentQueue<(string Title, string Body)> Shown = new();

        public Task ShowAsync(string title, string body, CancellationToken ct)
        {
            Shown.Enqueue((title, body));
            return Task.CompletedTask;
        }
    }
    private readonly ConcurrentQueue<ActivitySnapshot> _updates = new();
    private readonly ConcurrentQueue<(string Plugin, string Activity)> _ended = new();
    private readonly ConcurrentQueue<(string Plugin, PluginState State, string? Reason)> _states = new();
    private readonly ConcurrentQueue<PluginLogEntry> _logs = new();
    private PluginHost? _host;

    private static readonly PluginRunnerOptions FastRestart = new()
    {
        RestartBackoff = [TimeSpan.FromMilliseconds(50), TimeSpan.FromMilliseconds(50)],
    };

    private PluginHost Start(string js, PluginRunnerOptions? options = null, string id = "test.plugin")
    {
        WritePlugin(id, js);
        _host = new PluginHost(Services, options ?? FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PageUpdated += MirrorPage;
        _host.ChipUpdated += _chipViews.Enqueue;
        _host.AlertRaised += _alertsSeen.Enqueue;
        _host.ActivityEnded += (p, a) => _ended.Enqueue((p, a));
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
        return _host;
    }

    /// <summary>
    /// Bundled plugins marked optional start switched off; tests load plugins on purpose, so they turn them on —
    /// except the test about that default, which clears this.
    /// </summary>
    private bool _enableOptional = true;

    private void EnableOptional()
    {
        if (!_enableOptional || _host is null)
        {
            return;
        }
        foreach (var package in _host.Packages.Where(p => !_host.IsEnabled(p.Id)))
        {
            _host.SetEnabled(package.Id, true);
        }
    }

    /// <summary>Extra permissions for the next plugin written, as a JSON array body, e.g. "\"secrets\"".</summary>
    private string _permissions = "";

    /// <summary>The next plugin's <c>contributes</c> object as JSON, e.g. <c>{ "pages": [...] }</c>.</summary>
    private string _contributes = "{}";

    private string WritePlugin(string id, string js, string main = "main.js")
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{id}}", "name": "Test", "version": "1.0.0", "main": "{{main}}", "permissions": [{{_permissions}}], "contributes": {{_contributes}} }""");
        File.WriteAllText(Path.Combine(dir, main), js);
        return dir;
    }

    private T WaitFor<T>(Func<T?> probe, string what) where T : class
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (probe() is { } value)
            {
                return value;
            }
            Thread.Sleep(10);
        }
        throw Timeout(what);
    }

    private Exception Timeout(string what) =>
        new TimeoutException($"Timed out waiting for {what}. Log:\n" +
                             string.Join("\n", _logs.Select(l => $"{l.Level} {l.Message}")) +
                             "\nStates: " + string.Join(", ", _states.Select(s => $"{s.State}({s.Reason})")));

    private ActivitySnapshot WaitForUpdate(Func<ActivitySnapshot, bool> match, string what) =>
        WaitFor(() => _updates.LastOrDefault(match), what);

    private void WaitForState(PluginState state, int occurrence = 1) =>
        WaitFor(() => _states.Count(s => s.State == state) >= occurrence ? "ok" : null, $"state {state} #{occurrence}");

    private static string Text(VNode? n) => n?.GetString("text") ?? "";

    [Fact]
    public void Activate_renders_and_a_click_round_trips_to_the_plugin()
    {
        var host = Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              const act = island.activity.create('counter', { title: 'Counter', priority: 'active' });
              let n = 0;
              const render = () => act.update({
                compact: { leading: ui.icon('Hash'), trailing: ui.text(String(n)) },
                expanded: ui.stack({ gap: 'sm' }, [
                  ui.text(`count ${n}`, { size: 'xl' }),
                  ui.button('Add', { variant: 'primary', onClick: () => { n++; render(); } }),
                ]),
              });
              render();
            }
            """);

        var first = WaitForUpdate(s => s.ActivityId == "counter", "first render");
        Assert.Equal("Counter", first.Title);
        Assert.Equal(ActivityPriority.Active, first.Priority);
        Assert.Equal("icon", first.View.CompactLeading!.Type);
        Assert.Equal("Hash", first.View.CompactLeading.GetString("name"));
        Assert.Equal("0", Text(first.View.CompactTrailing));
        var button = first.View.Expanded!.Children[1];
        Assert.Equal("button", button.Type);
        Assert.Equal("primary", button.GetString("variant"));

        host.Dispatch("test.plugin", button.Handlers["onClick"]);
        var second = WaitForUpdate(s => Text(s.View.CompactTrailing) == "1", "re-render after click");
        Assert.True(second.Sequence > first.Sequence);

        // Handlers are re-registered on every render; the old id no longer does anything.
        host.Dispatch("test.plugin", button.Handlers["onClick"]);
        var newButton = second.View.Expanded!.Children[1];
        host.Dispatch("test.plugin", newButton.Handlers["onClick"]);
        WaitForUpdate(s => Text(s.View.CompactTrailing) == "2", "second click");
        Thread.Sleep(100);
        Assert.DoesNotContain(_updates, s => Text(s.View.CompactTrailing) == "3");
    }

    [Fact]
    public void A_render_identical_to_the_last_is_not_sent_again_and_its_handlers_still_work()
    {
        var host = Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              const act = island.activity.create('same', { title: 'Same', priority: 'active' });
              let n = 0;
              const render = () => act.update({
                compact: { leading: ui.icon('Hash'), trailing: ui.text(String(n)) },
                expanded: ui.button('Add', { onClick: () => { n++; render(); } }),
              });
              render();
              render(); // nothing changed: dropped, but the button now runs this render's closure
            }
            """);

        var first = WaitForUpdate(s => s.ActivityId == "same", "first render");
        Thread.Sleep(100);
        Assert.Single(_updates, s => s.ActivityId == "same");
        host.Dispatch("test.plugin", first.View.Expanded!.Handlers["onClick"]);
        WaitForUpdate(s => Text(s.View.CompactTrailing) == "1", "click on the deduplicated render");
    }

    [Fact]
    public void Timers_fire_and_intervals_can_be_cleared()
    {
        Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              const act = island.activity.create('t');
              let ticks = 0;
              setTimeout((x) => act.update({ expanded: ui.text('timeout ' + x) }), 20, 'ok');
              const h = setInterval(() => {
                ticks++;
                act.update({ expanded: ui.text('tick ' + ticks) });
                if (ticks === 3) clearInterval(h);
              }, 20);
            }
            """);

        WaitForUpdate(s => Text(s.View.Expanded) == "timeout ok", "setTimeout with argument");
        WaitForUpdate(s => Text(s.View.Expanded) == "tick 3", "third interval tick");
        Thread.Sleep(150);
        Assert.DoesNotContain(_updates, s => Text(s.View.Expanded) == "tick 4");
    }

    [Fact]
    public void A_throwing_handler_is_logged_and_the_plugin_keeps_running()
    {
        var host = Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              const act = island.activity.create('a');
              let ok = 0;
              const render = () => act.update({ expanded: ui.row({}, [
                ui.button('boom', { onClick: () => { throw new Error('kaboom'); } }),
                ui.button('ok', { onClick: () => { ok++; render(); } }),
                ui.text('ok ' + ok),
              ]) });
              render();
            }
            """);

        var view = WaitForUpdate(_ => true, "render").View.Expanded!;
        host.Dispatch("test.plugin", view.Children[0].Handlers["onClick"]);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.Contains("kaboom")), "error log");
        host.Dispatch("test.plugin", view.Children[1].Handlers["onClick"]);
        WaitForUpdate(s => Text(s.View.Expanded!.Children[2]) == "ok 1", "plugin still responsive");
        Assert.Equal(PluginState.Running, host.GetState("test.plugin"));
    }

    [Fact]
    public void A_runaway_handler_exceeds_its_budget_and_the_plugin_restarts()
    {
        var host = Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              const act = island.activity.create('loop');
              act.update({ expanded: ui.button('hang', { onClick: () => { for (;;) {} } }) });
            }
            """);

        var first = WaitForUpdate(_ => true, "first render");
        WaitForState(PluginState.Running);
        host.Dispatch("test.plugin", first.View.Expanded!.Handlers["onClick"]);

        WaitForState(PluginState.Restarting);
        Assert.Contains(_ended, e => e.Activity == "loop");          // its activities leave the island
        Assert.Contains(_states, s => s.Reason?.Contains("budget") == true);
        WaitForState(PluginState.Running, occurrence: 2);              // and it comes back
        WaitFor(() => _updates.Count >= 2 ? "ok" : null, "render after restart");
    }

    [Fact]
    public void A_plugin_that_keeps_faulting_is_disabled()
    {
        Start("export function activate() { for (;;) {} }", FastRestart with { ActivateTimeout = TimeSpan.FromMilliseconds(100) });
        WaitForState(PluginState.Disabled);
        Assert.Equal(2, _states.Count(s => s.State == PluginState.Restarting)); // backoff has two steps
    }

    [Fact]
    public void Imports_outside_the_plugin_folder_and_bare_packages_are_refused()
    {
        File.WriteAllText(Path.Combine(_root, "secret.js"), "export const x = 1;");
        Start("import { x } from '../secret.js'; export function activate() {}");
        WaitForState(PluginState.Restarting);
        Assert.Contains(_logs, l => l.Message.Contains("outside the plugin folder"));

        _host!.Dispose();
        _states.Clear();
        _logs.Clear();
        Directory.Delete(Path.Combine(_root, "test.plugin"), true);
        Start("import lodash from 'lodash'; export function activate() {}");
        WaitForState(PluginState.Restarting);
        Assert.Contains(_logs, l => l.Message.Contains("bundle other packages"));
    }

    [Fact]
    public void Relative_imports_inside_the_plugin_folder_work_including_json()
    {
        var dir = WritePlugin("test.plugin", """
            import { island, ui } from '@island/api';
            import { greet } from './lib/greet.js';
            import data from './data.json' with { type: 'json' };
            export function activate() {
              island.activity.create('a').update({ expanded: ui.text(greet(data.name)) });
            }
            """);
        Directory.CreateDirectory(Path.Combine(dir, "lib"));
        File.WriteAllText(Path.Combine(dir, "lib", "greet.js"), "export const greet = (n) => `hello ${n}`;");
        File.WriteAllText(Path.Combine(dir, "data.json"), """{ "name": "island" }""");
        _host = new PluginHost(Services, FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PageUpdated += MirrorPage;
        _host.ChipUpdated += _chipViews.Enqueue;
        _host.AlertRaised += _alertsSeen.Enqueue;
        _host.PluginLog += _logs.Enqueue;
        _host.Discover(_root, bundled: true);
        EnableOptional();
        _host.StartAll();
        WaitForUpdate(s => Text(s.View.Expanded) == "hello island", "render using imports");
    }

    [Fact]
    public void Console_output_reaches_the_host_log()
    {
        Start("export function activate() { console.log('hi', { a: 1 }); console.warn('careful'); }");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "hi {\"a\":1}" && l.Level == PluginLogLevel.Info), "info log");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "careful" && l.Level == PluginLogLevel.Warn), "warn log");
    }

    [Fact]
    public void A_plugin_may_hold_at_most_two_activities()
    {
        Start("""
            import { island, ui } from '@island/api';
            export function activate() {
              island.activity.create('a').update({ expanded: ui.text('a') });
              island.activity.create('b').update({ expanded: ui.text('b') });
              island.activity.create('c').update({ expanded: ui.text('c') });
            }
            """);
        WaitForState(PluginState.Restarting);
        Assert.Contains(_logs, l => l.Message.Contains("at most 2 activities"));
    }

    public void Dispose()
    {
        _host?.Dispose();
        foreach (var dir in new[] { _root, _data })
        {
            try
            {
                Directory.Delete(dir, true);
            }
            catch (IOException)
            {
            }
        }
    }
}
