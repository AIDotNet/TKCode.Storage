using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// The user's permission decisions, per plugin, in <c>&lt;data&gt;/grants.json</c>. A plugin with an entry has been
/// decided on (possibly granting only some, or none, of what it asks for); a plugin without one is still waiting.
/// </summary>
internal sealed class PermissionGrantStore(string path)
{
    private readonly Lock _gate = new();
    private Dictionary<string, HashSet<string>>? _grants;

    /// <summary>The granted permissions, or null when the user has not decided yet.</summary>
    public IReadOnlySet<string>? Get(string pluginId)
    {
        lock (_gate)
        {
            return Grants().GetValueOrDefault(pluginId);
        }
    }

    public void Set(string pluginId, IEnumerable<string> granted)
    {
        lock (_gate)
        {
            Grants()[pluginId] = new HashSet<string>(granted, StringComparer.Ordinal);
            Save();
        }
    }

    /// <summary>Forgets the decision (the plugin was removed): installing it again asks again.</summary>
    public void Remove(string pluginId)
    {
        lock (_gate)
        {
            if (Grants().Remove(pluginId))
            {
                Save();
            }
        }
    }

    private Dictionary<string, HashSet<string>> Grants()
    {
        if (_grants is not null)
        {
            return _grants;
        }
        _grants = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        if (File.Exists(path))
        {
            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
                foreach (var p in doc.RootElement.EnumerateObject())
                {
                    _grants[p.Name] = new HashSet<string>(
                        p.Value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!),
                        StringComparer.Ordinal);
                }
            }
            catch (JsonException)
            {
                // Unreadable decisions: ask again rather than guess. Keep the file for inspection.
                File.Move(path, path + ".corrupt", overwrite: true);
            }
        }
        return _grants;
    }

    private void Save()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (id, granted) in _grants!.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WriteStartArray(id);
                foreach (var p in granted.Order(StringComparer.Ordinal))
                {
                    w.WriteStringValue(p);
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path + ".tmp", buffer.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
