using System.Text.Json;

namespace Island.Core.Pages;

/// <summary>A page some plugin offers (docs/design/07-home-and-layout.md §2). <see cref="Key"/> is <c>plugin/page</c>.</summary>
public sealed record PageInfo(string Key, string Title, bool EnabledByDefault = true, string? Icon = null);

/// <summary>
/// Which pages the island shows and in what order, and which one is home (docs 07 §6, 页面). Pages the user never
/// touched keep their default and come after the ordered ones, in the order offered.
/// </summary>
public sealed record PagePreferences
{
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyDictionary<string, bool> Enabled { get; init; } = new Dictionary<string, bool>();
    public string? Home { get; init; }

    public bool IsEnabled(PageInfo page) => Enabled.TryGetValue(page.Key, out var on) ? on : page.EnabledByDefault;

    /// <summary>Every offered page in the user's order (enabled or not).</summary>
    public IReadOnlyList<PageInfo> Arrange(IEnumerable<PageInfo> offered)
    {
        var list = offered.ToList();
        int Rank(PageInfo p)
        {
            var i = IndexOf(Order, p.Key);
            return i < 0 ? int.MaxValue : i;
        }
        return [.. list.Select((p, i) => (p, i)).OrderBy(x => Rank(x.p)).ThenBy(x => x.i).Select(x => x.p)];
    }

    private static int IndexOf(IReadOnlyList<string> list, string key)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == key)
            {
                return i;
            }
        }
        return -1;
    }

    /// <summary>The pages the island shows, in order.</summary>
    public IReadOnlyList<PageInfo> Visible(IEnumerable<PageInfo> offered) => [.. Arrange(offered).Where(IsEnabled)];

    /// <summary>The home page: the user's choice while it is visible, otherwise the first visible page.</summary>
    public string? HomeOf(IReadOnlyList<PageInfo> visible) =>
        visible.Any(p => p.Key == Home) ? Home : visible.FirstOrDefault()?.Key;

    public PagePreferences WithEnabled(string key, bool enabled) =>
        this with { Enabled = new Dictionary<string, bool>(Enabled) { [key] = enabled } };
}

/// <summary>
/// The smart home rule (docs 07 §3.1): which page the island opens on.
/// </summary>
public static class SmartHome
{
    /// <summary>How long a page the user picked survives the island collapsing (the pointer wandered off and back).</summary>
    public static readonly TimeSpan StickyFor = TimeSpan.FromSeconds(30);

    /// <param name="visible">The visible pages, in order.</param>
    /// <param name="picked">The page the user last picked by hand, if the island showed it when it collapsed.</param>
    /// <param name="sinceCollapse">How long ago the island collapsed.</param>
    /// <param name="urgent">Pages of active or critical activities, most important first.</param>
    public static string? Choose(
        IReadOnlyList<PageInfo> visible,
        PagePreferences preferences,
        string? picked,
        TimeSpan sinceCollapse,
        IEnumerable<string> urgent)
    {
        bool Shown(string? key) => key is not null && visible.Any(p => p.Key == key);
        if (Shown(picked) && sinceCollapse < StickyFor)
        {
            return picked;
        }
        if (urgent.FirstOrDefault(Shown) is { } activityPage)
        {
            return activityPage;
        }
        return preferences.HomeOf(visible);
    }
}

/// <summary>Keeps <see cref="PagePreferences"/> in <c>pages.json</c> and tells listeners when they change.</summary>
public sealed class PagePreferencesStore
{
    private readonly string _path;

    public PagePreferencesStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public PagePreferences Current { get; private set; }

    public event Action<PagePreferences>? Changed;

    public void Set(PagePreferences preferences)
    {
        Current = preferences;
        Save();
        Changed?.Invoke(preferences);
    }

    private static PagePreferences Load(string path)
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
                foreach (var p in en.EnumerateObject())
                {
                    if (p.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    {
                        enabled[p.Name] = p.Value.GetBoolean();
                    }
                }
            }
            var home = r.TryGetProperty("home", out var h) && h.ValueKind == JsonValueKind.String ? h.GetString() : null;
            return new PagePreferences { Order = order, Enabled = enabled, Home = home };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new PagePreferences();
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
                if (Current.Home is { } home)
                {
                    w.WriteString("home", home);
                }
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
