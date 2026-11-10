using System.Text.Json;
using Island.Plugins.Host.Services;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>
/// The <c>notifications.listen</c> capability (docs/design/08-notifications-and-satellites.md §3): notifications of the apps the
/// plugin holds a permission for, cut by the host to what the user allows, and the satellite islands that show them.
/// </summary>
internal sealed partial class PluginRunner
{
    private IDisposable? _notificationSubscription;
    private readonly HashSet<(string Satellite, string Key)> _satelliteNotices = [];
    private readonly Queue<DateTimeOffset> _satelliteShows = new();

    private bool HasListen => _permissions.ListenApps.Count > 0 || _permissions.BadgeApps.Count > 0;

    private void SubscribeNotifications()
    {
        if (!HasListen || _services.NotificationHub is not { } hub)
        {
            return;
        }
        var generation = () => Volatile.Read(ref _generation);
        _notificationSubscription = hub.Subscribe(
            _permissions.ListenApps,
            _permissions.BadgeApps,
            json =>
            {
                var g = generation();
                Post(() =>
                {
                    if (g == _generation)
                    {
                        Call("notify", 0, json);
                    }
                });
            },
            (appId, count) =>
            {
                var g = generation();
                var payload = $"{{\"app\":{JsonSerializer.Serialize(appId, HostJsonContext.Default.String)},\"count\":{(count is { } c ? c.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null")}}}";
                Post(() =>
                {
                    if (g == _generation)
                    {
                        Call("badge", 0, payload);
                    }
                });
            });
    }

    private void UnsubscribeNotifications()
    {
        _notificationSubscription?.Dispose();
        _notificationSubscription = null;
    }

    /// <summary><c>notifications.list</c> / <c>.unread</c> / <c>.activate</c> / <c>.status</c>.</summary>
    private Func<CancellationToken, Task<string>> NotificationsCall(string op, string payload, ref JsCallContext c)
    {
        if (!HasListen)
        {
            throw c.TypeError("This plugin has no notifications.listen permission.");
        }
        var hub = _services.NotificationHub;
        if (hub is null)
        {
            return op == "notifications.status"
                ? _ => Task.FromResult("{\"granted\":false,\"access\":\"Unsupported\",\"content\":false,\"badge\":false}")
                : throw c.TypeError("Notifications are not available here.");
        }
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        string? Str(string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        switch (op)
        {
            case "notifications.status":
                return _ => Task.FromResult(hub.Status());
            case "notifications.list":
            {
                var app = Str("app");
                if (app is not null && !_permissions.ListenApps.Contains(app))
                {
                    throw c.TypeError($"This plugin may not read notifications of '{app}'.");
                }
                var limit = r.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, NotificationHub.MaxRecent) : 20;
                return _ => Task.FromResult(hub.List(_permissions.ListenApps, app, limit));
            }
            case "notifications.unread":
            {
                var app = Str("app") ?? throw c.TypeError("notifications.unread needs an app id.");
                if (!_permissions.CanSeeApp(app))
                {
                    throw c.TypeError($"This plugin may not read the unread count of '{app}'.");
                }
                return _ => Task.Run(() => hub.Unread(app) is { } count ? count.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null");
            }
            default: // notifications.activate
            {
                // By app: for a count read from the Dock badge, with no notification behind it this session.
                if (Str("app") is { } app)
                {
                    if (!_permissions.CanSeeApp(app))
                    {
                        throw c.TypeError($"This plugin may not open '{app}'.");
                    }
                    return _ => Task.Run(() => hub.ActivateApp(app) ? "true" : "false");
                }
                var id = Str("id") ?? throw c.TypeError("notifications.activate needs a notification id or an app id.");
                return _ => Task.Run(() => hub.Activate(id, _permissions.ListenApps.Contains) ? "true" : "false");
            }
        }
    }

    // ---- satellites ---------------------------------------------------------------------------------------------

    private const int MaxSatelliteShowsPerMinute = 30;
    private const int MaxSatelliteNotices = 8;

    /// <summary>
    /// <c>satellite</c> / <c>satelliteEnd</c>: (op, satellite id, notice key, options JSON { seconds, badge }, view JSON). The
    /// satellite must be declared in plugin.json. Shows beyond the rate limit are dropped, not errors.
    /// </summary>
    private JsValue Satellite(ref JsCallContext c, bool end, string satelliteId)
    {
        if (_package.Manifest.Contributes.Satellites.FirstOrDefault(x => x.Id == satelliteId) is not { } declared)
        {
            throw c.TypeError($"Satellite '{satelliteId}' is not declared in plugin.json (contributes.satellites).");
        }
        var key = Arg(ref c, 2);
        if (key.Length > 64)
        {
            throw c.TypeError("A satellite notice key is at most 64 characters.");
        }
        var side = declared.Side is "left" or "right" ? declared.Side : "auto";
        if (end)
        {
            if (key.Length == 0)
            {
                foreach (var k in _satelliteNotices.Where(n => n.Satellite == satelliteId).ToList())
                {
                    _satelliteNotices.Remove(k);
                }
            }
            else if (!_satelliteNotices.Remove((satelliteId, key)))
            {
                return JsValue.Undefined;
            }
            _sink.Satellite(new SatelliteSnapshot(Id, satelliteId, key, null, TimeSpan.Zero, null, side, ++_sequence));
            return JsValue.Undefined;
        }
        var now = DateTimeOffset.UtcNow;
        while (_satelliteShows.Count > 0 && now - _satelliteShows.Peek() > TimeSpan.FromMinutes(1))
        {
            _satelliteShows.Dequeue();
        }
        if (_satelliteShows.Count >= MaxSatelliteShowsPerMinute)
        {
            return JsValue.From(false);
        }
        if (!_satelliteNotices.Contains((satelliteId, key)) && _satelliteNotices.Count(n => n.Satellite == satelliteId) >= MaxSatelliteNotices)
        {
            return JsValue.From(false);
        }
        VNode? view;
        try
        {
            view = VNodeJson.ParseView(Arg(ref c, 4)).Expanded;
        }
        catch (InvalidDataException e)
        {
            throw c.TypeError($"Invalid view: {e.Message}");
        }
        double seconds = 4;
        int? badge = null;
        try
        {
            using var doc = JsonDocument.Parse(Arg(ref c, 3) is { Length: > 0 } o ? o : "{}");
            if (doc.RootElement.TryGetProperty("seconds", out var s) && s.TryGetDouble(out var sv) && double.IsFinite(sv))
            {
                seconds = sv <= 0 ? 0 : Math.Clamp(sv, 1, 10); // 0: stays until hidden
            }
            if (doc.RootElement.TryGetProperty("badge", out var b) && b.TryGetInt32(out var bv))
            {
                badge = Math.Clamp(bv, 0, 999);
            }
        }
        catch (JsonException)
        {
            // Options are advisory: defaults apply.
        }
        _satelliteShows.Enqueue(now);
        _satelliteNotices.Add((satelliteId, key));
        _sink.Satellite(new SatelliteSnapshot(Id, satelliteId, key, view, TimeSpan.FromSeconds(seconds), badge, side, ++_sequence));
        return JsValue.From(true);
    }

    /// <summary>The host takes a notice away (it timed out); the plugin may show the same key again.</summary>
    private void TearDownSatellites()
    {
        foreach (var (satellite, key) in _satelliteNotices)
        {
            _sink.Satellite(new SatelliteSnapshot(Id, satellite, key, null, TimeSpan.Zero, null, "auto", ++_sequence));
        }
        _satelliteNotices.Clear();
    }
}
