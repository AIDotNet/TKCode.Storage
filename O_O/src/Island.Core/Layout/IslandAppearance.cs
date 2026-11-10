using System.Globalization;
using System.Text.Json;

namespace Island.Core.Layout;

/// <summary>How the island looks (docs/design/07-home-and-layout.md §5).</summary>
public enum IslandShape
{
    /// <summary>A free-standing pill (the default). On notched Macs it sits on the notch, wider than it.</summary>
    Capsule,

    /// <summary>Hangs from the screen edge with concave shoulders; around a physical notch it wraps the notch.</summary>
    Notch,
}

/// <summary>Which screen edge the island lives on. Left and right arrive with side docking (07 §8, step 4).</summary>
public enum IslandDock
{
    Top,
    Left,
    Right,
}

/// <summary>Which screen the island lives on (docs 02 §4, docs 07 §5).</summary>
public enum IslandDisplay
{
    /// <summary>The primary screen (the default).</summary>
    Primary,

    /// <summary>The screen the pointer is on; the island moves when the pointer moves to another screen.</summary>
    FollowMouse,
}

/// <summary>What the island does while another application is full screen (docs 07 §5).</summary>
public enum FullScreenBehavior
{
    /// <summary>Hide until the full-screen window goes away (the default).</summary>
    Hide,

    /// <summary>Stay on screen.</summary>
    Show,
}

/// <param name="Scale">0.8–1.4; the whole island, text included. Ignored around a physical notch, which it must match.</param>
/// <param name="Offset">DIPs along the docked edge from its centre (top: rightwards). Ignored around a physical notch.</param>
public sealed record IslandAppearance(
    IslandShape Shape = IslandShape.Capsule,
    IslandDock Dock = IslandDock.Top,
    double Scale = 1.0,
    double Offset = 0,
    IslandDisplay Display = IslandDisplay.Primary,
    FullScreenBehavior FullScreen = FullScreenBehavior.Hide)
{
    public const double MinScale = 0.8;
    public const double MaxScale = 1.4;
    public const double MaxOffset = 600;

    /// <summary>The layout placement for this shape: the layout resolves it per screen (notch, top bar or neither).</summary>
    public IslandPlacement Placement => Shape == IslandShape.Notch ? IslandPlacement.Attached : IslandPlacement.Capsule;

    public IslandAppearance Normalized() => this with
    {
        Scale = Math.Clamp(double.IsFinite(Scale) ? Scale : 1, MinScale, MaxScale),
        Offset = Math.Clamp(double.IsFinite(Offset) ? Offset : 0, -MaxOffset, MaxOffset),
    };
}

/// <summary>Keeps the island's appearance in a JSON file and tells listeners when it changes.</summary>
public sealed class IslandAppearanceStore
{
    private readonly string _path;
    private IslandAppearance _current;

    public IslandAppearanceStore(string path)
    {
        _path = path;
        _current = Load(path);
    }

    public IslandAppearance Current => _current;

    /// <summary>Raised on the caller's thread after <see cref="Set"/> changed something.</summary>
    public event Action<IslandAppearance>? Changed;

    public void Set(IslandAppearance appearance)
    {
        appearance = appearance.Normalized();
        if (appearance == _current)
        {
            return;
        }
        _current = appearance;
        Save();
        Changed?.Invoke(appearance);
    }

    private static IslandAppearance Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            return new IslandAppearance(
                Enum.TryParse<IslandShape>(Str(r, "shape"), true, out var shape) ? shape : IslandShape.Capsule,
                Enum.TryParse<IslandDock>(Str(r, "dock"), true, out var dock) ? dock : IslandDock.Top,
                r.TryGetProperty("scale", out var s) && s.TryGetDouble(out var scale) ? scale : 1,
                r.TryGetProperty("offset", out var o) && o.TryGetDouble(out var offset) ? offset : 0,
                Enum.TryParse<IslandDisplay>(Str(r, "display"), true, out var display) ? display : IslandDisplay.Primary,
                Enum.TryParse<FullScreenBehavior>(Str(r, "fullScreen"), true, out var fullScreen) ? fullScreen : FullScreenBehavior.Hide)
                .Normalized();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new IslandAppearance();
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                w.WriteString("shape", _current.Shape.ToString().ToLowerInvariant());
                w.WriteString("dock", _current.Dock.ToString().ToLowerInvariant());
                w.WriteNumber("scale", Math.Round(_current.Scale, 2));
                w.WriteNumber("offset", Math.Round(_current.Offset));
                w.WriteString("display", _current.Display == IslandDisplay.FollowMouse ? "followMouse" : "primary");
                w.WriteString("fullScreen", _current.FullScreen.ToString().ToLowerInvariant());
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path + ".tmp", buffer.ToArray());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Appearance is a preference: failing to save it must not break the island.
        }
    }
}
