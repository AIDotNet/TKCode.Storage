using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>How much of another app's notification a plugin may see (docs/design/08-notifications-and-satellites.md §7).</summary>
public enum NotificationDisplay
{
    /// <summary>Sender and message text.</summary>
    Full,

    /// <summary>The sender only; the text is dropped.</summary>
    SenderOnly,

    /// <summary>Neither sender nor text: just that the app has something new.</summary>
    Hidden,
}

/// <summary>
/// The user's choice, edited in the console (never by plugins), of what plugins see of other apps' notifications: a default
/// and per-app overrides. Kept in <c>&lt;data&gt;/notifications.json</c>. The host cuts the fields before a notification
/// reaches any plugin, so no plugin can read more than the user allows.
/// </summary>
public sealed class NotificationPreferences(string dataDirectory)
{
    private readonly Lock _gate = new();
    private NotificationDisplay _default = NotificationDisplay.SenderOnly;
    private Dictionary<string, NotificationDisplay> _apps = new(StringComparer.Ordinal);
    private bool _loaded;

    private string FilePath => Path.Combine(dataDirectory, "notifications.json");

    /// <summary>Raised after any change.</summary>
    public event Action? Changed;

    /// <summary>What apps without an override show; <see cref="NotificationDisplay.SenderOnly"/> until the user chooses.</summary>
    public NotificationDisplay Default
    {
        get { lock (_gate) { Load(); return _default; } }
        set { lock (_gate) { Load(); _default = value; Save(); } Changed?.Invoke(); }
    }

    /// <summary>What <paramref name="appId"/> shows: its own setting, else the default.</summary>
    public NotificationDisplay For(string appId)
    {
        lock (_gate)
        {
            Load();
            return _apps.TryGetValue(appId, out var d) ? d : _default;
        }
    }

    /// <summary>Whether <paramref name="appId"/> has a setting of its own.</summary>
    public bool HasOverride(string appId)
    {
        lock (_gate) { Load(); return _apps.ContainsKey(appId); }
    }

    /// <summary>Sets <paramref name="appId"/>'s display; null removes the override so it follows the default again.</summary>
    public void Set(string appId, NotificationDisplay? display)
    {
        lock (_gate)
        {
            Load();
            if (display is { } d)
            {
                _apps[appId] = d;
            }
            else
            {
                _apps.Remove(appId);
            }
            Save();
        }
        Changed?.Invoke();
    }

    private static string Name(NotificationDisplay d) => d switch
    {
        NotificationDisplay.Full => "full",
        NotificationDisplay.Hidden => "hidden",
        _ => "senderOnly",
    };

    private static NotificationDisplay? Parse(string? s) => s switch
    {
        "full" => NotificationDisplay.Full,
        "senderOnly" => NotificationDisplay.SenderOnly,
        "hidden" => NotificationDisplay.Hidden,
        _ => null,
    };

    private void Load()
    {
        if (_loaded)
        {
            return;
        }
        _loaded = true;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(FilePath));
            var root = doc.RootElement;
            if (root.TryGetProperty("default", out var d) && Parse(d.GetString()) is { } def)
            {
                _default = def;
            }
            if (root.TryGetProperty("apps", out var apps) && apps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in apps.EnumerateObject())
                {
                    if (Parse(p.Value.GetString()) is { } v)
                    {
                        _apps[p.Name] = v;
                    }
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // No file yet, or unreadable: the defaults apply.
        }
    }

    private void Save()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("default", Name(_default));
            w.WriteStartObject("apps");
            foreach (var (id, d) in _apps.OrderBy(a => a.Key, StringComparer.Ordinal))
            {
                w.WriteString(id, Name(d));
            }
            w.WriteEndObject();
            w.WriteEndObject();
        }
        try
        {
            Directory.CreateDirectory(dataDirectory);
            File.WriteAllBytes(FilePath + ".tmp", buffer.ToArray());
            File.Move(FilePath + ".tmp", FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The choice still holds for this run.
        }
    }
}
