using System.Text.Json;
using System.Text.Json.Serialization;

namespace Island.Plugins;

/// <summary><c>plugin.json</c> (docs/design/03-plugin-system.md §3). Only the fields M0 uses are modelled.</summary>
public sealed class PluginManifest
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }

    // The source generator assigns every init property of a type with required members, null for fields missing
    // from the JSON, which would overwrite initialiser defaults; the getters supply the defaults instead.

    /// <summary>The ES module entry point, relative to the plugin folder.</summary>
    public string Main { get => field ?? "main.js"; init; }

    /// <summary>One sentence for the console's plugin card.</summary>
    public string? Description { get; init; }

    /// <summary>A Lucide icon name for the console's plugin card, e.g. <c>Clipboard</c>.</summary>
    public string? Icon { get; init; }

    /// <summary>
    /// Bundled plugins only: off until the user adds it in the console ("添加插件"). User plugins are on once installed
    /// (they still wait for the user's permission decision).
    /// </summary>
    public bool Optional { get; init; }

    public PluginEngines Engines { get => field ??= new(); init; }
    public IReadOnlyList<string> Permissions { get => field ?? []; init; }
    public PluginContributions Contributes { get => field ??= new(); init; }

    public static PluginManifest Parse(string json) =>
        JsonSerializer.Deserialize(json, PluginJsonContext.Default.PluginManifest)
        ?? throw new InvalidDataException("plugin.json is empty.");
}

public sealed class PluginEngines
{
    /// <summary>The <c>@island/api</c> version range the plugin needs, e.g. <c>^1.0.0</c>.</summary>
    public string Island { get => field ?? "^1.0.0"; init; }
}

public sealed class PluginContributions
{
    public IReadOnlyList<ActivityContribution> Activities { get => field ?? []; init; }

    /// <summary>Full views shown in the expanded island, one tab each (docs/design/07-home-and-layout.md §2).</summary>
    public IReadOnlyList<PageContribution> Pages { get => field ?? []; init; }

    /// <summary>Small items for the collapsed island (docs/design/07-home-and-layout.md §4).</summary>
    public IReadOnlyList<ChipContribution> Chips { get => field ?? []; init; }

    /// <summary>Implementations this plugin offers to other plugins (docs/design/03-plugin-system.md §7.1).</summary>
    public IReadOnlyList<ProviderContribution> Providers { get => field ?? []; init; }

    /// <summary>A JSON Schema subset describing the plugin's settings; see <see cref="SettingsSchema"/>.</summary>
    public JsonElement? Settings { get; init; }
}

/// <summary>
/// An implementation of a named interface that other plugins call through the host, e.g. interface "ai-usage", id
/// "codex". Calls run in the providing plugin's own engine, with its own permissions; only data crosses over.
/// </summary>
public sealed class ProviderContribution
{
    public required string Interface { get; init; }
    public required string Id { get; init; }
    public string? Title { get; init; }
}

/// <summary>A page: a plugin's full view in the expanded island, rendered with <c>island.page(id).update(view)</c>.</summary>
public sealed class PageContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>A Lucide icon name, for the console's page list.</summary>
    public string? Icon { get; init; }

    /// <summary>Whether the page shows until the user decides otherwise (e.g. the clock's 今日 page does not).</summary>
    public bool? Enabled { get; init; }
}

/// <summary>A chip: a small item on the collapsed island, rendered with <c>island.chip(id[, instance]).update(view)</c>.</summary>
public sealed class ChipContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>One chip per instance (e.g. one per AI account): <c>island.chip(id, instance)</c>.</summary>
    public bool Multiple { get; init; }

    /// <summary>Whether the chip shows until the user decides otherwise.</summary>
    public bool? Enabled { get; init; }

    /// <summary>"left" or "right" (default): which side of the island, or which wing beside a notch.</summary>
    public string? Side { get; init; }
}

public sealed class ActivityContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip,
    AllowTrailingCommas = true)]
[JsonSerializable(typeof(PluginManifest))]
[JsonSerializable(typeof(string))]
internal sealed partial class PluginJsonContext : JsonSerializerContext
{
}
