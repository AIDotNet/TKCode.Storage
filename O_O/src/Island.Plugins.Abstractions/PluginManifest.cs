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

    /// <summary>Plugins this one needs to be useful (e.g. a calendar source needs the calendar page); installed first.</summary>
    public IReadOnlyList<string> Requires { get => field ?? []; init; }

    /// <summary>Where it works: <c>macos</c>, <c>windows</c>, <c>linux</c>; empty means everywhere.</summary>
    public IReadOnlyList<string> Platforms { get => field ?? []; init; }

    /// <summary>This machine, in the words of <see cref="Platforms"/>.</summary>
    public static string CurrentPlatform =>
        OperatingSystem.IsMacOS() ? "macos" : OperatingSystem.IsWindows() ? "windows" : "linux";

    public static readonly IReadOnlySet<string> KnownPlatforms = new HashSet<string>(StringComparer.Ordinal) { "macos", "windows", "linux" };

    public bool RunsOn(string platform) => Platforms.Count == 0 || Platforms.Contains(platform, StringComparer.Ordinal);

    /// <summary>Checks <see cref="Requires"/> and <see cref="Platforms"/>.</summary>
    public void ValidateTargets(List<string> problems)
    {
        foreach (var platform in Platforms.Where(p => !KnownPlatforms.Contains(p)))
        {
            problems.Add($"unknown platform '{platform}' (known: macos, windows, linux)");
        }
        foreach (var id in Requires)
        {
            if (id == Id || id.Length is < 3 or > 100 || !id.Contains('.') ||
                !id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_'))
            {
                problems.Add($"requires '{id}' is not a plugin id");
            }
        }
    }

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

    /// <summary>Looks for every O_O window (docs/design/03-plugin-system.md §9), chosen on the console's 外观 page.</summary>
    public IReadOnlyList<ThemeContribution> Themes { get => field ?? []; init; }

    /// <summary>Island layouts the user can apply on the console's 岛体 page: shape, dock, size, pages and chips in one go.</summary>
    public IReadOnlyList<LayoutContribution> Layouts { get => field ?? []; init; }

    /// <summary>Small islands beside the main one for short-lived notices (docs/design/08-notifications-and-satellites.md §4).</summary>
    public IReadOnlyList<SatelliteContribution> Satellites { get => field ?? []; init; }

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

/// <summary>A satellite: a small island beside the main one, shown with <c>island.satellite(id).show(view, options)</c>.</summary>
public sealed class SatelliteContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>"auto" (default: the side with room, right first), "left" or "right"; the user can pin it in the console.</summary>
    public string? Side { get; init; }
}

/// <summary>
/// A theme (docs 03 §9): an accent seed, optionally an appearance and a corner scale, applied through Arc's own levers
/// so every Arc control follows. The island itself stays black (it is the notch's colour).
/// </summary>
public sealed class ThemeContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>One line for the console.</summary>
    public string? Description { get; init; }

    /// <summary>The accent seed, <c>#RRGGBB</c>; Arc derives the whole accent family (and its dark variant) from it.</summary>
    public string? Accent { get; init; }

    /// <summary><c>system</c>, <c>light</c> or <c>dark</c>; absent: the theme leaves the user's choice alone.</summary>
    public string? Appearance { get; init; }

    /// <summary>Arc's corner preset scaled (0.2–2; 0.6 is the macOS look); absent: 0.6.</summary>
    public double? Corners { get; init; }

    /// <summary>
    /// The island's own colours, by token key → <c>#RRGGBB</c> (or <c>#AARRGGBB</c>). Only the keys in
    /// <see cref="TokenKeys"/>; the notch's black surface cannot be overridden.
    /// </summary>
    public Dictionary<string, string>? Tokens { get; init; }

    /// <summary>The island colour tokens a theme may override.</summary>
    public static readonly IReadOnlySet<string> TokenKeys = new HashSet<string>(StringComparer.Ordinal)
    {
        "IslandForegroundBrush", "IslandSecondaryBrush", "IslandDividerBrush", "IslandCardBrush", "IslandCardHoverBrush",
    };

    /// <summary>Lists what is wrong with the declared themes (unknown appearance, bad colour, duplicate id…).</summary>
    public static void Validate(IReadOnlyList<ThemeContribution> themes, List<string> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in themes)
        {
            if (string.IsNullOrWhiteSpace(t.Id) || !ids.Add(t.Id))
            {
                problems.Add($"theme '{t.Id}': id missing or used twice");
            }
            if (t.Accent is not null && !IsHexColor(t.Accent))
            {
                problems.Add($"theme '{t.Id}': accent must be #RRGGBB");
            }
            if (t.Appearance is not null and not ("system" or "light" or "dark"))
            {
                problems.Add($"theme '{t.Id}': appearance must be system, light or dark");
            }
            if (t.Corners is { } c && (double.IsNaN(c) || c < 0.2 || c > 2))
            {
                problems.Add($"theme '{t.Id}': corners must be between 0.2 and 2");
            }
            foreach (var (key, value) in t.Tokens ?? [])
            {
                if (!TokenKeys.Contains(key))
                {
                    problems.Add($"theme '{t.Id}': token '{key}' cannot be overridden (allowed: {string.Join(", ", TokenKeys.Order(StringComparer.Ordinal))})");
                }
                else if (!IsHexColor(value))
                {
                    problems.Add($"theme '{t.Id}': token '{key}' must be #RRGGBB");
                }
            }
        }
    }

    private static bool IsHexColor(string s) =>
        s.Length is 7 or 9 && s[0] == '#' && s.Skip(1).All(Uri.IsHexDigit);
}

