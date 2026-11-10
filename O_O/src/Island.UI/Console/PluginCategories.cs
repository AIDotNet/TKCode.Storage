using Island.Plugins;

namespace Island.UI.Console;

/// <summary>
/// The groups of the plugin list and the filters of the plugin market. Worked out from what a plugin is (its id, what
/// it requires, its permissions, what it contributes), so catalog entries — which carry no category — group the same
/// way as installed plugins.
/// </summary>
public static class PluginCategories
{
    public const string All = "all";

    /// <summary>In display order; <see cref="All"/> is only a filter.</summary>
    public static readonly (string Key, string Label)[] Filters =
    [
        (All, "全部"), ("ai", "AI"), ("calendar", "日历"), ("productivity", "效率"), ("media", "媒体"),
        ("message", "消息"), ("look", "外观"), ("other", "其他"),
    ];

    private const string AiUsage = "com.o_o.ai-usage";
    private const string Calendar = "com.o_o.calendar";

    public static string Label(string key) => Filters.FirstOrDefault(f => f.Key == key).Label ?? "其他";

    public static int Order(string key) => Array.FindIndex(Filters, f => f.Key == key) is var i and >= 0 ? i : Filters.Length;

    public static string Of(PluginManifest manifest) =>
        Of(manifest.Id, manifest.Requires, manifest.Permissions,
            manifest.Contributes.Themes.Count > 0 || manifest.Contributes.Layouts.Count > 0);

    public static string Of(string id, IReadOnlyCollection<string> requires, IReadOnlyCollection<string> permissions, bool looks = false)
    {
        if (id == AiUsage || id.StartsWith("com.o_o.ai.", StringComparison.Ordinal) || requires.Contains(AiUsage))
        {
            return "ai";
        }
        if (id == Calendar || id.StartsWith("com.o_o.cal.", StringComparison.Ordinal) || requires.Contains(Calendar))
        {
            return "calendar";
        }
        if (looks || id.StartsWith("com.o_o.theme.", StringComparison.Ordinal) || id.StartsWith("com.o_o.layout.", StringComparison.Ordinal))
        {
            return "look";
        }
        if (id is "com.o_o.im" or "com.o_o.mail" || permissions.Any(p => p.StartsWith("notifications.listen", StringComparison.Ordinal)))
        {
            return "message";
        }
        if (id is "com.o_o.media" or "com.o_o.netease")
        {
            return "media";
        }
        return id.StartsWith("com.o_o.", StringComparison.Ordinal) ? "productivity" : "other";
    }
}
