using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// A plugin's key-value store (<c>storage</c> in <c>@island/api</c>, no permission needed): values are JSON, kept in
/// memory and written to <c>&lt;data&gt;/&lt;plugin&gt;/storage.json</c> shortly after a change. It outlives engine
/// restarts. A JSON file per plugin is enough for small state; the SQLite store of the design
/// (docs/design/01-architecture.md §5) replaces it when plugins need queries.
/// </summary>
internal sealed class PluginStorage
{
    public const int MaxKeyLength = 256;

    private readonly string _path;
    private readonly long _quotaBytes;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private long _bytes;
    private bool _dirty;

    public PluginStorage(string path, long quotaBytes)
    {
        _path = path;
        _quotaBytes = quotaBytes;
        Load();
    }

    /// <summary>The value's JSON text, or null when the key is absent.</summary>
    public string? Get(string key)
    {
        lock (_gate)
        {
            return _values.GetValueOrDefault(key);
        }
    }

    /// <exception cref="InvalidOperationException">Invalid key, invalid JSON or quota exceeded.</exception>
    public void Set(string key, string json)
    {
        if (key.Length is 0 or > MaxKeyLength)
        {
            throw new InvalidOperationException($"Storage keys must be 1 to {MaxKeyLength} characters.");
        }
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("Storage values must be JSON-serialisable.");
        }
        lock (_gate)
        {
            var old = _values.TryGetValue(key, out var previous) ? Size(key, previous) : 0;
            var next = _bytes - old + Size(key, json);
            if (next > _quotaBytes)
            {
                throw new InvalidOperationException($"Storage quota of {_quotaBytes / 1024} KB exceeded.");
            }
            _values[key] = json;
            _bytes = next;
            _dirty = true;
        }
    }

    public bool Delete(string key)
    {
        lock (_gate)
        {
            if (!_values.Remove(key, out var old))
            {
                return false;
            }
            _bytes -= Size(key, old);
            _dirty = true;
            return true;
        }
    }

    public IReadOnlyList<string> Keys()
    {
        lock (_gate)
        {
            return [.. _values.Keys.Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>Writes pending changes; a crash mid-write never leaves half a file.</summary>
    public void Flush()
    {
        byte[] bytes;
        lock (_gate)
        {
            if (!_dirty)
            {
                return;
            }
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (key, json) in _values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
                {
                    w.WritePropertyName(key);
                    w.WriteRawValue(json, skipInputValidation: true); // validated in Set
                }
                w.WriteEndObject();
            }
            bytes = buffer.ToArray();
            _dirty = false;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, bytes);
        File.Move(temporary, _path, overwrite: true);
    }

    private void Load()
    {
        if (!File.Exists(_path))
        {
            return;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                var json = p.Value.GetRawText();
                _values[p.Name] = json;
                _bytes += Size(p.Name, json);
            }
        }
        catch (JsonException)
        {
            // A damaged file: keep it aside rather than overwrite what might be recoverable.
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
    }

    private static long Size(string key, string json) => Encoding.UTF8.GetByteCount(key) + Encoding.UTF8.GetByteCount(json);
}
