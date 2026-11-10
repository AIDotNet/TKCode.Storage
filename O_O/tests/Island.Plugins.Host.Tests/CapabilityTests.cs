using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Island.Plugins.Host.Tests;

/// <summary>Privacy of the natives, permissions, storage, secrets and HTTP, end to end through real plugins.</summary>
public sealed partial class PluginHostTests
{
    private string Shown() => Text(WaitForUpdate(_ => true, "a render").View.Expanded);

    private string ShownWhen(Func<string, bool> match, string what) =>
        Text(WaitForUpdate(s => match(Text(s.View.Expanded)), what).View.Expanded);

    [Fact]
    public void Plugin_code_cannot_reach_the_host_natives_even_from_a_module_imported_first()
    {
        var dir = WritePlugin("test.plugin", """
            import { probe } from './evil.js';
            import { island, ui } from '@island/api';
            const here = [typeof __island_activity, typeof __island_call, typeof __island_storage,
                          typeof __island_handoff, typeof __island_dispatch, typeof __island_permissions].join(',');
            export function activate() {
              island.home.page('a').update(ui.text(probe + '|' + here));
            }
            """);
        // Evaluated before the plugin's import of @island/api: the host loaded the API first, so the handoff is gone.
        File.WriteAllText(Path.Combine(dir, "evil.js"), """
            export const probe = [typeof __island_handoff, typeof __island_call, typeof __island_activity].join(',');
            """);
        StartDiscovered();
        Assert.Equal("undefined,undefined,undefined|undefined,undefined,undefined,undefined,undefined,undefined", Shown());
    }

    [Fact]
    public void Capabilities_the_plugin_has_no_permission_for_are_undefined()
    {
        Start("""
            import { island, ui, http, secrets, storage, granted } from '@island/api';
            export function activate() {
              island.home.page('a').update(ui.text(
                [typeof http, typeof secrets, typeof storage, granted.length].join(',')));
            }
            """);
        Assert.Equal("undefined,undefined,object,0", Shown());
    }

    [Fact]
    public void Storage_survives_a_restart_of_the_host_and_enforces_its_quota()
    {
        const string js = """
            import { island, ui, storage } from '@island/api';
            export function activate() {
              const n = (storage.get('launches') ?? 0) + 1;
              storage.set('launches', n);
              storage.set('profile', { name: '岛', tags: ['a', 'b'] });
              let quota = 'none';
              try { storage.set('big', 'x'.repeat(4000)); } catch (e) { quota = e.message; }
              island.home.page('a').update(ui.text(
                `launches=${n} name=${storage.get('profile').name} keys=${storage.keys().join('+')} quota=${quota}`));
            }
            """;
        var services = Services with { StorageQuotaBytes = 2000 };
        StartWith(js, services);
        Assert.StartsWith("launches=1 name=岛 keys=launches+profile quota=Storage quota", Shown());

        _host!.Dispose(); // flushes storage
        _updates.Clear();
        StartWith(js, services);
        Assert.StartsWith("launches=2 ", ShownWhen(t => t.StartsWith("launches=2"), "second launch"));
        Assert.True(File.Exists(Path.Combine(_data, "test.plugin", "storage.json")));
    }

    [Fact]
    public void Secrets_round_trip_through_the_store_scoped_to_the_plugin()
    {
        _permissions = "\"secrets\"";
        Start("""
            import { island, ui, secrets } from '@island/api';
            export async function activate() {
              const act = island.home.page('a');
              await secrets.set('token', 's3cret');
              const back = await secrets.get('token');
              const missing = await secrets.get('nope');
              const removed = await secrets.delete('other');
              act.update(ui.text(`${back}|${missing}|${removed}`));
            }
            """);
        Assert.Equal("s3cret|null|false", ShownWhen(t => t.Contains('|'), "secret round trip"));
        Assert.Equal("s3cret", _secrets.Get("O_O/test.plugin", "token"));
    }

    [Fact]
    public void Http_reaches_allowed_hosts_and_refuses_others_including_through_redirects()
    {
        using var server = new TestServer();
        _permissions = $"\"network:{PluginPermissions.Localhost}\"";
        Start($$"""
            import { island, ui, http } from '@island/api';
            const settle = (p) => p.then((v) => 'ok:' + v, (e) => 'err:' + e.message);
            export async function activate() {
              const act = island.home.page('a');
              const hello = await settle(http.getJson('{{server.Url}}hello').then((j) => j.msg));
              const posted = await settle(http.fetch('{{server.Url}}echo', { method: 'POST', body: { a: 1 } })
                .then((r) => r.status + ' ' + r.text()));
              const other = await settle(http.fetch('https://example.org/'));
              const hop = await settle(http.fetch('{{server.Url}}redirect'));
              const header = await settle(http.fetch('{{server.Url}}hello', { headers: { Host: 'evil' } }));
              act.update(ui.text([hello, posted, other, hop, header].join('\n')));
            }
            """);
        var lines = ShownWhen(t => t.Split('\n').Length == 5, "five results").Split('\n');
        Assert.Equal("ok:hi", lines[0]);
        Assert.Equal("ok:200 {\"a\":1}", lines[1]);
        Assert.Contains("没有访问 'example.org' 的权限", lines[2]);
        Assert.Contains("没有访问 'example.com' 的权限", lines[3]); // the redirect target, refused before connecting
        Assert.Contains("'Host' cannot be set", lines[4]);
        Assert.DoesNotContain("example", string.Join(",", server.Paths)); // nothing was fetched from elsewhere
    }

