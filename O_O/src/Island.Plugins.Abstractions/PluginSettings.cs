using System.Globalization;
using System.Text.Json;

namespace Island.Plugins;

public enum SettingType
{
    String,
    Number,
    Integer,
    Boolean,
}

/// <summary>
/// One plugin setting, from <c>contributes.settings.properties</c> in plugin.json: a subset of JSON Schema that the
/// console turns into a form (docs/design/04-console.md §4). Values are validated by the host, not trusted from the UI.
/// </summary>
public sealed record SettingDefinition(
    string Key,
    SettingType Type,
    string Title,
    string? Description,
    string? DefaultJson,
    IReadOnlyList<string>? Enum,
    double? Minimum,
    double? Maximum,
    string? Format,
    bool Multiline)
{
    /// <summary>Password-like settings are kept in the OS credential store, never in settings files.</summary>
    public bool IsSecret => Format == "password";

    /// <summary>Checks a value and returns its canonical JSON, or throws with a message fit for the form.</summary>
    /// <exception cref="ArgumentException">The value does not fit the definition.</exception>
    public string Validate(JsonElement value)
    {
        switch (Type)
        {
            case SettingType.Boolean when value.ValueKind is JsonValueKind.True or JsonValueKind.False:
                return value.GetBoolean() ? "true" : "false";
            case SettingType.Number or SettingType.Integer when value.ValueKind == JsonValueKind.Number:
                var n = value.GetDouble();
                if (Type == SettingType.Integer && Math.Abs(n % 1) > 0)
                {
                    throw new ArgumentException($"{Title} must be a whole number.");
                }
                if (n < Minimum || n > Maximum)
                {
                    throw new ArgumentException($"{Title} must be between {Minimum?.ToString(CultureInfo.InvariantCulture) ?? "-∞"} and {Maximum?.ToString(CultureInfo.InvariantCulture) ?? "∞"}.");
                }
                return n.ToString("R", CultureInfo.InvariantCulture);
            case SettingType.String when value.ValueKind == JsonValueKind.String:
                var s = value.GetString()!;
                if (Enum is not null && !Enum.Contains(s))
                {
                    throw new ArgumentException($"{Title} must be one of: {string.Join(", ", Enum)}.");
                }
                if (s.Length > 4096)
                {
                    throw new ArgumentException($"{Title} is too long.");
                }
                return JsonSerializer.Serialize(s, PluginJsonContext.Default.String);
            default:
                throw new ArgumentException($"{Title} expects a {Type.ToString().ToLowerInvariant()}.");
        }
    }
}

public static class SettingsSchema
{
    private static readonly HashSet<string> Formats = ["password", "color", "hotkey", "date", "time", "url"];

    /// <summary>Parses <c>contributes.settings</c>; invalid entries are reported and skipped.</summary>
    public static IReadOnlyList<SettingDefinition> Parse(JsonElement? schema, List<string> problems)
    {
        var result = new List<SettingDefinition>();
        if (schema is not { ValueKind: JsonValueKind.Object } root ||
            !root.TryGetProperty("properties", out var properties) || properties.ValueKind != JsonValueKind.Object)
        {
            return result;
        }
        foreach (var p in properties.EnumerateObject())
        {
            var d = p.Value;
            if (d.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"setting '{p.Name}' is not an object");
                continue;
            }
            var typeName = Str(d, "type");
            SettingType? type = typeName switch
            {
                "string" => SettingType.String,
                "number" => SettingType.Number,
                "integer" => SettingType.Integer,
                "boolean" => SettingType.Boolean,
                _ => null,
            };
            if (type is null)
            {
                problems.Add($"setting '{p.Name}' has unsupported type '{typeName}'");
                continue;
            }
            var format = Str(d, "format");
            if (format is not null && !Formats.Contains(format))
            {
                problems.Add($"setting '{p.Name}' has unknown format '{format}'");
                continue;
            }
            List<string>? options = null;
            if (d.TryGetProperty("enum", out var e) && e.ValueKind == JsonValueKind.Array)
            {
                options = [.. e.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!)];
            }
            var definition = new SettingDefinition(
                p.Name,
                type.Value,
                Str(d, "title") ?? p.Name,
                Str(d, "description"),
                DefaultJson: null,
                options,
                Num(d, "minimum"),
                Num(d, "maximum"),
                format,
                d.TryGetProperty("x-multiline", out var m) && m.ValueKind == JsonValueKind.True);
            if (d.TryGetProperty("default", out var def))
            {
                try
                {
                    definition = definition with { DefaultJson = definition.Validate(def) };
                }
                catch (ArgumentException ex)
                {
                    problems.Add($"setting '{p.Name}' has an invalid default: {ex.Message}");
                    continue;
                }
            }
            if (definition.IsSecret && definition.DefaultJson is not null)
            {
                problems.Add($"setting '{p.Name}' is a password and cannot have a default");
                continue;
            }
            result.Add(definition);
        }
        return result;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
