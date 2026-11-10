using Island.Platform;
using Island.Platform.Linux;
using Island.Platform.Windows;

namespace Island.Platform.Tests;

public sealed class CrossPlatformNotificationTests
{
    [Theory]
    [InlineData("wechat", "com.tencent.xinWeChat")]
    [InlineData("WeChat.desktop", "com.tencent.xinWeChat")]
    [InlineData("Tencent.WeChat_8wekyb3d8bbwe!App", "com.tencent.xinWeChat")]
    [InlineData("Weixin.exe", "com.tencent.xinWeChat")]
    [InlineData("qq", "com.tencent.qq")]
    [InlineData("QQ.exe", "com.tencent.qq")]
    [InlineData("org.telegram.desktop", "ru.keepcoder.Telegram")]
    public void Native_ids_map_to_the_canonical_bundle_ids(string native, string canonical) =>
        Assert.Equal(canonical, KnownApps.Resolve(native)?.Id);

    [Fact]
    public void Unknown_apps_keep_their_own_id_and_nothing_matches_by_accident()
    {
        Assert.Null(KnownApps.Resolve("firefox"));
        Assert.Null(KnownApps.Resolve("qqmusic-unrelated"));   // "qq" only matches exactly
        Assert.Null(KnownApps.Resolve(null, ""));
        Assert.Equal("微信", KnownApps.Resolve(null, "WeChat")?.Name);
        Assert.True(KnownApps.IsWatched(["com.tencent.xinWeChat"], "com.tencent.xinWeChat", "wechat"));
        Assert.True(KnownApps.IsWatched(["firefox"], "firefox", null));
        Assert.False(KnownApps.IsWatched(["com.tencent.qq"], "com.tencent.xinWeChat", "wechat"));
    }

    private static List<DbusNotify> Parse(string text)
    {
        var parser = new DbusNotifyParser();
        var found = new List<DbusNotify>();
        foreach (var line in text.Replace("\r", "").Split('\n'))
        {
            if (parser.Feed(line) is { } n)
            {
                found.Add(n);
            }
        }
        return found;
    }

    private const string Call = """
        method call time=1700000000.123456 sender=:1.50 -> destination=org.freedesktop.Notifications serial=12 path=/org/freedesktop/Notifications; interface=org.freedesktop.Notifications; member=Notify
           string "WeChat"
           uint32 0
           string "wechat"
           string "老王"
           string "晚上吃什么"
           array [
           ]
           array [
              dict entry(
                 string "desktop-entry"
                 variant                   string "wechat"
              )
              dict entry(
                 string "urgency"
                 variant                   byte 1
              )
           ]
           int32 -1
        """;

    [Fact]
    public void A_dbus_monitor_notify_call_becomes_a_notification_with_its_desktop_entry()
    {
        var n = Assert.Single(Parse(Call));
        Assert.Equal(("WeChat", "wechat", "老王", "晚上吃什么", "wechat"), (n.AppName, n.AppIcon, n.Summary, n.Body, n.DesktopEntry));
    }

    [Fact]
    public void Other_messages_are_ignored_and_multiline_bodies_survive()
    {
        var text = """
            signal time=1.0 sender=:1.2 -> destination=(null destination) serial=3 path=/x; interface=org.freedesktop.Notifications; member=NotificationClosed
               uint32 5
            method call time=2.0 sender=:1.9 -> destination=org.freedesktop.Notifications serial=4 path=/org/freedesktop/Notifications; interface=org.freedesktop.Notifications; member=Notify
               string "qq"
               uint32 7
               string ""
               string "群聊"
               string "第一行
            第二行"
               array [
               ]
               array [
               ]
               int32 5000
            method call time=3.0 sender=:1.9 -> destination=org.freedesktop.Notifications serial=5 path=/x; interface=org.freedesktop.Notifications; member=GetCapabilities
            """;
        var n = Assert.Single(Parse(text));
        Assert.Equal("qq", n.AppName);
        Assert.Equal("第一行\n第二行", n.Body);
        Assert.Null(n.DesktopEntry);
    }

    [Fact]
    public void Markup_in_bodies_is_reduced_to_text() =>
        Assert.Equal("a & b", LinuxNotificationListener.StripMarkup("<b>a</b> &amp; <i>b</i>"));

    [Fact]
    public void Windows_reader_lines_are_parsed_and_junk_is_ignored()
    {
        Assert.Equal(new ToastLine.Access("Allowed"), ToastLine.Parse("""{"type":"access","value":"Allowed"}"""));
        var toast = Assert.IsType<ToastLine.Toast>(ToastLine.Parse(
            """{"type":"toast","app":"Tencent.WeChat_x!App","name":"微信","title":"老王","body":"在吗\n吃饭","time":1700000000000}"""));
        Assert.Equal(("Tencent.WeChat_x!App", "微信", "老王", "在吗\n吃饭"), (toast.App, toast.Name, toast.Title, toast.Body));
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1700000000000), toast.Time);
        Assert.Null(ToastLine.Parse("WARNING: something"));
        Assert.Null(ToastLine.Parse("{broken"));
        Assert.Null(ToastLine.Parse("""{"type":"toast"}"""));   // no app id
        var bare = Assert.IsType<ToastLine.Toast>(ToastLine.Parse("""{"type":"toast","app":"x"}"""));
        Assert.Null(bare.Title);
        Assert.Equal("x", bare.Name);
    }
}
