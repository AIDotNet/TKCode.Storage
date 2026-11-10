using System.Diagnostics;
using System.Text;

namespace Island.Platform.Windows;

/// <summary>
/// Reads other apps' toast notifications through the system's <c>UserNotificationListener</c>, driven from one long-lived
/// PowerShell while some plugin listens (the app avoids a WinRT projection: risk R5, as for media). It only observes. The
/// system asks the user once whether O_O may read notifications (Settings → Privacy → Notifications); until they allow
/// it, <see cref="Access"/> is <see cref="NotificationAccess.NeedsPermission"/>. Windows has no readable unread counts.
/// </summary>
public sealed class WindowsNotificationListener : INotificationListener
{
    // Output is one JSON object per line. Only notifications that appear after the script started are reported.
    private const string Script = """
        $ErrorActionPreference = 'Stop'
        [Console]::OutputEncoding = [Text.Encoding]::UTF8
        Add-Type -AssemblyName System.Runtime.WindowsRuntime
        $null = [Windows.UI.Notifications.Management.UserNotificationListener, Windows.UI.Notifications, ContentType = WindowsRuntime]
        $asTask = ([System.WindowsRuntimeSystemExtensions].GetMethods() | Where-Object { $_.Name -eq 'AsTask' -and $_.GetParameters().Count -eq 1 -and $_.GetParameters()[0].ParameterType.Name -eq 'IAsyncOperation`1' })[0]
        function Await($op, $type) { $t = $asTask.MakeGenericMethod($type).Invoke($null, @($op)); $t.Wait(-1) | Out-Null; $t.Result }
        $l = [Windows.UI.Notifications.Management.UserNotificationListener]::Current
        try { $access = Await ($l.RequestAccessAsync()) ([Windows.UI.Notifications.Management.UserNotificationListenerAccessStatus]) } catch { $access = 'Denied' }
        @{ type = 'access'; value = "$access" } | ConvertTo-Json -Compress
        if ("$access" -ne 'Allowed') { exit 0 }
        $seen = @{}
        $first = $true
        while ($true) {
          $list = Await ($l.GetNotificationsAsync([Windows.UI.Notifications.NotificationKinds]::Toast)) ([System.Collections.Generic.IReadOnlyList[Windows.UI.Notifications.UserNotification]])
          foreach ($n in $list) {
            if ($seen.ContainsKey($n.Id)) { continue }
            $seen[$n.Id] = 1
            if ($first) { continue }
            $b = $n.Notification.Visual.GetBinding([Windows.UI.Notifications.KnownNotificationBindings]::ToastGeneric)
            if ($null -eq $b) { continue }
            $texts = @($b.GetTextElements() | ForEach-Object { $_.Text })
            @{ type = 'toast'; app = $n.AppInfo.AppUserModelId; name = $n.AppInfo.DisplayInfo.DisplayName; title = $texts[0]; body = (($texts | Select-Object -Skip 1) -join "`n"); time = $n.CreationTime.ToUnixTimeMilliseconds() } | ConvertTo-Json -Compress
          }
          $first = $false
          Start-Sleep -Milliseconds 1000
        }
        """;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _nativeIds = new(StringComparer.OrdinalIgnoreCase); // canonical → AppUserModelId
    private IReadOnlyCollection<string> _watched = [];
    private Process? _reader;
    private volatile string _status = "Unknown";

    public NotificationListenerCapabilities Capabilities =>
        _status == "Allowed" ? NotificationListenerCapabilities.Content : NotificationListenerCapabilities.None;

    public NotificationAccess Access => _status switch
    {
        "Denied" or "Unspecified" => NotificationAccess.NeedsPermission,
        _ => NotificationAccess.Granted, // not asked yet: the first Watch() asks
    };

    public string? AccessHint => Access == NotificationAccess.NeedsPermission
        ? "请在 设置 → 隐私和安全性 → 通知 中允许 O_O 访问通知，才能读取其他应用的消息。"
        : null;

    public event Action<PlatformNotification>? Received;

    public event Action<string, int?>? BadgeChanged { add { } remove { } }

    public void Watch(IReadOnlyCollection<string> appIds)
    {
        lock (_gate)
        {
            _watched = [.. appIds];
            if (appIds.Count == 0)
            {
                Stop();
            }
            else if (_reader is null or { HasExited: true })
            {
                Start();
            }
        }
    }

    private void Start()
    {
        try
        {
            var info = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            var process = Process.Start(info);
            if (process is null)
            {
                return;
            }
            _reader = process;
            new Thread(() => Read(process)) { IsBackground = true, Name = "windows-notifications" }.Start();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            _reader = null;
        }
    }

    private void Stop()
    {
        try
        {
            _reader?.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
        _reader?.Dispose();
        _reader = null;
    }

    private void Read(Process process)
    {
        try
        {
            string? line;
            while ((line = process.StandardOutput.ReadLine()) is not null)
            {
                switch (ToastLine.Parse(line))
                {
                    case ToastLine.Access a:
                        _status = a.Status;
                        break;
                    case ToastLine.Toast t:
                        Handle(t);
                        break;
                }
            }
        }
        catch (Exception e) when (e is IOException or InvalidOperationException)
        {
            // Stopped by Watch(), or PowerShell went away.
        }
    }

    private void Handle(ToastLine.Toast t)
    {
        var known = KnownApps.Resolve(t.App, t.Name);
        var id = known?.Id ?? t.App;
        IReadOnlyCollection<string> watched;
        lock (_gate)
        {
            watched = _watched;
            _nativeIds[id] = t.App;
        }
        if (!KnownApps.IsWatched(watched, id, t.App))
        {
            return;
        }
        try
        {
            Received?.Invoke(new PlatformNotification(id, known?.Name ?? t.Name, t.Title, null, t.Body, null, t.Time));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
        }
    }

    public int? Unread(string appId) => null;

    // The letter badge stands in until app icons are read from the installed program.
    public byte[]? GetAppIcon(string appId, int size) => null;

    public bool Activate(string appId)
    {
        string? native;
        lock (_gate)
        {
            _nativeIds.TryGetValue(appId, out native);
        }
        native ??= appId;
        try
        {
            var info = new ProcessStartInfo("explorer.exe") { UseShellExecute = false };
            info.ArgumentList.Add("shell:AppsFolder\\" + native);
            using var p = Process.Start(info);
            return p is not null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    public void OpenAccessSettings()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ms-settings:privacy-notifications") { UseShellExecute = true });
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            Stop();
        }
    }
}
