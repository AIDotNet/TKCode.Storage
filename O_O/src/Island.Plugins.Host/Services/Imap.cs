using System.Globalization;
using System.Net.Security;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace Island.Plugins.Host.Services;

/// <summary>
/// A small, read-only IMAP4rev1 client for the <c>mail</c> capability (docs/design/05-builtin-plugins.md §9): just
/// what a mail notifier needs — log in (LOGIN or SASL XOAUTH2), STATUS, EXAMINE (never SELECT, so nothing is marked
/// read), UID SEARCH, UID FETCH of envelopes and a short text preview, LOGOUT. Implicit TLS with the system's
/// certificate validation. Every response is bounded in size and every operation in time.
/// </summary>
internal sealed partial class ImapClient : IAsyncDisposable
{
    public const int MaxLineBytes = 2 * 1024 * 1024;
    public const int MaxLiteralBytes = 1024 * 1024;
    public const int PreviewBytes = 1024;

    private readonly Stream _stream;
    private readonly byte[] _buffer = new byte[16 * 1024];
    private int _start;
    private int _end;
    private int _tag;

    private ImapClient(Stream stream) => _stream = stream;

    /// <summary>How a connection is opened: implicit TLS by default; tests may give a plain stream to a fake server.</summary>
    public delegate Task<Stream> Connector(string host, int port, CancellationToken ct);

