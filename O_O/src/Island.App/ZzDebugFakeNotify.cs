// TEMP DEBUG — remove after diagnosing the satellite click.
using Island.Platform;
namespace Island.App;
internal sealed class ZzDebugFakeNotify(INotificationListener inner) : INotificationListener
{
    public NotificationListenerCapabilities Capabilities => inner.Capabilities;
    public NotificationAccess Access => inner.Access;
    public string? AccessHint => inner.AccessHint;
    public event Action<PlatformNotification>? Received;
    public event Action<string, int?>? BadgeChanged { add => inner.BadgeChanged += value; remove => inner.BadgeChanged -= value; }
    private Timer? _t;
    public void Watch(IReadOnlyCollection<string> appIds)
    {
        inner.Watch(appIds);
        inner.Received -= Fwd; inner.Received += Fwd;
        _t ??= new Timer(_ => { Console.Error.WriteLine("[zz] fake notify"); Received?.Invoke(new PlatformNotification("com.tencent.qq", "QQ", "测试", null, "点我", null, DateTimeOffset.Now)); }, null, 6000, 15000);
    }
    private void Fwd(PlatformNotification n) => Received?.Invoke(n);
    public int? Unread(string appId) => inner.Unread(appId);
    public byte[]? GetAppIcon(string appId, int size) => inner.GetAppIcon(appId, size);
    public bool Activate(string appId) { var r = inner.Activate(appId); Console.Error.WriteLine($"[zz] Activate {appId} -> {r}"); return r; }
    public void OpenAccessSettings() => inner.OpenAccessSettings();
    public void Dispose() => inner.Dispose();
}
