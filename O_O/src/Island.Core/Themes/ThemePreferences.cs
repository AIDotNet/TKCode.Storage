using System.Globalization;
using System.Text.Json;

namespace Island.Core.Themes;

/// <summary>
/// The look the user chose beyond appearance and accent (those live in the app settings): which theme pack, an
/// automatic light/dark schedule, and taking the accent from the wallpaper (docs/design/05-builtin-plugins.md §3).
/// </summary>
/// <param name="Theme">The chosen theme, <c>plugin/theme</c>; null: none chosen (the Apple default look).</param>
/// <param name="LightAt">When the schedule switches to light.</param>
/// <param name="DarkAt">When the schedule switches to dark.</param>
public sealed record ThemePreferences(
    string? Theme = null,
    bool Schedule = false,
    TimeOnly LightAt = default,
    TimeOnly DarkAt = default,
    bool WallpaperAccent = false)
{
    public static readonly TimeOnly DefaultLightAt = new(7, 0);
    public static readonly TimeOnly DefaultDarkAt = new(19, 0);

    public static ThemePreferences Default { get; } = new(null, false, DefaultLightAt, DefaultDarkAt, false);

    /// <summary>
    /// What the schedule asks for at <paramref name="now"/>: light from <see cref="LightAt"/> until <see cref="DarkAt"/>,
    /// dark otherwise. Works across midnight either way round (e.g. light 20:00–06:00 for night shifts).
    /// </summary>
    public bool IsLightAt(TimeOnly now)
    {
        if (LightAt == DarkAt)
        {
            return true;
        }
        return LightAt < DarkAt ? now >= LightAt && now < DarkAt : now >= LightAt || now < DarkAt;
    }
}

/// <summary>Keeps <see cref="ThemePreferences"/> in <c>theme.json</c>.</summary>
public sealed class ThemePreferencesStore
{
    private readonly string _path;

    public ThemePreferencesStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public ThemePreferences Current { get; private set; }

    public event Action<ThemePreferences>? Changed;

    public void Set(ThemePreferences preferences)
    {
        if (preferences == Current)
        {
            return;
        }
        Current = preferences;
        Save();
        Changed?.Invoke(preferences);
    }

    private static ThemePreferences Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            return new ThemePreferences(
                Str(r, "theme"),
                r.TryGetProperty("schedule", out var s) && s.ValueKind == JsonValueKind.True,
                Time(Str(r, "lightAt")) ?? ThemePreferences.DefaultLightAt,
                Time(Str(r, "darkAt")) ?? ThemePreferences.DefaultDarkAt,
                r.TryGetProperty("wallpaperAccent", out var w) && w.ValueKind == JsonValueKind.True);
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return ThemePreferences.Default;
        }
    }

    private void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                if (Current.Theme is { } theme)
                {
                    w.WriteString("theme", theme);
                }
                w.WriteBoolean("schedule", Current.Schedule);
                w.WriteString("lightAt", Current.LightAt.ToString("HH:mm", CultureInfo.InvariantCulture));
                w.WriteString("darkAt", Current.DarkAt.ToString("HH:mm", CultureInfo.InvariantCulture));
                w.WriteBoolean("wallpaperAccent", Current.WallpaperAccent);
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

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static TimeOnly? Time(string? s) =>
        TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : null;
}