/// <summary>
/// A layout preset for the island (docs/design/07-home-and-layout.md): what the user gets when applying it. Everything is
/// optional; what is absent is left as the user has it. Applying never hides the pages or chips the preset does not
/// mention unless <see cref="Exclusive"/> is set.
/// </summary>
public sealed class LayoutContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }
    public string? Description { get; init; }

    /// <summary><c>capsule</c> or <c>notch</c>.</summary>
    public string? Shape { get; init; }

    /// <summary><c>top</c>, <c>left</c> or <c>right</c>.</summary>
    public string? Dock { get; init; }

    /// <summary>0.8–1.4.</summary>
    public double? Scale { get; init; }

    /// <summary>极简模式: collapsed, only the time and alerts.</summary>
    public bool? Minimal { get; init; }

    /// <summary>Pages in this order, first ones leading, by key <c>plugin/page</c>; all of them are switched on.</summary>
    public IReadOnlyList<string> Pages { get => field ?? []; init; }

    /// <summary>The page the island opens on when nothing is going on (<c>plugin/page</c>).</summary>
    public string? Home { get; init; }

    /// <summary>Chips in this order, on the given side, by key <c>plugin/chip</c>; all of them are switched on.</summary>
    public IReadOnlyList<LayoutChip> Chips { get => field ?? []; init; }

    /// <summary>Switch off the pages and chips this layout does not list.</summary>
    public bool Exclusive { get; init; }

    public static void Validate(IReadOnlyList<LayoutContribution> layouts, List<string> problems)
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var l in layouts)
        {
            if (string.IsNullOrWhiteSpace(l.Id) || !ids.Add(l.Id))
            {
                problems.Add($"layout '{l.Id}': id missing or used twice");
            }
            if (l.Shape is not null and not ("capsule" or "notch"))
            {
                problems.Add($"layout '{l.Id}': shape must be capsule or notch");
            }
            if (l.Dock is not null and not ("top" or "left" or "right"))
            {
                problems.Add($"layout '{l.Id}': dock must be top, left or right");
            }
            if (l.Scale is { } s && (double.IsNaN(s) || s < 0.8 || s > 1.4))
            {
                problems.Add($"layout '{l.Id}': scale must be between 0.8 and 1.4");
            }
            if (l.Pages.Any(k => !k.Contains('/', StringComparison.Ordinal)) || l.Home is { } h && !h.Contains('/', StringComparison.Ordinal))
            {
                problems.Add($"layout '{l.Id}': pages are written plugin/page");
            }
            foreach (var chip in l.Chips)
            {
                if (chip.Key is null || !chip.Key.Contains('/', StringComparison.Ordinal))
                {
                    problems.Add($"layout '{l.Id}': chips are written plugin/chip");
                }
                if (chip.Side is not null and not ("left" or "right"))
                {
                    problems.Add($"layout '{l.Id}': chip side must be left or right");
                }
            }
        }
    }
}

public sealed class LayoutChip
{
    public required string Key { get; init; }

    /// <summary><c>left</c> or <c>right</c>; absent: the chip's own default.</summary>
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
