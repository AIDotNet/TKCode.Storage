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

    /// <summary>Checks the island API asked for, <see cref="Requires"/> and <see cref="Platforms"/>.</summary>
    public void ValidateTargets(List<string> problems)
    {
        if (Engines.ApiMajor < 2)
        {
            problems.Add($"needs the island API {Engines.Island}; this O_O runs plugins written for ^2.0.0 (docs/design/09-island-v2.md)");
        }
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

    /// <summary>The major version of the island API asked for: 2 for <c>^2.0.0</c> (docs/design/09-island-v2.md §3), else 1.</summary>
    public int ApiMajor => Island.TrimStart('^', '~', '>', '=', ' ').Split('.')[0] is var major && int.TryParse(major, out var m) && m >= 2 ? 2 : 1;
}

public sealed class PluginContributions
{
    public IReadOnlyList<ActivityContribution> Activities { get => field ?? []; init; }

    /// <summary>Items beside the idle island (docs/design/09-island-v2.md §3).</summary>
    public IReadOnlyList<AmbientContribution> Ambient { get => field ?? []; init; }

    /// <summary>Pages of the home dashboard (09 §2), one tab each.</summary>
    public IReadOnlyList<PageContribution> Home { get => field ?? []; init; }

    /// <summary>Looks for every O_O window (docs/design/03-plugin-system.md §9), chosen on the console's 外观 page.</summary>
    public IReadOnlyList<ThemeContribution> Themes { get => field ?? []; init; }

    /// <summary>Island layouts the user can apply on the console's 岛体 page: shape, dock, size, pages and chips in one go.</summary>
    public IReadOnlyList<LayoutContribution> Layouts { get => field ?? []; init; }

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

/// <summary>A home page: a plugin's full view in the home dashboard, rendered with <c>island.home.page(id).update(view)</c>.</summary>
public sealed class PageContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>A Lucide icon name, for the console's page list.</summary>
    public string? Icon { get; init; }

    /// <summary>Whether the page shows until the user decides otherwise (e.g. the clock's 今日 page does not).</summary>
    public bool? Enabled { get; init; }
}

/// <summary>
/// The island layer of a theme (docs/design/09-island-v2.md §4). Declarative: the host draws it, so every plugin follows,
/// and the host keeps the limits (around a real notch the surface stays black; reduced motion wins over the spring).
/// </summary>
public sealed class IslandThemeSpec
{
    public IslandSurfaceSpec? Surface { get; init; }
    public IslandShapeSpec? Shape { get; init; }
    public IslandContentSpec? Content { get; init; }
    public IslandMotionSpec? Motion { get; init; }
    public IslandMascotSpec? Mascot { get; init; }
    public IslandSoundsSpec? Sounds { get; init; }
    public IslandLayoutSpec? Layout { get; init; }

