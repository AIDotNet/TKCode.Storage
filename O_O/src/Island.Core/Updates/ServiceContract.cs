using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.Core.Updates;

/// <summary><c>GET /api/version?platform=…&amp;current=…</c> (docs/design/08-updates-and-catalog.md).</summary>
public sealed class VersionInfo
{
    public string Version { get; init; } = "";
    public DateTimeOffset? PublishedAt { get; init; }
    public string? Notes { get; init; }
    public Dictionary<string, string> Downloads { get; init; } = [];

    /// <summary>The download for the platform asked about, or null when there is none.</summary>
    public string? Download { get; init; }

    public bool UpdateAvailable { get; init; }

    /// <summary>Whether this is newer than <paramref name="current"/> — checked here as well, not only taken from the service.</summary>
    public bool IsNewerThan(AppVersion current) => AppVersion.TryParse(Version, out var latest) && latest > current;
}

/// <summary><c>GET /api/plugins?platform=…</c>.</summary>
public sealed class PluginCatalog
{
    public List<CatalogPlugin> Plugins { get; init; } = [];
}

/// <summary>A plugin offered by the O_O plugin catalog.</summary>
public sealed class CatalogPlugin
{
    public string Id { get; init; } = "";
    public string Name { get; init; } = "";
    public string Version { get; init; } = "";
    public string? Description { get; init; }
    public string? Icon { get; init; }
    public List<string> Permissions { get; init; } = [];
    public List<string> Requires { get; init; } = [];

    /// <summary>macos, windows, linux; empty means everywhere.</summary>
    public List<string> Platforms { get; init; } = [];

    public long Size { get; init; }
    public string Sha256 { get; init; } = "";
    public string Download { get; init; } = "";

    public bool RunsOn(string platform) => Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.Ordinal);
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, PropertyNameCaseInsensitive = true)]
[JsonSerializable(typeof(VersionInfo))]
[JsonSerializable(typeof(PluginCatalog))]
public sealed partial class ServiceJsonContext : JsonSerializerContext;

public static class ServiceContract
{
    /// <exception cref="JsonException">Not the expected JSON.</exception>
    public static VersionInfo ParseVersion(string json) =>
        JsonSerializer.Deserialize(json, ServiceJsonContext.Default.VersionInfo) ?? throw new JsonException("empty");

    /// <exception cref="JsonException">Not the expected JSON.</exception>
    public static PluginCatalog ParseCatalog(string json) =>
        JsonSerializer.Deserialize(json, ServiceJsonContext.Default.PluginCatalog) ?? throw new JsonException("empty");
}
