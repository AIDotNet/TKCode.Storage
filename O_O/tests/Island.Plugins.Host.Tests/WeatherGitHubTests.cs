using Island.Platform;

namespace Island.Plugins.Host.Tests;

/// <summary>The shipped weather and GitHub plugins against local fake APIs.</summary>
public sealed partial class PluginHostTests
{
    [Fact]
    public void Weather_looks_up_the_city_and_shows_the_temperature_beside_the_island_and_on_its_home_page()
    {
        using var api = new FakeCalendarApi((path, _) =>
            path.StartsWith("/v1/search", StringComparison.Ordinal) ? """{ "results": [{ "name": "上海", "latitude": 31.2, "longitude": 121.4 }] }"""
            : path.StartsWith("/v1/forecast", StringComparison.Ordinal) ? """
              { "current": { "temperature_2m": 21.6, "apparent_temperature": 20.0, "relative_humidity_2m": 55, "weather_code": 0, "wind_speed_10m": 9.4 },
                "hourly": { "time": ["2026-11-10T10:00","2026-11-10T11:00","2026-11-10T12:00"], "temperature_2m": [21,22,23], "weather_code": [0,3,61], "precipitation_probability": [0,10,60] } }
              """
            : null);
        CopyProviderTo("com.o_o.weather", "https://geocoding-api.open-meteo.com", api.Url);
        var main = Path.Combine(_root, "com.o_o.weather", "main.js");
        File.WriteAllText(main, File.ReadAllText(main).Replace("https://api.open-meteo.com", api.Url));
        StartOAuthHost(OAuthServices(api));
        SetSettings("com.o_o.weather", new() { ["city"] = "上海" });
        EnableOptional();
        _host!.StartAll();

        var page = WaitForPage(s => s.PluginId == "com.o_o.weather" && Texts(s.View.Expanded).Contains("22°"), "temperature on the page");
        Assert.Contains("上海 · 晴", Texts(page.View.Expanded).ToList());
        Assert.Contains("60%", Texts(page.View.Expanded).ToList());
        var chip = WaitFor(() => _ambientSeen.LastOrDefault(c => c.Key == "com.o_o.weather/temp" && c.View is not null), "temperature chip");
        Assert.Contains("22°", Texts(chip.View).ToList());
        Assert.Contains(api.Requests, r => r.Contains("name=", StringComparison.Ordinal));
    }

    [Fact]
    public void GitHub_lists_notifications_and_review_requests_with_the_token_and_a_count_beside_the_island()
    {
        using var api = new FakeCalendarApi((path, _) =>
            path.StartsWith("/notifications", StringComparison.Ordinal) ? """
              [{ "id": "1", "reason": "mention", "subject": { "title": "Fix the build", "type": "PullRequest", "url": "https://api.github.com/repos/o/r/pulls/5" }, "repository": { "full_name": "o/r", "html_url": "https://github.com/o/r" } }]
              """
            : path.StartsWith("/search/issues", StringComparison.Ordinal) ? """
              { "items": [{ "id": 9, "title": "Add monitor", "html_url": "https://github.com/o/r/pull/7", "repository_url": "https://api.github.com/repos/o/r" }] }
              """
            : null);
        CopyProviderTo("com.o_o.github", "https://api.github.com", api.Url);
        StartOAuthHost(OAuthServices(api));
        SetSettings("com.o_o.github", new() { ["token"] = "ghp_test" });
        EnableOptional();
        _host!.StartAll();

        var page = WaitForPage(s => s.PluginId == "com.o_o.github" && Texts(s.View.Expanded).Contains("Fix the build"), "notification listed");
        Assert.Contains("o/r", Texts(page.View.Expanded).ToList());
        Assert.Contains(Nodes(page.View.Expanded), n => n.Type == "option" && n.GetString("label") == "待 review 1");
        var chip = WaitFor(() => _ambientSeen.LastOrDefault(c => c.Key == "com.o_o.github/unread" && c.View is not null), "count chip");
        Assert.Contains("2", Texts(chip.View).ToList());
        Assert.Contains(api.Requests, r => r.Contains("auth=Bearer ghp_test", StringComparison.Ordinal));
    }

    [Fact]
    public void NetEase_shows_a_live_activity_while_playing_or_paused_and_the_song_on_its_home_page()
    {
        using var api = new FakeCalendarApi((path, _) =>
            path.StartsWith("/api/search/get/web", StringComparison.Ordinal) ? """{ "result": { "songs": [{ "id": 186016, "name": "晴天", "duration": 269000 }] } }"""
            : path.StartsWith("/api/song/lyric", StringComparison.Ordinal) ? """{ "lrc": { "lyric": "[ti:晴天]\n[00:00.00]故事的小黄花\n[00:30.50]从出生那年就飘着" } }"""
            : path.StartsWith("/api/playlist/detail", StringComparison.Ordinal) ? """{ "result": { "tracks": [{ "id": 1, "name": "七里香", "artists": [{ "name": "周杰伦" }] }] } }"""
            : null);
        CopyProviderTo("com.o_o.netease", "https://music.163.com", api.Url);
        var media = new FakeMedia();
        StartOAuthHost(OAuthServices(api) with { Media = media });
        EnableOptional();
        _host!.StartAll();
        WaitForPage(s => s.PluginId == "com.o_o.netease" && Texts(s.View.Expanded).Contains("网易云音乐没有在播放"), "idle home page");
        Assert.Empty(_host.LiveActivities);

        media.Play(new NowPlaying("NeteaseMusic", "晴天", "周杰伦", null, true));
        var page = WaitForPage(s => s.PluginId == "com.o_o.netease" && Texts(s.View.Expanded).Contains("故事的小黄花"), "the lyric line");
        Assert.Contains("周杰伦", Texts(page.View.Expanded).ToList());
        var playing = WaitFor(() => _host.LiveActivities.FirstOrDefault(a => a.Kind == "com.o_o.netease/playing" && a.Priority == LivePriority.Normal), "playing activity");
        Assert.Equal("music", playing.Accent);
        Assert.Equal("com.netease.163music", playing.Open!.App);
        Assert.Contains("晴天", Texts(playing.View.Compact.Leading!).ToList());
        var bars = Assert.Single(Nodes(playing.View.Compact.Trailing!), n => n.Type == "spectrum");
        Assert.Null(bars.GetString("source"));              // never names a process: no audio is tapped, the bars move by themselves
        Assert.Contains(Nodes(playing.View.Expanded.Trailing!), n => n.Type == "iconButton" && n.GetString("icon") == "Pause");
        Assert.Contains(api.Requests, r => r.Contains("/api/search/get/web", StringComparison.Ordinal) && r.Contains("%E6%99%B4%E5%A4%A9", StringComparison.Ordinal));

        media.Play(new NowPlaying("NeteaseMusic", "晴天", "周杰伦", null, false));      // paused: still there, quieter, with a play button
        var paused = WaitFor(() => _host.LiveActivities.FirstOrDefault(a => a.Kind == "com.o_o.netease/playing" && a.Priority == LivePriority.Passive), "paused activity");
        Assert.Contains(Nodes(paused.View.Expanded.Trailing!), n => n.Type == "iconButton" && n.GetString("icon") == "Play");
        media.Play(new NowPlaying("Spotify", "Other", "Someone", null, true));          // another player: not ours
        WaitForPage(s => s.PluginId == "com.o_o.netease" && Texts(s.View.Expanded).Contains("网易云音乐没有在播放"), "idle again");
        WaitFor(() => _host.LiveActivities.Any(a => a.PluginId == "com.o_o.netease") ? null : "ended", "activity ended");
    }
}
