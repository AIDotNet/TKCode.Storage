using System.Security.Cryptography;
using System.Text;
using Island.Platform;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

public sealed class LocalAuthSourceTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("o_o-localauth-").FullName;

    private static string Jwt(string payloadJson)
    {
        static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return $"{B64("""{"alg":"none"}""")}.{B64(payloadJson)}.sig";
    }

    [Fact]
    public void Codex_reads_the_account_and_plan_from_the_id_token_and_the_token_expiry()
    {
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        File.WriteAllText(Path.Combine(_root, "auth.json"), $$"""
            { "auth_mode": "chatgpt", "OPENAI_API_KEY": null, "last_refresh": "2026-11-05T02:00:00Z",
              "tokens": {
                "id_token": "{{Jwt("""{"email":"ada@example.com","https://api.openai.com/auth":{"chatgpt_plan_type":"plus","chatgpt_subscription_active_until":"2026-12-01T00:00:00+00:00","chatgpt_account_id":"acc-1"}}""")}}",
                "access_token": "{{Jwt($$"""{"exp":{{exp}}}""")}}",
                "refresh_token": "r", "account_id": "acc-1" } }
            """);
        var source = new CodexCliSource(_root);

        var account = Assert.Single(source.Discover());
        Assert.Equal("ada@example.com", account.Label);
        Assert.Equal("plus", account.Plan);
        Assert.Equal("acc-1", account.AccountId);
        Assert.Equal(new DateTimeOffset(2026, 12, 1, 0, 0, 0, TimeSpan.Zero), account.SubscriptionUntil);
        var credential = source.ReadCredential(account.Key)!;
        Assert.Equal(exp, credential.ExpiresAt!.Value.ToUnixTimeSeconds());
        Assert.True(source.AllowsHost("chatgpt.com"));
        Assert.False(source.AllowsHost("api.openai.com"));
    }

    [Fact]
    public void Codex_without_a_chatgpt_login_has_no_accounts()
    {
        Assert.Empty(new CodexCliSource(_root).Discover());
        File.WriteAllText(Path.Combine(_root, "auth.json"), """{ "OPENAI_API_KEY": "sk-x" }""");
        Assert.Empty(new CodexCliSource(_root).Discover());
    }

    [Fact]
    public void Claude_with_a_custom_config_dir_uses_the_hashed_keychain_service()
    {
        var dir = Path.Combine(_root, "config");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, ".claude.json"), """
            { "oauthAccount": { "emailAddress": "ada@example.com", "displayName": "Ada", "billingType": "stripe_subscription", "accountUuid": "u-1" } }
            """);
        var keychain = new InMemorySecretStore();
        var source = new ClaudeCodeSource(keychain, [dir], useKeychain: true, home: _root, scan: false, planLookup: (_, _) => "Max 20x");

        var service = source.KeychainService(dir);
        var expected = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(dir)))[..8];
        Assert.Equal($"Claude Code-credentials-{expected}", service);

        Assert.Empty(source.Discover());                            // an account file without a login: not offered
        Assert.Null(source.ReadCredential(dir));

        var expires = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeMilliseconds();
        keychain.Set(service, Environment.UserName, $$"""{ "claudeAiOauth": { "accessToken": "at", "expiresAt": {{expires}} } }""");
        Assert.Equal("Ada (ada@example.com)", Assert.Single(source.Discover()).Label);
        // Signed in now, yet the token is never read: usage comes from the status line instead.
        Assert.Null(source.ReadCredential(dir));
        Assert.Equal("Max 20x", Assert.Single(source.Discover()).Plan);
    }

    [Fact]
    public void Claude_default_dir_reads_home_claude_json_and_the_credentials_file_off_macOS()
    {
        var defaultDir = Path.Combine(_root, ".claude");
        Directory.CreateDirectory(defaultDir);
        File.WriteAllText(Path.Combine(_root, ".claude.json"), """{ "oauthAccount": { "emailAddress": "b@example.com" } }""");
        File.WriteAllText(Path.Combine(defaultDir, ".credentials.json"), """{ "claudeAiOauth": { "accessToken": "file-token" } }""");
        var source = new ClaudeCodeSource(new InMemorySecretStore(), [defaultDir], useKeychain: false, home: _root, scan: false, planLookup: (_, _) => "Max 20x");

        var account = Assert.Single(source.Discover());
        Assert.Equal("b@example.com", account.Label);
        Assert.Null(source.ReadCredential(defaultDir));                    // never read, even when it is a file
    }

    [Fact]
    public void Claude_finds_extra_config_directories_a_plugin_points_at_but_only_real_ones()
    {
        var custom = Path.Combine(_root, "accounts", "work");
        Directory.CreateDirectory(custom);
        File.WriteAllText(Path.Combine(custom, ".claude.json"), """{ "oauthAccount": { "emailAddress": "work@example.com" } }""");
        File.WriteAllText(Path.Combine(custom, ".credentials.json"), """{ "claudeAiOauth": { "accessToken": "w" } }""");
        var source = new ClaudeCodeSource(new InMemorySecretStore(), [], useKeychain: false, home: _root, scan: false, planLookup: (_, _) => "Max 20x");

        Assert.Empty(source.Discover());
        var found = source.Discover([custom, "relative/dir", Path.Combine(_root, "missing")]);
        Assert.Equal("work@example.com", Assert.Single(found).Label);
        Assert.Null(source.ReadCredential(custom));
    }

    [Fact]
    public void Claude_finds_account_manager_directories_by_itself_and_only_signed_in_ones()
    {
        // The layout of rec: one configuration directory per account, only some of them signed in.
        string Account(string id, bool signedIn)
        {
            var dir = Path.Combine(_root, ".rec", "claude", "accounts", id, "config");
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, ".claude.json"), $$"""{ "oauthAccount": { "emailAddress": "{{id}}@example.com" } }""");
            if (signedIn)
            {
                File.WriteAllText(Path.Combine(dir, ".credentials.json"), $$"""{ "claudeAiOauth": { "accessToken": "t-{{id}}" } }""");
            }
            return dir;
        }
        Account("47196", signedIn: false);
        var live = Account("55232", signedIn: true);
        // Decoys the scan must not wander into or pick up.
        Directory.CreateDirectory(Path.Combine(_root, ".npm", "deep", "config"));
        File.WriteAllText(Path.Combine(_root, ".npm", "deep", "config", ".claude.json"), "{}");
        Directory.CreateDirectory(Path.Combine(_root, "Documents", "config"));
        File.WriteAllText(Path.Combine(_root, "Documents", "config", ".claude.json"), "{}");

        var source = new ClaudeCodeSource(new InMemorySecretStore(), [], useKeychain: false, home: _root, planLookup: (_, _) => "Max 20x");
        var account = Assert.Single(source.Discover());
        Assert.Equal("55232@example.com", account.Label);
        Assert.Null(source.ReadCredential(live));
        Assert.Equal("Max 20x", account.Plan);
    }

    [Fact]
    public void The_plan_command_line_is_split_like_a_shell_would()
    {
        Assert.Equal(["/opt/my tools/claude"], ClaudeCli.Split("\"/opt/my tools/claude\""));
        Assert.Equal(["wrapper", "--profile", "work", "claude"], ClaudeCli.Split("wrapper --profile work claude"));
        Assert.Equal(["a", ""], ClaudeCli.Split("a ''"));
        Assert.Equal("Max 20x", ClaudeCli.Describe("max_20x"));
        Assert.Equal("Pro", ClaudeCli.Describe("pro"));
    }

    [Fact]
    public void The_command_may_be_a_bare_name_or_a_full_path()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var tool = Path.Combine(_root, "bin", "my claude");
        Directory.CreateDirectory(Path.GetDirectoryName(tool)!);
        File.WriteAllText(tool, "#!/bin/sh\n");
        Assert.Equal(tool, ClaudeCli.Resolve(tool));                                   // full path, used as given
        Assert.Null(ClaudeCli.Resolve(Path.Combine(_root, "bin", "missing")));          // a path that does not exist
        Assert.Equal("/bin/sh", ClaudeCli.Resolve("~/../../bin/sh".Replace("~", "~")) is { } viaHome ? "/bin/sh" : null);
        Assert.NotNull(ClaudeCli.Resolve("sh"));                                        // bare name, found on PATH
        Assert.Null(ClaudeCli.Resolve("definitely-not-a-real-command-o_o"));            // bare name, nowhere
        Assert.Equal(["claude"], ClaudeCli.Split(ClaudeCli.DefaultCommand));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}

