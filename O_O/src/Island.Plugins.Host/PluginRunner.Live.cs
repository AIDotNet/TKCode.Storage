using System.Text.Json;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>
/// The 2.0 island API (docs/design/09-island-v2.md §3): live activities with fixed regions, alerts, ambient items.
/// Calls arrive as <c>__island_activity(op, id, instance, options JSON, view JSON)</c>.
/// </summary>
internal sealed partial class PluginRunner
{
    private const int MaxAmbient = 8;
    private const int MaxAlertsPerMinute = 30;

    private readonly HashSet<(string Id, string? Instance)> _live = [];
    private readonly HashSet<(string Id, string? Instance)> _ambient = [];
    private readonly Queue<DateTime> _alertTimes = new();

    private static bool IsLiveOp(string op) => op is "live" or "liveEnd" or "liveAlert" or "ambient" or "ambientEnd";

    private JsValue Live(ref JsCallContext c, string op, string id)
    {
        var instance = Arg(ref c, 2) is { Length: > 0 } i ? i : null;
        switch (op)
        {
            case "live":
                return LiveUpdate(ref c, id, instance);
            case "liveEnd":
                LiveEnd(id, instance);
                return JsValue.Undefined;
            case "liveAlert":
                return LiveAlert(ref c, id, instance);
            case "ambient":
                return Ambient(ref c, id, end: false);
            default:
                return Ambient(ref c, id, end: true);
        }
    }

    private ActivityContribution Declared(ref JsCallContext c, string id, string? instance)
    {
        if (_package.Manifest.Contributes.Activities.FirstOrDefault(a => a.Id == id) is not { } declared)
        {
            throw c.TypeError($"Activity '{id}' is not declared in plugin.json (contributes.activities).");
        }
        if (declared.Multiple != (instance is not null))
        {
            throw c.TypeError(declared.Multiple
                ? $"Activity '{id}' is declared multiple: give an instance."
                : $"Activity '{id}' is not declared multiple: give no instance.");
        }
        if (instance is { Length: > 64 })
        {
            throw c.TypeError("An activity instance id is at most 64 characters.");
        }
        return declared;
    }

    private JsValue LiveUpdate(ref JsCallContext c, string id, string? instance)
    {
        var declared = Declared(ref c, id, instance);
        var key = (id, instance);
        if (!_live.Contains(key) && _live.Count >= _options.MaxActivities)
        {
            throw c.TypeError($"A plugin may show at most {_options.MaxActivities} activities at once.");
        }
        var lastKey = "live\u0001" + id + "\u0001" + instance;
        var args = string.Concat(Arg(ref c, 3), "\u0001", Arg(ref c, 4));
        if (_live.Contains(key) && Unchanged(lastKey, args))
        {
            return JsValue.Undefined;
        }
        LiveActivityView view;
        try
        {
            view = RegionJson.ParseActivity(Arg(ref c, 4));
        }
        catch (InvalidDataException e)
        {
            throw c.TypeError($"Invalid activity view: {e.Message}");
        }
        var options = Options(ref c);
        var priority = Priority(Str(options, "priority") ?? declared.Priority);
        var accent = Str(options, "accent") ?? declared.Accent;
        LiveActivityOpen? open = options.TryGetValue("open", out var o) && o.ValueKind == JsonValueKind.Object
            ? new LiveActivityOpen(Str(o, "app"), Str(o, "url"))
            : null;
        _live.Add(key);
        _lastViews[lastKey] = args;
        _sink.LiveActivityUpdated(new LiveActivitySnapshot(Id, id, instance, Str(options, "title") ?? declared.Title ?? id,
            priority, accent, open, view, ++_sequence));
        return JsValue.Undefined;
    }

    private void LiveEnd(string id, string? instance)
    {
        _lastViews.Remove("live\u0001" + id + "\u0001" + instance);
        if (_live.Remove((id, instance)))
        {
            _sink.LiveActivityEnded(Id, instance is null ? $"{Id}/{id}" : $"{Id}/{id}#{instance}");
        }
    }

