using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;

namespace Island.Platform.MacOS;

/// <summary>
/// Maps the binary plist stored in a row of the notification database (<c>record.data</c>) to a
/// <see cref="PlatformNotification"/>. Pure and free of reflection: the plist is first turned into a small tree
/// (dictionaries, lists, strings, numbers) from either <c>plutil -convert json</c> or <c>-convert xml1</c> output.
/// </summary>
internal static class NotificationRecordParser
{
    // CFAbsoluteTime counts seconds from 2001-01-01 UTC.
    private static readonly DateTimeOffset CfEpoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public static DateTimeOffset FromCfAbsoluteTime(double seconds) => CfEpoch.AddSeconds(seconds);

    /// <summary>Parses the JSON that plutil produced; null when it is not JSON or carries neither a title nor a body.</summary>
    public static PlatformNotification? Parse(string json, string appId, string appName, DateTimeOffset time)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return Map(FromJson(doc.RootElement), appId, appName, time);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Same, from <c>plutil -convert xml1</c> output (used when JSON conversion fails, e.g. for NSData or dates).</summary>
    public static PlatformNotification? ParseXml(string xml, string appId, string appName, DateTimeOffset time)
    {
        try
        {
            var doc = XDocument.Parse(xml, LoadOptions.None);
            var root = doc.Root?.Elements().FirstOrDefault();
            return root is null ? null : Map(FromXml(root), appId, appName, time);
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    private static PlatformNotification? Map(object? root, string appId, string appName, DateTimeOffset time)
    {
        if (root is not Dictionary<string, object?> top)
        {
            return null;
        }
        // Layout A: { req: { titl, subt, body, thre, ... }, app: "..." }. Layout B: the fields at the top level.
        var req = top.TryGetValue("req", out var r) && r is Dictionary<string, object?> d ? d : top;
        var title = Str(req, "titl") ?? Str(top, "titl");
        var subtitle = Str(req, "subt") ?? Str(top, "subt");
        var body = Str(req, "body") ?? Str(top, "body");
        var thread = Str(req, "thre") ?? Str(top, "thre");
        if (title is null && body is null && subtitle is null)
        {
            return null;
        }
        return new PlatformNotification(appId, appName, title, subtitle, body, thread, time);
    }

    private static string? Str(Dictionary<string, object?> d, string key) =>
        d.TryGetValue(key, out var v) && v is string s && s.Length > 0 ? s : null;

    private static object? FromJson(JsonElement e)
    {
        switch (e.ValueKind)
        {
            case JsonValueKind.Object:
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var p in e.EnumerateObject())
                {
                    d[p.Name] = FromJson(p.Value);
                }
                return d;
            case JsonValueKind.Array:
                return e.EnumerateArray().Select(FromJson).ToList();
            case JsonValueKind.String:
                return e.GetString();
            case JsonValueKind.Number:
                return e.GetDouble();
            default:
                return null;
        }
    }

    private static object? FromXml(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
                var d = new Dictionary<string, object?>(StringComparer.Ordinal);
                var nodes = e.Elements().ToList();
                for (var i = 0; i + 1 < nodes.Count; i += 2)
                {
                    if (nodes[i].Name.LocalName == "key")
                    {
                        d[nodes[i].Value] = FromXml(nodes[i + 1]);
                    }
                }
                return d;
            case "array":
                return e.Elements().Select(FromXml).ToList();
            case "string":
                return e.Value;
            case "integer":
            case "real":
                return double.TryParse(e.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var n) ? n : null;
            default:
                return null; // data, date, true, false
        }
    }

    /// <summary>Parses the AXStatusLabel of a Dock item: digits (an optional trailing "+" is ignored); anything else is null.</summary>
    public static int? ParseBadgeLabel(string? label)
    {
        if (label is null)
        {
            return null;
        }
        var t = label.Trim().TrimEnd('+');
        return t.Length > 0 && t.All(char.IsAsciiDigit) && int.TryParse(t, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;
    }
}
