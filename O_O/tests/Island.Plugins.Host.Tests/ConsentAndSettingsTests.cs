using System.Text.Json;

namespace Island.Plugins.Host.Tests;

/// <summary>User consent for permissions, and plugin settings edited from outside (the console).</summary>
public sealed partial class PluginHostTests
{
    private PluginHost StartUserPlugin(string js, string? settingsSchema = null)
    {
        var dir = WritePlugin("test.plugin", js);
        if (settingsSchema is not null)
        {
            var manifest = File.ReadAllText(Path.Combine(dir, "plugin.json"));
            File.WriteAllText(Path.Combine(dir, "plugin.json"),
                manifest.TrimEnd().TrimEnd('}') + $$""", "contributes": { "settings": {{settingsSchema}} } }""");
        }
        _host = new PluginHost(Services, FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PageUpdated += MirrorPage;
        _host.ChipUpdated += _chipViews.Enqueue;
        _host.AlertRaised += _alertsSeen.Enqueue;
        SubscribeLive(_host);
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: false));
        EnableOptional();
        _host.StartAll();
        return _host;
    }

    private const string ShowGranted = """
        import { island, ui, granted, secrets, http } from '@island/api';
        export function activate() {
          island.activity.create('a').update({ expanded: ui.text(
            `granted=${[...granted].sort().join('+')} secrets=${typeof secrets} http=${typeof http}`) });
        }
        """;

    [Fact]
    public void A_user_plugin_waits_for_consent_and_runs_with_exactly_what_was_granted()
    {
        _permissions = "\"secrets\", \"network:api.example.com\"";
        var host = StartUserPlugin(ShowGranted);

        WaitForState(PluginState.AwaitingConsent);
        Assert.Contains(_states, s => s.Reason?.Contains("network:api.example.com") == true);
        Assert.Empty(_updates);                                   // never started
        var status = host.GetPermissions("test.plugin");
        Assert.False(status.Decided);
        Assert.Equal(2, status.Pending.Count());

        host.SetGranted("test.plugin", ["secrets", "network:evil.example"]); // undeclared ones are ignored
        Assert.Equal("granted=secrets secrets=object http=undefined", ShownWhen(t => t.StartsWith("granted"), "run after consent"));

        host.SetGranted("test.plugin", []);                       // revoke everything: restarts without them
        Assert.Equal("granted= secrets=undefined http=undefined", ShownWhen(t => t == "granted= secrets=undefined http=undefined", "restart"));
        Assert.True(host.GetPermissions("test.plugin").Decided);
    }

    [Fact]
    public void Decisions_are_remembered_across_restarts_of_the_host()
    {
        _permissions = "\"secrets\"";
        var host = StartUserPlugin(ShowGranted);
        WaitForState(PluginState.AwaitingConsent);
        host.SetGranted("test.plugin", ["secrets"]);
        ShownWhen(t => t.Contains("secrets=object"), "first run");

        host.Dispose();
        _updates.Clear();
        _states.Clear();
        _host = new PluginHost(Services, FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PageUpdated += MirrorPage;
        _host.ChipUpdated += _chipViews.Enqueue;
        _host.AlertRaised += _alertsSeen.Enqueue;
        SubscribeLive(_host);
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.Discover(_root, bundled: false);
        EnableOptional();
        _host.StartAll();
        Assert.Equal("granted=secrets secrets=object http=undefined", Shown());
        Assert.DoesNotContain(_states, s => s.State == PluginState.AwaitingConsent);
    }

    [Fact]
    public void Bundled_plugins_are_granted_what_they_declare_except_local_logins()
    {
        _permissions = "\"secrets\", \"localAuth:codex-cli\"";
        var host = Start(ShowGranted); // Start discovers as bundled
        WaitForState(PluginState.AwaitingConsent);                 // a local login always asks, even when bundled
        Assert.Contains(_states, s => s.Reason?.Contains("localAuth:codex-cli") == true);
        Assert.False(host.IsChangeable("test.plugin", "secrets"));
        Assert.True(host.IsChangeable("test.plugin", "localAuth:codex-cli"));

        host.SetGranted("test.plugin", []);                        // declining the login keeps the manifest's other permissions
        Assert.Equal("granted=secrets secrets=object http=undefined", ShownWhen(t => t.StartsWith("granted"), "run"));
    }

    private const string Schema = """
        { "properties": {
            "city":   { "type": "string", "title": "City", "default": "Shanghai" },
            "units":  { "type": "string", "title": "Units", "enum": ["metric", "imperial"], "default": "metric" },
            "every":  { "type": "integer", "title": "Refresh minutes", "minimum": 5, "maximum": 120, "default": 15 },
            "apiKey": { "type": "string", "title": "API key", "format": "password" } } }
        """;

    [Fact]
    public void Settings_have_defaults_validate_and_notify_the_plugin_and_passwords_go_to_the_credential_store()
    {
        var host = StartUserPlugin("""
            import { island, ui, settings } from '@island/api';
            export async function activate() {
              const act = island.activity.create('a');
              const show = async (why) => act.update({ expanded: ui.text(
                `${why} city=${settings.get('city')} every=${settings.get('every')} key=${await settings.getSecret('apiKey')} all=${JSON.stringify(settings.all())}`) });
              settings.onChange((key) => show('changed:' + key));
              await show('start');
            }
            """, Schema);

        Assert.Equal("""start city=Shanghai every=15 key=null all={"city":"Shanghai","units":"metric","every":15}""",
            ShownWhen(t => t.StartsWith("start"), "defaults"));

        var store = host.GetSettings("test.plugin");
        Assert.Equal(4, store.Definitions.Count);
        store.Set("city", JsonDocument.Parse("\"Hangzhou\"").RootElement);
        Assert.StartsWith("changed:city city=Hangzhou", ShownWhen(t => t.StartsWith("changed:city"), "change event"));

        Assert.Throws<ArgumentException>(() => store.Set("every", JsonDocument.Parse("3").RootElement));       // below minimum
        Assert.Throws<ArgumentException>(() => store.Set("every", JsonDocument.Parse("7.5").RootElement));     // not an integer
        Assert.Throws<ArgumentException>(() => store.Set("units", JsonDocument.Parse("\"kelvin\"").RootElement));
        Assert.Throws<ArgumentException>(() => store.Set("nope", JsonDocument.Parse("1").RootElement));

        store.Set("apiKey", JsonDocument.Parse("\"k-123\"").RootElement);
        Assert.Contains("key=k-123", ShownWhen(t => t.StartsWith("changed:apiKey"), "secret change"));
        Assert.Equal("k-123", _secrets.Get("O_O/test.plugin", "setting:apiKey"));
        var onDisk = File.ReadAllText(Path.Combine(_data, "test.plugin", "settings.json"));
        Assert.Contains("Hangzhou", onDisk);
        Assert.DoesNotContain("k-123", onDisk);                  // never written to the settings file
    }

    [Fact]
    public void An_invalid_settings_schema_rejects_the_plugin()
    {
        var dir = WritePlugin("test.plugin", "export function activate() {}");
        var manifest = File.ReadAllText(Path.Combine(dir, "plugin.json"));
        File.WriteAllText(Path.Combine(dir, "plugin.json"), manifest.TrimEnd().TrimEnd('}') + """
            , "contributes": { "settings": { "properties": {
                "a": { "type": "date" },
                "b": { "type": "integer", "default": "ten" },
                "c": { "type": "string", "format": "password", "default": "hunter2" } } } } }
            """);
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root));
        Assert.Contains("unsupported type 'date'", problem);
        Assert.Contains("'b' has an invalid default", problem);
        Assert.Contains("password and cannot have a default", problem);
    }
}