    public static readonly IReadOnlySet<string> Materials = new HashSet<string>(StringComparer.Ordinal) { "solid", "gradient", "translucent" };
    public static readonly IReadOnlySet<string> Springs = new HashSet<string>(StringComparer.Ordinal) { "calm", "default", "snappy", "bouncy" };
    public static readonly IReadOnlySet<string> Splits = new HashSet<string>(StringComparer.Ordinal) { "slide", "droplet" };
    public static readonly IReadOnlySet<string> MascotKinds = new HashSet<string>(StringComparer.Ordinal) { "classic", "pixel", "ghost", "cube" };
    public static readonly IReadOnlySet<string> ExpandedLayouts = new HashSet<string>(StringComparer.Ordinal) { "standard", "stacked", "media" };
    public static readonly IReadOnlySet<string> SoundTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".wav", ".mp3", ".aiff", ".aif" };

    /// <summary>The largest sound file a theme may ship.</summary>
    public const long MaxSoundBytes = 1024 * 1024;

    /// <summary>Accent names plugins use for activities; a theme may colour any of them (or others).</summary>
    public static readonly IReadOnlyList<string> AccentNames = ["music", "timer", "calendar", "message", "reminder", "warning"];

    public void Validate(string where, List<string> problems)
    {
        if (Surface is { } surface)
        {
            if (surface.Material is { } m && !Materials.Contains(m))
            {
                problems.Add($"{where}: island.surface.material must be solid, gradient or translucent");
            }
            if (surface.Color is { } c && !ThemeContribution.IsHexColor(c))
            {
                problems.Add($"{where}: island.surface.color must be #RRGGBB");
            }
            if (surface.Colors is { } colors && (colors.Count is < 2 or > 4 || colors.Any(x => !ThemeContribution.IsHexColor(x))))
            {
                problems.Add($"{where}: island.surface.colors must be 2 to 4 colours #RRGGBB");
            }
            if (surface.Material == "gradient" && surface.Colors is null)
            {
                problems.Add($"{where}: a gradient surface needs island.surface.colors");
            }
            if (surface.Opacity is { } o && (double.IsNaN(o) || o < 0.5 || o > 1))
            {
                problems.Add($"{where}: island.surface.opacity must be between 0.5 and 1");
            }
        }
        if (Shape?.Radius is { } r && (double.IsNaN(r) || r < 0.5 || r > 1.5))
        {
            problems.Add($"{where}: island.shape.radius must be between 0.5 and 1.5");
        }
        foreach (var (name, color) in Content?.Accents ?? [])
        {
            if (!ThemeContribution.IsHexColor(color))
            {
                problems.Add($"{where}: island.content.accents.{name} must be #RRGGBB");
            }
        }
        if (Motion?.Spring is { } spring && !Springs.Contains(spring))
        {
            problems.Add($"{where}: island.motion.spring must be calm, default, snappy or bouncy");
        }
        if (Motion?.Split is { } split && !Splits.Contains(split))
        {
            problems.Add($"{where}: island.motion.split must be slide or droplet");
        }
        if (Mascot?.Kind is { } kind && !MascotKinds.Contains(kind))
        {
            problems.Add($"{where}: island.mascot.kind must be classic, pixel, ghost or cube");
        }
        if (Mascot?.Color is { } mascotColor && !ThemeContribution.IsHexColor(mascotColor))
        {
            problems.Add($"{where}: island.mascot.color must be #RRGGBB");
        }
        if (Layout?.Expanded is { } layout && !ExpandedLayouts.Contains(layout))
        {
            problems.Add($"{where}: island.layout.expanded must be standard, stacked or media");
        }
        if (Sounds?.Alert is { } alert && (alert.Contains("..", StringComparison.Ordinal) || Path.IsPathRooted(alert) || !SoundTypes.Contains(Path.GetExtension(alert))))
        {
            problems.Add($"{where}: island.sounds.alert must be a .wav, .mp3 or .aiff file inside the plugin folder");
        }
    }

    /// <summary>Checks that the files the theme names exist in <paramref name="pluginDirectory"/> and are small enough.</summary>
    public void ValidateFiles(string where, string pluginDirectory, List<string> problems)
    {
        if (Sounds?.Alert is { } alert && !alert.Contains("..", StringComparison.Ordinal) && !Path.IsPathRooted(alert))
        {
            var file = new FileInfo(Path.Combine(pluginDirectory, alert));
            if (!file.Exists)
            {
                problems.Add($"{where}: island.sounds.alert '{alert}' is not in the plugin folder");
            }
            else if (file.Length > MaxSoundBytes)
            {
                problems.Add($"{where}: island.sounds.alert is larger than 1 MB");
            }
        }
    }
}

/// <summary>The island's surface away from a notch: one colour, a gradient, or a see-through tint.</summary>
public sealed class IslandSurfaceSpec
{
    /// <summary>solid (default), gradient or translucent.</summary>
    public string? Material { get; init; }

    /// <summary>The colour of a solid or translucent surface, #RRGGBB (default black).</summary>
    public string? Color { get; init; }

    /// <summary>A gradient's colours, 2 to 4, from start to end.</summary>
    public IReadOnlyList<string>? Colors { get; init; }

    /// <summary>A gradient's direction in degrees (0: left to right; default 135).</summary>
    public double? Angle { get; init; }

    /// <summary>How opaque a translucent surface is, 0.5–1 (default 0.85).</summary>
    public double? Opacity { get; init; }
}

public sealed class IslandShapeSpec
{
    /// <summary>The island's corners scaled, 0.5–1.5 (default 1); never beyond a capsule, and not around a notch.</summary>
    public double? Radius { get; init; }
}

