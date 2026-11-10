using System.Security.Cryptography;
using System.Text.Json;
using Island.Core.Updates;

namespace Island.Plugins.Host.Services;

/// <summary>
/// Installs plugins from the O_O plugin catalog (docs/design/08-updates-and-catalog.md): fetches the list for this
/// system, downloads a package (https only; plain http only from a development service on this machine), checks its
/// size and SHA-256 against the catalog, installs what it requires first, and remembers which installed plugins came
/// from the catalog so their updates can be offered. Catalog plugins are installed like user plugins: they ask for
/// their permissions before they run.
/// </summary>
public sealed class CatalogInstaller(PluginHost host, string userDirectory, HttpClient http, string dataDirectory)
{
    public const long MaxPackageBytes = PluginPackages.MaxBytes;

    private readonly string _originsPath = Path.Combine(dataDirectory, "catalog-installs.json");
    private readonly Lock _gate = new();

    /// <summary>The catalog for this system (plugins for other systems left out).</summary>
    /// <exception cref="PluginCapabilityException">The service could not be reached or answered something else.</exception>
    public async Task<PluginCatalog> FetchAsync(CancellationToken ct)
    {
        var platform = ServiceEndpoints.OsPlatform;
        var json = await GetStringAsync(ServiceEndpoints.Plugins(platform), ct);
        PluginCatalog catalog;
        try
        {
            catalog = ServiceContract.ParseCatalog(json);
        }
        catch (JsonException)
        {
            throw new PluginCapabilityException("插件目录返回的内容无法识别。");
        }
        return new PluginCatalog { Plugins = [.. catalog.Plugins.Where(p => p.RunsOn(platform) && PluginPackages.IsValidId(p.Id))] };
    }

    /// <summary>Whether <paramref name="pluginId"/> was installed from the catalog.</summary>
    public bool IsFromCatalog(string pluginId)
    {
        lock (_gate)
        {
            return LoadOrigins().ContainsKey(pluginId);
        }
    }

    /// <summary>Installed catalog plugins that the catalog has a newer version of.</summary>
    public IReadOnlyList<CatalogPlugin> Updates(PluginCatalog catalog) =>
        [.. catalog.Plugins.Where(p =>
            IsFromCatalog(p.Id) &&
            host.Packages.FirstOrDefault(x => x.Id == p.Id) is { } installed &&
            AppVersion.TryParse(p.Version, out var offered) && AppVersion.TryParse(installed.Manifest.Version, out var have) &&
            offered > have)];

    /// <summary>
    /// Catalog plugins installed here that this O_O no longer loads — written for an older island API
    /// (docs/design/09-island-v2.md) — are replaced by their catalog version, switched on or off as before. Called at
    /// start; a plugin the catalog does not offer, or one whose folder the user deleted, is left alone.
    /// </summary>
    /// <returns>The ids updated.</returns>
    /// <exception cref="PluginCapabilityException">The catalog could not be read (try again at the next start).</exception>
    public async Task<IReadOnlyList<string>> UpgradeStrandedAsync(CancellationToken ct)
    {
        string[] stranded;
        lock (_gate)
        {
            stranded = [.. LoadOrigins().Keys.Where(id =>
                host.Packages.All(p => p.Id != id) && Directory.Exists(Path.Combine(userDirectory, id)))];
        }
        if (stranded.Length == 0)
        {
            return [];
        }
        var catalog = await FetchAsync(ct);
        var upgraded = new List<string>();
        foreach (var id in stranded)
        {
            if (catalog.Plugins.FirstOrDefault(p => p.Id == id) is not { } offered)
            {
                continue;
            }
            var choice = host.HasChoice(id) ? host.IsEnabled(id) : (bool?)null;
            try
            {
                await InstallAsync(offered, catalog, ct);
            }
            catch (PluginCapabilityException)
            {
                continue; // tried again at the next start
            }
            if (choice == false)
            {
                host.SetEnabled(id, false); // installing switches it on; it was off
            }
            upgraded.Add(id);
        }
        return upgraded;
    }

    /// <summary>
    /// Installs <paramref name="plugin"/> (or updates it), after what it requires that is not installed yet.
    /// </summary>
    /// <returns>The packages installed, requirements first.</returns>
    /// <exception cref="PluginCapabilityException">Download, check or install failed; the message is for the user.</exception>
    public async Task<IReadOnlyList<PluginPackage>> InstallAsync(CatalogPlugin plugin, PluginCatalog catalog, CancellationToken ct)
    {
        var order = new List<CatalogPlugin>();
        Resolve(plugin, catalog, order, [], top: true);
        var installed = new List<PluginPackage>();
        foreach (var item in order)
        {
            installed.Add(await InstallOneAsync(item, ct));
        }
        return installed;
    }

