using System.Diagnostics;
using System.Text.RegularExpressions;

namespace Island.Platform.Linux;

/// <summary>
/// Reads the notifications other apps post on the session bus (<c>org.freedesktop.Notifications.Notify</c>) by watching
/// <c>dbus-monitor</c> while some plugin listens (no D-Bus library is bundled, as with <c>playerctl</c> for media). It
/// only observes; it never talks to the apps. Unread counts on tray icons have no common interface, so there are none.
/// </summary>
public sealed partial class LinuxNotificationListener : INotificationListener
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _icons = new(StringComparer.OrdinalIgnoreCase);   // canonical id → icon name
    private readonly Dictionary<string, string> _entries = new(StringComparer.OrdinalIgnoreCase); // canonical id → desktop entry
    private IReadOnlyCollection<string> _watched = [];
    private Process? _monitor;

    public NotificationListenerCapabilities Capabilities =>
        Available ? NotificationListenerCapabilities.Content | NotificationListenerCapabilities.Icon : NotificationListenerCapabilities.None;

    public NotificationAccess Access => Available ? NotificationAccess.Granted : NotificationAccess.Unsupported;

    public string? AccessHint => Available ? null : "需要安装 dbus-monitor（通常在 dbus 或 dbus-bin 软件包里），才能读取其他应用的通知。";

    public event Action<PlatformNotification>? Received;

    public event Action<string, int?>? BadgeChanged { add { } remove { } }

    private static bool Available => FindOnPath("dbus-monitor") is not null;

    public void Watch(IReadOnlyCollection<string> appIds)
    {
        lock (_gate)
        {
            _watched = [.. appIds];
            if (appIds.Count == 0)
            {
                StopMonitor();
            }
            else if (_monitor is null or { HasExited: true })
            {
                StartMonitor();
            }
        }
    }

    private void StartMonitor()
    {
        if (FindOnPath("dbus-monitor") is not { } exe)
        {
            return;
        }
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("--session");
            info.ArgumentList.Add("type='method_call',interface='org.freedesktop.Notifications',member='Notify'");
            var process = Process.Start(info);
            if (process is null)
            {
                return;
            }
            _monitor = process;
            var thread = new Thread(() => Read(process)) { IsBackground = true, Name = "dbus-notifications" };
            thread.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _monitor = null;
        }
    }

    private void StopMonitor()
    {
        try
        {
            _monitor?.Kill();
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _monitor?.Dispose();
        _monitor = null;
    }

    private void Read(Process process)
    {
        var parser = new DbusNotifyParser();
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                if (parser.Feed(line) is { } call)
                {
                    Handle(call);
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // The monitor went away (Watch() stopped it, or the bus did).
        }
    }

    private void Handle(DbusNotify call)
    {
        var known = KnownApps.Resolve(call.DesktopEntry, call.AppName);
        var id = known?.Id ?? call.DesktopEntry ?? call.AppName;
        IReadOnlyCollection<string> watched;
        lock (_gate)
        {
            watched = _watched;
            if (call.AppIcon.Length > 0)
            {
                _icons[id] = call.AppIcon;
            }
            if (call.DesktopEntry is not null)
            {
                _entries[id] = call.DesktopEntry;
            }
        }
        if (!KnownApps.IsWatched(watched, id, call.DesktopEntry ?? call.AppName))
        {
            return;
        }
        var title = call.Summary.Length > 0 ? call.Summary : null;
        var body = StripMarkup(call.Body);
        try
        {
            Received?.Invoke(new PlatformNotification(id, known?.Name ?? call.AppName, title, null, body.Length > 0 ? body : null, null, DateTimeOffset.Now));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A subscriber's failure must not stop the monitor.
        }
    }

    /// <summary>Notification bodies may carry a little HTML (b, i, u, a, img); show the text only.</summary>
    internal static string StripMarkup(string body) =>
        System.Net.WebUtility.HtmlDecode(Tags().Replace(body, "")).Trim();

    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tags();

    public int? Unread(string appId) => null;

    public byte[]? GetAppIcon(string appId, int size)
    {
        string? name;
        lock (_gate)
        {
            _icons.TryGetValue(appId, out name);
            if (name is null && _entries.TryGetValue(appId, out var entry))
            {
                name = entry;
            }
        }
        name ??= appId;
        try
        {
            if (Path.IsPathFullyQualified(name) && name.EndsWith(".png", StringComparison.OrdinalIgnoreCase) && File.Exists(name))
            {
                return File.ReadAllBytes(name);
            }
            // PNG icons only (Avalonia bitmaps): the hicolor theme sizes nearest the one asked for, then pixmaps.
            var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var roots = new[] { "/usr/share/icons/hicolor", "/usr/local/share/icons/hicolor", Path.Combine(home, ".local/share/icons/hicolor") };
            foreach (var s in new[] { size, 48, 64, 128, 256, 32, 512, 96, 72, 24 }.Distinct())
            {
                foreach (var root in roots)
                {
                    var file = Path.Combine(root, $"{s}x{s}", "apps", name + ".png");
                    if (File.Exists(file))
                    {
                        return File.ReadAllBytes(file);
                    }
                }
            }
            var pixmap = Path.Combine("/usr/share/pixmaps", name + ".png");
            return File.Exists(pixmap) ? File.ReadAllBytes(pixmap) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public bool Activate(string appId)
    {
        string? entry;
        lock (_gate)
        {
            _entries.TryGetValue(appId, out entry);
        }
        if (entry is null || FindOnPath("gtk-launch") is not { } exe)
        {
            return false;
        }
        try
        {
            var info = new ProcessStartInfo(exe) { UseShellExecute = false, RedirectStandardError = true, RedirectStandardOutput = true };
            info.ArgumentList.Add(entry);
            using var p = Process.Start(info);
            return p is not null && p.WaitForExit(3000) && p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void OpenAccessSettings()
    {
        // Nothing to allow: the session bus can be watched by the user's own processes.
    }

    public void Dispose()
    {
        lock (_gate)
        {
            StopMonitor();
        }
    }

    private static string? FindOnPath(string name) =>
        (Environment.GetEnvironmentVariable("PATH") ?? "")
            .Split(':', StringSplitOptions.RemoveEmptyEntries)
            .Select(d => Path.Combine(d, name))
            .FirstOrDefault(File.Exists);
}
