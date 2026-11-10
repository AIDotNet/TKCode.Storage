using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Tests;

/// <summary>
/// The shipped API-key providers (plugins/com.o_o.ai.deepseek, .kimi, .newapi), against a loopback server that answers
/// like the real services. Nothing leaves the machine: the services' base URLs are pointed at the server.
/// </summary>
public sealed partial class PluginHostTests
{
    /// <summary>Answers each path with a fixed JSON body when the bearer key is one it knows, 401 otherwise.</summary>
    private sealed class FakeBillingServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private readonly Dictionary<string, string> _routes;
        private readonly HashSet<string> _keys;

        public FakeBillingServer(Dictionary<string, string> routes, params string[] keys)
        {
            _routes = routes;
            _keys = [.. keys];
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
                var auth = ctx.Request.Headers["Authorization"] ?? "";
                lock (Requests)
                {
                    Requests.Add($"{ctx.Request.HttpMethod} {ctx.Request.Url!.PathAndQuery} {auth}");
                }
                var known = auth.StartsWith("Bearer ", StringComparison.Ordinal) && _keys.Contains(auth["Bearer ".Length..]);
                if (!known || !_routes.TryGetValue(ctx.Request.Url!.AbsolutePath, out var body))
                {
                    ctx.Response.StatusCode = known ? 404 : 401;
                    body = """{"error":{"message":"nope"}}""";
                }
                var bytes = Encoding.UTF8.GetBytes(body);
                ctx.Response.ContentType = "application/json";
                await ctx.Response.OutputStream.WriteAsync(bytes);
                ctx.Response.Close();
            }
        }

        public void Dispose() => _listener.Close();
    }

    /// <summary>
    /// Copies a shipped provider plugin, optionally pointing its service at <paramref name="baseUrl"/> (the real hosts
    /// replaced, and loopback allowed), then runs it with a consumer that asks every account for its usage.
    /// Returns, per provider id, { accounts: {ok|err}, usage: [{ok|err}…] }.
    /// </summary>
    private JsonElement RunProvider(string pluginId, string providerId, Dictionary<string, string> settings,
        string? baseUrl = null, params string[] realBases)
    {
        var dir = Path.Combine(_root, pluginId);
        CopyPlugin(RepoPlugin(pluginId), dir);
        if (baseUrl is not null)
        {
            var main = Path.Combine(dir, "main.js");
            var js = File.ReadAllText(main);
            foreach (var real in realBases)
            {
                Assert.Contains(real, js);
                js = js.Replace(real, baseUrl);
            }
            File.WriteAllText(main, js);
            var manifest = Path.Combine(dir, "plugin.json");
            File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("\"permissions\": [", "\"permissions\": [\"network:localhost\", "));
        }
        WritePluginWithManifest("test.consumer", "", "", $$"""
            import { island, ui } from '@island/api';
            const settle = (p) => p.then((v) => ({ ok: v }), (e) => ({ err: e.message }));
            export async function activate() {
              const act = island.home.page('r');
              for (let i = 0; i < 100; i++) {
                const list = await island.providers.list('ai-usage');
                if (list.some((p) => p.id === '{{providerId}}' && p.running)) break;
                await new Promise((r) => setTimeout(r, 50));
              }
              const accounts = await settle(island.providers.call('ai-usage', '{{providerId}}', 'accounts'));
              const usage = [];
              for (const a of accounts.ok ?? []) usage.push(await settle(island.providers.call('ai-usage', '{{providerId}}', 'usage', a)));
              act.update(ui.text(JSON.stringify({ accounts, usage })));
            }
            """);

        _host = new PluginHost(Services, FastRestart);
        _host.PageUpdated += MirrorPage;
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

    private static JsonElement Ok(JsonElement settled) =>
        settled.TryGetProperty("ok", out var ok) ? ok : throw new Xunit.Sdk.XunitException("failed: " + settled);

    private static string Err(JsonElement settled) =>
        settled.TryGetProperty("err", out var err) ? err.GetString()! : throw new Xunit.Sdk.XunitException("succeeded: " + settled);

    [Fact]
    public void DeepSeek_shows_each_keys_balance_and_says_when_a_key_is_wrong()
    {
        using var server = new FakeBillingServer(new()
        {
            ["/user/balance"] = """{"is_available":true,"balance_infos":[{"currency":"CNY","total_balance":"110.00","granted_balance":"10.00","topped_up_balance":"100.00"}]}""",
        }, "sk-deep-good");

        var r = RunProvider("com.o_o.ai.deepseek", "deepseek",
            new() { ["key1"] = "sk-deep-good", ["key2"] = "sk-deep-revoked", ["name2"] = "旧 Key" },
            server.Url, "https://api.deepseek.com");

        var accounts = Ok(r.GetProperty("accounts"));
        Assert.Equal(["key1", "key2"], accounts.EnumerateArray().Select(a => a.GetProperty("handle").GetString()));
        Assert.Equal(["DeepSeek", "旧 Key"], accounts.EnumerateArray().Select(a => a.GetProperty("label").GetString()));
        Assert.DoesNotContain("sk-deep", accounts.ToString()); // the key itself never leaves the provider

        var balance = Ok(r.GetProperty("usage")[0]).GetProperty("balance");
        Assert.Equal(110, balance.GetProperty("amount").GetDouble());
        Assert.Equal("CNY", balance.GetProperty("currency").GetString());
        Assert.Equal([("充值", 100.0), ("赠送", 10.0)],
            balance.GetProperty("details").EnumerateArray().Select(d => (d.GetProperty("label").GetString(), d.GetProperty("amount").GetDouble())));
        Assert.Contains("API Key 无效", Err(r.GetProperty("usage")[1]));
        Assert.Contains("GET /user/balance Bearer sk-deep-good", server.Requests);
    }

    [Fact]
    public void Kimi_reads_the_balance_from_the_chosen_site()
    {
        using var server = new FakeBillingServer(new()
        {
            ["/v1/users/me/balance"] = """{"code":0,"data":{"available_balance":49.58894,"voucher_balance":46.58893,"cash_balance":3.00001},"scode":"0x0","status":true}""",
        }, "sk-kimi");

        // International site: the .ai host is asked (the .cn one points nowhere useful) and amounts are in USD.
        var r = RunProvider("com.o_o.ai.kimi", "kimi", new() { ["region"] = "国际站", ["key1"] = "sk-kimi" },
            server.Url, "https://api.moonshot.ai");
        var balance = Ok(r.GetProperty("usage")[0]).GetProperty("balance");
        Assert.Equal("USD", balance.GetProperty("currency").GetString());
        Assert.Contains("GET /v1/users/me/balance Bearer sk-kimi", server.Requests);
    }

    [Fact]
    public void Kimi_on_the_china_site_reports_cash_and_vouchers_in_yuan()
    {
        using var server = new FakeBillingServer(new()
        {
            ["/v1/users/me/balance"] = """{"code":0,"data":{"available_balance":49.58894,"voucher_balance":46.58893,"cash_balance":3.00001},"scode":"0x0","status":true}""",
        }, "sk-kimi");

        var r = RunProvider("com.o_o.ai.kimi", "kimi", new() { ["key1"] = "sk-kimi" }, server.Url, "https://api.moonshot.cn");
        var balance = Ok(r.GetProperty("usage")[0]).GetProperty("balance");
        Assert.Equal(49.58894, balance.GetProperty("amount").GetDouble(), 5);
        Assert.Equal("CNY", balance.GetProperty("currency").GetString());
        Assert.Equal(["现金", "代金券"], balance.GetProperty("details").EnumerateArray().Select(d => d.GetProperty("label").GetString()));
    }

    [Fact]
    public void NewApi_shows_what_is_left_on_a_token_of_a_site_on_this_machine()
    {
        using var server = new FakeBillingServer(new()
        {
            ["/v1/dashboard/billing/subscription"] = """{"object":"billing_subscription","has_payment_method":true,"hard_limit_usd":25,"soft_limit_usd":25}""",
            ["/v1/dashboard/billing/usage"] = """{"object":"list","total_usage":750}""",
        }, "sk-relay");

        // The shipped manifest already allows loopback; the site address is a setting.
        var r = RunProvider("com.o_o.ai.newapi", "newapi", new() { ["baseUrl"] = server.Url + "/v1/", ["key1"] = "sk-relay" });
        var balance = Ok(r.GetProperty("usage")[0]).GetProperty("balance");
        Assert.Equal(17.5, balance.GetProperty("amount").GetDouble());
        Assert.Equal("USD", balance.GetProperty("currency").GetString());
        Assert.Equal("剩余额度", balance.GetProperty("label").GetString());
        Assert.Contains(server.Requests, q => q.StartsWith("GET /v1/dashboard/billing/usage?start_date=", StringComparison.Ordinal));
    }

}
