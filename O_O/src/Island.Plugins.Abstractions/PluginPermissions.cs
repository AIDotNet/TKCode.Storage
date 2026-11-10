namespace Island.Plugins;

/// <summary>
/// The permissions a plugin holds (docs/design/03-plugin-system.md §7). Parsed strictly: an unknown permission is a
/// manifest error, so a typo surfaces when the plugin is loaded instead of as a capability that silently never works.
/// </summary>
public sealed class PluginPermissions
{
    /// <summary>Permissions without an argument. Those not implemented yet are accepted so manifests stay valid.</summary>
    public static readonly IReadOnlySet<string> Simple = new HashSet<string>(StringComparer.Ordinal)
    {
        "secrets",
        "notifications",
        "media",
        "clipboard.read",
        "clipboard.write",
        "clipboard.watch",
        "clipboard.paste",
        "files.shelf",
        "system.info",
        "mail",
        "shell.openUrl",
        "sidecar",
        "storage.sql",
        "location.coarse",
    };

    /// <summary>Permissions that take an argument after a colon, e.g. <c>network:api.deepseek.com</c>.</summary>
    public static readonly IReadOnlySet<string> WithArgument = new HashSet<string>(StringComparer.Ordinal)
    {
        "network",
        "oauth",
        "localAuth",
    };

    /// <summary>Local clients whose login plugins may reuse (<c>localAuth:&lt;id&gt;</c>).</summary>
    public static readonly IReadOnlySet<string> LocalAuthSources = new HashSet<string>(StringComparer.Ordinal)
    {
        "claude-code",
        "codex-cli",
    };

    /// <summary>Sign-in providers the host knows (<c>oauth:&lt;id&gt;</c>); their endpoints are fixed in the host.</summary>
    public static readonly IReadOnlySet<string> OAuthProviders = new HashSet<string>(StringComparer.Ordinal)
    {
        "google",
        "feishu",
        "dingtalk",
    };

    /// <summary>The <c>network:</c> argument that allows plain-HTTP requests to this machine.</summary>
    public const string Localhost = "localhost";

    /// <summary>
    /// <c>network:@&lt;setting&gt;</c>: the site whose address the user types into that (string) setting, for
    /// self-hosted services whose host cannot be declared in advance. The plugin cannot change its settings, so the
    /// address always comes from the user.
    /// </summary>
    public const string SettingHostPrefix = "@";

    private readonly HashSet<string> _all;
    private readonly List<string> _hostSettings = [];
    private readonly HashSet<string> _hosts = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _wildcardSuffixes = [];

    private PluginPermissions(HashSet<string> all)
    {
        _all = all;
        foreach (var p in all)
        {
            if (!p.StartsWith("network:", StringComparison.Ordinal))
            {
                continue;
            }
            var host = p["network:".Length..];
            if (host.StartsWith(SettingHostPrefix, StringComparison.Ordinal))
            {
                _hostSettings.Add(host[SettingHostPrefix.Length..]);
            }
            else if (host.StartsWith("*.", StringComparison.Ordinal))
            {
                _wildcardSuffixes.Add(host[1..]); // ".feishu.cn"
            }
            else
            {
                _hosts.Add(host);
            }
        }
    }

    public static PluginPermissions None { get; } = new([]);

    public IReadOnlyCollection<string> All => _all;

    /// <summary>Only the permissions in <paramref name="granted"/> (the user's decision); never adds any.</summary>
    public PluginPermissions Restrict(IEnumerable<string> granted)
    {
        var set = new HashSet<string>(_all, StringComparer.Ordinal);
        set.IntersectWith(granted);
        return new PluginPermissions(set);
    }

    public bool Has(string permission) => _all.Contains(permission);

    public bool HasNetwork => _hosts.Count > 0 || _wildcardSuffixes.Count > 0 || _hostSettings.Count > 0;

    /// <summary>The settings whose URL value names a site the plugin may reach (<c>network:@key</c>).</summary>
    public IReadOnlyList<string> HostSettings => _hostSettings;

    /// <summary>
    /// Whether requests to <paramref name="host"/> are allowed. <c>network:*.feishu.cn</c> covers every subdomain of
    /// feishu.cn but not feishu.cn itself, which needs its own entry.
    /// </summary>
    public bool AllowsHost(string host)
    {
        host = host.TrimEnd('.');
        if (IsLoopback(host))
        {
            return _hosts.Contains(Localhost);
        }
        return _hosts.Contains(host) ||
               _wildcardSuffixes.Exists(s => host.Length > s.Length && host.EndsWith(s, StringComparison.OrdinalIgnoreCase));
    }

    public static bool IsLoopback(string host) =>
        host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || host is "127.0.0.1" or "::1" or "[::1]";

    /// <summary>Parses manifest permissions; returns null and lists the problems when any entry is invalid.</summary>
    public static PluginPermissions? Parse(IEnumerable<string> declared, List<string> problems)
    {
        var all = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in declared)
        {
            var p = raw.Trim();
            var colon = p.IndexOf(':');
            if (colon < 0)
            {
                if (!Simple.Contains(p))
                {
                    problems.Add($"unknown permission '{p}'");
                    continue;
                }
            }
            else
            {
                var kind = p[..colon];
                var arg = p[(colon + 1)..];
                if (!WithArgument.Contains(kind))
                {
                    problems.Add($"unknown permission '{p}'");
                    continue;
                }
                if (kind == "network" && arg.StartsWith(SettingHostPrefix, StringComparison.Ordinal))
                {
                    var key = arg[SettingHostPrefix.Length..];
                    if (key.Length is 0 or > 64 || !char.IsAsciiLetter(key[0]) || !key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
                    {
                        problems.Add($"invalid network permission '{p}': use network:@<setting key>");
                        continue;
                    }
                }
                else if (kind == "network" && !IsValidHostPattern(arg))
                {
                    problems.Add($"invalid network permission '{p}': use a host name such as api.example.com or *.example.com");
                    continue;
                }
                if (kind == "oauth" && !OAuthProviders.Contains(arg))
                {
                    problems.Add($"unknown sign-in provider in '{p}' (known: {string.Join(", ", OAuthProviders)})");
                    continue;
                }
                if (kind == "localAuth" && !LocalAuthSources.Contains(arg))
                {
                    problems.Add($"unknown local login source in '{p}' (known: {string.Join(", ", LocalAuthSources)})");
                    continue;
                }
                if (arg.Length == 0)
                {
                    problems.Add($"permission '{p}' needs an argument");
                    continue;
                }
            }
            all.Add(p);
        }
        return problems.Count == 0 ? new PluginPermissions(all) : null;
    }

    private static bool IsValidHostPattern(string arg)
    {
        var host = arg.StartsWith("*.", StringComparison.Ordinal) ? arg[2..] : arg;
        // A wildcard must keep at least two labels after it: "*.com" would allow half the internet.
        if (arg.StartsWith("*.", StringComparison.Ordinal) && !host.Contains('.'))
        {
            return false;
        }
        return host.Length is > 0 and <= 253 &&
               Uri.CheckHostName(host) is UriHostNameType.Dns or UriHostNameType.IPv4 &&
               !host.Contains('*');
    }
}
