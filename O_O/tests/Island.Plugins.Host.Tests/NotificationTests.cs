using System.Collections.Concurrent;
using Island.Platform;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>notifications.listen, the display cut and satellites (docs/design/08-notifications-and-satellites.md).</summary>
public sealed partial class PluginHostTests
{
    private const string WeChat = "com.tencent.xinWeChat";
    private const string Qq = "com.tencent.qq";

    private sealed class FakeListener : INotificationListener
    {
        public IReadOnlyCollection<string> Watched = [];
        public NotificationAccess AccessState = NotificationAccess.Granted;
        public readonly ConcurrentQueue<string> Activated = new();

        public NotificationListenerCapabilities Capabilities => NotificationListenerCapabilities.Content | NotificationListenerCapabilities.Badge;
        public NotificationAccess Access => AccessState;
        public string? AccessHint => AccessState == NotificationAccess.Granted ? null : "请在系统设置中允许";
        public event Action<PlatformNotification>? Received;
        public event Action<string, int?>? BadgeChanged;
        public void Watch(IReadOnlyCollection<string> appIds) => Watched = appIds;
        public int? Unread(string appId) => appId == WeChat ? 3 : null;
        public byte[]? GetAppIcon(string appId, int size) => null;
        public bool Activate(string appId) { Activated.Enqueue(appId); return true; }
        public void OpenAccessSettings() { }
        public void Dispose() { }
        public void Post(string app, string title, string body) =>
            Received?.Invoke(new PlatformNotification(app, app == WeChat ? "微信" : "QQ", title, null, body, "thread-" + title, DateTimeOffset.UtcNow));
        public void Badge(string app, int? n) => BadgeChanged?.Invoke(app, n);
    }

    private readonly ConcurrentQueue<SatelliteSnapshot> _satellites = new();

    private (FakeListener Listener, PluginServices Services) StartNotifier(string js, string permissions, NotificationDisplay? display = null)
    {
        var listener = new FakeListener();
        var services = Services with { NotificationListener = listener };
        if (display is { } d)
        {
            services.NotificationPrefs.Default = d;
        }
        _permissions = permissions;
        _contributes = """{ "satellites": [{ "id": "message", "title": "消息", "side": "auto" }] }""";
        WritePlugin("test.plugin", js);
        _host = new PluginHost(services, FastRestart);
        _host.SatelliteUpdated += _satellites.Enqueue;
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
        WaitForState(PluginState.AwaitingConsent); // reading other apps' notifications always asks
        _host.SetGranted("test.plugin", [.. permissions.Split(',').Select(p => p.Trim().Trim('"'))]);
        return (listener, services);
    }

    private const string ListenBoth = $"\"notifications.listen:{WeChat}\", \"notifications.listen:badge:{Qq}\"";

