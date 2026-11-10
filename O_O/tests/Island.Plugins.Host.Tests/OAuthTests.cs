using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

public sealed partial class PluginHostTests
{
    /// <summary>A fake provider: a token endpoint and an API that echoes the bearer token it got.</summary>
    private sealed class FakeOAuthServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        public readonly ConcurrentQueue<string> Requests = new();
        public int Issued;

        public FakeOAuthServer()
        {
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
                Requests.Enqueue($"{ctx.Request.HttpMethod} {ctx.Request.Url!.AbsolutePath} {body} {ctx.Request.Headers["Authorization"]}");
                string reply;
                if (ctx.Request.Url.AbsolutePath == "/token")
                {
                    var n = Interlocked.Increment(ref Issued);
                    // The first token is already expired, so the first API call has to refresh it.
                    reply = $$"""{"access_token":"at{{n}}","refresh_token":"rt{{n}}","expires_in":{{(n == 1 ? 0 : 3600)}}}""";
                }
                else
                {
                    reply = $$"""{"token":"{{ctx.Request.Headers["Authorization"]}}"}""";
                }
                var bytes = Encoding.UTF8.GetBytes(reply);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public void OAuth_signs_in_through_the_browser_and_the_host_refreshes_and_scopes_the_token()
    {
        using var provider = new FakeOAuthServer();
        Uri? authorize = null;
        var services = Services with
        {
            OAuthCallbackPort = FreePort(),
            OAuthProfiles = new Dictionary<string, OAuthProfile>
            {
                ["google"] = new("google", "Google", new(provider.Url + "/authorize"), new(provider.Url + "/token"),
                    OAuthTokenStyle.Form, ["127.0.0.1"]),
            },
            // The "browser": remembers the page and, like the user signing in, returns to the callback with a code.
            OpenBrowser = uri =>
            {
                authorize = uri;
                var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
                _ = Task.Run(async () =>
                {
                    using var browser = new HttpClient();
                    await browser.GetStringAsync($"http://127.0.0.1:{FreePortOf(q["redirect_uri"]!)}/oauth/callback?state=forged&code=evil")
                        .ContinueWith(_ => { });
                    await browser.GetStringAsync($"{q["redirect_uri"]}?state={q["state"]}&code=good");
                });
                return Task.CompletedTask;
            },
        };
        _permissions = "\"oauth:google\", \"network:localhost\"";
        StartWith($$"""
            import { oauth, http } from '@island/api';
            export async function activate() {
              const account = await oauth.signIn('google', { clientId: 'cid', clientSecret: 'cs', scopes: ['calendar.events.readonly'], label: 'me' });
              console.info('signed in ' + account.provider + ' ' + account.label + ' ' + (account.handle.startsWith('oauth_')));
              const r = await http.fetch('{{provider.Url}}/api', { auth: account });
              console.info('api ' + r.json().token);
              console.info('accounts ' + (await oauth.accounts()).length);
              try { await oauth.signIn('feishu', { clientId: 'x' }); } catch (e) { console.info('feishu: ' + e.message); }
            }
            """, services);

        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "signed in google me true"), "sign-in");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "api Bearer at2"), "refreshed token added by the host");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message == "accounts 1"), "account listed");
        WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("feishu: ") && l.Message.Contains("oauth:feishu")), "undeclared provider refused");

        var q = System.Web.HttpUtility.ParseQueryString(authorize!.Query);
        Assert.Equal("cid", q["client_id"]);
        Assert.Equal("S256", q["code_challenge_method"]);
        Assert.Equal("calendar.events.readonly", q["scope"]);
        var exchange = provider.Requests.First(r => r.StartsWith("POST /token") && r.Contains("grant_type=authorization_code"));
        Assert.Contains("code=good", exchange);                 // the forged callback was ignored
        Assert.Contains("code_verifier=", exchange);
        Assert.Contains(provider.Requests, r => r.StartsWith("POST /token") && r.Contains("grant_type=refresh_token") && r.Contains("refresh_token=rt1"));
    }

    private static int FreePortOf(string url) => new Uri(url).Port;
}
