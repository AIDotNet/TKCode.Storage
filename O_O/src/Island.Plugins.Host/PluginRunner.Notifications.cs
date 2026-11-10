using System.Text.Json;
using Island.Plugins.Host.Services;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>
/// The <c>notifications.listen</c> capability (docs/design/08-notifications-and-satellites.md §3): notifications of the apps the
/// plugin holds a permission for, cut by the host to what the user allows (shown with island.alert and island.activity).
/// </summary>
internal sealed partial class PluginRunner
{
    private IDisposable? _notificationSubscription;

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
}