    [Fact]
    public void Plugins_see_only_granted_apps_and_the_host_cuts_each_notification_to_the_users_choice()
    {
        var (listener, _) = StartNotifier("""
            import { notifications } from '@island/api';
            export function activate() {
              notifications.onReceived((n) => console.info(`got ${n.app.id}|${n.app.name}|${n.title}|${n.body}|${n.subtitle}|${n.threadId}`));
              console.info('ready');
            }
            """, ListenBoth, NotificationDisplay.SenderOnly);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "ready"), "ready");
        WaitFor(() => listener.Watched.Count == 2 ? "ok" : null, "watching both apps");
        Assert.Contains(WeChat, listener.Watched);
        Assert.Contains(Qq, listener.Watched);

        listener.Post(Qq, "小明", "不该看到");           // badge-only permission: no content at all
        listener.Post(WeChat, "老王", "晚上吃什么");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("got ")), "a notification");
        var got = _logs.Where(l => l.Message.StartsWith("got ")).Select(l => l.Message).ToList();
        Assert.Single(got);
        Assert.Equal($"got {WeChat}|微信|老王|null|null|thread-老王", got[0]);  // body cut: sender only
    }

    [Theory]
    [InlineData(NotificationDisplay.Full, "got 老王|晚上吃什么")]
    [InlineData(NotificationDisplay.Hidden, "got null|null")]
    public void The_display_choice_is_applied_before_the_plugin_sees_anything(NotificationDisplay display, string expected)
    {
        var (listener, _) = StartNotifier("""
            import { notifications } from '@island/api';
            export function activate() {
              notifications.onReceived((n) => console.info(`got ${n.title}|${n.body}`));
              console.info('ready');
            }
            """, $"\"notifications.listen:{WeChat}\"", display);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "ready"), "ready");
        listener.Post(WeChat, "老王", "晚上吃什么");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("got ")), "a notification");
        Assert.Equal(expected, _logs.First(l => l.Message.StartsWith("got ")).Message);
    }

    [Fact]
    public void List_unread_activate_and_status_respect_the_granted_apps()
    {
        var (listener, _) = StartNotifier($$"""
            import { notifications } from '@island/api';
            const settle = (p) => p.then((v) => 'ok:' + JSON.stringify(v), (e) => 'err:' + e.message);
            export async function activate() {
              notifications.onBadge((app, n) => console.info(`badge ${app} ${n}`));
              console.info('ready');
              await new Promise((r) => notifications.onReceived(() => r()));
              const list = await notifications.list();
              console.info('list ' + list.map((n) => n.title).join(','));
              console.info('unread ' + await settle(notifications.unread('{{WeChat}}')));
              console.info('qq ' + await settle(notifications.unread('{{Qq}}')));
              console.info('other ' + await settle(notifications.unread('com.example.other')));
              console.info('listQq ' + await settle(notifications.list({ app: '{{Qq}}' })));
              console.info('activate ' + await settle(notifications.activate(list[0])));
              const s = await notifications.status();
              console.info('status ' + s.granted + ' ' + s.access);
            }
            """, ListenBoth, NotificationDisplay.Full);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "ready"), "ready");
        listener.Badge(Qq, 5);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == $"badge {Qq} 5"), "badge pushed");
        listener.Post(WeChat, "老王", "hi");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("status ")), "status");
        var logs = _logs.Select(l => l.Message).ToList();
        Assert.Contains("list 老王", logs);
        Assert.Contains("unread ok:3", logs);
        Assert.Contains("qq ok:null", logs);                       // badge permission: allowed, the platform knows none
        Assert.Contains(logs, l => l.StartsWith("other err:") && l.Contains("may not"));
        Assert.Contains(logs, l => l.StartsWith("listQq err:") && l.Contains("may not"));  // badge permission gives no content
        Assert.Contains("activate ok:true", logs);
        Assert.Equal([WeChat], listener.Activated);
        Assert.Contains("status true Granted", logs);
    }

    [Fact]
    public void Without_the_permission_there_is_no_notifications_module_and_the_permission_always_asks()
    {
        Start("""
            import { notifications } from '@island/api';
            export function activate() { console.info('notifications ' + typeof notifications); }
            """);
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "notifications undefined"), "no export");
        Assert.True(PluginHost.AlwaysAsks($"notifications.listen:{WeChat}"));
    }

    [Fact]
    public void Notification_permissions_are_parsed_strictly()
    {
        var problems = new List<string>();
        var ok = PluginPermissions.Parse([$"notifications.listen:{WeChat}", $"notifications.listen:badge:{Qq}"], problems);
        Assert.NotNull(ok);
        Assert.Equal([WeChat], ok.ListenApps);
        Assert.Equal([Qq], ok.BadgeApps);
        Assert.True(ok.CanSeeApp(Qq));
        Assert.False(ok.CanSeeApp("com.other"));

        foreach (var bad in new[] { "notifications.listen:*", "notifications.listen:", "notifications.listen:a b", "notifications.listen:badge:" })
        {
            var p = new List<string>();
            Assert.Null(PluginPermissions.Parse([bad], p));
            Assert.NotEmpty(p);
        }
        var many = Enumerable.Range(0, 9).Select(i => $"notifications.listen:com.app{i}").ToList();
        Assert.Null(PluginPermissions.Parse(many, []));
    }

    [Fact]
    public void Satellites_show_replace_by_key_hide_and_are_bounded()
    {
        StartNotifier("""
            import { island, ui, notifications } from '@island/api';
            export async function activate() {
              const sat = island.satellite('message');
              const a = await sat.show(ui.row({}, [ui.appIcon('com.tencent.xinWeChat', { size: 18 }), ui.text('老王')]), { key: 'wx', seconds: 99, badge: 2, onClick: () => console.info('clicked') });
              await sat.show(ui.text('again'), { key: 'wx' });
              console.info('first ' + a);
              try { island.satellite('nope').show(ui.text('x')); } catch (e) { console.info('rejected: ' + e.message); }
              sat.hide('wx');
              let accepted = 0;
              for (let i = 0; i < 40; i++) if (await sat.show(ui.text('n' + i), { key: 'k' + (i % 5) })) accepted++;
              console.info('accepted ' + accepted);
            }
            """, $"\"notifications.listen:{WeChat}\"");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("accepted ")), "done");
        var snapshots = _satellites.ToList();
        var first = snapshots[0];
        Assert.Equal(("test.plugin", "message", "wx"), (first.PluginId, first.SatelliteId, first.Key));
        Assert.Equal(10, first.Duration.TotalSeconds);                 // seconds clamped to 10 (0 would mean: stays)
        Assert.Equal(2, first.Badge);
        Assert.Equal("auto", first.Side);
        Assert.Equal("item", first.View!.Type);                        // onClick wraps the view in an item
        Assert.Contains(Nodes(first.View), n => n.Type == "appIcon" && n.GetString("appId") == WeChat);
        Assert.Contains("first true", _logs.Select(l => l.Message));
        Assert.Contains(_logs, l => l.Message.StartsWith("rejected: Satellite 'nope' is not declared"));
        Assert.Contains(snapshots, s => s.Key == "wx" && s.View is null);   // hide
        // 2 earlier shows + 40 more: the rate limit is 30 a minute, so not all are accepted.
        Assert.InRange(int.Parse(_logs.First(l => l.Message.StartsWith("accepted ")).Message[9..]), 1, 29);
    }

    [Fact]
    public void The_im_plugin_shows_the_source_icon_merges_a_burst_and_keeps_an_unread_capsule()
    {
        var listener = new FakeListener();
        var services = Services with { NotificationListener = listener };
        services.NotificationPrefs.Default = NotificationDisplay.Full;
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.im"), Path.Combine(_root, "com.o_o.im"));
        _host = new PluginHost(services, FastRestart);
        _host.SatelliteUpdated += _satellites.Enqueue;
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
        WaitFor(() => _states.Any(s => s.Item1 == "com.o_o.im" && s.Item2 == PluginState.AwaitingConsent) ? "ok" : null, "asks first");
        _host.SetGranted("com.o_o.im", [$"notifications.listen:{WeChat}", $"notifications.listen:{Qq}"]);
        WaitFor(() => listener.Watched.Count == 2 ? "ok" : null, "watching WeChat and QQ");
        // activate() ends by reading the platform's unread count (3 for WeChat): the plugin is listening by then. The count is
        // its own capsule that stays (duration zero), separate from the message capsule.
        var unread = WaitFor(() => _satellites.LastOrDefault(s => s.Key == "unread" && s.View is not null && Texts(s.View).Contains("3")), "unread capsule from the platform's count");
        Assert.Equal(TimeSpan.Zero, unread.Duration);
        Assert.Contains(Nodes(unread.View!), n => n.Type == "appIcon" && n.GetString("appId") == WeChat);

        listener.Post(WeChat, "老王", "晚上吃什么");
        var first = WaitFor(() => _satellites.FirstOrDefault(s => s.PluginId == "com.o_o.im" && s.Key == "msg" && s.View is not null), "first notice");
        Assert.Equal(TimeSpan.FromSeconds(4), first.Duration);
        Assert.Null(first.Badge);
        Assert.Contains(Nodes(first.View!), n => n.Type == "appIcon" && n.GetString("appId") == WeChat);
        Assert.Contains("老王", Texts(first.View!));
        Assert.Contains("晚上吃什么", Texts(first.View!));

        listener.Post(WeChat, "老王", "在吗");
        var second = WaitFor(() => _satellites.LastOrDefault(s => s.Badge == 2), "merged notice");
        Assert.Equal("msg", second.Key);                        // the same notice, replaced in place
        WaitFor(() => _satellites.LastOrDefault(s => s.Key == "unread" && s.View is not null && Texts(s.View).Contains("5")), "unread capsule (3 + 2)");

        listener.Badge(WeChat, null);                           // the user read them
        WaitFor(() => _satellites.LastOrDefault(s => s.Key == "unread") is { View: null } ? "ok" : null, "unread capsule removed");
    }
}