    /// <summary>An alert: of a running activity (<paramref name="id"/> given) or a one-off one (id empty).</summary>
    private JsValue LiveAlert(ref JsCallContext c, string id, string? instance)
    {
        string? activityKey = null;
        var declaredPriority = (string?)null;
        if (id.Length > 0)
        {
            declaredPriority = Declared(ref c, id, instance).Priority;
            activityKey = instance is null ? $"{Id}/{id}" : $"{Id}/{id}#{instance}";
        }
        var now = DateTime.UtcNow;
        while (_alertTimes.Count > 0 && now - _alertTimes.Peek() > TimeSpan.FromMinutes(1))
        {
            _alertTimes.Dequeue();
        }
        if (_alertTimes.Count >= MaxAlertsPerMinute)
        {
            return JsValue.False; // over the limit: dropped, and the plugin learns it
        }
        RegionView view;
        try
        {
            view = RegionJson.ParseAlert(Arg(ref c, 4));
        }
        catch (InvalidDataException e)
        {
            throw c.TypeError($"Invalid alert: {e.Message}");
        }
        var options = Options(ref c);
        var seconds = options.TryGetValue("seconds", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetDouble(out var sec) && double.IsFinite(sec) ? sec : 3;
        _alertTimes.Enqueue(now);
        _sink.LiveAlert(new LiveAlertSnapshot(Id, activityKey, Priority(Str(options, "priority") ?? declaredPriority),
            view, TimeSpan.FromSeconds(Math.Clamp(seconds, 1, 10)), ++_sequence));
        return JsValue.True;
    }

    private JsValue Ambient(ref JsCallContext c, string id, bool end)
    {
        if (_package.Manifest.Contributes.Ambient.FirstOrDefault(a => a.Id == id) is not { } declared)
        {
            throw c.TypeError($"Ambient item '{id}' is not declared in plugin.json (contributes.ambient).");
        }
        var instance = Arg(ref c, 2) is { Length: > 0 } i ? i : null;
        if (declared.Multiple != (instance is not null))
        {
            throw c.TypeError(declared.Multiple
                ? $"Ambient item '{id}' is declared multiple: give an instance."
                : $"Ambient item '{id}' is not declared multiple: give no instance.");
        }
        if (instance is { Length: > 64 })
        {
            throw c.TypeError("An ambient item instance id is at most 64 characters.");
        }
        var side = declared.Side == "left" ? "left" : "right";
        var key = (id, instance);
        var lastKey = "ambient\u0001" + id + "\u0001" + instance;
        if (end)
        {
            _lastViews.Remove(lastKey);
            if (_ambient.Remove(key))
            {
                _sink.AmbientUpdated(new AmbientSnapshot(Id, id, side, null, ++_sequence, instance));
            }
            return JsValue.Undefined;
        }
        if (!_ambient.Contains(key) && _ambient.Count >= MaxAmbient)
        {
            throw c.TypeError($"A plugin may show at most {MaxAmbient} ambient items at once.");
        }
        var args = string.Concat(Arg(ref c, 3), "\u0001", Arg(ref c, 4));
        if (_ambient.Contains(key) && Unchanged(lastKey, args))
        {
            return JsValue.Undefined;
        }
        VNode view;
        try
        {
            view = VNodeJson.ParseNode(Arg(ref c, 4));
        }
        catch (InvalidDataException e)
        {
            throw c.TypeError($"Invalid ambient view: {e.Message}");
        }
        _ambient.Add(key);
        _lastViews[lastKey] = args;
        var title = Str(Options(ref c), "title") ?? declared.Title ?? id;
        _sink.AmbientUpdated(new AmbientSnapshot(Id, id, side, view, ++_sequence, instance, title, declared.Enabled ?? true));
        return JsValue.Undefined;
    }

    /// <summary>Ends every live activity and ambient item when the plugin stops.</summary>
    private void TearDownLive()
    {
        foreach (var (id, instance) in _live)
        {
            _sink.LiveActivityEnded(Id, instance is null ? $"{Id}/{id}" : $"{Id}/{id}#{instance}");
        }
        _live.Clear();
        foreach (var (id, instance) in _ambient)
        {
            var side = _package.Manifest.Contributes.Ambient.FirstOrDefault(a => a.Id == id)?.Side == "left" ? "left" : "right";
            _sink.AmbientUpdated(new AmbientSnapshot(Id, id, side, null, ++_sequence, instance));
        }
        _ambient.Clear();
        _alertTimes.Clear();
    }

    private static LivePriority Priority(string? name) => name switch
    {
        "passive" => LivePriority.Passive,
        "high" => LivePriority.High,
        "critical" => LivePriority.Critical,
        _ => LivePriority.Normal,
    };

    /// <summary>The options object (argument 3) as properties; empty when missing or not an object.</summary>
    private static Dictionary<string, JsonElement> Options(ref JsCallContext c)
    {
        var json = Arg(ref c, 3);
        if (json.Length == 0)
        {
            return [];
        }
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                ? doc.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal)
                : [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static string? Str(Dictionary<string, JsonElement> o, string name) =>
        o.TryGetValue(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;

    private static string? Str(JsonElement o, string name) =>
        o.TryGetProperty(name, out var e) && e.ValueKind == JsonValueKind.String && e.GetString() is { Length: > 0 } s ? s : null;
}
