using Island.Platform.MacOS;

namespace Island.Platform.Tests;

public sealed class MacNetEaseMediaTests
{
    [Theory]
    [InlineData("晴天 - 周杰伦", "晴天", "周杰伦")]
    [InlineData("Hello - Adele - Live", "Hello - Adele", "Live")]   // the last separator splits artist from title
    [InlineData("Only a title", "Only a title", null)]
    [InlineData("网易云音乐", null, null)]
    [InlineData("NeteaseMusic", null, null)]
    [InlineData("", null, null)]
    [InlineData(null, null, null)]
    public void The_window_title_gives_the_track(string? raw, string? title, string? artist)
    {
        Assert.Equal((title, artist), MacNetEaseMedia.Parse(raw));
    }
}
