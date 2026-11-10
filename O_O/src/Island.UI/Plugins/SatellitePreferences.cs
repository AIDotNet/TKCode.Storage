using System.Text.Json;

namespace Island.UI.Plugins;

/// <summary>The user's say over one satellite: switched off, and/or pinned to a side (<c>left</c>/<c>right</c>; null follows the plugin).</summary>
public sealed record SatelliteSetting(bool Enabled = true, string? Side = null);

/// <summary>Per <c>plugin/satellite</c> overrides, kept in a JSON file (same pattern as the page preferences).</summary>
public sealed class SatellitePreferencesStore
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, SatelliteSetting> _items;

    public SatellitePreferencesStore(string path)
    {
        _path = path;
        _items = Load(path);
    }

    public event Action? Changed;

    public SatelliteSetting Get(string key)
    {
        lock (_gate)
        {
            return _items.TryGetValue(key, out var s) ? s : new SatelliteSetting();
        }
    }

    public bool IsEnabled(string key) => Get(key).Enabled;

    public string? SideOf(string key) => Get(key).Side;

    public void Set(string key, SatelliteSetting setting)
    {
        lock (_gate)
        {
            if (setting == new SatelliteSetting())
            {
                _items.Remove(key);
            }
            else
            {
                _items[key] = setting with { Side = setting.Side is "left" or "right" ? setting.Side : null };
            }
            Save();
        }
        Changed?.Invoke();
    }

    public void SetEnabled(string key, bool enabled) => Set(key, Get(key) with { Enabled = enabled });

    public void SetSide(string key, string? side) => Set(key, Get(key) with { Side = side });

    private static Dictionary<string, SatelliteSetting> Load(string path)
    {
        var items = new Dictionary<string, SatelliteSetting>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }
                var enabled = !(p.Value.TryGetProperty("enabled", out var e) && e.ValueKind == JsonValueKind.False);
                var side = p.Value.TryGetProperty("side", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
                items[p.Name] = new SatelliteSetting(enabled, side is "left" or "right" ? side : null);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No file yet or unreadable: every satellite follows its plugin.
        }
        return items;
    }

    private void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (key, s) in _items.OrderBy(i => i.Key, StringComparer.Ordinal))
                {
                    w.WriteStartObject(key);
                    w.WriteBoolean("enabled", s.Enabled);
                    if (s.Side is { } side)
                    {
                        w.WriteString("side", side);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path + ".tmp", buffer.ToArray());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference: failing to save it must not break anything.
        }
    }
}
