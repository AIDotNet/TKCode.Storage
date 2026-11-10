using System.IO.Compression;

namespace Island.Plugins.Host.Tests;

/// <summary>Plugin packages (.zip), enabling and removing plugins.</summary>
public sealed partial class PluginHostTests
{
    private string UserPlugins => Path.Combine(_data, "user-plugins");

    private string PackSample(string id, string manifestExtra = "", string js = "export function activate() { console.info('hello'); }")
    {
        var dir = Path.Combine(_data, "src-" + id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "plugin.json"),
            $$"""{ "id": "{{id}}", "name": "Sample", "version": "1.2.0", "permissions": ["secrets"] {{manifestExtra}} }""");
        File.WriteAllText(Path.Combine(dir, "main.js"), js);
        Directory.CreateDirectory(Path.Combine(dir, ".git"));
        File.WriteAllText(Path.Combine(dir, ".git", "HEAD"), "ref");
        var zip = Path.Combine(_data, id + ".zip");
        Assert.Empty(PluginPackages.Pack(dir, zip));
        return zip;
    }

    [Fact]
    public void A_packed_plugin_installs_asks_for_consent_and_can_be_removed()
    {
        var zip = PackSample("com.example.sample");
        using (var archive = ZipFile.OpenRead(zip))
        {
            Assert.Equal(["main.js", "plugin.json"], archive.Entries.Select(e => e.FullName).Order());   // no .git
        }
        StartDiscovered(); // nothing in _root
        var package = _host!.Install(zip, UserPlugins);
        Assert.Equal("com.example.sample", package.Id);
        Assert.False(package.Bundled);
        Assert.True(_host.IsEnabled(package.Id));
        WaitForState(PluginState.AwaitingConsent);                 // a user plugin asks first

        _host.SetGranted(package.Id, ["secrets"]);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "hello"), "runs after consent");

        // Updating replaces it in place.
        var update = PackSample("com.example.sample", js: "export function activate() { console.info('v2'); }");
        _host.Install(update, UserPlugins);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "v2"), "updated plugin runs (the decision stays)");

        _host.Uninstall(package.Id);
        Assert.DoesNotContain(_host.Packages, p => p.Id == package.Id);
        Assert.False(Directory.Exists(Path.Combine(UserPlugins, package.Id)));
    }

    [Fact]
    public void Bad_packages_are_refused_with_a_reason()
    {
        StartDiscovered();
        var evil = Path.Combine(_data, "evil.zip");
        using (var archive = ZipFile.Open(evil, ZipArchiveMode.Create))
        {
            using (var w = new StreamWriter(archive.CreateEntry("plugin.json").Open()))
            {
                w.Write("""{ "id": "com.example.evil", "name": "Evil", "version": "1.0.0" }""");
            }
            using (var w = new StreamWriter(archive.CreateEntry("main.js").Open()))
            {
                w.Write("export function activate() {}");
            }
            using (var w = new StreamWriter(archive.CreateEntry("../../escape.txt").Open()))
            {
                w.Write("x");
            }
        }
        Assert.Contains("不安全的路径", Assert.Throws<InvalidDataException>(() => _host!.Install(evil, UserPlugins)).Message);
        Assert.False(File.Exists(Path.Combine(_data, "escape.txt")));

        var noManifest = Path.Combine(_data, "empty.zip");
        using (var archive = ZipFile.Open(noManifest, ZipArchiveMode.Create))
        {
            archive.CreateEntry("readme.txt");
        }
        Assert.Contains("plugin.json", Assert.Throws<InvalidDataException>(() => _host!.Install(noManifest, UserPlugins)).Message);

        var badPermission = Path.Combine(_data, "src-bad");
        Directory.CreateDirectory(badPermission);
        File.WriteAllText(Path.Combine(badPermission, "plugin.json"), """{ "id": "com.example.bad", "name": "Bad", "version": "1", "permissions": ["camera"] }""");
        File.WriteAllText(Path.Combine(badPermission, "main.js"), "");
        Assert.Contains(PluginPackages.Pack(badPermission, Path.Combine(_data, "bad.zip")), p => p.Contains("camera"));
    }

    [Fact]
    public void Optional_bundled_plugins_wait_to_be_added_and_bundled_ids_cannot_be_replaced()
    {
        _enableOptional = false;
        WritePluginWithManifest("com.example.optional", """, "optional": true""", "", "export function activate() { console.info('optional on'); }");
        WritePluginWithManifest("com.example.core", "", "", "export function activate() { console.info('core on'); }");
        StartDiscovered();
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "core on"), "non-optional runs");
        Assert.False(_host!.IsEnabled("com.example.optional"));
        Assert.DoesNotContain(_logs, l => l.Message == "optional on");

        _host.SetEnabled("com.example.optional", true);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "optional on"), "added");
        _host.SetEnabled("com.example.core", false);
        WaitFor(() => _host.GetState("com.example.core") == PluginState.Stopped ? "ok" : null, "turned off");

        var impostor = PackSample("com.example.core");
        Assert.Contains("内置插件", Assert.Throws<InvalidDataException>(() => _host.Install(impostor, UserPlugins)).Message);
    }
    [Fact]
    public void Turning_a_plugin_on_turns_on_what_it_requires()
    {
        WritePluginWithManifest("com.example.display", "", "", "export function activate() { console.info('display on'); }");
        WritePluginWithManifest("com.example.feed", """, "requires": ["com.example.display", "com.example.absent"]""", "",
            "export function activate() { console.info('feed on'); }");
        StartDiscovered();
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "display on"), "display runs");
        _host!.SetEnabled("com.example.display", false);
        _host.SetEnabled("com.example.feed", false);
        WaitFor(() => _host.GetState("com.example.display") == PluginState.Stopped ? "ok" : null, "display off");
        _logs.Clear();

        _host.SetEnabled("com.example.feed", true);
        Assert.True(_host.IsEnabled("com.example.display"));
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "display on"), "the requirement starts again");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "feed on"), "the plugin itself runs");

        // Turning the requirement off later is still the user's choice.
        _host.SetEnabled("com.example.display", false);
        Assert.False(_host.IsEnabled("com.example.display"));
        Assert.True(_host.IsEnabled("com.example.feed"));
    }
}
