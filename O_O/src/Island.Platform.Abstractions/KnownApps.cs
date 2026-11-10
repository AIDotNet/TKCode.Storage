namespace Island.Platform;

/// <summary>
/// The messaging apps O_O knows by a canonical id (their macOS bundle id), so a plugin's
/// <c>notifications.listen:com.tencent.xinWeChat</c> also matches WeChat on Windows (its AppUserModelId) and Linux (its
/// desktop-entry name). Platform listeners translate native ids to these before raising events and when matching
/// <c>Watch()</c>; apps not in the table keep their native id.
/// </summary>
public static class KnownApps
{
    private sealed record Entry(string Id, string Name, string[] Exact, string[] Contains);

    private static readonly Entry[] Table =
    [
        new("com.tencent.xinWeChat", "微信", ["wechat", "weixin", "com.tencent.wechat", "com.tencent.xinwechat", "wechat.exe", "weixin.exe"], ["wechat", "weixin"]),
        new("com.tencent.qq", "QQ", ["qq", "qq.exe", "com.tencent.qq", "tencent.qq", "com.qq.qq", "linuxqq"], []),
        new("com.tencent.WeWorkMac", "企业微信", ["wxwork", "wecom", "com.tencent.weworkmac", "wxwork.exe"], ["wxwork", "wecom"]),
        new("ru.keepcoder.Telegram", "Telegram", ["telegram", "org.telegram.desktop", "telegram.exe", "ru.keepcoder.telegram"], ["telegram"]),
    ];

    /// <summary>The canonical id and display name for a native source id or application name, or null when unknown.</summary>
    public static (string Id, string Name)? Resolve(params string?[] candidates)
    {
        foreach (var raw in candidates)
        {
            if (string.IsNullOrWhiteSpace(raw))
            {
                continue;
            }
            var s = raw.Trim().ToLowerInvariant();
            if (s.EndsWith(".desktop", StringComparison.Ordinal))
            {
                s = s[..^".desktop".Length];
            }
            foreach (var e in Table)
            {
                if (e.Exact.Contains(s, StringComparer.Ordinal) || e.Contains.Any(c => s.Contains(c, StringComparison.Ordinal)))
                {
                    return (e.Id, e.Name);
                }
            }
        }
        return null;
    }

    /// <summary>The display name of a canonical id, or null.</summary>
    public static string? NameOf(string canonicalId) =>
        Table.FirstOrDefault(e => string.Equals(e.Id, canonicalId, StringComparison.OrdinalIgnoreCase))?.Name;

    /// <summary>Whether <paramref name="watched"/> (canonical ids and native ids) includes the source given by <paramref name="canonical"/> or <paramref name="native"/>.</summary>
    public static bool IsWatched(IReadOnlyCollection<string> watched, string canonical, string? native) =>
        watched.Contains(canonical, StringComparer.OrdinalIgnoreCase) ||
        (native is not null && watched.Contains(native, StringComparer.OrdinalIgnoreCase));
}
