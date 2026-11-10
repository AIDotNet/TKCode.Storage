namespace Island.Plugins.Host.Tests;

/// <summary>Plugins calling each other through the host (docs/design/03-plugin-system.md §7.1).</summary>
public sealed partial class PluginHostTests
{
    private void WritePluginWithManifest(string id, string manifestExtra, string permissions, string js)
    {
        var dir = Path.Combine(_root, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "1.0.0", "permissions": [{{permissions}}] {{manifestExtra}} }""");
        File.WriteAllText(Path.Combine(dir, "main.js"), js);
    }

    [Fact]
    public void A_provider_runs_in_its_own_plugin_with_its_own_permissions_and_returns_data_only()
    {
        WritePluginWithManifest("test.provider",
            """, "contributes": { "providers": [{ "interface": "echo", "id": "one", "title": "Echo one" }] }""",
            "\"secrets\"", """
            import { island, secrets } from '@island/api';
            export function activate() {
              island.provide('echo', 'one', {
                async greet(name, n) {
                  await secrets.set('seen', name);             // a permission only the provider holds
                  return { text: `hello ${name}`, twice: n * 2, secret: await secrets.get('seen') };
                },
                fail() { throw new Error('provider broke'); },
              });
            }
            """);
        WritePluginWithManifest("test.consumer", "", "", """
            import { island, ui, secrets } from '@island/api';
            const settle = (p) => p.then((v) => 'ok:' + JSON.stringify(v), (e) => 'err:' + e.message);
            export async function activate() {
              const act = island.activity.create('a');
              await new Promise((r) => setTimeout(r, 300));     // let the provider start
              const list = await island.providers.list('echo');
              const greet = await settle(island.providers.call('echo', 'one', 'greet', 'ada', 21));
              const fail = await settle(island.providers.call('echo', 'one', 'fail'));
              const missing = await settle(island.providers.call('echo', 'two', 'greet'));
              const nomethod = await settle(island.providers.call('echo', 'one', 'nope'));
              act.update({ expanded: ui.text([JSON.stringify(list), typeof secrets, greet, fail, missing, nomethod].join('\n')) });
            }
            """);
        StartDiscovered();

        var lines = ShownWhen(t => t.Split('\n').Length == 6, "six results").Split('\n');
        Assert.Equal("""[{"id":"one","title":"Echo one","plugin":"test.provider","running":true}]""", lines[0]);
        Assert.Equal("undefined", lines[1]);                                  // the consumer has no secrets…
        Assert.Equal("""ok:{"text":"hello ada","twice":42,"secret":"ada"}""", lines[2]); // …the provider used its own
        Assert.Equal("err:provider broke", lines[3]);
        Assert.Equal("err:No plugin provides echo/two.", lines[4]);
        Assert.Equal("err:echo/one has no method 'nope'", lines[5]);
        Assert.Equal("ada", _secrets.Get("O_O/test.provider", "seen"));   // stored under the provider's identity
    }
}
