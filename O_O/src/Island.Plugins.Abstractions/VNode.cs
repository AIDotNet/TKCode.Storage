using System.Text.Json;

namespace Island.Plugins;

/// <summary>
/// One node of a plugin's declarative view (docs/design/03-plugin-system.md §6). Plugins never touch Avalonia objects;
/// they produce this tree and the host maps whitelisted types onto Arc controls.
/// </summary>
/// <param name="Type">The node type, e.g. <c>text</c>, <c>row</c>, <c>button</c>.</param>
/// <param name="Key">Optional identity among siblings, so a reordered list keeps its controls.</param>
/// <param name="Props">Plain values: string, double or bool.</param>
/// <param name="Handlers">Event name (e.g. <c>onClick</c>) → handler id in the plugin's runtime.</param>
public sealed record VNode(
    string Type,
    string? Key,
    IReadOnlyDictionary<string, object> Props,
    IReadOnlyDictionary<string, int> Handlers,
    IReadOnlyList<VNode> Children)
{
    public string? GetString(string name) => Props.TryGetValue(name, out var v) ? v as string : null;

    public double? GetNumber(string name) => Props.TryGetValue(name, out var v) && v is double d ? d : null;

    public bool? GetBool(string name) => Props.TryGetValue(name, out var v) && v is bool b ? b : null;

    /// <summary>Same type and key: the control rendered for <paramref name="other"/> can be updated in place.</summary>
    public bool IsSameSlot(VNode other) => Type == other.Type && Key == other.Key;
}

/// <summary>What an activity shows in each island state.</summary>
/// <param name="Page">The page (of the same plugin) the island opens on while this activity runs (docs 07 §3.1).</param>
/// <param name="Status">A page's status: a short node the island header shows while the page is on show (pages only).</param>
public sealed record ActivityView(VNode? CompactLeading, VNode? CompactTrailing, VNode? Expanded, string? Page = null, VNode? Status = null);

/// <summary>The latest view of one chip (docs/design/07-home-and-layout.md §4).</summary>
/// <param name="Instance">Which one, for chips declared <c>multiple</c>; null otherwise.</param>
/// <param name="Title">The instance's name for the console (e.g. the account), or the declared title.</param>
/// <param name="View">The chip's content; null when the plugin removed it.</param>
public sealed record ChipSnapshot(string PluginId, string ChipId, string? Instance, string Title, VNode? View, long Sequence)
{
    /// <summary>Identifies the chip across plugins: <c>plugin/chip</c> or <c>plugin/chip#instance</c>.</summary>
    public string Key => ChipKey(PluginId, ChipId, Instance);

    public static string ChipKey(string pluginId, string chipId, string? instance) =>
        instance is null ? $"{pluginId}/{chipId}" : $"{pluginId}/{chipId}#{instance}";
}

/// <summary>A short-lived notice that takes the collapsed island's place for a few seconds ("已复制", "额度 95%").</summary>
public sealed record AlertSnapshot(string PluginId, VNode? Leading, VNode? Trailing, TimeSpan Duration, long Sequence);

/// <summary>The latest view of one page (docs/design/07-home-and-layout.md §2).</summary>
public sealed record PageSnapshot(string PluginId, string PageId, string Title, VNode? View, long Sequence, VNode? Status = null)
{
    /// <summary>Identifies the page across plugins: <c>plugin/page</c>.</summary>
    public string Key => PageKey(PluginId, PageId);

    public static string PageKey(string pluginId, string pageId) => $"{pluginId}/{pageId}";
}

/// <summary>How strongly an activity claims the island (docs 02 §2.1).</summary>
public enum ActivityPriority
{
    Ambient,
    Passive,
    Active,
    Critical,
}

/// <summary>The latest state of one running activity.</summary>
public sealed record ActivitySnapshot(
    string PluginId,
    string ActivityId,
    string Title,
    ActivityPriority Priority,
    ActivityView View,
    long Sequence);

