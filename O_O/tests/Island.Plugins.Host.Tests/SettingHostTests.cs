using System.Text.Json;

namespace Island.Plugins.Host.Tests;

/// <summary><c>network:@setting</c>: a plugin may reach exactly the site the user typed into that setting.</summary>
public sealed partial class PluginHostTests
{
    [Fact]
    public void A_plugin_reaches_only_the_site_the_user_entered_and_follows_changes()
    {
        using var site = new FakeBillingServer(new() { ["/ping"] = "\"pong\"" });
        using var other = new FakeBillingServer(new() { ["/ping"] = "\"other\"" });
        WritePluginWithManifest("test.site",
            """, "contributes": { "settings": { "properties": { "site": { "type": "string", "default": "" } } } }""",
            "\"network:@site\"", $$"""
            import { http, settings } from '@island/api';
            const probe = async (label, url) => {
              try { console.info(`${label}: reached ${(await http.fetch(url)).status > 0}`); } catch (e) { console.info(`${label}: refused ${e.message}`); }
            };
            export async function activate() {
              await probe('before', '{{site.Url}}/ping');
              settings.onChange(async () => {
                await probe('site', '{{site.Url}}/ping');
                await probe('other port', '{{other.Url}}/ping');
              });
            }
            """);
        StartDiscovered();

        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("before: refused")), "nothing allowed before the user enters a site");
        _host!.GetSettings("test.site").Set("site", JsonDocument.Parse($"\"{site.Url}/\"").RootElement);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "site: reached true"), "the entered site (plain http, because the user typed http)");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("other port: refused")), "another port is another site");
    }

    [Fact]
    public void A_setting_host_permission_needs_a_plain_text_setting()
    {
        WritePluginWithManifest("test.bad",
            """, "contributes": { "settings": { "properties": { "key": { "type": "string", "format": "password" } } } }""",
            "\"network:@key\", \"network:@missing\"", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("network:@key needs a string setting", problem);
        Assert.Contains("network:@missing needs a string setting", problem);
    }
}
