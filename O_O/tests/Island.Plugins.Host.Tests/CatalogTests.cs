using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Island.Core.Updates;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>The O_O plugin catalog client, against a fake O_O service on this machine.</summary>
public sealed partial class PluginHostTests
{
    /// <summary>Serves /api/plugins (the given JSON) and /plugins/&lt;file&gt; from a folder.</summary>
    private sealed class FakeCatalogService : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly string Folder = Directory.CreateTempSubdirectory("o_o-catalog-").FullName;
        public string CatalogJson = """{ "plugins": [] }""";
        public readonly List<string> Requests = [];

        public FakeCatalogService()
        {
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
            }
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Serve();
        }

        public string Url { get; }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                lock (Requests) { Requests.Add(ctx.Request.Url!.PathAndQuery); }
                byte[] body;
                if (ctx.Request.Url!.AbsolutePath == "/api/plugins")
                {
                    body = Encoding.UTF8.GetBytes(CatalogJson);
                    ctx.Response.ContentType = "application/json";
                }
                else if (ctx.Request.Url.AbsolutePath.StartsWith("/plugins/", StringComparison.Ordinal) &&
                         File.Exists(Path.Combine(Folder, Path.GetFileName(ctx.Request.Url.AbsolutePath))))
                {
                    body = File.ReadAllBytes(Path.Combine(Folder, Path.GetFileName(ctx.Request.Url.AbsolutePath)));
                }
                else
                {
                    ctx.Response.StatusCode = 404;
                    body = [];
                }
                await ctx.Response.OutputStream.WriteAsync(body);
                ctx.Response.Close();
            }
        }

        /// <summary>Packs a tiny plugin into the served folder; returns its catalog entry as JSON.</summary>
        public string Publish(string id, string version, string requires = "[]", string platforms = "[]", string? sha256Override = null)
        {
            var file = Path.Combine(Folder, $"{id}-{version}.zip");
            File.Delete(file);
            using (var zip = ZipFile.Open(file, ZipArchiveMode.Create))
            {
                using (var w = new StreamWriter(zip.CreateEntry("plugin.json").Open()))
                {
                    w.Write($$"""{ "id": "{{id}}", "name": "{{id}}", "version": "{{version}}", "engines": { "island": "^2.0.0" }, "requires": {{requires}}, "platforms": {{platforms}} }""");
                }
                using (var w = new StreamWriter(zip.CreateEntry("main.js").Open()))
                {
                    w.Write($"export function activate() {{ console.info('{id} {version} running'); }}");
                }
            }
            var sha = sha256Override ?? Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file))).ToLowerInvariant();
            return $$"""{ "id": "{{id}}", "name": "{{id}}", "version": "{{version}}", "requires": {{requires}}, "platforms": {{platforms}}, "size": {{new FileInfo(file).Length}}, "sha256": "{{sha}}", "download": "{{Url}}/plugins/{{id}}-{{version}}.zip" }""";
        }

        public static string Catalog(params string[] entries) => "{ \"plugins\": [ " + string.Join(", ", entries) + " ] }";

        public void Dispose()
        {
            _listener.Close();
            Directory.Delete(Folder, recursive: true);
        }
    }

    private CatalogInstaller CatalogFor(FakeCatalogService service)
    {
        Environment.SetEnvironmentVariable("O_O_SERVICE_URL", service.Url);
        return new CatalogInstaller(_host!, Path.Combine(_data, "user-plugins"), new HttpClient(), _data);
    }

    [Fact]
    public async Task Catalog_plugins_install_with_what_they_require_and_updates_are_offered()
    {
        using var service = new FakeCatalogService();
        var other = PluginManifest.CurrentPlatform == "macos" ? "windows" : "macos";
        service.CatalogJson = FakeCatalogService.Catalog(
            service.Publish("com.example.calendar", "0.1.0"),
            service.Publish("com.example.cal.ics", "0.1.0", requires: "[\"com.example.calendar\"]"),
            service.Publish("com.example.elsewhere", "0.1.0", platforms: $"[\"{other}\"]"));
        StartDiscovered();
        try
        {
            var installer = CatalogFor(service);
            var catalog = await installer.FetchAsync(CancellationToken.None);
            Assert.DoesNotContain(catalog.Plugins, p => p.Id == "com.example.elsewhere");      // other systems' plugins left out
            Assert.Contains(service.Requests, r => r == $"/api/plugins?platform={ServiceEndpoints.OsPlatform}");

            var ics = catalog.Plugins.Single(p => p.Id == "com.example.cal.ics");
            var installed = await installer.InstallAsync(ics, catalog, CancellationToken.None);
            Assert.Equal(["com.example.calendar", "com.example.cal.ics"], installed.Select(p => p.Id));   // requirement first
            Assert.True(installer.IsFromCatalog("com.example.cal.ics"));
            Assert.All(installed, p => Assert.False(p.Bundled));                                       // they ask for permissions
            Assert.Empty(installer.Updates(catalog));

            // A newer version in the catalog is offered as an update, and installs in place.
            service.CatalogJson = FakeCatalogService.Catalog(
                service.Publish("com.example.calendar", "0.1.0"),
                service.Publish("com.example.cal.ics", "0.2.0", requires: "[\"com.example.calendar\"]"));
            catalog = await installer.FetchAsync(CancellationToken.None);
            var update = Assert.Single(installer.Updates(catalog));
            Assert.Equal("0.2.0", update.Version);
            Assert.Equal(["com.example.cal.ics"], (await installer.InstallAsync(update, catalog, CancellationToken.None)).Select(p => p.Id));
            WaitFor(() => _logs.FirstOrDefault(l => l.Message == "com.example.cal.ics 0.2.0 running"), "updated plugin runs");
        }
        finally
        {
            Environment.SetEnvironmentVariable("O_O_SERVICE_URL", null);
        }
    }

    [Fact]
    public async Task Catalog_plugins_written_for_the_old_island_api_are_replaced_by_their_catalog_version_at_start()
    {
        using var service = new FakeCatalogService();
        service.CatalogJson = FakeCatalogService.Catalog(service.Publish("com.example.calendar", "0.2.0"), service.Publish("com.example.off", "0.2.0"));
        // Installed from the catalog before island API 2: still on disk, no longer loaded.
        var user = Path.Combine(_data, "user-plugins");
        foreach (var id in new[] { "com.example.calendar", "com.example.off", "com.example.gone" })
        {
            if (id != "com.example.gone")
            {
                Directory.CreateDirectory(Path.Combine(user, id));
                File.WriteAllText(Path.Combine(user, id, "plugin.json"), $$"""{ "id": "{{id}}", "name": "old", "version": "0.1.0", "main": "main.js" }""");
                File.WriteAllText(Path.Combine(user, id, "main.js"), "export function activate() {}");
            }
        }
        File.WriteAllText(Path.Combine(_data, "catalog-installs.json"),
            """{ "com.example.calendar": "0.1.0", "com.example.off": "0.1.0", "com.example.gone": "0.1.0" }""");
        File.WriteAllText(Path.Combine(_data, "enabled.json"), """{ "com.example.off": false }""");
        StartDiscovered();
        Assert.Contains(_host!.Discover(user), p => p.Contains("needs the island API ^1.0.0"));
        try
        {
            var installer = CatalogFor(service);
            var upgraded = await installer.UpgradeStrandedAsync(CancellationToken.None);
            Assert.Equal(["com.example.calendar", "com.example.off"], upgraded.Order());             // not the one deleted by hand
            Assert.Equal("0.2.0", _host.Packages.Single(p => p.Id == "com.example.calendar").Manifest.Version);
            Assert.True(_host.IsEnabled("com.example.calendar"));
            Assert.False(_host.IsEnabled("com.example.off"));                                         // off as it was
            Assert.Empty(await installer.UpgradeStrandedAsync(CancellationToken.None));               // nothing left to do
        }
        finally
        {
            Environment.SetEnvironmentVariable("O_O_SERVICE_URL", null);
        }
    }

    [Fact]
    public async Task A_package_that_does_not_match_its_checksum_is_refused()
    {
        using var service = new FakeCatalogService();
        service.CatalogJson = FakeCatalogService.Catalog(service.Publish("com.example.tampered", "0.1.0", sha256Override: new string('0', 64)));
        StartDiscovered();
        try
        {
            var installer = CatalogFor(service);
            var catalog = await installer.FetchAsync(CancellationToken.None);
            var error = await Assert.ThrowsAsync<PluginCapabilityException>(() =>
                installer.InstallAsync(catalog.Plugins.Single(), catalog, CancellationToken.None));
            Assert.Contains("校验不一致", error.Message);
            Assert.DoesNotContain(_host!.Packages, p => p.Id == "com.example.tampered");
        }
        finally
        {
            Environment.SetEnvironmentVariable("O_O_SERVICE_URL", null);
        }
    }

    [Fact]
    public void Catalog_plugins_are_not_shipped_and_bundled_ones_are_valid()
    {
        var shipped = Directory.GetDirectories(Path.Combine(RepoRoot(), "plugins")).Select(Path.GetFileName).ToHashSet();
        var catalog = Directory.GetDirectories(Path.Combine(RepoRoot(), "catalog", "plugins")).Select(Path.GetFileName).ToList();
        Assert.NotEmpty(catalog);
        Assert.Empty(catalog.Where(shipped.Contains));
        foreach (var id in new[] { "com.o_o.ai-usage", "com.o_o.ai.claude", "com.o_o.ai.codex", "com.o_o.clipboard", "com.o_o.shelf",
                     "com.o_o.todo", "com.o_o.focus", "com.o_o.weather", "com.o_o.monitor", "com.o_o.theme.default" })
        {
            Assert.Contains(id, shipped);
        }
        // Only plugins/ goes into the app's output (the app project copies ..\..\plugins\**).
        var project = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Island.App", "Island.App.csproj"));
        Assert.DoesNotContain("catalog", project, StringComparison.OrdinalIgnoreCase);
        // Every catalog plugin packs (as the service will serve it).
        foreach (var dir in Directory.GetDirectories(Path.Combine(RepoRoot(), "catalog", "plugins")))
        {
            Assert.Empty(PluginPackages.Validate(dir, out _));
        }
    }

    [Fact]
    public void Manifests_check_requires_and_platforms_and_other_systems_plugins_are_skipped()
    {
        WritePluginWithManifest("com.example.badtargets", """, "requires": ["nope"], "platforms": ["amiga"]""", "", "export function activate() {}");
        var other = PluginManifest.CurrentPlatform == "linux" ? "windows" : "linux";
        WritePluginWithManifest("com.example.elsewhere", $$""", "platforms": ["{{other}}"]""", "", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problem = Assert.Single(_host.Discover(_root, bundled: true));
        Assert.Contains("unknown platform 'amiga'", problem);
        Assert.Contains("requires 'nope' is not a plugin id", problem);
        Assert.DoesNotContain(_host.Packages, p => p.Id == "com.example.elsewhere");   // not offered here, not an error
    }
}