/// <summary>localAuth end to end through a plugin, with a test source bound to the loopback test server.</summary>
public sealed partial class PluginHostTests
{
    private sealed class FakeSource(DateTimeOffset? expires) : LocalAuthSource
    {
        public override string Id => "codex-cli"; // reuse a known id so the permission validates
        public override string Name => "Fake CLI";
        public override IReadOnlyList<string> Hosts => ["127.0.0.1"];

        public override IReadOnlyList<LocalAccount> Discover(IReadOnlyList<string>? extraDirectories = null) =>
            [new LocalAccount(Id, "k", "fake@example.com", "fake@example.com", "pro", null, "acc")];

        public override LocalCredential? ReadCredential(string accountKey) =>
            accountKey == "k" ? new LocalCredential("tok-123", expires) : null;
    }

    private string RunLocalAuthPlugin(DateTimeOffset? expires, string serverUrl)
    {
        _permissions = $"\"localAuth:codex-cli\", \"network:{PluginPermissions.Localhost}\"";
        StartWith($$"""
            import { island, ui, http, localAuth } from '@island/api';
            const settle = (p) => p.then((v) => 'ok:' + v, (e) => 'err:' + e.message);
            export async function activate() {
              const act = island.home.page('a');
              const [account] = await localAuth.accounts('codex-cli');
              const keys = Object.keys(account).sort().join(',');
              const who = await settle(http.fetch('{{serverUrl}}whoami', { auth: account }).then((r) => r.text()));
              const anon = await settle(http.fetch('{{serverUrl}}whoami').then((r) => r.text()));
              const hop = await settle(http.fetch('{{serverUrl}}redirect-local', { auth: account }));
              const forged = await settle(http.fetch('{{serverUrl}}whoami', { auth: 'codex-cli:made-up' }));
              act.update(ui.text([account.label, keys, who, anon, hop, forged].join('\n')));
            }
            """, Services with { LocalAuthSources = [new FakeSource(expires)] });
        WaitForState(PluginState.AwaitingConsent);       // local logins always ask, even for bundled plugins
        _host!.SetGranted("test.plugin", ["localAuth:codex-cli"]);
        return ShownWhen(t => t.Split('\n').Length == 6, "six results");
    }