    public static async Task<Stream> ConnectTls(string host, int port, CancellationToken ct)
    {
        var tcp = new TcpClient();
        try
        {
            await tcp.ConnectAsync(host, port, ct);
            var tls = new SslStream(tcp.GetStream(), leaveInnerStreamOpen: false);
            await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = host }, ct);
            return tls;
        }
        catch (Exception e) when (e is SocketException or IOException or System.Security.Authentication.AuthenticationException)
        {
            tcp.Dispose();
            throw new PluginCapabilityException(e is System.Security.Authentication.AuthenticationException
                ? $"{host} 的证书无效，已拒绝连接。"
                : $"无法连接邮件服务器 {host}:{port}：{e.Message}");
        }
    }

    public static async Task<ImapClient> OpenAsync(Connector connect, string host, int port, CancellationToken ct)
    {
        var client = new ImapClient(await connect(host, port, ct));
        var greeting = await client.ReadLineAsync(ct);
        if (!greeting.Text.StartsWith("* OK", StringComparison.OrdinalIgnoreCase) &&
            !greeting.Text.StartsWith("* PREAUTH", StringComparison.OrdinalIgnoreCase))
        {
            await client.DisposeAsync();
            throw new PluginCapabilityException($"{host} 不是可用的 IMAP 服务器。");
        }
        return client;
    }

    // ---- authentication ----------------------------------------------------------------------------------------

    public async Task LoginAsync(string user, string password, CancellationToken ct)
    {
        var (ok, text, _) = await CommandAsync(["LOGIN", Arg(user), Arg(password)], ct);
        if (!ok)
        {
            throw new PluginCapabilityException($"登录失败：{Reason(text)}请检查用户名和授权码（应用专用密码），并确认邮箱已开启 IMAP。");
        }
    }

    /// <summary>SASL XOAUTH2 (Gmail, Outlook) with the initial response in the command (SASL-IR).</summary>
    public async Task AuthenticateXOAuth2Async(string user, string token, CancellationToken ct)
    {
        var sasl = Convert.ToBase64String(Encoding.UTF8.GetBytes($"user={user}\u0001auth=Bearer {token}\u0001\u0001"));
        var (ok, text, _) = await CommandAsync(["AUTHENTICATE", "XOAUTH2", sasl], ct, onContinuation: () => [] /* error details: answer empty */);
        if (!ok)
        {
            throw new PluginCapabilityException($"邮箱登录被拒绝：{Reason(text)}请重新登录这个账户。");
        }
    }

    // ---- mailbox -----------------------------------------------------------------------------------------------

    public async Task<(int Total, int Unread)> StatusAsync(CancellationToken ct)
    {
        var (ok, text, untagged) = await CommandAsync(["STATUS", "INBOX", "(MESSAGES UNSEEN)"], ct);
        if (!ok)
        {
            throw new PluginCapabilityException($"读取收件箱失败：{text}");
        }
        foreach (var line in untagged)
        {
            if (line.Count >= 3 && Atom(line[0]) == "STATUS" && line[^1] is List<object> items)
            {
                int total = 0, unread = 0;
                for (var i = 0; i + 1 < items.Count; i += 2)
                {
                    var value = int.TryParse(Atom(items[i + 1]), CultureInfo.InvariantCulture, out var n) ? n : 0;
                    switch (Atom(items[i])?.ToUpperInvariant())
                    {
                        case "MESSAGES":
                            total = value;
                            break;
                        case "UNSEEN":
                            unread = value;
                            break;
                    }
                }
                return (total, unread);
            }
        }
        throw new PluginCapabilityException("服务器没有返回收件箱状态。");
    }

    /// <summary>The newest messages in the inbox (read-only: EXAMINE, BODY.PEEK), newest first.</summary>
    public async Task<List<MailMessage>> RecentAsync(int limit, bool unreadOnly, CancellationToken ct)
    {
        var (ok, text, _) = await CommandAsync(["EXAMINE", "INBOX"], ct);
        if (!ok)
        {
            throw new PluginCapabilityException($"打开收件箱失败：{text}");
        }
        (ok, text, var search) = await CommandAsync(unreadOnly ? ["UID", "SEARCH", "UNSEEN"] : ["UID", "SEARCH", "ALL"], ct);
        if (!ok)
        {
            throw new PluginCapabilityException($"搜索邮件失败：{text}");
        }
        var uids = search.Where(l => l.Count > 0 && Atom(l[0]) == "SEARCH")
            .SelectMany(l => l.Skip(1))
            .Select(t => long.TryParse(Atom(t), CultureInfo.InvariantCulture, out var u) ? u : -1)
            .Where(u => u > 0)
            .OrderByDescending(u => u)
            .Take(Math.Clamp(limit, 1, 50))
            .ToList();
        if (uids.Count == 0)
        {
            return [];
        }
        (ok, text, var fetched) = await CommandAsync(
            ["UID", "FETCH", string.Join(',', uids), "(UID FLAGS INTERNALDATE ENVELOPE BODYSTRUCTURE)"], ct);
        if (!ok)
        {
            throw new PluginCapabilityException($"读取邮件失败：{text}");
        }
        var messages = new List<MailMessage>();
        foreach (var line in fetched)
        {
            if (line.Count >= 3 && Atom(line[1]) == "FETCH" && line[2] is List<object> items && MailMessage.Parse(items) is { } message)
            {
                messages.Add(message);
            }
        }
        foreach (var message in messages)
        {
            if (message.PreviewPart is { } part)
            {
                message.Preview = await PreviewAsync(message.Uid, part, ct);
            }
        }
        return [.. messages.OrderByDescending(m => m.Uid)];
    }

    private async Task<string?> PreviewAsync(long uid, TextPart part, CancellationToken ct)
    {
        var section = $"BODY.PEEK[{part.Path}]<0.{PreviewBytes}>";
        var (ok, _, fetched) = await CommandAsync(["UID", "FETCH", uid.ToString(CultureInfo.InvariantCulture), $"({section})"], ct);
        if (!ok)
        {
            return null;
        }
        foreach (var line in fetched)
        {
            if (line.Count >= 3 && line[2] is List<object> items)
            {
                for (var i = 0; i + 1 < items.Count; i += 2)
                {
                    if (Atom(items[i])?.StartsWith("BODY[", StringComparison.OrdinalIgnoreCase) == true && Bytes(items[i + 1]) is { } raw)
                    {
                        return MimeText.Preview(raw, part);
                    }
                }
            }
        }
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            using var quick = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await CommandAsync(["LOGOUT"], quick.Token);
        }
        catch (Exception e) when (e is IOException or OperationCanceledException or PluginCapabilityException or ObjectDisposedException)
        {
            // Leaving anyway.
        }
        await _stream.DisposeAsync();
    }

    // ---- protocol ----------------------------------------------------------------------------------------------

    /// <summary>An argument that the client sends as an atom, a quoted string or a literal, whichever it needs.</summary>
    private readonly record struct Argument(string Value, bool Raw);

    private static Argument Arg(string value) => new(value, Raw: false);

    /// <summary>
    /// Sends a command and collects the untagged responses until the tagged one. Strings that are not plain atoms are
    /// quoted; strings that cannot be quoted (non-ASCII, CR/LF) go as literals after the server's "+" continuation.
    /// </summary>
    private async Task<(bool Ok, string Text, List<List<object>> Untagged)> CommandAsync(
        IReadOnlyList<object> parts, CancellationToken ct, Func<byte[]>? onContinuation = null)
    {
        var tag = $"A{++_tag}";
        var line = new StringBuilder(tag);
        var pending = new List<(string Prefix, byte[] Literal)>();
        foreach (var part in parts)
        {
            line.Append(' ');
            if (part is Argument a)
            {
                if (IsQuotable(a.Value))
                {
                    line.Append('"').Append(a.Value.Replace("\\", "\\\\").Replace("\"", "\\\"")).Append('"');
                }
                else
                {
                    var bytes = Encoding.UTF8.GetBytes(a.Value);
                    line.Append('{').Append(bytes.Length).Append('}');
                    pending.Add((line.ToString(), bytes));
                    line.Clear();
                }
            }
            else
            {
                line.Append((string)part);
            }
        }
        // Literals: send up to each {n}, wait for "+", then the bytes, then continue.
        foreach (var (prefix, literal) in pending)
        {
            await WriteAsync(prefix + "\r\n", ct);
            var go = await ReadLineAsync(ct);
            if (!go.Text.StartsWith('+'))
            {
                return (false, go.Text, []);
            }
            await _stream.WriteAsync(literal, ct);
        }
        await WriteAsync(line + "\r\n", ct);

        var untagged = new List<List<object>>();
        while (true)
        {
            var response = await ReadLineAsync(ct);
            if (response.Text.StartsWith(tag + " ", StringComparison.Ordinal))
            {
                var rest = response.Text[(tag.Length + 1)..];
                var ok = rest.StartsWith("OK", StringComparison.OrdinalIgnoreCase);
                return (ok, rest.Length > 3 ? rest[3..].Trim() : rest, untagged);
            }
            if (response.Text.StartsWith('+'))
            {
                // A server asking for more (e.g. XOAUTH2 error details): answer as the caller says, else cancel.
                await _stream.WriteAsync(onContinuation?.Invoke() ?? Encoding.ASCII.GetBytes("*"), ct);
                await WriteAsync("\r\n", ct);
                continue;
            }
            if (response.Text.StartsWith("* ", StringComparison.Ordinal))
            {
                var tokens = Parse(response);
                if (tokens.Count > 0)
                {
                    untagged.Add(tokens.Skip(0).ToList());
                }
            }
        }
    }

    private static bool IsQuotable(string s) =>
        s.Length <= 1000 && s.All(c => c is >= ' ' and <= '~');

    private Task WriteAsync(string text, CancellationToken ct) => _stream.WriteAsync(Encoding.UTF8.GetBytes(text), ct).AsTask();

    /// <summary>One response: its text with each literal replaced by a marker, and the literals.</summary>
    private sealed record Response(string Text, List<byte[]> Literals);

    private const char LiteralMark = '\u0001';

    private async Task<Response> ReadLineAsync(CancellationToken ct)
    {
        var text = new StringBuilder();
        var literals = new List<byte[]>();
        var total = 0;
        while (true)
        {
            var segment = await ReadRawLineAsync(ct);
            total += segment.Length;
            if (total > MaxLineBytes)
            {
                throw new PluginCapabilityException("服务器的响应太长。");
            }
            var s = Encoding.UTF8.GetString(segment);
            var m = LiteralAtEnd().Match(s);
            if (!m.Success)
            {
                text.Append(s);
                return new Response(text.ToString(), literals);
            }
            var size = long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            if (size > MaxLiteralBytes)
            {
                throw new PluginCapabilityException("服务器返回的内容太大。");
            }
            text.Append(s[..m.Index]).Append(LiteralMark).Append(literals.Count.ToString(CultureInfo.InvariantCulture)).Append(LiteralMark);
            literals.Add(await ReadExactAsync((int)size, ct));
            total += (int)size;
        }
    }

    [GeneratedRegex(@"\{(\d{1,9})\+?\}$")]
    private static partial Regex LiteralAtEnd();

    private async Task<byte[]> ReadRawLineAsync(CancellationToken ct)
    {
        using var line = new MemoryStream();
        while (true)
        {
            if (_start == _end && !await FillAsync(ct))
            {
                throw new PluginCapabilityException("邮件服务器断开了连接。");
            }
            var newline = Array.IndexOf(_buffer, (byte)'\n', _start, _end - _start);
            if (newline < 0)
            {
                line.Write(_buffer, _start, _end - _start);
                _start = _end;
                if (line.Length > MaxLineBytes)
                {
                    throw new PluginCapabilityException("服务器的响应太长。");
                }
                continue;
            }
            line.Write(_buffer, _start, newline - _start);
            _start = newline + 1;
            var bytes = line.ToArray();
            return bytes.Length > 0 && bytes[^1] == '\r' ? bytes[..^1] : bytes;
        }
    }

    private async Task<byte[]> ReadExactAsync(int size, CancellationToken ct)
    {
        var result = new byte[size];
        var filled = 0;
        while (filled < size)
        {
            if (_start == _end && !await FillAsync(ct))
            {
                throw new PluginCapabilityException("邮件服务器断开了连接。");
            }
            var n = Math.Min(size - filled, _end - _start);
            Buffer.BlockCopy(_buffer, _start, result, filled, n);
            _start += n;
            filled += n;
        }
        return result;
    }

    private async Task<bool> FillAsync(CancellationToken ct)
    {
        _start = 0;
        _end = await _stream.ReadAsync(_buffer, ct);
        return _end > 0;
    }

    // ---- parsing -----------------------------------------------------------------------------------------------

    /// <summary>
    /// Parses an untagged response after "* " into tokens: atoms and quoted strings as <see cref="string"/>, literals as
    /// byte[], NIL as null, parenthesised lists as List&lt;object&gt;. Status responses (OK/NO/BAD/BYE) are skipped.
    /// </summary>
    private static List<object> Parse(Response response)
    {
        var s = response.Text;
        var i = 2;
        var first = s.IndexOf(' ', i) is var sp and > 0 ? s[i..sp] : s[i..];
        if (first is "OK" or "NO" or "BAD" or "BYE" or "PREAUTH")
        {
            return [];
        }
        var result = new List<object>();
        while (i < s.Length)
        {
            if (s[i] == ' ')
            {
                i++;
                continue;
            }
            result.Add(Token(s, ref i, response.Literals)!);
        }
        return result;
    }

    private static object? Token(string s, ref int i, List<byte[]> literals)
    {
        var c = s[i];
        if (c == '(')
        {
            i++;
            var list = new List<object>();
            while (i < s.Length && s[i] != ')')
            {
                if (s[i] == ' ')
                {
                    i++;
                    continue;
                }
                list.Add(Token(s, ref i, literals)!);
            }
            i++; // ')'
            return list;
        }
        if (c == '"')
        {
            var sb = new StringBuilder();
            i++;
            while (i < s.Length && s[i] != '"')
            {
                if (s[i] == '\\' && i + 1 < s.Length)
                {
                    i++;
                }
                sb.Append(s[i++]);
            }
            i++;
            return sb.ToString();
        }
        if (c == LiteralMark)
        {
            var end = s.IndexOf(LiteralMark, i + 1);
            var index = int.Parse(s[(i + 1)..end], CultureInfo.InvariantCulture);
            i = end + 1;
            return literals[index];
        }
        // An atom; brackets keep their contents together (BODY[1.2]<0>, [UIDVALIDITY 3]).
        var start = i;
        var depth = 0;
        while (i < s.Length)
        {
            var ch = s[i];
            if (ch == '[')
            {
                depth++;
            }
            else if (ch == ']')
            {
                depth--;
            }
            else if (depth == 0 && (ch == ' ' || ch == ')' || ch == '('))
            {
                break;
            }
            i++;
        }
        var atom = s[start..i];
        return atom.Equals("NIL", StringComparison.OrdinalIgnoreCase) ? null : atom;
    }

    internal static string? Atom(object? token) => token switch
    {
        string s => s,
        byte[] b => Encoding.UTF8.GetString(b),
        _ => null,
    };

    internal static byte[]? Bytes(object? token) => token switch
    {
        byte[] b => b,
        string s => Encoding.UTF8.GetBytes(s),
        _ => null,
    };

    private static string Reason(string text) =>
        text.Length == 0 ? "" : $"{Regex.Replace(text, @"^\[[^\]]*\]\s*", "")}。";
}

