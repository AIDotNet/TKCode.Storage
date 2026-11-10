using System.Text.Json;

namespace Island.Core.Activities;

/// <summary>
/// The user's say over activities (09 §5, 岛体 → 活动): an order among kinds of the same priority, what each kind may do,
/// and one kind pinned to the front. Kinds are <c>plugin/activity</c> as declared.
/// </summary>
public sealed record ActivityPreferences
{
    public IReadOnlyList<string> Order { get; init; } = [];
    public IReadOnlyDictionary<string, ActivityPolicy> Policies { get; init; } = new Dictionary<string, ActivityPolicy>();
    public string? Pinned { get; init; }

    public ActivityPolicy PolicyOf(string kind) => Policies.TryGetValue(kind, out var p) ? p : ActivityPolicy.Allow;

    /// <summary>Whether an alert of this kind may show (everything but <see cref="ActivityPolicy.Off"/>).</summary>
    public bool MayAlert(string kind) => PolicyOf(kind) != ActivityPolicy.Off;

    public ActivityPreferences WithPolicy(string kind, ActivityPolicy policy) =>
        this with { Policies = new Dictionary<string, ActivityPolicy>(Policies, StringComparer.Ordinal) { [kind] = policy } };
}

/// <summary>Keeps <see cref="ActivityPreferences"/> in <c>activities.json</c> and tells listeners when they change.</summary>
public sealed class ActivityPreferencesStore
{
    private readonly string _path;

    public ActivityPreferencesStore(string path)
    {
        _path = path;
        Current = Load(path);
    }

    public ActivityPreferences Current { get; private set; }

    public event Action<ActivityPreferences>? Changed;

    public void Set(ActivityPreferences preferences)
    {
        Current = preferences;
        Save();
        Changed?.Invoke(preferences);
    }

    private static ActivityPreferences Load(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            var r = doc.RootElement;
            var order = r.TryGetProperty("order", out var o) && o.ValueKind == JsonValueKind.Array
                ? o.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
                : [];
            var policies = new Dictionary<string, ActivityPolicy>(StringComparer.Ordinal);
            if (r.TryGetProperty("policies", out var ps) && ps.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in ps.EnumerateObject())
                {
                    if (p.Value.ValueKind == JsonValueKind.String && Enum.TryParse<ActivityPolicy>(p.Value.GetString(), true, out var policy))
                    {
                        policies[p.Name] = policy;
                    }
                }
            }
            var pinned = r.TryGetProperty("pinned", out var pn) && pn.ValueKind == JsonValueKind.String ? pn.GetString() : null;
            return new ActivityPreferences { Order = order, Policies = policies, Pinned = pinned };
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return new ActivityPreferences();
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
                foreach (var kind in Current.Order)
                {
                    w.WriteStringValue(kind);
                }
                w.WriteEndArray();
                w.WriteStartObject("policies");
                foreach (var (kind, policy) in Current.Policies)
                {
                    w.WriteString(kind, policy switch
                    {
                        ActivityPolicy.AlertOnly => "alertOnly",
                        ActivityPolicy.Off => "off",
                        _ => "allow",
                    });
                }
                w.WriteEndObject();
                if (Current.Pinned is { } pinned)
                {
                    w.WriteString("pinned", pinned);
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path, buffer.ToArray());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Not saved: the choice still holds for this session.
        }
    }
}