    [Fact]
    public void Manifests_with_unknown_or_too_broad_permissions_are_rejected()
    {
        _permissions = "\"secrets\", \"camera\", \"network:*.com\"";
        WritePlugin("test.plugin", "export function activate() {}");
        _host = new PluginHost(Services, FastRestart);
        var problems = _host.Discover(_root, bundled: true);
        var problem = Assert.Single(problems);
        Assert.Contains("unknown permission 'camera'", problem);
        Assert.Contains("invalid network permission 'network:*.com'", problem);
        Assert.Empty(_host.Packages);
    }

    private void StartDiscovered() => StartWith(null, Services);

    private void StartWith(string? js, PluginServices services)
    {
        if (js is not null)
        {
            WritePlugin("test.plugin", js);
        }
        _host = new PluginHost(services, FastRestart);
        _host.PageUpdated += MirrorPage;
        SubscribeLive(_host);
        _host.PluginStateChanged += (p, s, r) => _states.Enqueue((p, s, r));
        _host.PluginLog += _logs.Enqueue;
        Assert.Empty(_host.Discover(_root, bundled: true));
        EnableOptional();
        _host.StartAll();
    }

    /// <summary>A loopback HTTP server: /hello (JSON), /echo (echoes the body), /redirect (to example.com).</summary>
    private sealed class TestServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public List<string> Paths { get; } = [];

        public TestServer()
        {
            var port = FreePort();
            Url = $"http://127.0.0.1:{port}/";
            _listener.Prefixes.Add(Url);
            _listener.Start();
            _ = Serve();
        }

        public string Url { get; }

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
                lock (Paths)
                {
                    Paths.Add(ctx.Request.Url!.AbsolutePath);
                }
                var path = ctx.Request.Url!.AbsolutePath;
                if (path == "/redirect-local")
                {
                    // Same machine, different host name: still not a host the credential may go to.
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = $"http://localhost:{ctx.Request.Url.Port}/whoami";
                    ctx.Response.Close();
                    continue;
                }
                if (path == "/whoami")
                {
                    var authorized = ctx.Request.Headers["Authorization"] == "Bearer tok-123";
                    var answer = Encoding.UTF8.GetBytes(authorized ? "authorized" : "anonymous");
                    await ctx.Response.OutputStream.WriteAsync(answer);
                    ctx.Response.Close();
                    continue;
                }
                if (path == "/redirect")
                {
                    ctx.Response.StatusCode = 302;
                    ctx.Response.RedirectLocation = "https://example.com/stolen";
                    ctx.Response.Close();
                    continue;
                }
                var body = path == "/echo"
                    ? await new StreamReader(ctx.Request.InputStream).ReadToEndAsync()
                    : """{"msg":"hi"}""";
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        private static int FreePort()
        {
            using var socket = new TcpListener(IPAddress.Loopback, 0);
            socket.Start();
            return ((IPEndPoint)socket.LocalEndpoint).Port;
        }

        public void Dispose() => _listener.Close();
    }
}

public class PluginPermissionsTests
{
    private static PluginPermissions Parse(params string[] p)
    {
        var problems = new List<string>();
        return PluginPermissions.Parse(p, problems) ?? throw new Xunit.Sdk.XunitException(string.Join("; ", problems));
    }

    [Fact]
    public void Wildcards_cover_subdomains_but_not_the_apex()
    {
        var p = Parse("network:*.feishu.cn", "network:api.deepseek.com");
        Assert.True(p.AllowsHost("open.feishu.cn"));
        Assert.True(p.AllowsHost("a.b.feishu.cn"));
        Assert.False(p.AllowsHost("feishu.cn"));
        Assert.False(p.AllowsHost("evilfeishu.cn"));
        Assert.True(p.AllowsHost("API.DeepSeek.com"));
        Assert.False(p.AllowsHost("deepseek.com"));
    }

    [Fact]
    public void Loopback_needs_the_localhost_permission()
    {
        Assert.False(Parse("network:api.deepseek.com").AllowsHost("127.0.0.1"));
        var local = Parse("network:localhost");
        Assert.True(local.AllowsHost("localhost"));
        Assert.True(local.AllowsHost("127.0.0.1"));
        Assert.True(local.AllowsHost("::1"));
    }
}
