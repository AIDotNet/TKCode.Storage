using Island.Platform.MacOS;

namespace Island.Platform.Tests;

public class MacNotificationTests
{
    private static readonly DateTimeOffset T = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ParsesReqLayout()
    {
        var n = NotificationRecordParser.Parse("""{"app":"com.tencent.xinWeChat","req":{"titl":"小明","subt":"群","body":"hi","thre":"t1","extra":{"a":1}}}""", "com.tencent.xinWeChat", "微信", T);
        Assert.NotNull(n);
        Assert.Equal("小明", n!.Title);
        Assert.Equal("群", n.Subtitle);
        Assert.Equal("hi", n.Body);
        Assert.Equal("t1", n.ThreadId);
        Assert.Equal("微信", n.AppName);
        Assert.Equal(T, n.Time);
    }

    [Fact]
    public void ParsesFlatLayoutAndMissingKeys()
    {
        var n = NotificationRecordParser.Parse("""{"titl":"A"}""", "x.y", "y", T);
        Assert.Equal("A", n!.Title);
        Assert.Null(n.Body);
        Assert.Null(n.Subtitle);
        Assert.Null(n.ThreadId);
    }

    [Fact]
    public void RejectsEmptyOrBroken()
    {
        Assert.Null(NotificationRecordParser.Parse("""{"req":{"thre":"x"}}""", "a.b", "b", T));
        Assert.Null(NotificationRecordParser.Parse("not json", "a.b", "b", T));
        Assert.Null(NotificationRecordParser.Parse("[1,2]", "a.b", "b", T));
        Assert.Null(NotificationRecordParser.ParseXml("<nope", "a.b", "b", T));
    }

    [Fact]
    public void ParsesXmlWithDataAndDates()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <plist version="1.0"><dict>
              <key>date</key><date>2026-01-01T00:00:00Z</date>
              <key>req</key><dict>
                <key>iden</key><data>AAEC</data>
                <key>titl</key><string>Bob</string>
                <key>body</key><string>yo &amp; co</string>
                <key>thre</key><string>th</string>
                <key>flag</key><true/>
              </dict>
            </dict></plist>
            """;
        var n = NotificationRecordParser.ParseXml(xml, "a.b", "b", T);
        Assert.Equal("Bob", n!.Title);
        Assert.Equal("yo & co", n.Body);
        Assert.Equal("th", n.ThreadId);
    }

    [Fact]
    public void ConvertsCfAbsoluteTime()
    {
        Assert.Equal(new DateTimeOffset(2001, 1, 1, 0, 0, 10, TimeSpan.Zero), NotificationRecordParser.FromCfAbsoluteTime(10));
    }

    [Theory]
    [InlineData("3", 3)]
    [InlineData(" 12\n", 12)]
    [InlineData("99+", 99)]
    [InlineData("•", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("-1", null)]
    public void ParsesBadgeLabels(string? label, int? expected) =>
        Assert.Equal(expected, NotificationRecordParser.ParseBadgeLabel(label));

    [Fact]
    public void KnownNames()
    {
        Assert.Equal("微信", MacNotificationListener.KnownName("com.tencent.xinWeChat"));
        Assert.Equal("QQ", MacNotificationListener.KnownName("com.tencent.qq"));
        Assert.Null(MacNotificationListener.KnownName("com.foo.bar"));
        Assert.Equal("bar", MacNotificationListener.LastSegment("com.foo.bar"));
        Assert.Equal("solo", MacNotificationListener.LastSegment("solo"));
    }
}
