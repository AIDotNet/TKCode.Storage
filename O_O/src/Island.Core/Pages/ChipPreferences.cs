using System.Text.Json;

namespace Island.Core.Pages;

/// <summary>Which side of the collapsed island a chip sits on; beside a notch, which wing.</summary>
public enum ChipSide
{
    Left,
    Right,
}

/// <summary>A chip some plugin shows (docs/design/07-home-and-layout.md §4). <see cref="Key"/> is <c>plugin/chip[#instance]</c>.</summary>
public sealed record ChipInfo(string Key, string Title, bool EnabledByDefault = true, ChipSide DefaultSide = ChipSide.Right);

/// <summary>What the collapsed island shows (docs 07 §4, 收起时显示): which chips, in what order, on which side.</summary>
public sealed record ChipPreferences
{
    /// <summary>At most this many chips per side; the rest are left out, from the end.</summary>
    public const int MaxPerSide = 3;

    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyDictionary<string, bool> Enabled { get; init; } = new Dictionary<string, bool>();
    public IReadOnlyDictionary<string, ChipSide> Sides { get; init; } = new Dictionary<string, ChipSide>();

    public bool IsEnabled(ChipInfo chip) => Enabled.TryGetValue(chip.Key, out var on) ? on : chip.EnabledByDefault;

    public ChipSide SideOf(ChipInfo chip) => Sides.TryGetValue(chip.Key, out var side) ? side : chip.DefaultSide;

    /// <summary>Every offered chip in the user's order (enabled or not); untouched ones follow, as offered.</summary>
    public IReadOnlyList<ChipInfo> Arrange(IEnumerable<ChipInfo> offered)
    {
        var order = Order.Select((key, i) => (key, i)).ToDictionary(x => x.key, x => x.i);
        return [.. offered.Select((c, i) => (c, i))
            .OrderBy(x => order.TryGetValue(x.c.Key, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.c)];
    }

    /// <summary>The chips the collapsed island shows on each side, in order.</summary>
    public (IReadOnlyList<ChipInfo> Left, IReadOnlyList<ChipInfo> Right) Visible(IEnumerable<ChipInfo> offered)
    {
        var shown = Arrange(offered).Where(IsEnabled).ToList();
        return ([.. shown.Where(c => SideOf(c) == ChipSide.Left).Take(MaxPerSide)],
                [.. shown.Where(c => SideOf(c) == ChipSide.Right).Take(MaxPerSide)]);
    }

    public ChipPreferences WithEnabled(string key, bool enabled) =>
        this with { Enabled = new Dictionary<string, bool>(Enabled) { [key] = enabled } };

    public ChipPreferences WithSide(string key, ChipSide side) =>
        this with { Sides = new Dictionary<string, ChipSide>(Sides) { [key] = side } };
}

/// <summary>Keeps <see cref="ChipPreferences"/> in <c>chips.json</c> and tells listeners when they change.</summary>
public sealed class ChipPreferencesStore
{
    private readonly string _path;

    public ChipPreferencesStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public ChipPreferences Current { get; private set; }

    public event Action<ChipPreferences>? Changed;

    public void Set(ChipPreferences preferences)
    {
        Current = preferences;
        Save();
        Changed?.Invoke(preferences);
    }

    private static ChipPreferences Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            var order = r.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            var enabled = new Dictionary<string, bool>();
            if (r.TryGetProperty("enabled", out var en) && en.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in en.EnumerateObject().Where(p => p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False))
                {
                    enabled[p.Name] = p.Value.GetBoolean();
                }
            }
            var sides = new Dictionary<string, ChipSide>();
            if (r.TryGetProperty("sides", out var sd) && sd.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in sd.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && Enum.TryParse<ChipSide>(p.Value.GetString(), true, out var side))
                    {
                        sides[p.Name] = side;
                    }
                }
            }
            return new ChipPreferences { Order = order, Enabled = enabled, Sides = sides };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ChipPreferences();
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
                w.WriteStartArray("order");
                foreach (var key in Current.Order)
                {
                    w.WriteStringValue(key);
                }
                w.WriteEndArray();
                w.WriteStartObject("enabled");
                foreach (var (key, on) in Current.Enabled)
                {
                    w.WriteBoolean(key, on);
                }
                w.WriteEndObject();
                w.WriteStartObject("sides");
                foreach (var (key, side) in Current.Sides)
                {
                    w.WriteString(key, side.ToString().ToLowerInvariant());
                }
                w.WriteEndObject();
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path + ".tmp", buffer.ToArray());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference: failing to save it must not break the island.
        }
    }
}
