using System.Text.Json;
using Island.Platform;

namespace Island.Plugins.Host.Services;

/// <summary>
/// The values of one plugin's settings, shared by the console (which edits them) and the plugin's runner (which reads
/// them). Ordinary values live in <c>&lt;data&gt;/&lt;plugin&gt;/settings.json</c>; password settings go to the OS
/// credential store under account <c>setting:&lt;key&gt;</c> and never touch disk.
/// </summary>
public sealed class PluginSettingsStore
{
    private readonly string _path;
    private readonly string _service;
    private readonly ISecretStore _secrets;
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();

    internal PluginSettingsStore(string pluginId, IReadOnlyList<SettingDefinition> definitions, string path, ISecretStore secrets)
    {
        Definitions = definitions;
        _path = path;
        _service = $"O_O/{pluginId}";
        _secrets = secrets;
        Load();
    }

    public IReadOnlyList<SettingDefinition> Definitions { get; }

    /// <summary>Raised with the key after a value changes (on the caller's thread).</summary>
    public event Action<string>? Changed;

    public SettingDefinition? Find(string key) => Definitions.FirstOrDefault(d => d.Key == key);

    /// <summary>The value's JSON (or the default); null when unset. Not for password settings.</summary>
    public string? GetJson(string key)
    {
        var d = Find(key);
        if (d is null || d.IsSecret)
        {
            return null;
        }
        lock (_gate)
        {
            return _values.GetValueOrDefault(key) ?? d.DefaultJson;
        }
    }

    /// <summary>Every non-secret setting with its value or default, as a JSON object.</summary>
    public string AllJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var d in Definitions.Where(d => !d.IsSecret))
            {
                if (GetJson(d.Key) is { } json)
                {
                    w.WritePropertyName(d.Key);
                    w.WriteRawValue(json, skipInputValidation: true);
                }
            }
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Whether a password setting has a value (the console shows "set" without reading it).</summary>
    public bool HasSecret(string key) => Find(key) is { IsSecret: true } && _secrets.Get(_service, Account(key)) is not null;

    public string? GetSecret(string key) => Find(key) is { IsSecret: true } ? _secrets.Get(_service, Account(key)) : null;

    /// <summary>Validates and stores a value. Password settings take a string and go to the credential store.</summary>
    /// <exception cref="ArgumentException">Unknown key or invalid value; the message is fit for the form.</exception>
    public void Set(string key, JsonElement value)
    {
        var d = Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.");
        var json = d.Validate(value);
        if (d.IsSecret)
        {
            _secrets.Set(_service, Account(key), value.GetString()!);
        }
        else
        {
            lock (_gate)
            {
                _values[key] = json;
                Save();
            }
        }
        Changed?.Invoke(key);
    }

    /// <summary>Back to the default (or unset).</summary>
    public void Reset(string key)
    {
        var d = Find(key) ?? throw new ArgumentException($"Unknown setting '{key}'.");
        if (d.IsSecret)
        {
            _secrets.Delete(_service, Account(key));
        }
        else
        {
            lock (_gate)
            {
                _values.Remove(key);
                Save();
            }
        }
        Changed?.Invoke(key);
    }

    private static string Account(string key) => $"setting:{key}";

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
                // Values that no longer fit the schema (after a plugin update) fall back to the default.
                if (Find(p.Name) is { IsSecret: false } d)
                {
                    try
                    {
                        _values[p.Name] = d.Validate(p.Value);
                    }
                    catch (ArgumentException)
                    {
                    }
                }
            }
        }
        catch (JsonException)
        {
            File.Move(_path, _path + ".corrupt", overwrite: true);
        }
    }

    private void Save()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (key, json) in _values.OrderBy(kv => kv.Key, StringComparer.Ordinal))
            {
                w.WritePropertyName(key);
                w.WriteRawValue(json, skipInputValidation: true);
            }
            w.WriteEndObject();
        }
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temporary = _path + ".tmp";
        File.WriteAllBytes(temporary, buffer.ToArray());
        File.Move(temporary, _path, overwrite: true);
    }
}
