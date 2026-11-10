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
    private sealed class SilentSystem : IMediaPlatform
    {
        public List<MediaCommand> Sent { get; } = [];
        public bool PushesChanges => false;
        public event Action? Changed { add { } remove { } }
        public NowPlaying? Query() => null;
        public bool Send(MediaCommand command) { Sent.Add(command); return true; }
    }

    [Fact]
    public void A_pause_sent_from_here_counts_at_once_even_while_the_app_still_outputs()
    {
        var sounding = true;
        var system = new SilentSystem();
        var media = new MacNetEaseMedia(system, () => sounding, () => "晴天 - 周杰伦");
        Assert.True(media.Query()!.Playing);

        media.Send(MediaCommand.Toggle);                  // pause; the client keeps its output running for a while
        var paused = media.Query()!;
        Assert.False(paused.Playing);
        Assert.Equal("晴天", paused.Title);                // still there, so the island keeps a play button

        media.Send(MediaCommand.Toggle);                  // play again from the island
        Assert.True(media.Query()!.Playing);
        Assert.Equal([MediaCommand.Toggle, MediaCommand.Toggle], system.Sent);
    }

    [Fact]
    public void Resuming_in_the_app_itself_ends_the_pause()
    {
        var sounding = true;
        var media = new MacNetEaseMedia(new SilentSystem(), () => sounding, () => "晴天 - 周杰伦");
        media.Query();
        media.Send(MediaCommand.Pause);
        sounding = false;                                 // the output stops at last
        Assert.False(media.Query()!.Playing);
        sounding = true;                                  // and starts again: the user pressed play in NetEase
        Assert.True(media.Query()!.Playing);
    }
}
