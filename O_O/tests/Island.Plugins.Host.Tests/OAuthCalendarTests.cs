using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>
/// The OAuth calendar providers (plugins/com.o_o.cal.google, .feishu, .dingtalk) end to end: sign-in through the host's
/// oauth capability with a fake browser, then events from a loopback server that plays the provider's token endpoint
/// and calendar API. The plugins' API hosts are rewritten to the loopback server, and the sign-in profiles point at it.
/// </summary>
public sealed partial class PluginHostTests
{
    private sealed class FakeCalendarApi : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Func<string, string?, string?> _route;
        public readonly ConcurrentQueue<string> Requests = new();

        /// <param name="route">(path and query, body) → JSON answer, or null for 404.</param>
        public FakeCalendarApi(Func<string, string?, string?> route)
        {
            _route = route;
            Url = $"http://127.0.0.1:{FreePort()}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Serve();
        }

        public string Url { get; }

        private async Task Serve()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); } catch { return; }
                var body = await new StreamReader(ctx.Request.InputStream).ReadToEndAsync();
                var path = ctx.Request.Url!.PathAndQuery;
                Requests.Enqueue($"{ctx.Request.HttpMethod} {path} auth={ctx.Request.Headers["Authorization"]} ding={ctx.Request.Headers["x-acs-dingtalk-access-token"]} {body}");
                var answer = path.StartsWith("/token", StringComparison.Ordinal)
                    ? Token(body)
                    : _route(path, body);
                ctx.Response.StatusCode = answer is null ? 404 : 200;
                ctx.Response.ContentType = "application/json";
                var bytes = Encoding.UTF8.GetBytes(answer ?? "{}");
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        // Every style the host speaks: form (Google), JSON (Feishu), camelCase JSON (DingTalk).
        private static string Token(string body) => body.Contains("clientId", StringComparison.Ordinal)
            ? """{"accessToken":"ding-at","refreshToken":"ding-rt","expireIn":7200}"""
            : """{"code":0,"access_token":"at-1","refresh_token":"rt-1","expires_in":7200,"token_type":"Bearer"}""";

        public void Dispose() => _listener.Close();
    }

    /// <summary>Services whose sign-in profiles point at <paramref name="api"/> and whose "browser" signs in at once.</summary>
    private PluginServices OAuthServices(FakeCalendarApi api) => Services with
    {
        OAuthCallbackPort = FreePort(),
        OAuthProfiles = new Dictionary<string, OAuthProfile>
        {
            ["google"] = new("google", "Google", new(api.Url + "/authorize"), new(api.Url + "/token"), OAuthTokenStyle.Form, ["127.0.0.1"]),
            ["feishu"] = new("feishu", "飞书", new(api.Url + "/authorize"), new(api.Url + "/token"), OAuthTokenStyle.Json, ["127.0.0.1"]),
            ["dingtalk"] = new("dingtalk", "钉钉", new(api.Url + "/authorize"), new(api.Url + "/token"), OAuthTokenStyle.DingTalk, ["127.0.0.1"],
                Pkce: false, TokenHeader: "x-acs-dingtalk-access-token"),
        },
        OpenBrowser = uri =>
        {
            var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
            _ = Task.Run(async () =>
            {
                using var browser = new HttpClient();
                await browser.GetStringAsync($"{q["redirect_uri"]}?state={q["state"]}&code=the-code");
            });
            return Task.CompletedTask;
        },
    };

    /// <summary>Copies a provider plugin with its API base pointed at the fake server (and loopback allowed).</summary>
    private void CopyProviderTo(string pluginId, string realBase, string fakeBase)
    {
        var target = Path.Combine(_root, pluginId);
        CopyPlugin(Path.Combine(RepoPlugins(), pluginId), target);
        var main = Path.Combine(target, "main.js");
        File.WriteAllText(main, File.ReadAllText(main).Replace(realBase, fakeBase));
        var manifest = Path.Combine(target, "plugin.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"permissions\": [", "\"permissions\": [\"network:localhost\", "));
    }

    private void SetSettings(string pluginId, Dictionary<string, string> values)
    {
        var store = _host!.GetSettings(pluginId);
        foreach (var (key, value) in values)
        {
            store.Set(key, JsonSerializer.SerializeToElement(value));
        }
    }

    /// <summary>
    /// A consumer that signs a provider in the way the 日程 page does (status → signIn), waits for its account, and
    /// shows { before, after, accounts, calendars, events } for today and tomorrow as JSON.
    /// </summary>
    private void WriteSignInConsumer(string providerId) =>
        WritePluginWithManifest("test.consumer", "", "", $$"""
            import { island, ui } from '@island/api';
            const settle = (p) => p.then((v) => ({ ok: v }), (e) => ({ err: e.message }));
            const call = (m, ...a) => island.providers.call('calendar', '{{providerId}}', m, ...a);
            export async function activate() {
              const act = island.activity.create('r');
              for (let i = 0; i < 100; i++) {
                if ((await island.providers.list('calendar')).some((p) => p.id === '{{providerId}}' && p.running)) break;
                await new Promise((r) => setTimeout(r, 50));
              }
              const before = await call('status');
              await call('signIn');
              let accounts = [];
              for (let i = 0; i < 100 && accounts.length === 0; i++) {
                await new Promise((r) => setTimeout(r, 100));
                accounts = await call('accounts');
              }
              for (let i = 0; i < 20 && (await call('status')).signingIn; i++) await new Promise((r) => setTimeout(r, 100));
              accounts = await call('accounts');  // with its label now
              const from = new Date(); from.setHours(0, 0, 0, 0);
              const range = { from: from.toISOString(), to: new Date(from.getTime() + 2 * 86400000).toISOString() };
              const calendars = await settle(call('listCalendars', accounts[0]));
              const events = await settle(call('listEvents', accounts[0], range));
              const after = await call('status');
              act.update({ expanded: ui.text(JSON.stringify({ before, after, accounts, calendars, events })) });
            }
            """);

    /// <summary>Signs <paramref name="pluginId"/> in through a consumer plugin and returns what it read.</summary>
    private JsonElement SignInAndRead(FakeCalendarApi api, string pluginId, string providerId, string realBase, Dictionary<string, string> settings)
    {
        CopyProviderTo(pluginId, realBase, api.Url);
        WriteSignInConsumer(providerId);
        StartOAuthHost(OAuthServices(api));
        SetSettings(pluginId, settings);
        EnableOptional();
        _host!.StartAll();
        var result = WaitForUpdate(s => s.PluginId == "test.consumer", "signed-in provider results");
        return JsonDocument.Parse(Text(result.View.Expanded)).RootElement;
    }

    private void StartOAuthHost(PluginServices services)
    {
        _host = new PluginHost(services, FastRestart);
        _host.ActivityUpdated += _updates.Enqueue;
        _host.PageUpdated += MirrorPage;
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
    }

    private static long LocalSeconds(int dayOffset, int hour) =>
        new DateTimeOffset(DateTime.Today.AddDays(dayOffset).AddHours(hour)).ToUnixTimeSeconds();

    private static string LocalIso(int dayOffset, int hour) =>
        new DateTimeOffset(DateTime.Today.AddDays(dayOffset).AddHours(hour)).ToString("yyyy-MM-dd'T'HH:mm:sszzz");

    [Fact]
    public void Google_signs_in_from_the_calendar_page_and_its_events_appear()
    {
        var today = DateTime.Today.ToString("yyyy-MM-dd");
        var tomorrow = DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");
        using var api = new FakeCalendarApi((path, _) => path.StartsWith("/calendar/v3/calendars/primary/events", StringComparison.Ordinal)
            ? $$"""
              { "summary": "me@example.com", "items": [
                { "id": "a", "status": "confirmed", "summary": "Design review", "start": { "dateTime": "{{LocalIso(0, 23)}}" }, "end": { "dateTime": "{{LocalIso(1, 0)}}" },
                  "hangoutLink": "https://meet.google.com/abc-defg-hij", "attendees": [ { "self": true, "responseStatus": "accepted" } ] },
                { "id": "b", "status": "confirmed", "summary": "Offsite", "start": { "date": "{{today}}" }, "end": { "date": "{{tomorrow}}" } },
                { "id": "c", "status": "cancelled", "summary": "Gone", "start": { "dateTime": "{{LocalIso(0, 22)}}" }, "end": { "dateTime": "{{LocalIso(0, 23)}}" } }
              ] }
              """
            : null);
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.calendar"), Path.Combine(_root, "com.o_o.calendar"));
        CopyProviderTo("com.o_o.cal.google", "https://www.googleapis.com", api.Url);
        StartOAuthHost(OAuthServices(api));
        SetSettings("com.o_o.cal.google", new() { ["clientId"] = "cid.apps.googleusercontent.com", ["clientSecret"] = "shh" });
        EnableOptional();
        _host!.StartAll();

        // The 日程 page offers to sign in; clicking the row opens the (fake) browser.
        var page = WaitForPage(s => s.PluginId == "com.o_o.calendar" && Texts(s.View.Expanded).Contains("登录 Google 日历"), "sign-in row");
        var row = Nodes(page.View.Expanded).First(n => n.Type == "item" && Texts(n).Contains("登录 Google 日历"));
        _host.Dispatch("com.o_o.calendar", row.Handlers["onClick"]);

        // Once signed in, the row goes and the events come (the aggregator looks again every few seconds).
        page = WaitForPage(s => s.PluginId == "com.o_o.calendar" && Texts(s.View.Expanded).Contains("Design review") &&
                                !Texts(s.View.Expanded).Contains("登录 Google 日历"), "events after sign-in");
        var texts = Texts(page.View.Expanded).ToList();
        Assert.Contains("Offsite", texts);
        Assert.DoesNotContain("Gone", texts);

        var call = api.Requests.First(r => r.StartsWith("GET /calendar/v3/calendars/primary/events?timeMin=", StringComparison.Ordinal));
        Assert.Contains("singleEvents=true", call);
        Assert.Contains("auth=Bearer at-1", call);                  // added by the host, never seen by the plugin
        var exchange = api.Requests.First(r => r.StartsWith("POST /token", StringComparison.Ordinal));
        Assert.Contains("client_secret=shh", exchange);
        Assert.Contains("code_verifier=", exchange);
    }

    [Fact]
    public void Google_maps_meetings_all_day_events_and_labels_the_account_with_the_calendar_name()
    {
        using var api = new FakeCalendarApi((path, _) => path.StartsWith("/calendar/v3/calendars/primary/events", StringComparison.Ordinal)
            ? $$"""
              { "summary": "me@example.com", "items": [
                { "id": "a", "summary": "Sync", "start": { "dateTime": "{{LocalIso(0, 10)}}" }, "end": { "dateTime": "{{LocalIso(0, 11)}}" },
                  "conferenceData": { "entryPoints": [ { "entryPointType": "phone", "uri": "tel:+1" }, { "entryPointType": "video", "uri": "https://zoom.us/j/1" } ] } },
                { "id": "b", "summary": "Holiday", "start": { "date": "{{DateTime.Today:yyyy-MM-dd}}" }, "end": { "date": "{{DateTime.Today.AddDays(1):yyyy-MM-dd}}" } }
              ] }
              """
            : null);
        var result = SignInAndRead(api, "com.o_o.cal.google", "google", "https://www.googleapis.com", new() { ["clientId"] = "cid" });

        Assert.True(result.GetProperty("before").GetProperty("needsSignIn").GetBoolean());
        Assert.False(result.GetProperty("after").GetProperty("needsSignIn").GetBoolean());
        Assert.Equal("Google · me@example.com", result.GetProperty("accounts")[0].GetProperty("label").GetString());
        var events = result.GetProperty("events").GetProperty("ok").EnumerateArray().ToList();
        Assert.Equal("https://zoom.us/j/1", events.Single(e => e.GetProperty("title").GetString() == "Sync").GetProperty("meetingUrl").GetString());
        var holiday = events.Single(e => e.GetProperty("title").GetString() == "Holiday");
        Assert.True(holiday.GetProperty("allDay").GetBoolean());
        Assert.Equal(DateTime.Today.ToString("yyyy-MM-dd"), holiday.GetProperty("start").GetString());
    }

    [Fact]
    public void Feishu_reads_every_readable_calendar_and_expands_recurring_series()
    {
        string? Route(string path, string? body)
        {
            if (path.StartsWith("/open-apis/authen/v1/user_info", StringComparison.Ordinal))
            {
                return """{"code":0,"msg":"ok","data":{"name":"小明","email":"ming@corp.com"}}""";
            }
            if (path.StartsWith("/open-apis/calendar/v4/calendars?", StringComparison.Ordinal))
            {
                return """
                  {"code":0,"msg":"ok","data":{"has_more":false,"calendar_list":[
                    {"calendar_id":"cal_primary","summary":"小明","type":"primary","role":"owner"},
                    {"calendar_id":"cal_team","summary":"团队","type":"shared","role":"reader"},
                    {"calendar_id":"cal_busy","summary":"只看忙闲","type":"shared","role":"free_busy_reader"},
                    {"calendar_id":"cal_room","summary":"会议室","type":"resource","role":"reader"}]}}
                  """;
            }
            if (path.StartsWith("/open-apis/calendar/v4/calendars/cal_primary/events?", StringComparison.Ordinal))
            {
                return $$$"""
                  {"code":0,"msg":"ok","data":{"has_more":false,"items":[
                    {"event_id":"standup","summary":"站会","status":"confirmed","recurrence":"FREQ=DAILY;INTERVAL=1",
                     "start_time":{"timestamp":"{{{LocalSeconds(-3, 10)}}}","timezone":"Asia/Shanghai"},"end_time":{"timestamp":"{{{LocalSeconds(-3, 10) + 900}}}"}},
                    {"event_id":"review","summary":"评审","status":"confirmed","vchat":{"meeting_url":"https://vc.feishu.cn/j/123"},
                     "location":{"name":"3楼"},
                     "start_time":{"timestamp":"{{{LocalSeconds(0, 15)}}}"},"end_time":{"timestamp":"{{{LocalSeconds(0, 16)}}}"}},
                    {"event_id":"gone","summary":"取消了","status":"cancelled",
                     "start_time":{"timestamp":"{{{LocalSeconds(0, 17)}}}"},"end_time":{"timestamp":"{{{LocalSeconds(0, 18)}}}"}}]}}
                  """;
            }
            if (path.StartsWith("/open-apis/calendar/v4/calendars/cal_team/events?", StringComparison.Ordinal))
            {
                return $$$"""
                  {"code":0,"msg":"ok","data":{"has_more":false,"items":[
                    {"event_id":"party","summary":"团建","status":"confirmed",
                     "start_time":{"date":"{{{DateTime.Today.AddDays(1):yyyy-MM-dd}}}"},"end_time":{"date":"{{{DateTime.Today.AddDays(2):yyyy-MM-dd}}}"}}]}}
                  """;
            }
            return null;
        }
        using var api = new FakeCalendarApi(Route);
        var result = SignInAndRead(api, "com.o_o.cal.feishu", "feishu", "https://open.feishu.cn", new() { ["appId"] = "cli_123", ["appSecret"] = "s3" });

        Assert.Equal("飞书 · ming@corp.com", result.GetProperty("accounts")[0].GetProperty("label").GetString());
        Assert.Equal(["小明", "团队"], result.GetProperty("calendars").GetProperty("ok").EnumerateArray().Select(c => c.GetProperty("name").GetString()));
        var events = result.GetProperty("events").GetProperty("ok").EnumerateArray().ToList();
        // The daily stand-up that began three days ago, today and tomorrow; the review; the all-day team event.
        Assert.Equal(2, events.Count(e => e.GetProperty("title").GetString() == "站会"));
        Assert.Contains(events, e => e.GetProperty("title").GetString() == "站会" &&
                                     DateTimeOffset.Parse(e.GetProperty("start").GetString()!).ToUnixTimeSeconds() == LocalSeconds(0, 10));
        var review = events.Single(e => e.GetProperty("title").GetString() == "评审");
        Assert.Equal("https://vc.feishu.cn/j/123", review.GetProperty("meetingUrl").GetString());
        Assert.Equal("3楼", review.GetProperty("location").GetString());
        Assert.True(events.Single(e => e.GetProperty("title").GetString() == "团建").GetProperty("allDay").GetBoolean());
        Assert.DoesNotContain(events, e => e.GetProperty("title").GetString() == "取消了");

        Assert.Contains(api.Requests, r => r.StartsWith($"GET /open-apis/calendar/v4/calendars/cal_primary/events?start_time={LocalSeconds(0, 0)}", StringComparison.Ordinal) &&
                                           r.Contains("auth=Bearer at-1"));
        Assert.DoesNotContain(api.Requests, r => r.Contains("cal_busy/events") || r.Contains("cal_room/events"));
        var exchange = api.Requests.First(r => r.StartsWith("POST /token", StringComparison.Ordinal));
        Assert.Contains("\"client_id\":\"cli_123\"", exchange);
        Assert.Contains("\"code_verifier\"", exchange);
    }

    [Fact]
    public void DingTalk_finds_the_user_then_reads_the_expanded_events_view_with_its_own_token_header()
    {
        using var api = new FakeCalendarApi((path, _) =>
        {
            if (path == "/v1.0/contact/users/me")
            {
                return """{"unionId":"U123","nick":"小红","email":"hong@corp.com"}""";
            }
            if (path.StartsWith("/v1.0/calendar/users/U123/calendars/primary/eventsview?", StringComparison.Ordinal))
            {
                return $$$"""
                  {"events":[
                    {"id":"e1","summary":"周会","status":"confirmed","isAllDay":false,"seriesMasterId":"m1",
                     "start":{"dateTime":"{{{LocalIso(0, 9)}}}","timeZone":"Asia/Shanghai"},"end":{"dateTime":"{{{LocalIso(0, 10)}}}"},
                     "onlineMeetingInfo":{"type":"dingtalk","url":"https://meeting.dingtalk.com/j/9"},"location":{"displayName":"A区"}},
                    {"id":"e2","summary":"年假","status":"confirmed","isAllDay":true,
                     "start":{"date":"{{{DateTime.Today:yyyy-MM-dd}}}"},"end":{"date":"{{{DateTime.Today.AddDays(1):yyyy-MM-dd}}}"}},
                    {"id":"e3","summary":"取消","status":"cancelled","start":{"dateTime":"{{{LocalIso(0, 11)}}}"},"end":{"dateTime":"{{{LocalIso(0, 12)}}}"}}
                  ],"nextToken":null}
                  """;
            }
            return null;
        });
        var result = SignInAndRead(api, "com.o_o.cal.dingtalk", "dingtalk", "https://api.dingtalk.com", new() { ["clientId"] = "ding_cid", ["clientSecret"] = "ds" });

        Assert.Equal("钉钉 · hong@corp.com", result.GetProperty("accounts")[0].GetProperty("label").GetString());
        var events = result.GetProperty("events").GetProperty("ok").EnumerateArray().ToList();
        Assert.Equal(["周会", "年假"], events.Select(e => e.GetProperty("title").GetString()));
        Assert.Equal("https://meeting.dingtalk.com/j/9", events[0].GetProperty("meetingUrl").GetString());
        Assert.Equal("A区", events[0].GetProperty("location").GetString());
        Assert.True(events[1].GetProperty("allDay").GetBoolean());

        // DingTalk's token goes in its own header, never as a bearer token; the token request is DingTalk's shape.
        var view = api.Requests.First(r => r.Contains("/eventsview?"));
        Assert.Contains("ding=ding-at", view);
        Assert.Contains("auth= ", view);
        Assert.Contains(api.Requests, r => r.StartsWith("POST /token", StringComparison.Ordinal) && r.Contains("\"grantType\":\"authorization_code\"") && r.Contains("\"clientSecret\":\"ds\""));
    }

    [Fact]
    public void Without_an_app_id_signing_in_explains_what_to_fill_in()
    {
        using var api = new FakeCalendarApi((_, _) => null);
        CopyProviderTo("com.o_o.cal.feishu", "https://open.feishu.cn", api.Url);
        WritePluginWithManifest("test.consumer", "", "", """
            import { island, ui } from '@island/api';
            export async function activate() {
              const act = island.activity.create('r');
              for (let i = 0; i < 100; i++) {
                if ((await island.providers.list('calendar')).some((p) => p.id === 'feishu' && p.running)) break;
                await new Promise((r) => setTimeout(r, 50));
              }
              const status = await island.providers.call('calendar', 'feishu', 'status');
              const err = await island.providers.call('calendar', 'feishu', 'signIn').then(() => 'started', (e) => e.message);
              act.update({ expanded: ui.text(JSON.stringify({ status, err })) });
            }
            """);
        StartOAuthHost(OAuthServices(api));
        EnableOptional();
        _host!.StartAll();
        var result = JsonDocument.Parse(Text(WaitForUpdate(s => s.PluginId == "test.consumer", "status").View.Expanded)).RootElement;
        Assert.False(result.GetProperty("status").GetProperty("configured").GetBoolean());
        Assert.True(result.GetProperty("status").GetProperty("needsSignIn").GetBoolean());
        Assert.Contains("App ID", result.GetProperty("err").GetString());
        Assert.DoesNotContain(api.Requests, r => r.Contains("/token"));
    }
}
