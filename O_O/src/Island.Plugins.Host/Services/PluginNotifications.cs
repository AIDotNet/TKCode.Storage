using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>Shows a system notification (Notification Center, Windows toast, the Linux desktop's notifications).</summary>
public interface IPluginNotifier
{
    Task ShowAsync(string title, string body, CancellationToken ct);
}

/// <summary>
/// The default notifier, through the system's own tools: <c>osascript</c> on macOS, PowerShell on Windows,
/// <c>notify-send</c> on Linux. Text is passed as arguments or environment variables, never spliced into a script.
/// </summary>
public sealed class SystemNotifier : IPluginNotifier
{
    public async Task ShowAsync(string title, string body, CancellationToken ct)
    {
        ProcessStartInfo info;
        if (OperatingSystem.IsMacOS())
        {
            info = new ProcessStartInfo("osascript") { ArgumentList =
            {
                "-e", "on run argv", "-e", "display notification (item 2 of argv) with title (item 1 of argv)", "-e", "end run",
                title, body,
            } };
        }
        else if (OperatingSystem.IsWindows())
        {
            // A toast through the WinRT notification API, with the text from environment variables (no quoting issues).
            const string Script = """
                [Windows.UI.Notifications.ToastNotificationManager, Windows.UI.Notifications, ContentType = WindowsRuntime] | Out-Null
                $x = [Windows.UI.Notifications.ToastNotificationManager]::GetTemplateContent([Windows.UI.Notifications.ToastTemplateType]::ToastText02)
                $t = $x.GetElementsByTagName('text')
                $t.Item(0).AppendChild($x.CreateTextNode($env:O_O_TITLE)) | Out-Null
                $t.Item(1).AppendChild($x.CreateTextNode($env:O_O_BODY)) | Out-Null
                [Windows.UI.Notifications.ToastNotificationManager]::CreateToastNotifier('O_O').Show([Windows.UI.Notifications.ToastNotification]::new($x))
                """;
            info = new ProcessStartInfo("powershell.exe")
            {
                ArgumentList = { "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script)) },
                Environment = { ["O_O_TITLE"] = title, ["O_O_BODY"] = body },
                CreateNoWindow = true,
            };
        }
        else
        {
            info = new ProcessStartInfo("notify-send") { ArgumentList = { "--app-name=O_O", title, body } };
        }
        info.UseShellExecute = false;
        info.RedirectStandardError = true;
        info.RedirectStandardOutput = true;
        try
        {
            using var process = Process.Start(info) ?? throw new PluginCapabilityException("无法显示系统通知。");
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new PluginCapabilityException("这台电脑上没有可用的系统通知工具。");
        }
        catch (TimeoutException)
        {
            // Shown or not, the notifier did not answer in time: nothing more to do.
        }
    }
}

/// <summary>One plugin's notifications: the plugin's name as the title prefix, and a rate limit.</summary>
internal sealed class PluginNotifications(string pluginName, IPluginNotifier notifier, Func<DateTimeOffset>? clock = null)
{
    public static readonly TimeSpan MinInterval = TimeSpan.FromSeconds(10);
    public const int MaxPerHour = 20;

    private readonly Queue<DateTimeOffset> _sent = new();
    private readonly Lock _gate = new();

    public async Task<string> ShowAsync(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        var title = Text(r, "title", 80);
        var body = Text(r, "body", 300);
        if (title.Length == 0 && body.Length == 0)
        {
            throw new PluginCapabilityException("通知需要 title 或 body。");
        }
        var now = (clock ?? (() => DateTimeOffset.UtcNow))();
        lock (_gate)
        {
            while (_sent.Count > 0 && now - _sent.Peek() > TimeSpan.FromHours(1))
            {
                _sent.Dequeue();
            }
            if (_sent.Count > 0 && now - _sent.Last() < MinInterval)
            {
                throw new PluginCapabilityException("通知太频繁：同一个插件 10 秒内只能发一条。");
            }
            if (_sent.Count >= MaxPerHour)
            {
                throw new PluginCapabilityException($"通知太多：同一个插件每小时最多 {MaxPerHour} 条。");
            }
            _sent.Enqueue(now);
        }
        // The plugin's name always leads, so a notification cannot pretend to come from something else.
        await notifier.ShowAsync(title.Length == 0 ? pluginName : $"{pluginName} · {title}", body, ct);
        return "true";
    }

    private static string Text(JsonElement r, string name, int max)
    {
        var s = r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
        s = new string([.. s.Where(c => !char.IsControl(c) || c == '\n')]);
        return s.Length > max ? s[..max] + "…" : s;
    }
}
