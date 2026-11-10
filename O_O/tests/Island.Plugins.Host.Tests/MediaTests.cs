using Island.Platform;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    private sealed class FakeMedia : IPluginMedia
    {
        public NowPlaying? Current;
        public readonly System.Collections.Concurrent.ConcurrentQueue<MediaCommand> Commands = new();

        public event Action<NowPlaying?>? Changed;

        public void Play(NowPlaying? now)
        {
            Current = now;
            Changed?.Invoke(now);
        }

        public Task<NowPlaying?> NowPlayingAsync(CancellationToken ct) => Task.FromResult(Current);

        public Task<bool> CommandAsync(MediaCommand command, CancellationToken ct)
        {
            Commands.Enqueue(command);
            return Task.FromResult(true);
        }
    }

    [Fact]
    public void Media_needs_its_permission_and_rejects_unknown_commands()
    {
        var media = new FakeMedia { Current = new NowPlaying("Music", "Song", "Artist", null, true, 12, 200) };
        _permissions = "\"media\"";
        StartWith("""
            import { media } from '@island/api';
            export async function activate() {
              const n = await media.nowPlaying();
              console.info(`now ${n.app} ${n.title} ${n.playing} ${n.position}/${n.duration} album:${n.album === undefined}`);
              try { await media.command('rewind'); } catch (e) { console.info('bad: ' + e.message); }
              console.info('sent ' + await media.command('next'));
              media.onChange((x) => console.info('changed ' + (x === null ? 'null' : x.title)));
            }
            """, Services with { Media = media });
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "now Music Song true 12/200 album:true"), "now playing");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("bad: Unknown media command 'rewind'")), "unknown command");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "sent true"), "command");
        Assert.Equal([MediaCommand.Next], media.Commands);
        Thread.Sleep(100);
        media.Play(null);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "changed null"), "change pushed");
    }

    [Fact]
    public void Without_the_media_permission_there_is_no_media()
    {
        StartWith("""
            import { media } from '@island/api';
            export function activate() { console.info('media ' + typeof media); }
            """, Services with { Media = new FakeMedia() });
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "media undefined"), "no export");
    }

    [Fact]
    public void The_music_plugin_shows_the_track_claims_the_island_while_playing_and_sends_commands()
    {
        var media = new FakeMedia();
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.media"), Path.Combine(_root, "com.o_o.media"));
        StartWith(null, Services with { Media = media });
        _host!.ActivityEnded += (p, a) => _ended.Enqueue((p, a));
        WaitForPage(s => s.PluginId == "com.o_o.media" && Texts(s.View.Expanded).Contains("没有正在播放的内容"), "nothing playing");

        media.Play(new NowPlaying("Spotify", "Blue in Green", "Miles Davis", "Kind of Blue", true, 30, 337));
        var page = WaitForPage(s => s.PluginId == "com.o_o.media" && Texts(s.View.Expanded).Contains("Blue in Green"), "track shown");
        Assert.Contains("Miles Davis · Kind of Blue", Texts(page.View.Expanded));
        Assert.Contains(Nodes(page.View.Expanded), n => n.Type == "progress" && n.GetNumber("max") == 337);
        var activity = WaitForUpdate(s => s.PluginId == "com.o_o.media", "playing activity");
        Assert.Equal(ActivityPriority.Active, activity.Priority);
        Assert.Equal("now", activity.View.Page);
        WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.media/playing" && c.View is not null), "playing chip");

        var latest = _pageViews.Last(s => s.PluginId == "com.o_o.media");
        var pause = Nodes(latest.View.Expanded).First(n => n.Type == "iconButton" && n.GetString("icon") == "Pause");
        _host!.Dispatch("com.o_o.media", pause.Handlers["onClick"]);
        WaitFor(() => media.Commands.Contains(MediaCommand.Pause) ? "ok" : null, "pause sent");

        media.Play(new NowPlaying("Spotify", "Blue in Green", "Miles Davis", "Kind of Blue", false, 31, 337));
        WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.media/playing") is { View: null } ? "ok" : null, "chip removed when paused");
        WaitFor(() => _ended.Any(e => e.Plugin == "com.o_o.media") ? "ok" : null, "activity ended when paused");
    }
}

public sealed class PlatformMediaTests
{
    private sealed class PushingPlatform : IMediaPlatform
    {
        public NowPlaying? State;
        public bool PushesChanges => true;
        public event Action? Changed;
        public void Push(NowPlaying? state) { State = state; Changed?.Invoke(); }
        public NowPlaying? Query() => State;
        public bool Send(MediaCommand command) => true;
    }

    [Fact]
    public void Only_track_state_or_seek_changes_are_reported_not_the_position_moving_on()
    {
        var platform = new PushingPlatform();
        using var media = new PlatformMedia(platform);
        var seen = new System.Collections.Concurrent.ConcurrentQueue<NowPlaying?>();
        media.Changed += seen.Enqueue;
        var t0 = DateTimeOffset.Now;
        void Push(NowPlaying? s, int expected)
        {
            platform.Push(s);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (seen.Count < expected && DateTime.UtcNow < deadline) Thread.Sleep(10);
            Thread.Sleep(50);
            Assert.Equal(expected, seen.Count);
        }
        Push(new NowPlaying("Music", "A", null, null, true, 10, 100, t0), 1);
        Push(new NowPlaying("Music", "A", null, null, true, 15, 100, t0.AddSeconds(5)), 1);   // just playing on
        Push(new NowPlaying("Music", "A", null, null, true, 60, 100, t0.AddSeconds(6)), 2);   // a seek
        Push(new NowPlaying("Music", "A", null, null, false, 60, 100, t0.AddSeconds(7)), 3);  // paused
        Push(null, 4);                                                                         // stopped
    }
}
