using System.Globalization;
using System.Text;
using System.Text.Json;
using Island.Plugins.Host.Services;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>
/// The <c>mail</c> capability (docs/design/05-builtin-plugins.md §9): read-only IMAP — unread count and recent messages.
/// A plugin describes an account; the host decides where it may connect and supplies the secret itself:
/// <list type="bullet">
/// <item><c>{ host | hostSetting, port?, user | userSetting, passwordSetting }</c>: a known provider's server, or the one
/// the user typed into a setting; the password is read from the plugin's own password setting, never from JS.</item>
/// <item><c>{ oauth: handle }</c>: an account from <c>oauth.signIn</c> (Google, Microsoft); the server is fixed by the
/// provider and the token goes over SASL XOAUTH2 — it never reaches JS.</item>
/// </list>
/// </summary>
internal sealed partial class PluginRunner
{
    public static readonly TimeSpan MailTimeout = TimeSpan.FromSeconds(45);

    /// <summary>
    /// IMAP servers a plugin may name directly (implicit TLS); any other server must come from what the user typed.
    /// </summary>
    public static readonly IReadOnlySet<string> KnownMailHosts = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "imap.qq.com", "imap.exmail.qq.com", "imap.163.com", "imap.126.com", "imap.yeah.net", "imap.188.com",
        "imap.sina.com", "imap.sohu.com", "imap.aliyun.com", "imap.qiye.aliyun.com", "imap.139.com", "imap.189.cn",
        "imap.mail.me.com", "imap.gmail.com", "outlook.office365.com", "imap.mail.yahoo.com", "imap.zoho.com",
        "imap.fastmail.com", "imap.yandex.com", "imap.feishu.cn", "imap.dingtalk.com",
    };

    /// <summary><c>mail.check</c>: <c>{ account, limit?, unreadOnly?, recent? }</c> → <c>{ host, user, unread, total, messages? }</c>.</summary>
    private Func<CancellationToken, Task<string>> MailCall(string payload, ref JsCallContext c)
    {
        if (!_permissions.Has("mail"))
        {
            throw c.TypeError("This plugin has no mail permission.");
        }
        var request = MailRequest.Parse(payload);
        return ct => CheckMail(request, ct);
    }

    private sealed record MailRequest(JsonElement Account, int Limit, bool UnreadOnly, bool Recent)
    {
        public static MailRequest Parse(string payload)
        {
            using var doc = JsonDocument.Parse(payload);
            var r = doc.RootElement;
            if (!r.TryGetProperty("account", out var account) || account.ValueKind != JsonValueKind.Object)
            {
                throw new PluginCapabilityException("需要 account。");
            }
            var limit = r.TryGetProperty("limit", out var l) && l.TryGetInt32(out var n) ? Math.Clamp(n, 1, 50) : 10;
            var unreadOnly = r.TryGetProperty("unreadOnly", out var u) && u.ValueKind == JsonValueKind.True;
            var recent = !(r.TryGetProperty("recent", out var rc) && rc.ValueKind == JsonValueKind.False);
            return new MailRequest(account.Clone(), limit, unreadOnly, recent);
        }
    }

    private async Task<string> CheckMail(MailRequest request, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(MailTimeout);
        var login = ResolveMailAccount(request.Account);
        var connect = _services.ImapConnect is { } custom ? new ImapClient.Connector(custom) : ImapClient.ConnectTls;
        try
        {
            await using var imap = await ImapClient.OpenAsync(connect, login.Host, login.Port, timeout.Token);
            if (login.Token is { } token)
            {
                await imap.AuthenticateXOAuth2Async(login.User, token, timeout.Token);
            }
            else
            {
                await imap.LoginAsync(login.User, login.Password!, timeout.Token);
            }
            var (total, unread) = await imap.StatusAsync(timeout.Token);
            var messages = request.Recent ? await imap.RecentAsync(request.Limit, request.UnreadOnly, timeout.Token) : null;
            return MailJson(login, total, unread, messages);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new PluginCapabilityException($"连接 {login.Host} 超时。");
        }
        catch (IOException e)
        {
            throw new PluginCapabilityException($"与 {login.Host} 的连接中断：{e.Message}");
        }
    }

    private sealed record MailLogin(string Host, int Port, string User, string? Password, string? Token);

    private MailLogin ResolveMailAccount(JsonElement a)
    {
        static string? S(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

        if (S(a, "oauth") is { } handle)
        {
            if (!HasOAuth)
            {
                throw new PluginCapabilityException("This plugin has no oauth: permission.");
            }
            var (oauthHost, oauthUser, token) = _oauth.ImapLogin(handle);
            return new MailLogin(oauthHost, 993, oauthUser, null, token);
        }

        string host;
        if (S(a, "host") is { } named)
        {
            if (!KnownMailHosts.Contains(named))
            {
                throw new PluginCapabilityException($"不能直接连接 {named}：请让用户在设置中填写服务器地址（hostSetting）。");
            }
            host = named;
        }
        else if (S(a, "hostSetting") is { } hostKey && SettingText(hostKey) is { Length: > 0 } typed)
        {
            host = typed.Trim();
        }
        else
        {
            throw new PluginCapabilityException("还没有填写邮件服务器地址。");
        }
        if (Uri.CheckHostName(host) is not (UriHostNameType.Dns or UriHostNameType.IPv4 or UriHostNameType.IPv6))
        {
            throw new PluginCapabilityException($"服务器地址 '{host}' 不正确。");
        }
        var port = a.TryGetProperty("port", out var p) && p.TryGetInt32(out var portNumber) ? portNumber : 993;
        if (port is < 1 or > 65535)
        {
            throw new PluginCapabilityException("端口不正确。");
        }
        var user = S(a, "user") ?? (S(a, "userSetting") is { } userKey ? SettingText(userKey) : null);
        if (user is not { Length: > 0 and <= 320 } || user.Any(char.IsControl))
        {
            throw new PluginCapabilityException("还没有填写邮箱账号。");
        }
        var passwordKey = S(a, "passwordSetting") ?? "";
        if (_settings.Find(passwordKey) is not { IsSecret: true })
        {
            throw new PluginCapabilityException($"'{passwordKey}' 不是这个插件的密码设置。");
        }
        var password = _settings.GetSecret(passwordKey);
        if (string.IsNullOrEmpty(password))
        {
            throw new PluginCapabilityException("还没有填写授权码（应用专用密码）。");
        }
        return new MailLogin(host, port, user.Trim(), password, null);
    }

    private static string MailJson(MailLogin login, int total, int unread, List<MailMessage>? messages)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("host", login.Host);
            w.WriteString("user", login.User);
            w.WriteNumber("unread", unread);
            w.WriteNumber("total", total);
            if (messages is not null)
            {
                w.WriteStartArray("messages");
                foreach (var m in messages)
                {
                    w.WriteStartObject();
                    w.WriteNumber("uid", m.Uid);
                    w.WriteString("from", m.From);
                    if (m.FromAddress is not null)
                    {
                        w.WriteString("address", m.FromAddress);
                    }
                    w.WriteString("subject", m.Subject);
                    if (m.Date is { } date)
                    {
                        w.WriteString("date", date.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture));
                    }
                    w.WriteBoolean("unread", m.Unread);
                    if (m.Preview is not null)
                    {
                        w.WriteString("preview", m.Preview);
                    }
                    w.WriteEndObject();
                }
                w.WriteEndArray();
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
