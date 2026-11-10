using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// <c>shell.openUrl</c>: opens a link with the system (browser, mail app, meeting app). Only web, mail and a short list
/// of meeting-app schemes — never file:, javascript: or arbitrary app schemes — and only right after the user clicked
/// something in the plugin (checked by the runner).
/// </summary>
internal static class PluginShell
{
    private static readonly HashSet<string> Schemes = new(StringComparer.OrdinalIgnoreCase)
    {
        "https", "http", "mailto",
        "tencentmeeting", "wemeet", "feishu", "lark", "dingtalk", "zoommtg", "msteams", "webex",
    };

    public static async Task<string> OpenUrl(string payload, Func<Uri, Task> open)
    {
        using var doc = JsonDocument.Parse(payload);
        var raw = doc.RootElement.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;
        if (raw is null || raw.Length > 4096 || !Uri.TryCreate(raw.Trim(), UriKind.Absolute, out var uri) || !Schemes.Contains(uri.Scheme))
        {
            throw new PluginCapabilityException("只能打开 http(s)、mailto 或会议应用的链接。");
        }
        await open(uri);
        return "true";
    }
}
