using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>How a provider's token endpoint wants to be asked.</summary>
public enum OAuthTokenStyle
{
    /// <summary>RFC 6749: form-encoded <c>grant_type</c>, <c>code</c>, <c>client_id</c>… (Google).</summary>
    Form,

    /// <summary>Feishu v2: the same fields as a JSON body; answers carry <c>code</c> 0 on success.</summary>
    Json,

    /// <summary>DingTalk: camelCase JSON (<c>clientId</c>, <c>grantType</c>…), answers <c>accessToken</c> / <c>expireIn</c>.</summary>
    DingTalk,
}

/// <summary>
/// A sign-in provider the host knows (permission <c>oauth:&lt;Id&gt;</c>). Endpoints are fixed here, never taken from a
/// plugin, and the token only ever goes to <see cref="ApiHosts"/> — so a plugin cannot point a login at its own server.
/// </summary>
/// <param name="TokenHeader">Where the access token goes: null for <c>Authorization: Bearer</c>, else this header, raw.</param>
public sealed record OAuthProfile(
    string Id,
    string Name,
    Uri AuthorizeUrl,
    Uri TokenUrl,
    OAuthTokenStyle Style,
    IReadOnlyList<string> ApiHosts,
    bool Pkce = true,
    string? TokenHeader = null,
    IReadOnlyDictionary<string, string>? ExtraAuthorizeParameters = null)
{
    /// <summary>
    /// The providers O_O signs in to (docs/design/05-builtin-plugins.md §6.2). Google is the standard desktop flow;
    /// the Feishu and DingTalk details follow their docs as researched (2026-11-09) and still need a real sign-in.
    /// </summary>
    public static IReadOnlyDictionary<string, OAuthProfile> Defaults { get; } = new Dictionary<string, OAuthProfile>(StringComparer.Ordinal)
    {
        ["google"] = new("google", "Google",
            new("https://accounts.google.com/o/oauth2/v2/auth"), new("https://oauth2.googleapis.com/token"),
            OAuthTokenStyle.Form, ["www.googleapis.com"],
            ExtraAuthorizeParameters: new Dictionary<string, string> { ["access_type"] = "offline", ["prompt"] = "consent" }),
        ["feishu"] = new("feishu", "飞书",
            new("https://accounts.feishu.cn/open-apis/authen/v1/authorize"), new("https://open.feishu.cn/open-apis/authen/v2/oauth/token"),
            OAuthTokenStyle.Json, ["open.feishu.cn"]),
        ["dingtalk"] = new("dingtalk", "钉钉",
            new("https://login.dingtalk.com/oauth2/auth"), new("https://api.dingtalk.com/v1.0/oauth2/userAccessToken"),
            OAuthTokenStyle.DingTalk, ["api.dingtalk.com"], Pkce: false, TokenHeader: "x-acs-dingtalk-access-token",
            ExtraAuthorizeParameters: new Dictionary<string, string> { ["prompt"] = "consent" }),
    };

    public bool AllowsHost(string host) => ApiHosts.Contains(host, StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// One plugin's OAuth sessions (docs 03 §7, <c>oauth</c>): authorization code + PKCE in the system browser with a
/// loopback callback; tokens in the keychain, refreshed by the host when they are about to expire. These are O_O's own
/// sessions, unlike <c>localAuth</c>, so refreshing is fine. The plugin only ever holds an account handle.
/// </summary>
internal sealed class OAuthSessions(
    string pluginId,
    string dataDirectory,
    Island.Platform.ISecretStore secrets,
    HttpClient http,
    IReadOnlyDictionary<string, OAuthProfile> profiles,
    Func<Uri, Task> openBrowser,
    int callbackPort)
{
    public static readonly TimeSpan SignInTimeout = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan RefreshMargin = TimeSpan.FromSeconds(60);

    // One sign-in at a time on the machine's callback port.
    private static readonly SemaphoreSlim SignInGate = new(1, 1);

    private readonly string _service = $"O_O/{pluginId}";
    private readonly string _registryPath = Path.Combine(dataDirectory, pluginId, "oauth.json");
    private readonly Lock _gate = new();
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _refreshing = new();

    public string RedirectUri => $"http://127.0.0.1:{callbackPort}/oauth/callback";

    public static bool IsHandle(string handle) => handle.StartsWith("oauth_", StringComparison.Ordinal);

    /// <summary>Signs in through the browser: payload <c>{ provider, clientId, clientSecret?, scopes: [], label? }</c>.</summary>
    /// <returns>The new account as JSON.</returns>
    public async Task<string> SignInAsync(string payload, Func<string, bool> mayUse, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        var provider = Str(r, "provider") ?? "";
        if (!profiles.TryGetValue(provider, out var profile) || !mayUse(provider))
        {
            throw new PluginCapabilityException($"这个插件不能登录 '{provider}'（需要 oauth:{provider} 权限）。");
        }
        var clientId = Str(r, "clientId") is { Length: > 0 } id ? id : throw new PluginCapabilityException("需要 clientId（应用的 Client ID / App ID）。");
        var clientSecret = Str(r, "clientSecret");
        var scopes = r.TryGetProperty("scopes", out var s) && s.ValueKind == JsonValueKind.Array
            ? s.EnumerateArray().Select(e => e.GetString() ?? "").Where(x => x.Length > 0).ToList()
            : [];

        if (!await SignInGate.WaitAsync(TimeSpan.Zero, ct))
        {
            throw new PluginCapabilityException("另一个登录正在进行，请先在浏览器中完成或关闭它。");
        }
        try
        {
            var state = Random(24);
            var verifier = profile.Pkce ? Random(48) : null;
            using var listener = new HttpListener();
            listener.Prefixes.Add(RedirectUri + "/");
            try
            {
                listener.Start();
            }
            catch (HttpListenerException e)
            {
                throw new PluginCapabilityException($"无法在本机端口 {callbackPort} 等待登录回调：{e.Message}");
            }
            await openBrowser(AuthorizeUri(profile, clientId, scopes, state, verifier));
            var code = await WaitForCode(listener, state, ct);

            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["redirect_uri"] = RedirectUri,
                ["client_id"] = clientId,
            };
            if (clientSecret is { Length: > 0 })
            {
                fields["client_secret"] = clientSecret;
            }
            if (verifier is not null)
            {
                fields["code_verifier"] = verifier;
            }
            var tokens = await RequestTokens(profile, fields, ct);
            var handle = "oauth_" + Random(16);
            var session = new Session(provider, clientId, clientSecret, string.Join(' ', scopes), tokens.Access, tokens.Refresh, tokens.ExpiresAt);
            secrets.Set(_service, "oauth:" + handle, session.ToJson());
            var account = new Account(handle, provider, Str(r, "label"), DateTimeOffset.UtcNow);
            lock (_gate)
            {
                SaveRegistry([.. LoadRegistry(), account]);
            }
            return account.ToJson(profile.Name);
        }
        finally
        {
            SignInGate.Release();
        }
    }

    /// <summary>This plugin's signed-in accounts as JSON.</summary>
    public string Accounts()
    {
        List<Account> accounts;
        lock (_gate)
        {
            accounts = LoadRegistry();
        }
        return "[" + string.Join(",", accounts.Select(a => a.ToJson(profiles.TryGetValue(a.Provider, out var p) ? p.Name : a.Provider))) + "]";
    }

    public string SetLabel(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var handle = Str(doc.RootElement, "handle") ?? "";
        var label = Str(doc.RootElement, "label") is { } l ? l[..Math.Min(l.Length, 80)] : null;
        lock (_gate)
        {
            var accounts = LoadRegistry();
            var i = accounts.FindIndex(a => a.Handle == handle);
            if (i < 0)
            {
                throw new PluginCapabilityException("没有这个账户。");
            }
            accounts[i] = accounts[i] with { Label = label };
            SaveRegistry(accounts);
        }
        return "true";
    }

    public string SignOut(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var handle = Str(doc.RootElement, "handle") ?? "";
        lock (_gate)
        {
            var accounts = LoadRegistry();
            if (accounts.RemoveAll(a => a.Handle == handle) == 0)
            {
                return "false";
            }
            SaveRegistry(accounts);
        }
        secrets.Delete(_service, "oauth:" + handle);
        return "true";
    }

    /// <summary>
    /// The token for a request to <paramref name="uri"/> with account <paramref name="handle"/>: only to the provider's
    /// API hosts, refreshed first when it is about to expire. Called on every hop, so a redirect cannot carry it away.
    /// </summary>
    public LocalCredential CredentialFor(string handle, Uri uri)
    {
        Account? account;
        lock (_gate)
        {
            account = LoadRegistry().FirstOrDefault(a => a.Handle == handle);
        }
        if (account is null || !profiles.TryGetValue(account.Provider, out var profile))
        {
            throw new PluginCapabilityException("账户句柄无效或已退出登录。");
        }
        if (!profile.AllowsHost(uri.IdnHost))
        {
            throw new PluginCapabilityException($"{profile.Name} 的登录凭据只能发送到 {string.Join("、", profile.ApiHosts)}，不能发送到 '{uri.IdnHost}'。");
        }
        var session = Load(handle) ?? throw new PluginCapabilityException($"{profile.Name} 的登录已失效，请重新登录。");
        if (session.ExpiresAt is { } expires && expires - RefreshMargin <= DateTimeOffset.UtcNow)
        {
            session = Refresh(handle, profile, session);
        }
        return new LocalCredential(session.Access, session.ExpiresAt, profile.TokenHeader);
    }

    private Session Refresh(string handle, OAuthProfile profile, Session session)
    {
        var gate = _refreshing.GetOrAdd(handle, _ => new SemaphoreSlim(1, 1));
        gate.Wait();
        try
        {
            // Another request may have refreshed it meanwhile.
            if (Load(handle) is { } current && current.ExpiresAt is { } e && e - RefreshMargin > DateTimeOffset.UtcNow)
            {
                return current;
            }
            if (session.Refresh is null)
            {
                throw new PluginCapabilityException($"{profile.Name} 的登录已过期，请重新登录。");
            }
            var fields = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = session.Refresh,
                ["client_id"] = session.ClientId,
            };
            if (session.ClientSecret is { Length: > 0 } secret)
            {
                fields["client_secret"] = secret;
            }
            var tokens = RequestTokens(profile, fields, CancellationToken.None).GetAwaiter().GetResult();
            var refreshed = session with
            {
                Access = tokens.Access,
                Refresh = tokens.Refresh ?? session.Refresh, // providers that do not rotate keep the old one
                ExpiresAt = tokens.ExpiresAt,
            };
            secrets.Set(_service, "oauth:" + handle, refreshed.ToJson());
            return refreshed;
        }
        finally
        {
            gate.Release();
        }
    }

    private Uri AuthorizeUri(OAuthProfile profile, string clientId, List<string> scopes, string state, string? verifier)
    {
        var q = new List<(string, string)>
        {
            ("response_type", "code"),
            ("client_id", clientId),
            ("redirect_uri", RedirectUri),
            ("state", state),
        };
        if (scopes.Count > 0)
        {
            q.Add(("scope", string.Join(' ', scopes)));
        }
        if (verifier is not null)
        {
            q.Add(("code_challenge", Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))));
            q.Add(("code_challenge_method", "S256"));
        }
        foreach (var (k, v) in profile.ExtraAuthorizeParameters ?? new Dictionary<string, string>())
        {
            q.Add((k, v));
        }
        var query = string.Join("&", q.Select(p => $"{Uri.EscapeDataString(p.Item1)}={Uri.EscapeDataString(p.Item2)}"));
        return new Uri($"{profile.AuthorizeUrl}?{query}");
    }

    private static async Task<string> WaitForCode(HttpListener listener, string state, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(SignInTimeout);
        using var stop = timeout.Token.Register(listener.Stop);
        while (true)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                throw ct.IsCancellationRequested
                    ? new OperationCanceledException(ct)
                    : new PluginCapabilityException("登录超时：5 分钟内没有在浏览器中完成。");
            }
            var query = context.Request.QueryString;
            if (query["state"] != state)
            {
                await Respond(context, 400, "这个登录链接已失效，请回到 O_O 重新登录。");
                continue; // a stale or forged callback: keep waiting for ours
            }
            if (query["error"] is { } error)
            {
                await Respond(context, 200, "已取消登录，可以关闭此页面。");
                throw new PluginCapabilityException($"登录被拒绝或取消：{query["error_description"] ?? error}");
            }
            if (query["code"] is not { Length: > 0 } code)
            {
                await Respond(context, 400, "回调里没有授权码。");
                throw new PluginCapabilityException("登录回调里没有授权码。");
            }
            await Respond(context, 200, "登录成功，可以关闭此页面并回到 O_O。");
            return code;
        }
    }

    private static async Task Respond(HttpListenerContext context, int status, string message)
    {
        var html = $"<!doctype html><meta charset=utf-8><title>O_O</title><body style=\"font:16px -apple-system,system-ui,sans-serif;padding:48px\"><p>{WebUtility.HtmlEncode(message)}</p>";
        var bytes = Encoding.UTF8.GetBytes(html);
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.ContentLength64 = bytes.Length;
        await context.Response.OutputStream.WriteAsync(bytes);
        context.Response.Close();
    }

    private sealed record Tokens(string Access, string? Refresh, DateTimeOffset? ExpiresAt);

    private async Task<Tokens> RequestTokens(OAuthProfile profile, Dictionary<string, string> fields, CancellationToken ct)
    {
        using var message = new HttpRequestMessage(HttpMethod.Post, profile.TokenUrl);
        message.Headers.Accept.ParseAdd("application/json");
        message.Content = profile.Style switch
        {
            OAuthTokenStyle.Form => new FormUrlEncodedContent(fields),
            OAuthTokenStyle.Json => Json(fields),
            _ => Json(new Dictionary<string, string>
            {
                ["clientId"] = fields["client_id"],
                ["clientSecret"] = fields.GetValueOrDefault("client_secret", ""),
                ["grantType"] = fields["grant_type"],
                [fields["grant_type"] == "refresh_token" ? "refreshToken" : "code"] =
                    fields.GetValueOrDefault("refresh_token") ?? fields["code"],
            }),
        };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        string body;
        HttpStatusCode status;
        try
        {
            using var response = await http.SendAsync(message, timeout.Token);
            status = response.StatusCode;
            body = await response.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            throw new PluginCapabilityException($"无法连接 {profile.Name} 的登录服务：{e.Message}");
        }
        try
        {
            using var doc = JsonDocument.Parse(body);
            var r = doc.RootElement;
            // Feishu wraps errors as { code: non-zero, msg }; others use { error, error_description } or HTTP errors.
            var feishuError = r.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number && c.GetInt64() != 0;
            var access = Str(r, "access_token") ?? Str(r, "accessToken");
            if ((int)status >= 400 || feishuError || access is null)
            {
                var reason = Str(r, "error_description") ?? Str(r, "msg") ?? Str(r, "message") ?? Str(r, "error") ?? $"HTTP {(int)status}";
                throw new PluginCapabilityException($"{profile.Name} 登录失败：{reason}");
            }
            var seconds = Num(r, "expires_in") ?? Num(r, "expireIn");
            return new Tokens(access, Str(r, "refresh_token") ?? Str(r, "refreshToken"),
                seconds is { } n ? DateTimeOffset.UtcNow.AddSeconds(n) : null);
        }
        catch (JsonException)
        {
            throw new PluginCapabilityException($"{profile.Name} 登录失败：返回的不是 JSON（HTTP {(int)status}）。");
        }
    }

    private static StringContent Json(Dictionary<string, string> fields)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            foreach (var (k, v) in fields)
            {
                w.WriteString(k, v);
            }
            w.WriteEndObject();
        }
        return new StringContent(Encoding.UTF8.GetString(buffer.ToArray()), Encoding.UTF8, "application/json");
    }

    // ---- storage -----------------------------------------------------------------------------------------------

    private sealed record Account(string Handle, string Provider, string? Label, DateTimeOffset Created)
    {
        public string ToJson(string providerName)
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("handle", Handle);
                w.WriteString("provider", Provider);
                w.WriteString("providerName", providerName);
                if (Label is not null)
                {
                    w.WriteString("label", Label);
                }
                w.WriteString("created", Created.ToString("O"));
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private sealed record Session(string Provider, string ClientId, string? ClientSecret, string Scopes, string Access, string? Refresh, DateTimeOffset? ExpiresAt)
    {
        public string ToJson()
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("provider", Provider);
                w.WriteString("clientId", ClientId);
                if (ClientSecret is not null)
                {
                    w.WriteString("clientSecret", ClientSecret);
                }
                w.WriteString("scopes", Scopes);
                w.WriteString("access", Access);
                if (Refresh is not null)
                {
                    w.WriteString("refresh", Refresh);
                }
                if (ExpiresAt is { } e)
                {
                    w.WriteString("expiresAt", e.ToString("O"));
                }
                w.WriteEndObject();
            }
            return Encoding.UTF8.GetString(buffer.ToArray());
        }
    }

    private Session? Load(string handle)
    {
        if (secrets.Get(_service, "oauth:" + handle) is not { } json)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        return new Session(Str(r, "provider") ?? "", Str(r, "clientId") ?? "", Str(r, "clientSecret"), Str(r, "scopes") ?? "",
            Str(r, "access") ?? "", Str(r, "refresh"),
            Str(r, "expiresAt") is { } e && DateTimeOffset.TryParse(e, out var at) ? at : null);
    }

    private List<Account> LoadRegistry()
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(_registryPath));
            return [.. doc.RootElement.EnumerateArray().Select(e => new Account(
                Str(e, "handle") ?? "", Str(e, "provider") ?? "", Str(e, "label"),
                Str(e, "created") is { } c && DateTimeOffset.TryParse(c, out var at) ? at : default)).Where(a => IsHandle(a.Handle))];
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            return [];
        }
    }

    private void SaveRegistry(List<Account> accounts)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_registryPath)!);
        var json = "[" + string.Join(",", accounts.Select(a => a.ToJson(a.Provider))) + "]";
        File.WriteAllText(_registryPath + ".tmp", json);
        File.Move(_registryPath + ".tmp", _registryPath, overwrite: true);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static double? Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static string Random(int bytes) => Base64Url(RandomNumberGenerator.GetBytes(bytes));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    /// <summary>The system browser: where the user signs in (never an embedded web view, so the password stays with the provider).</summary>
    public static Task OpenSystemBrowser(Uri uri)
    {
        var info = OperatingSystem.IsWindows()
            ? new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }
            : new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { UseShellExecute = false, ArgumentList = { uri.AbsoluteUri } };
        Process.Start(info)?.Dispose();
        return Task.CompletedTask;
    }
}
