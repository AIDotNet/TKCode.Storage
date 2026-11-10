using System.Text.Json;
using Island.Platform;

namespace Island.Plugins.Host.Services;

/// <summary>
/// Sits between the platform's notification reader and the plugins that hold <c>notifications.listen</c>
/// (docs/design/08-notifications-and-satellites.md). It tells the platform which apps to watch (the union of what running
/// plugins may see), keeps the last few notifications in memory only, and cuts every notification to what the user's
/// "消息显示" choice allows <em>before</em> any plugin sees it.
/// </summary>
public sealed class NotificationHub : IDisposable
{
    public const int MaxRecent = 50;

    private readonly INotificationListener _listener;
    private readonly NotificationPreferences _prefs;
    private readonly Lock _gate = new();
    private readonly List<Subscription> _subscriptions = [];
    private readonly LinkedList<Stored> _recent = new();
    private long _next;

    public NotificationHub(INotificationListener listener, NotificationPreferences prefs)
    {
        _listener = listener;
        _prefs = prefs;
        _listener.Received += OnReceived;
        _listener.BadgeChanged += OnBadge;
    }

    public INotificationListener Listener => _listener;

    /// <summary>One plugin's interest: the apps it may read and the apps whose unread count it may read.</summary>
    public IDisposable Subscribe(
        IReadOnlySet<string> apps,
        IReadOnlySet<string> badgeApps,
        Action<string> onNotification,
        Action<string, int?> onBadge)
    {
        var sub = new Subscription(this, apps, badgeApps, onNotification, onBadge);
        lock (_gate)
        {
            _subscriptions.Add(sub);
        }
        Rewatch();
        return sub;
    }

    private void Rewatch()
    {
        string[] all;
        lock (_gate)
        {
            all = [.. _subscriptions.SelectMany(s => s.Apps.Concat(s.BadgeApps)).Distinct(StringComparer.Ordinal)];
        }
        try
        {
            _listener.Watch(all);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A platform failure must not take plugins down; they simply get nothing.
        }
    }

    private sealed record Stored(string Id, PlatformNotification Notification);

    private void OnReceived(PlatformNotification n)
    {
        Subscription[] targets;
        string id;
        lock (_gate)
        {
            id = "n" + ++_next;
            _recent.AddLast(new Stored(id, n));
            while (_recent.Count > MaxRecent)
            {
                _recent.RemoveFirst();
            }
            targets = [.. _subscriptions.Where(s => s.Apps.Contains(n.AppId))];
        }
        if (targets.Length == 0)
        {
            return;
        }
        var json = ToJson(id, n, _prefs.For(n.AppId));
        foreach (var t in targets)
        {
            t.OnNotification(json);
        }
    }

    private void OnBadge(string appId, int? count)
    {
        Subscription[] targets;
        lock (_gate)
        {
            targets = [.. _subscriptions.Where(s => s.Apps.Contains(appId) || s.BadgeApps.Contains(appId))];
        }
        foreach (var t in targets)
        {
            t.OnBadge(appId, count);
        }
    }

    /// <summary>The recent notifications of <paramref name="apps"/> (newest last) as a JSON array, cut to the user's choice.</summary>
    public string List(IReadOnlySet<string> apps, string? app, int limit)
    {
        Stored[] items;
        lock (_gate)
        {
            items = [.. _recent.Where(s => apps.Contains(s.Notification.AppId) && (app is null || s.Notification.AppId == app))];
        }
        if (items.Length > limit)
        {
            items = items[^limit..];
        }
        return "[" + string.Join(",", items.Select(s => ToJson(s.Id, s.Notification, _prefs.For(s.Notification.AppId)))) + "]";
    }

    /// <summary>Unread count of <paramref name="appId"/>, or null.</summary>
    public int? Unread(string appId)
    {
        try
        {
            return _listener.Unread(appId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return null;
        }
    }

    /// <summary>Brings the app a recent notification came from to the front; <paramref name="allowed"/> limits which apps.</summary>
    public bool Activate(string id, Func<string, bool> allowed)
    {
        string? appId;
        lock (_gate)
        {
            appId = _recent.FirstOrDefault(s => s.Id == id)?.Notification.AppId;
        }
        return appId is not null && allowed(appId) && _listener.Activate(appId);
    }

    /// <summary>Brings <paramref name="appId"/> to the front; the caller has checked the plugin may see it.</summary>
    public bool ActivateApp(string appId)
    {
        try
        {
            return _listener.Activate(appId);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return false;
        }
    }

    /// <summary>The access state and capabilities as JSON for <c>notifications.status()</c>.</summary>
    public string Status()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("granted", _listener.Access == NotificationAccess.Granted);
            w.WriteString("access", _listener.Access.ToString());
            if (_listener.AccessHint is { } hint)
            {
                w.WriteString("hint", hint);
            }
            w.WriteBoolean("content", _listener.Capabilities.HasFlag(NotificationListenerCapabilities.Content));
            w.WriteBoolean("badge", _listener.Capabilities.HasFlag(NotificationListenerCapabilities.Badge));
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>The notification as plugins see it: the fields the user allows, nothing else.</summary>
    internal static string ToJson(string id, PlatformNotification n, NotificationDisplay display)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("id", id);
            w.WriteStartObject("app");
            w.WriteString("id", n.AppId);
            w.WriteString("name", n.AppName);
            w.WriteEndObject();
            Nullable(w, "title", display == NotificationDisplay.Hidden ? null : n.Title);
            Nullable(w, "subtitle", display == NotificationDisplay.Hidden ? null : n.Subtitle);
            Nullable(w, "body", display == NotificationDisplay.Full ? n.Body : null);
            Nullable(w, "threadId", display == NotificationDisplay.Hidden ? null : n.ThreadId);
            w.WriteNumber("time", n.Time.ToUnixTimeMilliseconds());
            w.WriteString("source", "notification");
            w.WriteEndObject();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static void Nullable(Utf8JsonWriter w, string name, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            w.WriteNull(name);
        }
        else
        {
            w.WriteString(name, value.Length > 2000 ? value[..2000] + "…" : value);
        }
    }

    public void Dispose()
    {
        _listener.Received -= OnReceived;
        _listener.BadgeChanged -= OnBadge;
        try
        {
            _listener.Watch([]);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    private sealed class Subscription(
        NotificationHub hub,
        IReadOnlySet<string> apps,
        IReadOnlySet<string> badgeApps,
        Action<string> onNotification,
        Action<string, int?> onBadge) : IDisposable
    {
        public IReadOnlySet<string> Apps => apps;
        public IReadOnlySet<string> BadgeApps => badgeApps;
        public void OnNotification(string json) => onNotification(json);
        public void OnBadge(string appId, int? count) => onBadge(appId, count);

        public void Dispose()
        {
            lock (hub._gate)
            {
                hub._subscriptions.Remove(this);
            }
            hub.Rewatch();
        }
    }
}