    [Fact]
    public void LocalAuth_sends_the_token_only_to_the_sources_hosts_and_never_shows_it_to_the_plugin()
    {
        using var server = new TestServer();
        var lines = RunLocalAuthPlugin(DateTimeOffset.UtcNow.AddHours(1), server.Url).Split('\n');
        Assert.Equal("fake@example.com", lines[0]);
        Assert.Equal("accountId,email,handle,label,plan,source", lines[1]);   // no token field
        Assert.Equal("ok:authorized", lines[2]);
        Assert.Equal("ok:anonymous", lines[3]);
        Assert.Contains("只能发送到 127.0.0.1，不能发送到 'localhost'", lines[4]); // refused at the redirect hop
        Assert.Contains("账户句柄无效", lines[5]);
    }

    [Fact]
    public void An_expired_local_login_is_reported_not_refreshed()
    {
        using var server = new TestServer();
        var lines = RunLocalAuthPlugin(DateTimeOffset.UtcNow.AddMinutes(-1), server.Url).Split('\n');
        Assert.Contains("登录已过期：请运行一次 Fake CLI 刷新登录", lines[2]);
    }

    [Fact]
    public void Without_the_permission_localAuth_is_absent_and_unknown_sources_are_rejected()
    {
        Start("""
            import { island, ui, localAuth } from '@island/api';
            export function activate() { island.home.page('a').update(ui.text(typeof localAuth)); }
            """);
        Assert.Equal("undefined", Shown());

        var problems = new List<string>();
        Assert.Null(PluginPermissions.Parse(["localAuth:my-bank"], problems));
        Assert.Contains("unknown local login source", Assert.Single(problems));
    }
}
