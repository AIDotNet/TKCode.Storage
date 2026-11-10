using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// <c>http.fetch</c> for plugins (docs/design/03-plugin-system.md §7): only hosts the plugin declared with
/// <c>network:</c> permissions, HTTPS only (plain HTTP only to this machine with <c>network:localhost</c>), redirects
/// followed by hand so every hop is checked against the same allow-list, bounded response size and time.
/// </summary>
/// <param name="userUrls">
/// The addresses in the plugin's <c>network:@setting</c> settings (what the user typed). A request may go to such a
/// site — same scheme, host and port — including plain http when that is what the user entered.
/// </param>
internal sealed class PluginHttp(HttpClient client, PluginPermissions permissions, string userAgent, Func<IEnumerable<string?>>? userUrls = null)
{
    public const int MaxRedirects = 5;
    public const int MaxResponseBytes = 5 * 1024 * 1024;
    public const int MaxRequestBytes = 1024 * 1024;
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaxTimeout = TimeSpan.FromSeconds(60);

    // Headers the plugin may not set: they belong to the HTTP stack or would bypass its framing.
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "host", "content-length", "connection", "transfer-encoding", "expect", "upgrade", "te", "trailer",
        "keep-alive", "proxy-authorization", "proxy-connection",
    };

    /// <summary>The shared handler: no automatic redirects (checked per hop here), decompression on.</summary>
    public static HttpClient CreateClient() => new(new SocketsHttpHandler
    {
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.All,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
        UseCookies = false,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan, // per request below
    };

    /// <summary>Runs a request described by <paramref name="requestJson"/>; returns the response as JSON.</summary>
    /// <exception cref="PluginCapabilityException">The request is not allowed or failed.</exception>
    /// <param name="credentialFor">
    /// For requests with an <c>auth</c> handle: returns the token for that handle and URL, or throws when the
    /// credential may not go to that host. Called for every hop, so a redirect cannot carry a token elsewhere.
    /// </param>
    public async Task<string> FetchAsync(
        string requestJson,
        CancellationToken cancellationToken,
        Func<string, Uri, LocalCredential>? credentialFor = null)
    {
        var request = Parse(requestJson);
        if (request.Auth is not null && credentialFor is null)
        {
            throw new PluginCapabilityException("This plugin has no localAuth permission.");
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(request.Timeout);
        try
        {
            var uri = request.Uri;
            var method = request.Method;
            var body = request.Body;
            for (var hop = 0; ; hop++)
            {
                Authorize(uri);
                using var message = new HttpRequestMessage(method, uri);
                message.Headers.UserAgent.ParseAdd(userAgent);
                if (body is not null)
                {
                    message.Content = new StringContent(body, Encoding.UTF8);
                    message.Content.Headers.ContentType = null;
                }
                if (request.Auth is not null)
                {
                    var credential = credentialFor!(request.Auth, uri);
                    if (credential.Header is { } header)
                    {
                        message.Headers.TryAddWithoutValidation(header, credential.AccessToken);
                    }
                    else
                    {
                        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credential.AccessToken);
                    }
                }
                foreach (var (name, value) in request.Headers)
                {
                    if (request.Auth is not null && (name.Equals("authorization", StringComparison.OrdinalIgnoreCase) ||
                                                     name.StartsWith("x-acs-dingtalk-access-token", StringComparison.OrdinalIgnoreCase)))
                    {
                        continue; // the host's credential wins
                    }
                    if (!message.Headers.TryAddWithoutValidation(name, value))
                    {
                        message.Content ??= new ByteArrayContent([]);
                        message.Content.Headers.TryAddWithoutValidation(name, value);
                    }
                }

                using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (IsRedirect(response.StatusCode) && response.Headers.Location is { } location)
                {
                    if (hop >= MaxRedirects)
                    {
                        throw new PluginCapabilityException($"Too many redirects (more than {MaxRedirects}).");
                    }
                    uri = location.IsAbsoluteUri ? location : new Uri(uri, location);
                    // 303, and 301/302 after a POST, continue as GET without a body, as browsers do.
                    if (response.StatusCode == HttpStatusCode.SeeOther ||
                        (method == HttpMethod.Post && response.StatusCode is HttpStatusCode.Moved or HttpStatusCode.Found))
                    {
                        method = HttpMethod.Get;
                        body = null;
                    }
                    continue;
                }
                var text = await ReadBodyAsync(response, timeout.Token);
                return Serialize(response, uri, text);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new PluginCapabilityException($"请求超时（{request.Timeout.TotalSeconds:0} 秒）。");
        }
        catch (HttpRequestException e)
        {
            throw new PluginCapabilityException($"请求失败：{e.Message}");
        }
    }

    private void Authorize(Uri uri)
    {
        if (IsUserSite(uri))
        {
            return;
        }
        var loopback = PluginPermissions.IsLoopback(uri.Host);
        if (uri.Scheme != Uri.UriSchemeHttps && !(uri.Scheme == Uri.UriSchemeHttp && loopback))
        {
            throw new PluginCapabilityException($"Only https:// URLs are allowed (got {uri.Scheme}://).");
        }
        if (!permissions.AllowsHost(uri.IdnHost))
        {
            var needed = loopback ? PluginPermissions.Localhost : uri.IdnHost;
            throw new PluginCapabilityException(
                $"没有访问 '{uri.IdnHost}' 的权限：需要在插件清单中声明 \"network:{needed}\"。");
        }
    }

    /// <summary>The request goes to a site the user entered in a <c>network:@setting</c> setting.</summary>
    private bool IsUserSite(Uri uri)
    {
        if (userUrls is null || uri.Scheme is not ("https" or "http"))
        {
            return false;
        }
        foreach (var raw in userUrls())
        {
            // webcal:// is how calendar subscriptions are shared; it means https.
            var text = raw?.Trim();
            if (text?.StartsWith("webcal://", StringComparison.OrdinalIgnoreCase) == true)
            {
                text = "https://" + text["webcal://".Length..];
            }
            if (Uri.TryCreate(text, UriKind.Absolute, out var site) && site.Scheme is "https" or "http" &&
                site.Scheme == uri.Scheme && site.IdnHost.Equals(uri.IdnHost, StringComparison.OrdinalIgnoreCase) &&
                site.Port == uri.Port && string.IsNullOrEmpty(site.UserInfo))
            {
                return true;
            }
        }
        return false;
    }

    private static bool IsRedirect(HttpStatusCode code) => code is HttpStatusCode.Moved or HttpStatusCode.Found
        or HttpStatusCode.SeeOther or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect;

    private static async Task<string> ReadBodyAsync(HttpResponseMessage response, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > MaxResponseBytes)
        {
            throw new PluginCapabilityException($"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token);
        using var buffer = new MemoryStream();
        var chunk = new byte[16 * 1024];
        int read;
        while ((read = await stream.ReadAsync(chunk, token)) > 0)
        {
            if (buffer.Length + read > MaxResponseBytes)
            {
                throw new PluginCapabilityException($"The response is larger than {MaxResponseBytes / 1024 / 1024} MB.");
            }
            buffer.Write(chunk, 0, read);
        }
        var charset = response.Content.Headers.ContentType?.CharSet;
        var encoding = Encoding.UTF8;
        if (charset is not null)
        {
            try
            {
                encoding = Encoding.GetEncoding(charset.Trim('"'));
            }
            catch (ArgumentException)
            {
            }
        }
        return encoding.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    private static string Serialize(HttpResponseMessage response, Uri finalUri, string body)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteNumber("status", (int)response.StatusCode);
            w.WriteString("statusText", response.ReasonPhrase ?? "");
            w.WriteString("url", finalUri.ToString());
            w.WriteStartObject("headers");
            foreach (var (name, values) in response.Headers.Concat(response.Content.Headers))
            {
                w.WriteString(name.ToLowerInvariant(), string.Join(", ", values));
            }
            w.WriteEndObject();
            w.WriteString("body", body);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private sealed record Request(
        Uri Uri, HttpMethod Method, IReadOnlyList<(string, string)> Headers, string? Body, TimeSpan Timeout, string? Auth);

    private static Request Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var url = root.TryGetProperty("url", out var u) ? u.GetString() : null;
        if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            throw new PluginCapabilityException($"Invalid URL '{url}'.");
        }
        var method = root.TryGetProperty("method", out var m) && m.GetString() is { Length: > 0 } verb
            ? new HttpMethod(verb.ToUpperInvariant())
            : HttpMethod.Get;
        var headers = new List<(string, string)>();
        if (root.TryGetProperty("headers", out var h) && h.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in h.EnumerateObject())
            {
                if (ForbiddenHeaders.Contains(p.Name) || p.Name.StartsWith("proxy-", StringComparison.OrdinalIgnoreCase))
                {
                    throw new PluginCapabilityException($"The header '{p.Name}' cannot be set by plugins.");
                }
                headers.Add((p.Name, p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString()! : p.Value.GetRawText()));
            }
        }
        string? body = root.TryGetProperty("body", out var b) && b.ValueKind == JsonValueKind.String ? b.GetString() : null;
        if (body is not null && Encoding.UTF8.GetByteCount(body) > MaxRequestBytes)
        {
            throw new PluginCapabilityException($"The request body is larger than {MaxRequestBytes / 1024} KB.");
        }
        var timeout = root.TryGetProperty("timeoutMs", out var t) && t.TryGetDouble(out var ms) && ms > 0
            ? TimeSpan.FromMilliseconds(Math.Min(ms, MaxTimeout.TotalMilliseconds))
            : DefaultTimeout;
        var auth = root.TryGetProperty("auth", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null;
        return new Request(uri, method, headers, body, timeout, auth);
    }
}

/// <summary>A capability call the host refuses or cannot complete; its message reaches the plugin as an Error.</summary>
/// <summary>A capability call the plugin got wrong or the host cannot serve; its message goes back to the plugin.</summary>
public sealed class PluginCapabilityException(string message) : Exception(message);
