namespace Island.Platform;

/// <summary>One notification another application posted (docs/design/08-notifications-and-satellites.md §6).</summary>
/// <param name="AppId">The source: macOS bundle id, Windows AppUserModelId, Linux desktop-entry name.</param>
/// <param name="Title">Usually the sender or the group; null when the platform cannot tell.</param>
/// <param name="Body">The message text; null when the source hides previews or the platform cannot read it.</param>
/// <param name="ThreadId">The conversation, when the platform exposes one; used to merge messages.</param>
public sealed record PlatformNotification(
    string AppId,
    string AppName,
    string? Title,
    string? Subtitle,
    string? Body,
    string? ThreadId,
    DateTimeOffset Time);

/// <summary>What a platform's listener can deliver; the host degrades gracefully for what is missing.</summary>
[Flags]
public enum NotificationListenerCapabilities
{
    None = 0,

    /// <summary>Sender and message text.</summary>
    Content = 1,

    /// <summary>Unread counts on the app's Dock or tray icon.</summary>
    Badge = 2,

    /// <summary>Real application icons through <see cref="INotificationListener.GetAppIcon"/>.</summary>
    Icon = 4,
}

public enum NotificationAccess
{
    /// <summary>Nothing is being watched, or the system lets us read what we need.</summary>
    Granted,

    /// <summary>The user has to allow it in the system's settings (<see cref="INotificationListener.AccessHint"/> says how).</summary>
    NeedsPermission,

    /// <summary>This platform offers no way to read other apps' notifications.</summary>
    Unsupported,
}

/// <summary>
/// The native half of the notification capability: reads the notifications other apps post and the unread counts on their
/// icons. Never injects into or talks to those apps. Events may come from any thread.
/// </summary>
public interface INotificationListener : IDisposable
{
    NotificationListenerCapabilities Capabilities { get; }

    /// <summary>Whether the listener can read what it needs right now; re-evaluated by <see cref="Watch"/>.</summary>
    NotificationAccess Access { get; }

    /// <summary>A short, user-facing sentence on how to grant access (shown in the console); null when granted.</summary>
    string? AccessHint { get; }

    /// <summary>Raised for each new notification of a watched app, in order. Notifications older than the watch are not replayed.</summary>
    event Action<PlatformNotification>? Received;

    /// <summary>Raised when a watched app's unread count changes (null: it has none or it cannot be known).</summary>
    event Action<string, int?>? BadgeChanged;

    /// <summary>
    /// Sets which apps are observed (replacing the previous set; empty stops all work). Called whenever the set of
    /// plugins holding the permission changes.
    /// </summary>
    void Watch(IReadOnlyCollection<string> appIds);

    /// <summary>The app's current unread count, or null when it has none or it cannot be known. May block briefly.</summary>
    int? Unread(string appId);

    /// <summary>The application's icon as PNG bytes about <paramref name="size"/> points square, or null. May block; cache the result.</summary>
    byte[]? GetAppIcon(string appId, int size);

    /// <summary>Brings the application to the front; false when it could not.</summary>
    bool Activate(string appId);

    /// <summary>Opens the system settings page where the user grants <see cref="Access"/>.</summary>
    void OpenAccessSettings();
}

/// <summary>For tests and platforms without a notification reader.</summary>
public sealed class NullNotificationListener : INotificationListener
{
    public NotificationListenerCapabilities Capabilities => NotificationListenerCapabilities.None;
    public NotificationAccess Access => NotificationAccess.Unsupported;
    public string? AccessHint => "这个系统暂不支持读取其他应用的通知。";
    public event Action<PlatformNotification>? Received { add { } remove { } }
    public event Action<string, int?>? BadgeChanged { add { } remove { } }
    public void Watch(IReadOnlyCollection<string> appIds) { }
    public int? Unread(string appId) => null;
    public byte[]? GetAppIcon(string appId, int size) => null;
    public bool Activate(string appId) => false;
    public void OpenAccessSettings() { }
    public void Dispose() { }
}
