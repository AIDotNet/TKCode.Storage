using System.Text.Json;

namespace Island.Plugins;

/// <summary>How much a live activity matters (docs/design/09-island-v2.md §2.1); the host arbitrates with it.</summary>
public enum LivePriority
{
    Passive,
    Normal,
    High,
    Critical,
}

/// <summary>
/// The fixed regions a plugin fills (09 §3). Compact uses leading and trailing; an alert leading, center and trailing;
/// the expanded card all four. The host lays the regions out, so themes and motion apply to every plugin alike.
/// </summary>
public sealed record RegionView(VNode? Leading = null, VNode? Trailing = null, VNode? Center = null, VNode? Bottom = null)
{
    public static readonly RegionView Empty = new();

    public bool IsEmpty => Leading is null && Trailing is null && Center is null && Bottom is null;
}

/// <summary>Everything a live activity shows: beside the notch, as the bubble, and as the card.</summary>
public sealed record LiveActivityView(RegionView Compact, VNode? Minimal, RegionView Expanded);

/// <summary>What ⌘-click (or a double click) opens: the source application, or a link.</summary>
public sealed record LiveActivityOpen(string? App, string? Url);

/// <summary>The latest state of one live activity (<c>island.activity(id[, instance])</c>).</summary>
/// <param name="ActivityId">The declared activity (contributes.activities): the kind the user's preferences are about.</param>
/// <param name="Instance">Which one, for activities declared multiple (one per account, per timer); null otherwise.</param>
/// <param name="Accent">The accent's semantic name (music, timer, message…) or #RRGGBB; the theme gives names a colour.</param>
public sealed record LiveActivitySnapshot(
    string PluginId,
    string ActivityId,
    string? Instance,
    string Title,
    LivePriority Priority,
    string? Accent,
    LiveActivityOpen? Open,
    LiveActivityView View,
    long Sequence)
{
    /// <summary>The kind across plugins: <c>plugin/activity</c>.</summary>
    public string Kind => $"{PluginId}/{ActivityId}";

    /// <summary>This instance across plugins: <c>plugin/activity[#instance]</c>.</summary>
    public string Key => Instance is null ? Kind : $"{Kind}#{Instance}";
}

/// <summary>A brief alert (<c>island.alert</c>, or <c>activity.alert</c> with <see cref="ActivityKey"/>).</summary>
public sealed record LiveAlertSnapshot(
    string PluginId,
    string? ActivityKey,
    LivePriority Priority,
    RegionView View,
    TimeSpan Duration,
    long Sequence)
{
    /// <summary>Alerts with the same key replace each other: the activity's, or one per plugin for one-off alerts.</summary>
    public string Key => ActivityKey ?? $"{PluginId}/alert";

    /// <summary>The kind the user's policies are about: the activity's, or the plugin for one-off alerts.</summary>
    public string Kind => ActivityKey is { } k && k.IndexOf('#') is var i and > 0 ? k[..i] : ActivityKey ?? PluginId;
}

/// <summary>An ambient item (<c>island.ambient(id[, instance])</c>): shown beside the idle island. A null view removes it.</summary>
/// <param name="Title">What the console calls it: the instance's title (the account), else the declared one.</param>
/// <param name="EnabledByDefault">Whether it shows until the user decides (declared <c>enabled</c>).</param>
public sealed record AmbientSnapshot(
    string PluginId,
    string AmbientId,
    string Side,
    VNode? View,
    long Sequence,
    string? Instance = null,
    string? Title = null,
    bool EnabledByDefault = true)
{
    /// <summary><c>plugin/id[#instance]</c>: the same key the 1.x chip had, so the user's choices carry over.</summary>
    public string Key => Instance is null ? $"{PluginId}/{AmbientId}" : $"{PluginId}/{AmbientId}#{Instance}";
}

/// <summary>Parses the region JSON the 2.0 API sends (09 §3): <c>{ compact: { leading, trailing }, minimal, expanded: { … } }</c>.</summary>
public static class RegionJson
{
    /// <exception cref="InvalidDataException">The JSON is malformed or the view breaks a limit.</exception>
    public static LiveActivityView ParseActivity(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        var budget = VNodeJson.MaxNodes;
        var compact = Regions(root, "compact", ref budget);
        var minimal = Node(root, "minimal", ref budget);
        var expanded = Regions(root, "expanded", ref budget);
        if (compact.IsEmpty && minimal is null && expanded.IsEmpty)
        {
            throw new InvalidDataException("An activity needs at least one of compact, minimal or expanded.");
        }
        return new LiveActivityView(compact with { Center = null, Bottom = null }, minimal, expanded);
    }

    /// <summary>An alert's regions: <c>{ leading, center, trailing }</c>.</summary>
    /// <exception cref="InvalidDataException">The JSON is malformed or the view breaks a limit.</exception>
    public static RegionView ParseAlert(string json)
    {
        using var doc = Parse(json);
        var budget = VNodeJson.MaxNodes;
        var view = Regions(doc.RootElement, ref budget);
        if (view.Leading is null && view.Center is null && view.Trailing is null)
        {
            throw new InvalidDataException("An alert needs leading, center or trailing.");
        }
        return view with { Bottom = null };
    }

    private static RegionView Regions(JsonElement parent, string name, ref int budget) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? Regions(e, ref budget) : RegionView.Empty;

    private static RegionView Regions(JsonElement e, ref int budget) =>
        new(Node(e, "leading", ref budget), Node(e, "trailing", ref budget), Node(e, "center", ref budget), Node(e, "bottom", ref budget));

    private static VNode? Node(JsonElement parent, string name, ref int budget) =>
        parent.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? VNodeJson.ParseNode(e, ref budget) : null;

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = VNodeJson.MaxDepth * 2 + 8 });
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }
}