/// <summary>The text part of a message used for its preview: section path, transfer encoding, charset, HTML or not.</summary>
internal sealed record TextPart(string Path, string Encoding, string? Charset, bool Html);

/// <summary>One message as the mail capability reports it.</summary>
internal sealed class MailMessage
{
    public long Uid { get; init; }
    public bool Unread { get; init; }
    public string From { get; init; } = "";
    public string? FromAddress { get; init; }
    public string Subject { get; init; } = "";
    public DateTimeOffset? Date { get; init; }
    public TextPart? PreviewPart { get; init; }
    public string? Preview { get; set; }

    /// <summary>From a FETCH response's item list (UID, FLAGS, INTERNALDATE, ENVELOPE, BODYSTRUCTURE).</summary>
    public static MailMessage? Parse(List<object> items)
    {
        long uid = 0;
        var unread = true;
        DateTimeOffset? date = null;
        List<object>? envelope = null;
        List<object>? structure = null;
        for (var i = 0; i + 1 < items.Count; i += 2)
        {
            switch (ImapClient.Atom(items[i])?.ToUpperInvariant())
            {
                case "UID":
                    long.TryParse(ImapClient.Atom(items[i + 1]), CultureInfo.InvariantCulture, out uid);
                    break;
                case "FLAGS":
                    unread = !(items[i + 1] is List<object> flags && flags.Any(f => string.Equals(ImapClient.Atom(f), "\\Seen", StringComparison.OrdinalIgnoreCase)));
                    break;
                case "INTERNALDATE":
                    if (DateTimeOffset.TryParseExact(ImapClient.Atom(items[i + 1]), "d-MMM-yyyy HH:mm:ss zzz", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        date = d;
                    }
                    break;
                case "ENVELOPE":
                    envelope = items[i + 1] as List<object>;
                    break;
                case "BODYSTRUCTURE":
                    structure = items[i + 1] as List<object>;
                    break;
            }
        }
        if (uid <= 0)
        {
            return null;
        }
        string subject = "", from = "";
        string? address = null;
        if (envelope is { Count: >= 3 })
        {
            subject = MimeText.DecodeWords(ImapClient.Atom(envelope[1]) ?? "");
            if (envelope[2] is List<object> { Count: > 0 } senders && senders[0] is List<object> { Count: >= 4 } sender)
            {
                var name = MimeText.DecodeWords(ImapClient.Atom(sender[0]) ?? "");
                address = $"{ImapClient.Atom(sender[2])}@{ImapClient.Atom(sender[3])}";
                from = name.Length > 0 ? name : address;
            }
            if (date is null && DateTimeOffset.TryParse(ImapClient.Atom(envelope[0]), CultureInfo.InvariantCulture, DateTimeStyles.None, out var sent))
            {
                date = sent;
            }
        }
        return new MailMessage
        {
            Uid = uid,
            Unread = unread,
            From = from,
            FromAddress = address,
            Subject = subject,
            Date = date,
            PreviewPart = structure is null ? null : FindText(structure, ""),
        };
    }

    /// <summary>The first text/plain part (else text/html) in a BODYSTRUCTURE, with its section path.</summary>
    private static TextPart? FindText(List<object> structure, string path)
    {
        if (structure.Count > 0 && structure[0] is List<object>)
        {
            // multipart: (part)(part)… "subtype" …
            TextPart? html = null;
            var n = 0;
            foreach (var child in structure.TakeWhile(t => t is List<object>).Cast<List<object>>())
            {
                n++;
                var found = FindText(child, path.Length == 0 ? n.ToString(CultureInfo.InvariantCulture) : $"{path}.{n}");
                if (found is { Html: false })
                {
                    return found;
                }
                html ??= found;
            }
            return html;
        }
        if (structure.Count < 6 || !string.Equals(ImapClient.Atom(structure[0]), "TEXT", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }
        var subtype = ImapClient.Atom(structure[1])?.ToUpperInvariant();
        if (subtype is not ("PLAIN" or "HTML"))
        {
            return null;
        }
        string? charset = null;
        if (structure[2] is List<object> parameters)
        {
            for (var i = 0; i + 1 < parameters.Count; i += 2)
            {
                if (string.Equals(ImapClient.Atom(parameters[i]), "CHARSET", StringComparison.OrdinalIgnoreCase))
                {
                    charset = ImapClient.Atom(parameters[i + 1]);
                }
            }
        }
        // A single-part message's text is section 1 too.
        return new TextPart(path.Length == 0 ? "1" : path, ImapClient.Atom(structure[5])?.ToUpperInvariant() ?? "7BIT", charset, subtype == "HTML");
    }
}

/// <summary>Decoding of mail text: RFC 2047 encoded words, transfer encodings and charsets (GBK, Big5…).</summary>
internal static partial class MimeText
{
    static MimeText()
    {
        // GBK / GB18030 / Big5 / Shift_JIS… are common in Chinese and Japanese mail and not built into .NET.
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static Encoding Charset(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return Encoding.UTF8;
        }
        var n = name.Trim().Trim('"').ToLowerInvariant();
        n = n switch
        {
            "gb2312" or "gbk" or "x-gbk" or "cp936" => "gb18030", // a superset: decodes all three
            "utf8" => "utf-8",
            _ => n,
        };
        try
        {
            return Encoding.GetEncoding(n);
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    [GeneratedRegex(@"=\?([^?\s]+)\?([BbQq])\?([^?\s]*)\?=")]
    private static partial Regex EncodedWord();

    [GeneratedRegex(@"(=\?[^?\s]+\?[BbQq]\?[^?\s]*\?=)\s+(?==\?)")]
    private static partial Regex BetweenWords();

    /// <summary>Decodes RFC 2047 encoded words; whitespace between two encoded words is dropped (RFC 2047 §6.2).</summary>
    public static string DecodeWords(string text)
    {
        if (!text.Contains("=?", StringComparison.Ordinal))
        {
            return text;
        }
        text = BetweenWords().Replace(text, "$1");
        return EncodedWord().Replace(text, m =>
        {
            var charset = m.Groups[1].Value.Split('*')[0]; // RFC 2231 language suffix
            var bytes = m.Groups[2].Value is "B" or "b" ? Base64(m.Groups[3].Value) : QuotedPrintable(m.Groups[3].Value.Replace('_', ' '));
            return Charset(charset).GetString(bytes);
        });
    }

    /// <summary>A one-line preview from the start of a text part (it may be cut mid-way: decoding is lenient).</summary>
    public static string Preview(byte[] raw, TextPart part)
    {
        var ascii = Encoding.Latin1.GetString(raw);
        var bytes = part.Encoding switch
        {
            "BASE64" => Base64(ascii),
            "QUOTED-PRINTABLE" => QuotedPrintable(ascii),
            _ => raw,
        };
        var text = Charset(part.Charset).GetString(bytes);
        if (part.Html)
        {
            text = Regex.Replace(text, @"<(style|script)[\s\S]*?</\1>", " ", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]*>", " ");
            text = System.Net.WebUtility.HtmlDecode(text);
        }
        text = Regex.Replace(text, @"\s+", " ").Trim().TrimEnd('�');
        return text.Length > 140 ? text[..140] + "…" : text;
    }

    private static byte[] Base64(string text)
    {
        var clean = new string(text.Where(c => char.IsAsciiLetterOrDigit(c) || c is '+' or '/').ToArray());
        // Padding is re-added; a cut-off preview may end in a group of one character, which carries no whole byte.
        if (clean.Length % 4 == 1)
        {
            clean = clean[..^1];
        }
        clean = clean.PadRight(clean.Length + (4 - clean.Length % 4) % 4, '=');
        try
        {
            return Convert.FromBase64String(clean);
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static byte[] QuotedPrintable(string text)
    {
        using var output = new MemoryStream();
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '=')
            {
                if (i + 2 < text.Length && Uri.IsHexDigit(text[i + 1]) && Uri.IsHexDigit(text[i + 2]))
                {
                    output.WriteByte(Convert.ToByte(text.Substring(i + 1, 2), 16));
                    i += 2;
                }
                else if (i + 1 < text.Length && text[i + 1] is '\r' or '\n')
                {
                    // Soft line break.
                    i += text[i + 1] == '\r' && i + 2 < text.Length && text[i + 2] == '\n' ? 2 : 1;
                }
                // A cut-off "=X" at the end is dropped.
            }
            else
            {
                output.WriteByte((byte)c);
            }
        }
        return output.ToArray();
    }
}
