using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>The mail capability (read-only IMAP) and the shipped mail plugin, against a fake IMAP server on this machine.</summary>
public sealed partial class PluginHostTests
{
    private static readonly Encoding Gbk = GbkEncoding();

    private static Encoding GbkEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding("gb18030");
    }

    /// <summary>A tiny IMAP server: three messages, LOGIN / XOAUTH2, STATUS, EXAMINE, UID SEARCH / FETCH, literals.</summary>
    private sealed class FakeImapServer : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        public readonly ConcurrentQueue<string> Commands = new();
        public readonly ConcurrentQueue<string> Hosts = new();
        public readonly ConcurrentQueue<string> Logins = new();   // "user|password" or "xoauth2|user|token"
        public string Password = "p\"a\\ss";
        public string Token = "at-good";
        private readonly List<(long Uid, bool Seen, string Meta, byte[] Part)> _messages = [];
        private readonly Lock _gate = new();

        public FakeImapServer()
        {
            _listener.Start();
            _ = Serve();
            var subject1 = $"=?GBK?B?{Convert.ToBase64String(Gbk.GetBytes("周报"))}?=";
            var qp = string.Concat(Gbk.GetBytes("你好").Select(b => $"={b:X2}"));
            Add(101, seen: false,
                $"ENVELOPE (\"Mon, 10 Nov 2026 09:00:00 +0800\" \"{subject1}\" ((\"=?UTF-8?Q?=E5=BC=A0=E4=B8=89?=\" NIL \"zhang\" \"example.com\")) NIL NIL NIL NIL NIL NIL \"<1@x>\") " +
                "BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"gbk\") NIL NIL \"QUOTED-PRINTABLE\" 12 1 NIL NIL NIL NIL)",
                Encoding.ASCII.GetBytes(qp + "=\r\n"));
            // The subject arrives as a literal; the text is the base64 plain part of a multipart/alternative.
            var subject2 = Encoding.UTF8.GetBytes("季度总结");
            Add(102, seen: true,
                $"ENVELOPE (\"Mon, 10 Nov 2026 08:00:00 +0800\" {{{subject2.Length}}}\r\n{Encoding.UTF8.GetString(subject2)} ((\"Li Si\" NIL \"lisi\" \"example.com\")) NIL NIL NIL NIL NIL NIL \"<2@x>\") " +
                "BODYSTRUCTURE ((\"TEXT\" \"PLAIN\" (\"CHARSET\" \"utf-8\") NIL NIL \"BASE64\" 20 1 NIL NIL NIL NIL)(\"TEXT\" \"HTML\" (\"CHARSET\" \"utf-8\") NIL NIL \"7BIT\" 40 1 NIL NIL NIL NIL) \"ALTERNATIVE\" (\"BOUNDARY\" \"b\") NIL NIL NIL)",
                Encoding.ASCII.GetBytes(Convert.ToBase64String(Encoding.UTF8.GetBytes("季度总结已经发出，请查收。")) + "\r\n"));
            Add(103, seen: false,
                "ENVELOPE (\"Mon, 10 Nov 2026 10:00:00 +0800\" \"Hello\" ((NIL NIL \"bob\" \"example.org\")) NIL NIL NIL NIL NIL NIL \"<3@x>\") " +
                "BODYSTRUCTURE (\"TEXT\" \"HTML\" (\"CHARSET\" \"utf-8\") NIL NIL \"7BIT\" 30 1 NIL NIL NIL NIL)",
                Encoding.UTF8.GetBytes("<style>p{color:red}</style><p>Hi <b>there</b></p>"));
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public void Add(long uid, bool seen, string meta, byte[] part)
        {
            lock (_gate)
            {
                _messages.Add((uid, seen, meta, part));
            }
        }

        /// <summary>Stands in for TLS: records which server O_O wanted, connects to this one.</summary>
        public async Task<Stream> Connect(string host, int port, CancellationToken ct)
        {
            Hosts.Enqueue($"{host}:{port}");
            var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, Port, ct);
            return tcp.GetStream();
        }

        private async Task Serve()
        {
            while (true)
            {
                TcpClient client;
                try { client = await _listener.AcceptTcpClientAsync(); } catch { return; }
                _ = Task.Run(() => Handle(client));
            }
        }

        private static async Task<string?> ReadLine(Stream s)
        {
            var bytes = new List<byte>();
            var one = new byte[1];
            while (await s.ReadAsync(one) == 1)
            {
                if (one[0] == '\n')
                {
                    return Encoding.UTF8.GetString([.. bytes]).TrimEnd('\r');
                }
                bytes.Add(one[0]);
            }
            return null;
        }

        private static Task Send(Stream s, string text) => s.WriteAsync(Encoding.UTF8.GetBytes(text)).AsTask();

        private async Task Handle(TcpClient client)
        {
            using var _ = client;
            var s = client.GetStream();
            await Send(s, "* OK [CAPABILITY IMAP4rev1 AUTH=XOAUTH2] fake ready\r\n");
            while (true)
            {
                // A command, with any literals folded back in as quoted strings.
                var line = await ReadLine(s);
                if (line is null)
                {
                    return;
                }
                var full = new StringBuilder();
                while (System.Text.RegularExpressions.Regex.Match(line, @"\{(\d+)\}$") is { Success: true } m)
                {
                    await Send(s, "+ go ahead\r\n");
                    var literal = new byte[int.Parse(m.Groups[1].Value)];
                    var read = 0;
                    while (read < literal.Length)
                    {
                        read += await s.ReadAsync(literal.AsMemory(read));
                    }
                    var text = Encoding.UTF8.GetString(literal).Replace("\\", "\\\\").Replace("\"", "\\\"");
                    full.Append(line[..m.Index]).Append('"').Append(text).Append('"');
                    line = await ReadLine(s) ?? "";
                }
                full.Append(line);
                var command = full.ToString();
                Commands.Enqueue(command);
                var tag = command.Split(' ')[0];
                var rest = command[(tag.Length + 1)..];
                var verb = rest.Split(' ')[0].ToUpperInvariant();
                switch (verb)
                {
                    case "LOGIN":
                        var args = Quoted(rest["LOGIN ".Length..]);
                        Logins.Enqueue($"{args[0]}|{args[1]}");
                        await Send(s, args[1] == Password || args[1] == "密码1"
                            ? $"{tag} OK LOGIN completed\r\n"
                            : $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials\r\n");
                        break;
                    case "AUTHENTICATE":
                        var sasl = Encoding.UTF8.GetString(Convert.FromBase64String(rest.Split(' ')[2]));
                        var parts = sasl.Split('\u0001');
                        var user = parts[0]["user=".Length..];
                        var token = parts[1]["auth=Bearer ".Length..];
                        Logins.Enqueue($"xoauth2|{user}|{token}");
                        if (token == Token)
                        {
                            await Send(s, $"{tag} OK Success\r\n");
                        }
                        else
                        {
                            await Send(s, $"+ {Convert.ToBase64String(Encoding.UTF8.GetBytes("{\"status\":\"401\"}"))}\r\n");
                            await ReadLine(s); // the client's empty answer
                            await Send(s, $"{tag} NO [AUTHENTICATIONFAILED] Invalid credentials (Failure)\r\n");
                        }
                        break;
                    case "STATUS":
                        lock (_gate)
                        {
                            Send(s, $"* STATUS INBOX (MESSAGES {_messages.Count} UNSEEN {_messages.Count(x => !x.Seen)})\r\n{tag} OK STATUS completed\r\n").Wait();
                        }
                        break;
                    case "EXAMINE":
                        await Send(s, $"* {_messages.Count} EXISTS\r\n* OK [UIDVALIDITY 7] ok\r\n{tag} OK [READ-ONLY] EXAMINE completed\r\n");
                        break;
                    case "UID" when rest.Contains("SEARCH"):
                        lock (_gate)
                        {
                            var unseen = rest.Contains("UNSEEN");
                            Send(s, $"* SEARCH {string.Join(' ', _messages.Where(x => !unseen || !x.Seen).Select(x => x.Uid))}\r\n{tag} OK SEARCH completed\r\n").Wait();
                        }
                        break;
                    case "UID" when rest.Contains("FETCH"):
                        var set = rest.Split(' ')[2].Split(',').Select(long.Parse).ToHashSet();
                        List<(long Uid, bool Seen, string Meta, byte[] Part)> chosen;
                        lock (_gate)
                        {
                            chosen = [.. _messages.Where(x => set.Contains(x.Uid))];
                        }
                        foreach (var msg in chosen)
                        {
                            var seq = _messages.IndexOf(msg) + 1;
                            if (rest.Contains("BODYSTRUCTURE"))
                            {
                                await Send(s, $"* {seq} FETCH (UID {msg.Uid} FLAGS ({(msg.Seen ? "\\Seen" : "")}) INTERNALDATE \"10-Nov-2026 09:00:00 +0800\" {msg.Meta})\r\n");
                            }
                            else
                            {
                                await Send(s, $"* {seq} FETCH (UID {msg.Uid} BODY[1]<0> {{{msg.Part.Length}}}\r\n");
                                await s.WriteAsync(msg.Part);
                                await Send(s, ")\r\n");
                            }
                        }
                        await Send(s, $"{tag} OK FETCH completed\r\n");
                        break;
                    case "LOGOUT":
                        await Send(s, $"* BYE bye\r\n{tag} OK LOGOUT completed\r\n");
                        return;
                    default:
                        await Send(s, $"{tag} BAD unknown command\r\n");
                        break;
                }
            }
        }

        /// <summary>The quoted strings in an argument list, unescaped.</summary>
        private static List<string> Quoted(string args)
        {
            var result = new List<string>();
            for (var i = 0; i < args.Length; i++)
            {
                if (args[i] != '"')
                {
                    continue;
                }
                var sb = new StringBuilder();
                for (i++; i < args.Length && args[i] != '"'; i++)
                {
                    if (args[i] == '\\')
                    {
                        i++;
                    }
                    sb.Append(args[i]);
                }
                result.Add(sb.ToString());
            }
            return result;
        }

        public void Dispose() => _listener.Stop();
    }

    private const string MailSettings = """
        , "contributes": { "settings": { "properties": {
            "pw": { "type": "string", "format": "password" }, "pw2": { "type": "string", "format": "password" },
            "server": { "type": "string", "default": "" } } } }
        """;

    private void SetSetting(string plugin, string key, string json) => _host!.GetSettings(plugin).Set(key, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void Mail_reads_the_inbox_without_marking_anything_read_and_decodes_chinese_mail()
    {
        using var imap = new FakeImapServer();
        WritePluginWithManifest("test.mail", MailSettings, "\"mail\"", """
            import { mail } from '@island/api';
            const settle = (p) => p.then((v) => JSON.stringify(v), (e) => 'ERR ' + e.message);
            export function activate() {
              setTimeout(async () => {
                console.info('qq ' + await settle(mail.check({ host: 'imap.qq.com', user: 'me@qq.com', passwordSetting: 'pw' }, { limit: 10 })));
                console.info('custom ' + await settle(mail.status({ hostSetting: 'server', port: 993, user: 'boss@corp.test', passwordSetting: 'pw2' })));
                console.info('evil ' + await settle(mail.status({ host: 'imap.evil.test', user: 'x', passwordSetting: 'pw' })));
                console.info('notsecret ' + await settle(mail.status({ host: 'imap.qq.com', user: 'x', passwordSetting: 'server' })));
              }, 300);
            }
            """);
        StartWith(null, Services with { ImapConnect = imap.Connect });
        SetSetting("test.mail", "pw", JsonSerializer.Serialize(imap.Password));
        SetSetting("test.mail", "pw2", "\"密码1\"");              // not quotable: sent as a literal
        SetSetting("test.mail", "server", "\"mail.corp.test\"");

        var qq = WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("qq ")), "qq check").Message["qq ".Length..];
        Assert.False(qq.StartsWith("ERR"), qq);
        using var doc = JsonDocument.Parse(qq);
        var r = doc.RootElement;
        Assert.Equal(2, r.GetProperty("unread").GetInt32());
        Assert.Equal(3, r.GetProperty("total").GetInt32());
        var messages = r.GetProperty("messages").EnumerateArray().ToList();
        Assert.Equal([103L, 102L, 101L], messages.Select(m => m.GetProperty("uid").GetInt64()));    // newest first
        Assert.Equal("bob@example.org", messages[0].GetProperty("from").GetString());
        Assert.Equal("Hi there", messages[0].GetProperty("preview").GetString());                  // HTML stripped
        Assert.Equal("季度总结", messages[1].GetProperty("subject").GetString());                    // literal subject
        Assert.Equal("季度总结已经发出，请查收。", messages[1].GetProperty("preview").GetString());  // base64 plain part
        Assert.False(messages[1].GetProperty("unread").GetBoolean());
        Assert.Equal("周报", messages[2].GetProperty("subject").GetString());                        // GBK encoded word
        Assert.Equal("张三", messages[2].GetProperty("from").GetString());                           // Q encoded word
        Assert.Equal("你好", messages[2].GetProperty("preview").GetString());                        // GBK quoted-printable

        var custom = WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("custom ")), "custom server").Message;
        Assert.Contains("\"unread\":2", custom);
        Assert.Contains("imap.qq.com:993", imap.Hosts);
        Assert.Contains("mail.corp.test:993", imap.Hosts);                                            // what the user typed
        Assert.Contains("me@qq.com|" + imap.Password, imap.Logins);                                   // quoting round-trips
        Assert.Contains("boss@corp.test|密码1", imap.Logins);                                         // literal round-trips

        Assert.Contains("不能直接连接 imap.evil.test", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("evil ")), "unknown host").Message);
        Assert.Contains("不是这个插件的密码设置", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("notsecret ")), "not a password").Message);
        Assert.DoesNotContain(imap.Hosts, h => h.StartsWith("imap.evil.test"));
        // Read-only: EXAMINE and BODY.PEEK, never SELECT or STORE (nothing gets marked read).
        Assert.Contains(imap.Commands, c => c.Contains("EXAMINE INBOX"));
        Assert.DoesNotContain(imap.Commands, c => c.Contains("SELECT") || c.Contains("STORE") || (c.Contains("BODY[") && !c.Contains("BODY.PEEK[")));
    }

    /// <summary>A token endpoint that answers with an id_token naming the account's e-mail.</summary>
    private sealed class FakeIdTokenServer : IDisposable
    {
        private readonly HttpListener _listener = new();

        public FakeIdTokenServer(string email, string accessToken)
        {
            Url = $"http://127.0.0.1:{FreePort()}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            var payload = Convert.ToBase64String(Encoding.UTF8.GetBytes($$"""{"email":"{{email}}"}""")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            _ = Task.Run(async () =>
            {
                while (_listener.IsListening)
                {
                    HttpListenerContext ctx;
                    try { ctx = await _listener.GetContextAsync(); } catch { return; }
                    var bytes = Encoding.UTF8.GetBytes($$"""{"access_token":"{{accessToken}}","refresh_token":"r","expires_in":3600,"id_token":"h.{{payload}}.s"}""");
                    ctx.Response.ContentType = "application/json";
                    await ctx.Response.OutputStream.WriteAsync(bytes);
                    ctx.Response.Close();
                }
            });
        }

        public string Url { get; }

        public void Dispose() => _listener.Close();
    }

    private static Func<Uri, Task> SigningInBrowser() => uri =>
    {
        var q = System.Web.HttpUtility.ParseQueryString(uri.Query);
        _ = Task.Run(async () =>
        {
            using var browser = new HttpClient();
            await browser.GetStringAsync($"{q["redirect_uri"]}?state={q["state"]}&code=ok");
        });
        return Task.CompletedTask;
    };

    [Fact]
    public void Signed_in_mail_uses_xoauth2_on_the_providers_own_server_only()
    {
        using var imap = new FakeImapServer();
        using var google = new FakeIdTokenServer("me@gmail.com", "at-good");
        using var other = new FakeIdTokenServer("me@corp.test", "at-other");
        var services = Services with
        {
            ImapConnect = imap.Connect,
            OAuthCallbackPort = FreePort(),
            OpenBrowser = SigningInBrowser(),
            OAuthProfiles = new Dictionary<string, OAuthProfile>
            {
                ["google"] = new("google", "Google", new(google.Url + "/authorize"), new(google.Url + "/token"),
                    OAuthTokenStyle.Form, [], ImapHosts: ["imap.gmail.com"]),
                // A provider whose sign-in is not for mail.
                ["feishu"] = new("feishu", "飞书", new(other.Url + "/authorize"), new(other.Url + "/token"), OAuthTokenStyle.Form, []),
            },
        };
        _permissions = "\"mail\", \"oauth:google\", \"oauth:feishu\"";
        StartWith("""
            import { mail, oauth } from '@island/api';
            const settle = (p) => p.then((v) => JSON.stringify(v), (e) => 'ERR ' + e.message);
            export function activate() {
              setTimeout(async () => {
                const g = await oauth.signIn('google', { clientId: 'cid', scopes: ['openid', 'email', 'https://mail.google.com/'] });
                console.info('label ' + g.label);
                console.info('gmail ' + await settle(mail.status({ oauth: g.handle, host: 'imap.evil.test' })));
                const f = await oauth.signIn('feishu', { clientId: 'cid' });
                console.info('feishu ' + await settle(mail.status({ oauth: f.handle })));
              }, 100);
            }
            """, services);

        Assert.Equal("label me@gmail.com", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("label ")), "label from id_token").Message);
        var gmail = WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("gmail ")), "gmail").Message;
        Assert.Contains("\"unread\":2", gmail);
        Assert.Equal(["imap.gmail.com:993"], imap.Hosts);                         // the plugin's "host" is ignored
        Assert.Contains("xoauth2|me@gmail.com|at-good", imap.Logins);
        Assert.Contains("不能用来收邮件", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("feishu ")), "feishu").Message);
        Assert.Single(imap.Hosts);                                                 // the feishu token never left
    }

    [Fact]
    public void A_rejected_token_is_reported_after_answering_the_servers_challenge()
    {
        using var imap = new FakeImapServer { Token = "something-else" };
        using var google = new FakeIdTokenServer("me@gmail.com", "at-good");
        var services = Services with
        {
            ImapConnect = imap.Connect,
            OAuthCallbackPort = FreePort(),
            OpenBrowser = SigningInBrowser(),
            OAuthProfiles = new Dictionary<string, OAuthProfile>
            {
                ["google"] = new("google", "Google", new(google.Url + "/authorize"), new(google.Url + "/token"),
                    OAuthTokenStyle.Form, [], ImapHosts: ["imap.gmail.com"]),
            },
        };
        _permissions = "\"mail\", \"oauth:google\"";
        StartWith("""
            import { mail, oauth } from '@island/api';
            export function activate() {
              setTimeout(async () => {
                const g = await oauth.signIn('google', { clientId: 'cid', scopes: ['email'] });
                try { await mail.status({ oauth: g.handle }); } catch (e) { console.info('rejected: ' + e.message); }
              }, 100);
            }
            """, services);
        Assert.Contains("邮箱登录被拒绝", WaitFor(() => _logs.FirstOrDefault(l => l.Message.StartsWith("rejected: ")), "rejected").Message);
    }

    [Fact]
    public void The_mail_plugin_shows_unread_mail_and_announces_new_mail_once()
    {
        using var imap = new FakeImapServer();
        CopyPlugin(Path.Combine(RepoPlugins(), "com.o_o.mail"), Path.Combine(_root, "com.o_o.mail"));
        StartWith(null, Services with { ImapConnect = imap.Connect });
        SetSetting("com.o_o.mail", "password1", JsonSerializer.Serialize(imap.Password));
        SetSetting("com.o_o.mail", "user1", "\"me@qq.com\"");                       // preset1 defaults to QQ 邮箱

        var chip = WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.mail/unread" && c.View is not null && Texts(c.View).Contains("2")), "unread chip");
        Assert.NotNull(chip);
        var page = WaitForPage(s => s.PluginId == "com.o_o.mail" && Nodes(s.View.Expanded).Count(n => n.Type == "item") == 3, "three mails");
        var rows = Nodes(page.View.Expanded).Where(n => n.Type == "item").Select(r => string.Join("|", Texts(r))).ToList();
        Assert.StartsWith("●|bob@example.org|Hello", rows[0]);
        Assert.Contains("周报", rows[2]);
        Assert.Contains("imap.qq.com:993", imap.Hosts);
        Assert.Empty(_alertsSeen);                                                   // what was there is not "new"

        imap.Add(104, seen: false,
            "ENVELOPE (\"Mon, 10 Nov 2026 11:00:00 +0800\" \"Lunch?\" ((\"Amy\" NIL \"amy\" \"example.com\")) NIL NIL NIL NIL NIL NIL \"<4@x>\") " +
            "BODYSTRUCTURE (\"TEXT\" \"PLAIN\" (\"CHARSET\" \"utf-8\") NIL NIL \"7BIT\" 5 1 NIL NIL NIL NIL)", Encoding.UTF8.GetBytes("12:00"));
        SetSetting("com.o_o.mail", "every", "2");                                  // any change checks again
        var alert = WaitFor(() => _alertsSeen.FirstOrDefault(a => a.PluginId == "com.o_o.mail"), "new mail alert");
        Assert.Equal("1 封新邮件", Text(alert.Trailing));
        WaitFor(() => _chipViews.LastOrDefault(c => c.Key == "com.o_o.mail/unread" && Texts(c.View).Contains("3")), "chip counts the new one");

        SetSetting("com.o_o.mail", "every", "3");                                  // checked again: nothing new
        WaitFor(() => imap.Commands.Count(c => c.Contains("STATUS")) >= 4 ? "ok" : null, "third check");
        Thread.Sleep(300);
        Assert.Single(_alertsSeen, a => a.PluginId == "com.o_o.mail");
    }
}