public sealed class IslandContentSpec
{
    /// <summary>Activity accent names (music, timer, …) → #RRGGBB.</summary>
    public Dictionary<string, string>? Accents { get; init; }
}

public sealed class IslandMotionSpec
{
    /// <summary>calm, default, snappy or bouncy: how the island's shape springs between sizes.</summary>
    public string? Spring { get; init; }

    /// <summary>slide (default) or droplet: how the second activity's bubble leaves the island.</summary>
    public string? Split { get; init; }
}

/// <summary>The mascot the theme suggests; applied when the theme is chosen, the user may change it afterwards.</summary>
public sealed class IslandMascotSpec
{
    /// <summary>classic, pixel, ghost or cube.</summary>
    public string? Kind { get; init; }

    /// <summary>#RRGGBB; absent: the theme's accent.</summary>
    public string? Color { get; init; }
}

/// <summary>Sounds the theme ships (played only when the user turns alert sounds on).</summary>
public sealed class IslandSoundsSpec
{
    /// <summary>A .wav, .mp3 or .aiff file in the plugin folder, at most 1 MB, played when an alert opens the island.</summary>
    public string? Alert { get; init; }
}

/// <summary>How the host lays out the regions plugins fill.</summary>
public sealed class IslandLayoutSpec
{
    /// <summary>
    /// The activity card: standard (leading | center | trailing, bottom underneath), stacked (one column), or media (a
    /// large leading region on the left, the rest beside it).
    /// </summary>
    public string? Expanded { get; init; }
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

    /// <summary>The island's own look (docs/design/09-island-v2.md §4): surface, shape, accents, motion.</summary>
    public IslandThemeSpec? Island { get; init; }

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
            t.Island?.Validate($"theme '{t.Id}'", problems);
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

    internal static bool IsHexColor(string s) =>
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

    /// <summary>One activity per instance (one per account, per timer): <c>island.activity(id, spec, { instance })</c>.</summary>
    public bool Multiple { get; init; }

    /// <summary>passive, normal (default), high or critical: what the activity claims unless an update says otherwise.</summary>
    public string? Priority { get; init; }

    /// <summary>The accent's semantic name (music, timer, message…) or #RRGGBB.</summary>
    public string? Accent { get; init; }
}

/// <summary>Checks the 2.0 contributions (docs/design/09-island-v2.md §3): activities, ambient items, home pages.</summary>
public static class IslandContributions
{
    public static readonly IReadOnlySet<string> Priorities = new HashSet<string>(StringComparer.Ordinal) { "passive", "normal", "high", "critical" };

    public static void Validate(PluginContributions c, List<string> problems)
    {
        static bool IsId(string? id) => id is { Length: > 0 and <= 64 } && id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or '.');

        void Ids(IEnumerable<string?> ids, string what)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var id in ids)
            {
                if (!IsId(id) || !seen.Add(id!))
                {
                    problems.Add($"{what} '{id}': id missing, invalid or used twice");
                }
            }
        }

        Ids(c.Activities.Select(a => a.Id), "activity");
        foreach (var a in c.Activities)
        {
            if (a.Priority is { } p && !Priorities.Contains(p))
            {
                problems.Add($"activity '{a.Id}': priority must be passive, normal, high or critical");
            }
            if (a.Accent is { } accent && accent.StartsWith('#') && !ThemeContribution.IsHexColor(accent))
            {
                problems.Add($"activity '{a.Id}': accent must be a name or #RRGGBB");
            }
        }
        Ids(c.Ambient.Select(a => a.Id), "ambient item");
        foreach (var a in c.Ambient.Where(a => a.Side is { } side && side is not ("left" or "right")))
        {
            problems.Add($"ambient item '{a.Id}': side must be left or right");
        }
        Ids(c.Home.Select(h => h.Id), "home page");
    }
}

/// <summary>An ambient item (docs/design/09-island-v2.md §3): a small piece beside the idle island.</summary>
public sealed class AmbientContribution
{
    public required string Id { get; init; }
    public string? Title { get; init; }

    /// <summary>"left" or "right" (default).</summary>
    public string? Side { get; init; }

    /// <summary>Whether it shows until the user decides otherwise.</summary>
    public bool? Enabled { get; init; }

    /// <summary>One item per instance (one per account): <c>island.ambient(id, instance)</c>.</summary>
    public bool Multiple { get; init; }
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