/// <summary>
/// Parses the JSON a plugin sends for an activity. The wire format is compact: <c>t</c> type, <c>k</c> key,
/// <c>p</c> props, <c>c</c> children; a prop <c>{"$h": n}</c> is an event handler.
/// Unknown shapes are rejected rather than guessed at.
/// </summary>
public static class VNodeJson
{
    public const int MaxDepth = 32;
    public const int MaxNodes = 2000;

    // Each view level is an object plus a children array, and the view itself wraps the roots in two more objects.
    private static readonly JsonDocumentOptions DocumentOptions = new() { MaxDepth = MaxDepth * 2 + 8 };

    /// <exception cref="InvalidDataException">The JSON is malformed or the view breaks a limit.</exception>
    public static ActivityView ParseView(string json)
    {
        using var doc = Parse(json);
        var root = doc.RootElement;
        var budget = MaxNodes;
        VNode? Read(string name) =>
            root.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.Object ? ParseNode(e, 0, ref budget) : null;
        VNode? compactLeading = null, compactTrailing = null;
        if (root.TryGetProperty("compact", out var compact) && compact.ValueKind == JsonValueKind.Object)
        {
            if (compact.TryGetProperty("leading", out var l) && l.ValueKind == JsonValueKind.Object)
            {
                compactLeading = ParseNode(l, 0, ref budget);
            }
            if (compact.TryGetProperty("trailing", out var t) && t.ValueKind == JsonValueKind.Object)
            {
                compactTrailing = ParseNode(t, 0, ref budget);
            }
        }
        var page = root.TryGetProperty("page", out var pg) && pg.ValueKind == JsonValueKind.String ? pg.GetString() : null;
        return new ActivityView(compactLeading, compactTrailing, Read("expanded"), string.IsNullOrEmpty(page) ? null : page, Read("status"));
    }

    /// <exception cref="InvalidDataException">The JSON is malformed or the node breaks a limit.</exception>
    public static VNode ParseNode(string json)
    {
        using var doc = Parse(json);
        var budget = MaxNodes;
        return ParseNode(doc.RootElement, 0, ref budget);
    }

    private static JsonDocument Parse(string json)
    {
        try
        {
            return JsonDocument.Parse(json, DocumentOptions);
        }
        catch (JsonException e)
        {
            throw new InvalidDataException(e.Message, e);
        }
    }

    private static VNode ParseNode(JsonElement e, int depth, ref int budget)
    {
        if (depth > MaxDepth)
        {
            throw new InvalidDataException($"View is nested deeper than {MaxDepth} levels.");
        }
        if (--budget < 0)
        {
            throw new InvalidDataException($"View has more than {MaxNodes} nodes.");
        }
        if (!e.TryGetProperty("t", out var t) || t.ValueKind != JsonValueKind.String)
        {
            throw new InvalidDataException("A view node has no type.");
        }
        string? key = e.TryGetProperty("k", out var k) && k.ValueKind is JsonValueKind.String or JsonValueKind.Number
            ? k.ToString()
            : null;

        var props = new Dictionary<string, object>(StringComparer.Ordinal);
        var handlers = new Dictionary<string, int>(StringComparer.Ordinal);
        if (e.TryGetProperty("p", out var p) && p.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in p.EnumerateObject())
            {
                switch (prop.Value.ValueKind)
                {
                    case JsonValueKind.String:
                        props[prop.Name] = prop.Value.GetString()!;
                        break;
                    case JsonValueKind.Number:
                        props[prop.Name] = prop.Value.GetDouble();
                        break;
                    case JsonValueKind.True:
                    case JsonValueKind.False:
                        props[prop.Name] = prop.Value.GetBoolean();
                        break;
                    case JsonValueKind.Object when prop.Value.TryGetProperty("$h", out var h) && h.TryGetInt32(out var id):
                        handlers[prop.Name] = id;
                        break;
                    // null/undefined props and anything structured are dropped: props are plain values only.
                }
            }
        }

        var children = new List<VNode>();
        if (e.TryGetProperty("c", out var c) && c.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in c.EnumerateArray())
            {
                if (child.ValueKind == JsonValueKind.Object)
                {
                    children.Add(ParseNode(child, depth + 1, ref budget));
                }
            }
        }
        return new VNode(t.GetString()!, key, props, handlers, children);
    }
}
