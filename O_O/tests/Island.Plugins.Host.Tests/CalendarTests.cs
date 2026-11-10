using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Tests;

/// <summary>
/// The calendar plugins (plugins/com.o_o.calendar, com.o_o.cal.caldav, com.o_o.cal.ics) end to end, against a
/// loopback CalDAV / ICS server. Event times are built around "now", so recurrence, time zones and the aggregator's
/// "starting soon" logic are checked without waiting.
/// </summary>
public sealed partial class PluginHostTests
{
    /// <summary>A tiny CalDAV server: principal discovery, a calendar home with one event calendar, and REPORT.</summary>
    private sealed class FakeCalendarServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<string, string, string?, (int Status, string Body)> _answer;

        public FakeCalendarServer(Func<string, string, string?, (int, string)> answer)
        {
            _answer = answer;
            using (var probe = new TcpListener(IPAddress.Loopback, 0))
            {
                probe.Start();
                Url = $"http://127.0.0.1:{((IPEndPoint)probe.LocalEndpoint).Port}";
            }
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Serve();
        }

        public string Url { get; }
        public List<string> Requests { get; } = [];

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try
                {
                    ctx = await _listener.GetContextAsync();
                }
                catch (Exception)
                {
                    return;
                }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var auth = ctx.Request.Headers["Authorization"];
                lock (Requests)
                {
                    Requests.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} depth={ctx.Request.Headers["Depth"]} {auth} {body}");
                }
                var (status, answer) = _answer(ctx.Request.HttpMethod, ctx.Request.Url!.AbsolutePath, auth);
                ctx.Response.StatusCode = status;
                ctx.Response.ContentType = answer.StartsWith("BEGIN:", StringComparison.Ordinal) ? "text/calendar" : "application/xml; charset=utf-8";
                var bytes = Encoding.UTF8.GetBytes(answer);
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static string Stamp(DateTime utc) => utc.ToString("yyyyMMdd'T'HHmmss'Z'");
    private static string DateStamp(DateTime local) => local.ToString("yyyyMMdd");

    /// <summary>A calendar around today (local): recurring, all-day, time-zoned, overridden, cancelled events.</summary>
    private static string TestCalendar()
    {
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        var nineUtcThreeDaysAgo = DateTime.SpecifyKind(today.AddDays(-3), DateTimeKind.Utc).AddHours(9);
        var todayNineUtc = DateTime.SpecifyKind(today, DateTimeKind.Utc).AddHours(9);
        var tomorrowNineUtc = todayNineUtc.AddDays(1);
        string Wd(DateTime d) => d.DayOfWeek.ToString()[..2].ToUpperInvariant();
        return string.Join("\r\n",
            "BEGIN:VCALENDAR", "VERSION:2.0", "PRODID:-//test//EN", "X-WR-CALNAME:测试日历",
            "BEGIN:VTIMEZONE", "TZID:Custom/Plus2",
            "BEGIN:STANDARD", "DTSTART:19700101T000000", "TZOFFSETFROM:+0200", "TZOFFSETTO:+0200", "END:STANDARD",
            "END:VTIMEZONE",
            // Daily stand-up at 09:00 UTC from three days ago; tomorrow's is cancelled (EXDATE), today's moved to 15:00.
            "BEGIN:VEVENT", "UID:standup", "SUMMARY:站会", $"DTSTART:{Stamp(nineUtcThreeDaysAgo)}", "DURATION:PT15M",
            "RRULE:FREQ=DAILY;COUNT=10", $"EXDATE:{Stamp(tomorrowNineUtc)}", "END:VEVENT",
            "BEGIN:VEVENT", "UID:standup", $"RECURRENCE-ID:{Stamp(todayNineUtc)}", "SUMMARY:站会（改到下午）",
            $"DTSTART:{Stamp(todayNineUtc.AddHours(6))}", $"DTEND:{Stamp(todayNineUtc.AddHours(6).AddMinutes(15))}", "END:VEVENT",
            // Weekly on today's and tomorrow's weekdays, from two weeks ago, floating time 14:00.
            "BEGIN:VEVENT", "UID:weekly", "SUMMARY:周会", $"DTSTART:{DateStamp(today.AddDays(-14))}T140000",
            $"DTEND:{DateStamp(today.AddDays(-14))}T150000", $"RRULE:FREQ=WEEKLY;BYDAY={Wd(today)},{Wd(tomorrow)}", "END:VEVENT",
            // All day today.
            "BEGIN:VEVENT", "UID:allday", "SUMMARY:团建", $"DTSTART;VALUE=DATE:{DateStamp(today)}",
            $"DTEND;VALUE=DATE:{DateStamp(tomorrow)}", "END:VEVENT",
            // Tomorrow 10:00 in Shanghai (UTC+8, no VTIMEZONE sent) with a meeting link folded across lines.
            "BEGIN:VEVENT", "UID:sh", "SUMMARY:评审", $"DTSTART;TZID=Asia/Shanghai:{DateStamp(tomorrow)}T100000",
            $"DTEND;TZID=Asia/Shanghai:{DateStamp(tomorrow)}T110000",
            "DESCRIPTION:加入会议：\\nhttps://meeting.tencent.com/dm/ab", " cd123 密码 0000", "LOCATION:3 楼", "END:VEVENT",
            // Tomorrow 12:00 in a zone the file defines (+02:00).
            "BEGIN:VEVENT", "UID:plus2", "SUMMARY:午餐", $"DTSTART;TZID=Custom/Plus2:{DateStamp(tomorrow)}T120000",
            "DURATION:PT1H", "END:VEVENT",
            "BEGIN:VEVENT", "UID:gone", "SUMMARY:已取消", "STATUS:CANCELLED", $"DTSTART:{Stamp(todayNineUtc)}", "END:VEVENT",
            "END:VCALENDAR", "");
    }

    private static string Xml(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private static (int, string) CalDav(string method, string path, string? auth)
    {
        if (auth != "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes("我@example.com:s3cret")))
        {
            return (401, "");
        }
        const string ns = """xmlns:D="DAV:" xmlns:C="urn:ietf:params:xml:ns:caldav" xmlns:A="http://apple.com/ns/ical/" """;
        return (method, path) switch
        {
            ("PROPFIND", "/") => (207, $"""<?xml version="1.0"?><D:multistatus {ns}><D:response><D:href>/</D:href><D:propstat><D:prop><D:current-user-principal><D:href>/principals/me/</D:href></D:current-user-principal></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>"""),
            ("PROPFIND", "/principals/me/") => (207, $"""<?xml version="1.0"?><D:multistatus {ns}><D:response><D:href>/principals/me/</D:href><D:propstat><D:prop><C:calendar-home-set><D:href>/calendars/me/</D:href></C:calendar-home-set></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>"""),
            ("PROPFIND", "/calendars/me/") => (207, $"""
                <?xml version="1.0"?><D:multistatus {ns}>
                <D:response><D:href>/calendars/me/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                <D:response><D:href>/calendars/me/work/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>工作</D:displayname><A:calendar-color>#FF2968FF</A:calendar-color><C:supported-calendar-component-set><C:comp name="VEVENT"/></C:supported-calendar-component-set></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                <D:response><D:href>/calendars/me/tasks/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/><C:calendar/></D:resourcetype><D:displayname>提醒</D:displayname><C:supported-calendar-component-set><C:comp name="VTODO"/></C:supported-calendar-component-set></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response>
                <D:response><D:href>/calendars/me/inbox/</D:href><D:propstat><D:prop><D:resourcetype><D:collection/><C:schedule-inbox/></D:resourcetype></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat><D:propstat><D:prop><C:calendar/></D:prop><D:status>HTTP/1.1 404 Not Found</D:status></D:propstat></D:response>
                </D:multistatus>
                """),
            ("REPORT", "/calendars/me/work/") => (207, $"""<?xml version="1.0"?><D:multistatus {ns}><D:response><D:href>/calendars/me/work/a.ics</D:href><D:propstat><D:prop><D:getetag>"1"</D:getetag><C:calendar-data>{Xml(TestCalendar())}</C:calendar-data></D:prop><D:status>HTTP/1.1 200 OK</D:status></D:propstat></D:response></D:multistatus>"""),
            _ => (404, ""),
        };
    }

    /// <summary>Runs a shipped calendar provider with the given settings and a consumer that lists today's and tomorrow's events.</summary>
    private JsonElement RunCalendarProvider(string pluginId, string providerId, Dictionary<string, string> settings)
    {
        CopyPlugin(RepoPlugin(pluginId), Path.Combine(_root, pluginId));
        WritePluginWithManifest("test.consumer", "", "", $$"""
            import { island, ui } from '@island/api';
            const settle = (p) => p.then((v) => ({ ok: v }), (e) => ({ err: e.message }));
            export async function activate() {
              const act = island.activity.create('r');
              for (let i = 0; i < 100; i++) {
                if ((await island.providers.list('calendar')).some((p) => p.id === '{{providerId}}' && p.running)) break;
                await new Promise((r) => setTimeout(r, 50));
              }
              const d = new Date();
              const from = new Date(d.getFullYear(), d.getMonth(), d.getDate());
              const range = { from: from.toISOString(), to: new Date(from.getTime() + 2 * 86400000).toISOString() };
              const accounts = await settle(island.providers.call('calendar', '{{providerId}}', 'accounts'));
              const events = [];
              const calendars = [];
              for (const a of accounts.ok ?? []) {
                calendars.push(await settle(island.providers.call('calendar', '{{providerId}}', 'listCalendars', a)));
                events.push(await settle(island.providers.call('calendar', '{{providerId}}', 'listEvents', a, range)));
              }
              act.update({ expanded: ui.text(JSON.stringify({ accounts, calendars, events })) });
            }
            """);
        _host = new PluginHost(Services, FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        var store = _host.GetSettings(pluginId);
        foreach (var (key, value) in settings)
        {
            store.Set(key, JsonSerializer.SerializeToElement(value));
        }
        EnableOptional();
        _host.StartAll();
        var result = WaitForUpdate(s => s.PluginId == "test.consumer", "provider results");
        return JsonDocument.Parse(Text(result.View.Expanded)).RootElement;
    }

    private static List<JsonElement> EventsOf(JsonElement r, int account = 0) =>
        [.. Ok(r.GetProperty("events")[account]).EnumerateArray()];

    [Fact]
    public void CalDav_discovers_the_calendars_and_expands_recurrence_zones_and_all_day_events()
    {
        using var server = new FakeCalendarServer(CalDav);
        var r = RunCalendarProvider("com.o_o.cal.caldav", "caldav", new()
        {
            ["serverUrl"] = server.Url + "/", ["username"] = "我@example.com", ["password"] = "s3cret", ["name"] = "工作账户",
        });

        Assert.Equal("工作账户", Ok(r.GetProperty("accounts"))[0].GetProperty("label").GetString());
        var calendars = Ok(r.GetProperty("calendars")[0]).EnumerateArray().ToList();
        var work = Assert.Single(calendars); // the VTODO list and the inbox are not event calendars
        Assert.Equal("工作", work.GetProperty("name").GetString());
        Assert.Equal("#FF2968", work.GetProperty("color").GetString());

        var events = EventsOf(r);
        var today = DateTime.Today;
        var tomorrow = today.AddDays(1);
        string Utc(DateTime d) => d.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        string LocalUtc(DateTime local) => Utc(local.ToUniversalTime());

        // Stand-up: today's moved to 15:00 UTC by its override, tomorrow's excluded — but only if they fall in range.
        var todayNine = DateTime.SpecifyKind(today, DateTimeKind.Utc).AddHours(9);
        Assert.DoesNotContain(events, e => e.GetProperty("start").GetString() == Utc(todayNine));
        Assert.DoesNotContain(events, e => e.GetProperty("start").GetString() == Utc(todayNine.AddDays(1)));
        if (todayNine.AddHours(6).ToLocalTime().Date == today)
        {
            Assert.Contains(events, e => e.GetProperty("title").GetString() == "站会（改到下午）");
        }
        // Weekly, floating 14:00 local, today and tomorrow.
        var weekly = events.Where(e => e.GetProperty("title").GetString() == "周会").Select(e => e.GetProperty("start").GetString()).ToList();
        Assert.Equal([LocalUtc(today.AddHours(14)), LocalUtc(tomorrow.AddHours(14))], weekly);
        // All day.
        var allDay = Assert.Single(events, e => e.GetProperty("title").GetString() == "团建");
        Assert.True(allDay.GetProperty("allDay").GetBoolean());
        Assert.Equal(today.ToString("yyyy-MM-dd"), allDay.GetProperty("start").GetString());
        Assert.Equal(tomorrow.ToString("yyyy-MM-dd"), allDay.GetProperty("end").GetString());
        // Shanghai: 10:00 there is 02:00 UTC; the folded description still yields the meeting link.
        var review = Assert.Single(events, e => e.GetProperty("title").GetString() == "评审");
        Assert.Equal(Utc(DateTime.SpecifyKind(tomorrow, DateTimeKind.Utc).AddHours(2)), review.GetProperty("start").GetString());
        Assert.Equal("https://meeting.tencent.com/dm/abcd123", review.GetProperty("meetingUrl").GetString());
        Assert.Equal("3 楼", review.GetProperty("location").GetString());
        // The file's own zone, +02:00: 12:00 there is 10:00 UTC.
        var lunch = Assert.Single(events, e => e.GetProperty("title").GetString() == "午餐");
        Assert.Equal(Utc(DateTime.SpecifyKind(tomorrow, DateTimeKind.Utc).AddHours(10)), lunch.GetProperty("start").GetString());
        Assert.DoesNotContain(events, e => e.GetProperty("title").GetString() == "已取消");

        // Discovery went principal → home → calendars, and the REPORT asked for a time range.
        Assert.Contains(server.Requests, q => q.StartsWith("PROPFIND /principals/me/ depth=0", StringComparison.Ordinal));
        Assert.Contains(server.Requests, q => q.StartsWith("PROPFIND /calendars/me/ depth=1", StringComparison.Ordinal));
        Assert.Contains(server.Requests, q => q.StartsWith("REPORT /calendars/me/work/ depth=1", StringComparison.Ordinal) && q.Contains("time-range start=\""));
    }

    [Fact]
    public void CalDav_says_when_the_password_is_wrong()
    {
        using var server = new FakeCalendarServer(CalDav);
        var r = RunCalendarProvider("com.o_o.cal.caldav", "caldav", new()
        {
            ["serverUrl"] = server.Url + "/", ["username"] = "我@example.com", ["password"] = "wrong",
        });
        Assert.Contains("用户名或密码不对", Err(r.GetProperty("events")[0]));
    }

    [Fact]
    public void Ics_subscriptions_are_read_and_webcal_links_mean_https()
    {
        using var server = new FakeCalendarServer((method, path, _) => path == "/feed.ics" ? (200, TestCalendar()) : (404, ""));
        var r = RunCalendarProvider("com.o_o.cal.ics", "ics", new()
        {
            // webcal:// means https:// — tried against the local (plain-http) server, so it fails at TLS, never leaving
            // this machine; what matters is that it was taken as an https subscription, not refused as webcal.
            ["url1"] = server.Url + "/feed.ics", ["url2"] = server.Url.Replace("http://", "webcal://") + "/feed.ics", ["name2"] = "节假日",
        });
        var accounts = Ok(r.GetProperty("accounts")).EnumerateArray().Select(a => a.GetProperty("handle").GetString()).ToList();
        Assert.Equal(["url1", "url2"], accounts);
        Assert.Equal("测试日历", Ok(r.GetProperty("calendars")[0])[0].GetProperty("name").GetString());
        Assert.Contains(EventsOf(r), e => e.GetProperty("title").GetString() == "团建");
        Assert.DoesNotContain("webcal", Err(r.GetProperty("events")[1]));
    }

    private sealed class FakeSystemCalendar(string json) : Island.Platform.ICalendarPlatform
    {
        public List<(DateTimeOffset From, DateTimeOffset To)> Reads { get; } = [];

        public Task<string> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
        {
            Reads.Add((from, to));
            return Task.FromResult(json);
        }
    }

    [Fact]
    public void System_calendar_provider_reads_the_native_calendar_and_adds_colours_and_meeting_links()
    {
        var fake = new FakeSystemCalendar("""
            { "calendars": [{ "id": "c1", "name": "工作", "account": "iCloud", "color": "#34aadc" }],
              "events": [
                { "id": "e1", "calendarId": "c1", "title": "评审", "allDay": false, "start": "2030-01-01T02:00:00.000Z", "end": "2030-01-01T03:00:00.000Z",
                  "location": "会议室", "notes": "加入 https://meet.google.com/abc-defg-hij 。" },
                { "id": "e2", "calendarId": "c1", "title": "小雪", "allDay": true, "start": "2030-01-02", "end": "2030-01-03" } ] }
            """);
        _systemCalendar = fake;
        var r = RunCalendarProvider("com.o_o.cal.system", "system", []);
        Assert.Equal("工作（iCloud）", Ok(r.GetProperty("calendars")[0])[0].GetProperty("name").GetString());
        var events = EventsOf(r);
        Assert.Equal(2, events.Count);
        Assert.Equal("https://meet.google.com/abc-defg-hij", events[0].GetProperty("meetingUrl").GetString());
        Assert.Equal("#34aadc", events[0].GetProperty("color").GetString());
        Assert.False(events[0].TryGetProperty("notes", out _));
        Assert.True(events[1].GetProperty("allDay").GetBoolean());
        Assert.NotEmpty(fake.Reads);
        Assert.True(fake.Reads[0].To > fake.Reads[0].From);
    }

    [Fact]
    public void System_calendar_provider_turns_a_denied_permission_into_a_readable_error()
    {
        _systemCalendar = new FakeSystemCalendar("""{ "error": "denied" }""");
        var r = RunCalendarProvider("com.o_o.cal.system", "system", []);
        Assert.Contains("日历", Err(r.GetProperty("events")[0]));
    }

    /// <summary>A fake calendar provider plugin whose events are given as JS expressions relative to `now`.</summary>
    private void WriteFakeCalendarProvider(string id, string providerId, string eventsJs) =>
        WritePluginWithManifest(id,
            $$$""", "contributes": { "providers": [{ "interface": "calendar", "id": "{{{providerId}}}", "title": "{{{providerId}}}" }] }""", "", $$"""
            import { island } from '@island/api';
            const M = 60000;
            export function activate() {
              island.provide('calendar', '{{providerId}}', {
                accounts: () => [{ handle: 'a', label: '{{providerId}} 账户' }],
                listCalendars: () => [{ id: 'c', name: 'c' }],
                listEvents: () => { const now = Date.now(); return {{eventsJs}}; },
              });
            }
            """);

    [Fact]
    public void The_calendar_merges_sources_and_announces_an_event_that_starts_soon()
    {
        CopyPlugin(RepoPlugin("com.o_o.calendar"), Path.Combine(_root, "com.o_o.calendar"));
        // The same meeting in two calendars, five minutes from now, plus a later one only in the first.
        const string meeting = "{ id: 'x', title: '周会', start: new Date(Math.floor(now / M) * M + 5 * M).toISOString(), end: new Date(Math.floor(now / M) * M + 35 * M).toISOString(), allDay: false, meetingUrl: 'https://meet.google.com/abc-defg-hij' }";
        WriteFakeCalendarProvider("test.cal.a", "a", $"[{meeting}, {{ id: 'y', title: '晚点的', start: new Date(now + 600 * M).toISOString(), end: new Date(now + 630 * M).toISOString(), allDay: false }}]");
        WriteFakeCalendarProvider("test.cal.b", "b", $"[{meeting}]");
        StartDiscovered();

        var page = WaitForPage(s => s.PluginId == "com.o_o.calendar" && Nodes(s.View.Expanded).Any(n => n.Type == "item" && Texts(n).Contains("周会")), "events listed");
        var rows = Nodes(page.View.Expanded).Where(n => n.Type == "item").ToList(); // the month's day cells are items too
        Assert.Single(rows, r => Texts(r).Contains("周会"));                                     // merged
        var activity = WaitForUpdate(s => s.PluginId == "com.o_o.calendar" && s.ActivityId == "soon", "starting-soon activity");
        Assert.Equal(ActivityPriority.Passive, activity.Priority);
        Assert.Equal("today", activity.View.Page);
        var alert = WaitFor(() => _alertsSeen.FirstOrDefault(a => a.PluginId == "com.o_o.calendar"), "alert");
        Assert.Contains("分钟后 · 周会", Text(alert.Trailing));
        Assert.Equal("Video", alert.Leading!.GetString("name"));
        var chip = WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.calendar/next" && c.View is not null), "next chip");
        Assert.Matches(@"^\d{2}:\d{2}$", Texts(chip.View).Single());
        Thread.Sleep(300);
        Assert.Single(_alertsSeen, a => a.PluginId == "com.o_o.calendar");                        // once, not per refresh

        // Clicking the meeting opens its link (shell.openUrl, right after the click).
        var clipboard = new FakeClipboard();
        var opened = new System.Collections.Concurrent.ConcurrentQueue<Uri>();
        _host!.Dispose();
        _updates.Clear();
        _pageViews.Clear();
        StartWith(null, Services with { Clipboard = clipboard, OpenBrowser = u => { opened.Enqueue(u); return Task.CompletedTask; } });
        WaitForPage(s => s.PluginId == "com.o_o.calendar" && Nodes(s.View.Expanded).Any(n => n.Type == "item" && Texts(n).Contains("周会")), "events again");
        // The page drawn from the cache is drawn again once the sources answer, with new handlers: click on the latest.
        Thread.Sleep(1000);
        page = _pageViews.Last(s => s.PluginId == "com.o_o.calendar");
        _host!.Dispatch("com.o_o.calendar", Nodes(page.View.Expanded).First(n => n.Type == "item" && Texts(n).Contains("周会")).Handlers["onClick"]);
        WaitFor(() => opened.FirstOrDefault()?.AbsoluteUri == "https://meet.google.com/abc-defg-hij" ? "ok" : null, "link opened");
        Assert.Null(clipboard.Content); // opened, so nothing was copied
    }

    [Fact]
    public void An_event_under_way_makes_the_activity_active_and_without_sources_the_page_says_what_to_do()
    {
        CopyPlugin(RepoPlugin("com.o_o.calendar"), Path.Combine(_root, "com.o_o.calendar"));
        WriteFakeCalendarProvider("test.cal.a", "a",
            "[{ id: 'z', title: '正在进行的长会议', start: new Date(now - 2 * M).toISOString(), end: new Date(now + 60 * M).toISOString(), allDay: false }]");
        StartDiscovered();
        var activity = WaitForUpdate(s => s.PluginId == "com.o_o.calendar" && s.ActivityId == "soon" && s.Priority == ActivityPriority.Active, "active");
        Assert.Equal("today", activity.View.Page);
        var chip = WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.calendar/next" && c.View is not null), "chip");
        Assert.Contains("正在进行…", Texts(chip.View).ToList());

        _host!.Dispose();
        Directory.Delete(Path.Combine(_root, "test.cal.a"), true);
        _pageViews.Clear();
        StartDiscovered();
        // The cached events are shown at once; once the refresh finds no providers the cache is replaced.
        WaitForPage(s => s.PluginId == "com.o_o.calendar" && Texts(s.View.Expanded).Contains("还没有日历来源"), "no sources");
    }
}
