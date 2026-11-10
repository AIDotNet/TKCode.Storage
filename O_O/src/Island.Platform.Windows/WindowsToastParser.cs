using System.Text.Json;

namespace Island.Platform.Windows;

/// <summary>What the notification reader script prints, one JSON object per line.</summary>
internal abstract record ToastLine
{
    /// <summary>The result of asking the system for access to notifications ("Allowed", "Denied", "Unspecified").</summary>
    public sealed record Access(string Status) : ToastLine;

    public sealed record Toast(string App, string Name, string? Title, string? Body, DateTimeOffset Time) : ToastLine;

    public static ToastLine? Parse(string line)
    {
        line = line.Trim();
        if (line.Length == 0 || line[0] != '{')
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            string? Str(string name) =>
                r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;
            switch (Str("type"))
            {
                case "access":
                    return new Access(Str("value") ?? "Unspecified");
                case "toast":
                    var app = Str("app");
                    if (app is null)
                    {
                        return null;
                    }
                    var time = r.TryGetProperty("time", out var t) && t.TryGetInt64(out var ms)
                        ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                        : DateTimeOffset.Now;
                    return new Toast(app, Str("name") ?? app, Str("title"), Str("body"), time);
                default:
                    return null;
            }
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