    /// <summary>Requirements first (depth first), skipping those already installed; refuses cycles and gaps.</summary>
    private void Resolve(CatalogPlugin plugin, PluginCatalog catalog, List<CatalogPlugin> order, HashSet<string> visiting, bool top)
    {
        if (!visiting.Add(plugin.Id))
        {
            throw new PluginCapabilityException($"插件目录里 {plugin.Name} 的依赖有循环。");
        }
        foreach (var id in plugin.Requires)
        {
            if (host.Packages.Any(p => p.Id == id) || order.Any(p => p.Id == id))
            {
                continue; // already here (bundled, installed, or earlier in this install)
            }
            var required = catalog.Plugins.FirstOrDefault(p => p.Id == id)
                ?? throw new PluginCapabilityException($"{plugin.Name} 需要的插件 {id} 不在插件目录里。");
            Resolve(required, catalog, order, visiting, top: false);
        }
        visiting.Remove(plugin.Id);
        if (top || host.Packages.All(p => p.Id != plugin.Id))
        {
            order.Add(plugin);
        }
    }

    private async Task<PluginPackage> InstallOneAsync(CatalogPlugin plugin, CancellationToken ct)
    {
        if (!Uri.TryCreate(plugin.Download, UriKind.Absolute, out var url) || !ServiceEndpoints.IsAllowed(url))
        {
            throw new PluginCapabilityException($"{plugin.Name} 的下载地址不安全或无效。");
        }
        if (plugin.Size > MaxPackageBytes)
        {
            throw new PluginCapabilityException($"{plugin.Name} 太大（超过 {MaxPackageBytes / 1024 / 1024} MB）。");
        }
        var file = Path.Combine(Path.GetTempPath(), $"o_o-catalog-{Guid.NewGuid():N}.zip");
        try
        {
            using (var response = await Send(url, ct))
            {
                if (response.Content.Headers.ContentLength > MaxPackageBytes)
                {
                    throw new PluginCapabilityException($"{plugin.Name} 太大（超过 {MaxPackageBytes / 1024 / 1024} MB）。");
                }
                await using var source = await response.Content.ReadAsStreamAsync(ct);
                await using var target = File.Create(file);
                var buffer = new byte[81920];
                long total = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct)) > 0)
                {
                    total += read;
                    if (total > MaxPackageBytes)
                    {
                        throw new PluginCapabilityException($"{plugin.Name} 太大（超过 {MaxPackageBytes / 1024 / 1024} MB）。");
                    }
                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }
            string actual;
            await using (var check = File.OpenRead(file))
            {
                actual = Convert.ToHexString(await SHA256.HashDataAsync(check, ct)).ToLowerInvariant();
            }
            if (!string.Equals(actual, plugin.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
            {
                throw new PluginCapabilityException($"{plugin.Name} 的安装包校验不一致（可能下载不完整或被篡改），没有安装。");
            }
            PluginPackage package;
            try
            {
                package = await Task.Run(() => host.Install(file, userDirectory), ct);
            }
            catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
            {
                throw new PluginCapabilityException($"{plugin.Name} 安装失败：{e.Message}");
            }
            if (package.Id != plugin.Id)
            {
                throw new PluginCapabilityException($"插件目录里的 {plugin.Id} 实际是 {package.Id}。");
            }
            RecordOrigin(plugin.Id, package.Manifest.Version);
            return package;
        }
        finally
        {
            File.Delete(file);
        }
    }

    private async Task<string> GetStringAsync(Uri url, CancellationToken ct)
    {
        using var response = await Send(url, ct);
        return await response.Content.ReadAsStringAsync(ct);
    }

    private async Task<HttpResponseMessage> Send(Uri url, CancellationToken ct)
    {
        if (!ServiceEndpoints.IsAllowed(url))
        {
            throw new PluginCapabilityException("插件目录地址必须是 https。");
        }
        HttpResponseMessage response;
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PluginCapabilityException("无法连接 O_O 插件目录。");
        }
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            response.Dispose();
            throw new PluginCapabilityException($"插件目录返回 {status}。");
        }
        return response;
    }

    private Dictionary<string, string> LoadOrigins()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_originsPath));
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString()!, StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void RecordOrigin(string id, string version)
    {
        lock (_gate)
        {
            var origins = LoadOrigins();
            origins[id] = version;
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (key, value) in origins.OrderBy(o => o.Key, StringComparer.Ordinal))
                {
                    w.WriteString(key, value);
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_originsPath)!);
            File.WriteAllBytes(_originsPath, buffer.ToArray());
        }
    }
}
