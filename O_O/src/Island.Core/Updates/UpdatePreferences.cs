using System.Text.Json;

namespace Island.Core.Updates;

/// <summary>The update check's settings (updates.json): whether to check by itself, and which version was announced.</summary>
public sealed class UpdatePreferences
{
    private readonly string _path;

    public UpdatePreferences(string path)
    {
        _path = path;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            AutoCheck = !r.TryGetProperty("autoCheck", out var a) || a.ValueKind != JsonValueKind.False;
            AutoInstall = !r.TryGetProperty("autoInstall", out var i) || i.ValueKind != JsonValueKind.False;
            AnnouncedVersion = r.TryGetProperty("announced", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            AutoCheck = true;
            AutoInstall = true;
        }
    }

    /// <summary>Check at start and once a day (on by default).</summary>
    public bool AutoCheck { get; private set; }

    /// <summary>Download a newer version by itself and install it when O_O quits or restarts (on by default).</summary>
    public bool AutoInstall { get; private set; }

    public void SetAutoInstall(bool on)
    {
        AutoInstall = on;
        Save();
    }

    /// <summary>The newest version the island already announced, so each version is announced once.</summary>
    public string? AnnouncedVersion { get; private set; }

    public void SetAutoCheck(bool on)
    {
        AutoCheck = on;
        Save();
    }

    public void SetAnnounced(string version)
    {
        AnnouncedVersion = version;
        Save();
    }

    private void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteBoolean("autoCheck", AutoCheck);
                w.WriteBoolean("autoInstall", AutoInstall);
                if (AnnouncedVersion is not null)
                {
                    w.WriteString("announced", AnnouncedVersion);
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, buffer.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference: failing to save it must not break anything.
        }
    }
}
