using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Island.Platform;

namespace Island.Plugins.Host.Services;

/// <summary>An account found in a local client's login, without any secret (shown in consent and in the UI).</summary>
public sealed record LocalAccount(
    string Source,
    string Key,
    string Label,
    string? Email,
    string? Plan,
    DateTimeOffset? SubscriptionUntil,
    string? AccountId);

/// <summary>The credential behind a <see cref="LocalAccount"/>. Never handed to plugin code.</summary>
/// <param name="Header">Where the token goes: null for <c>Authorization: Bearer</c>, else this header with the raw token.</param>
public sealed record LocalCredential(string AccessToken, DateTimeOffset? ExpiresAt, string? Header = null);

/// <summary>
/// A local client whose existing login O_O may reuse, read-only (docs/design/05-builtin-plugins.md §10.3).
/// The host never refreshes a token: refreshing rotates the refresh token and would sign the client itself out.
/// </summary>
public abstract class LocalAuthSource
{
    /// <summary>The id used in permissions: <c>localAuth:&lt;Id&gt;</c>.</summary>
    public abstract string Id { get; }

    /// <summary>Human name, for messages: "Claude Code".</summary>
    public abstract string Name { get; }

    /// <summary>The only hosts the credential may be sent to.</summary>
    public abstract IReadOnlyList<string> Hosts { get; }

    /// <summary>Accounts this machine is signed in with. Reads no secret, so it never triggers an OS prompt.</summary>
    /// <param name="extraDirectories">More configuration directories to look in (validated by the source).</param>
    public abstract IReadOnlyList<LocalAccount> Discover(IReadOnlyList<string>? extraDirectories = null);

    /// <summary>The credential, or null when the login is gone. May show an OS prompt (macOS Keychain).</summary>
    public abstract LocalCredential? ReadCredential(string accountKey);

    public bool AllowsHost(string host) => Hosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));

    /// <summary>Configuration directories with a signed-in account, for the console (no secret is read).</summary>
    public virtual IReadOnlyList<LocalAccount> Accounts() => Discover();

    public static IReadOnlyList<LocalAuthSource> CreateDefaults(ISecretStore secrets) =>
        [new CodexCliSource(), new ClaudeCodeSource(secrets)];

    /// <summary>The payload of a JWT, unverified: only used to read display claims and expiry of our own tokens.</summary>
    protected static JsonElement? JwtPayload(string? jwt)
    {
        var parts = jwt?.Split('.');
        if (parts is not { Length: 3 })
        {
            return null;
        }
        try
        {
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload += new string('=', (4 - payload.Length % 4) % 4);
            using var doc = JsonDocument.Parse(Convert.FromBase64String(payload));
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is FormatException or JsonException)
        {
            return null;
        }
    }

    protected static string? Str(JsonElement? e, string name) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    protected static DateTimeOffset? Epoch(JsonElement? e, string name, bool milliseconds = false) =>
        e is { ValueKind: JsonValueKind.Object } o && o.TryGetProperty(name, out var v) && v.TryGetInt64(out var n)
            ? milliseconds ? DateTimeOffset.FromUnixTimeMilliseconds(n) : DateTimeOffset.FromUnixTimeSeconds(n)
            : null;

    protected static JsonElement? ReadJson(string path)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            return doc.RootElement.Clone();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

/// <summary>
/// The Codex CLI's ChatGPT login: <c>$CODEX_HOME/auth.json</c>, default <c>~/.codex/auth.json</c> (verified on macOS).
/// Plan and subscription end come from the id_token's claims, so showing them needs no network call.
/// </summary>
public sealed class CodexCliSource(string? codexHome = null) : LocalAuthSource
{
    private const string AuthClaims = "https://api.openai.com/auth";

    private readonly string _home = codexHome
        ?? Environment.GetEnvironmentVariable("CODEX_HOME")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex");

    public override string Id => "codex-cli";
    public override string Name => "Codex";
    public override IReadOnlyList<string> Hosts => ["chatgpt.com"];

    private string AuthPath => Path.Combine(_home, "auth.json");

    public override IReadOnlyList<LocalAccount> Discover(IReadOnlyList<string>? extraDirectories = null)
    {
        var auth = ReadJson(AuthPath);
        if (auth is not { } root || !root.TryGetProperty("tokens", out var tokens) || Str(tokens, "access_token") is null)
        {
            return []; // not signed in, or signed in with an API key only
        }
        var claims = JwtPayload(Str(tokens, "id_token"));
        JsonElement? openai = claims is { } c && c.TryGetProperty(AuthClaims, out var a) ? a : null;
        var email = Str(claims, "email");
        return
        [
            new LocalAccount(
                Id,
                "default",
                email ?? "Codex",
                email,
                Str(openai, "chatgpt_plan_type"),
                ParseDate(Str(openai, "chatgpt_subscription_active_until")),
                Str(tokens, "account_id") ?? Str(openai, "chatgpt_account_id")),
        ];
    }

    public override LocalCredential? ReadCredential(string accountKey)
    {
        if (accountKey != "default" || ReadJson(AuthPath) is not { } root || !root.TryGetProperty("tokens", out var tokens))
        {
            return null;
        }
        var token = Str(tokens, "access_token");
        return token is null ? null : new LocalCredential(token, Epoch(JwtPayload(token), "exp"));
    }

    private static DateTimeOffset? ParseDate(string? s) => DateTimeOffset.TryParse(s, out var d) ? d : null;
}

/// <summary>
/// Claude Code's login (verified on macOS, see docs/design/05-builtin-plugins.md §10.3):
/// account details in <c>&lt;config&gt;/.claude.json</c> → <c>oauthAccount</c> (for the default config directory the file
/// is <c>~/.claude.json</c>); the credential in the macOS Keychain under <c>Claude Code-credentials</c>, suffixed with
/// <c>-&lt;first 8 hex of SHA-256(config dir)&gt;</c> when <c>CLAUDE_CONFIG_DIR</c> is set, or in
/// <c>&lt;config&gt;/.credentials.json</c> on Windows and Linux.
/// </summary>
public sealed class ClaudeCodeSource : LocalAuthSource
{
    private readonly ISecretStore _keychain;
    private readonly bool _useKeychain;
    private readonly string _home;
    private readonly IReadOnlyList<string> _configDirs;

    /// <param name="configDirs">Directories to look in; default: <c>$CLAUDE_CONFIG_DIR</c> and <c>~/.claude</c>.</param>
    /// <param name="scan">Also look for configuration directories under the hidden folders of the home directory.</param>
    public ClaudeCodeSource(
        ISecretStore keychain,
        IReadOnlyList<string>? configDirs = null,
        bool? useKeychain = null,
        string? home = null,
        bool scan = true,
        Func<string, string?, string?>? planLookup = null)
    {
        _scan = scan;
        _planLookup = planLookup ?? ClaudeCli.Plan;
        _keychain = keychain;
        _useKeychain = useKeychain ?? OperatingSystem.IsMacOS();
        _home = home ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _configDirs = configDirs ??
            new[] { Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR"), DefaultDir }
                .Where(d => !string.IsNullOrEmpty(d)).Distinct().ToArray()!;
    }

    public override string Id => "claude-code";
    public override string Name => "Claude Code";
    public override IReadOnlyList<string> Hosts => ["api.anthropic.com"];

    private string DefaultDir => Path.Combine(_home, ".claude");

    /// <summary>Most extra configuration directories a plugin may point at.</summary>
    public const int MaxExtraDirectories = 8;

    // ---- automatic discovery ---------------------------------------------------------------------------------------
    // Account managers such as rec give every account its own CLAUDE_CONFIG_DIR (e.g. ~/.rec/claude/accounts/<id>/config),
    // which O_O, started on its own, does not inherit. So the hidden folders of the home directory are scanned for
    // Claude Code configuration directories (folders holding .claude.json). Only names are read while scanning.

    private const int ScanDepth = 5;
    private const int ScanMaxEntries = 20_000;
    private static readonly TimeSpan ScanBudget = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan ScanCacheFor = TimeSpan.FromMinutes(5);

    // Large or irrelevant trees that never hold a Claude Code configuration.
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ".Trash", ".npm", ".cache", ".cargo", ".rustup", ".gradle", ".m2", ".nuget", ".dotnet", ".pnpm-store", ".yarn",
        ".bun", ".deno", ".nvm", ".pyenv", ".rbenv", ".gem", ".local", ".vscode", ".cursor", ".docker", ".android",
        ".git", "node_modules", "Library", "Caches", "logs", "backups", "projects", "file-history", "shell-snapshots",
    };

    private readonly bool _scan;
    private IReadOnlyList<string> _scanned = [];
    private DateTime _scannedAt = DateTime.MinValue;

    private IReadOnlyList<string> Scanned()
    {
        if (!_scan)
        {
            return [];
        }
        lock (_extra)
        {
            if (DateTime.UtcNow - _scannedAt < ScanCacheFor)
            {
                return _scanned;
            }
        }
        var found = new List<string>();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var entries = 0;
        var queue = new Queue<(string Path, int Depth)>();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_home, ".*"))
            {
                if (!SkipNames.Contains(Path.GetFileName(dir)))
                {
                    queue.Enqueue((dir, 1));
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        var options = new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        while (queue.Count > 0 && entries < ScanMaxEntries && clock.Elapsed < ScanBudget)
        {
            var (dir, depth) = queue.Dequeue();
            entries++;
            if (File.Exists(Path.Combine(dir, ".claude.json")))
            {
                found.Add(dir);
                continue; // a configuration directory does not contain others worth finding
            }
            if (depth >= ScanDepth)
            {
                continue;
            }
            try
            {
                foreach (var child in Directory.EnumerateDirectories(dir, "*", options))
                {
                    if (!SkipNames.Contains(Path.GetFileName(child)))
                    {
                        queue.Enqueue((child, depth + 1));
                    }
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }
        lock (_extra)
        {
            _scanned = found;
            _scannedAt = DateTime.UtcNow;
        }
        return found;
    }

    private readonly HashSet<string> _extra = new(StringComparer.Ordinal);

    public override IReadOnlyList<LocalAccount> Discover(IReadOnlyList<string>? extraDirectories = null)
    {
        // Extra directories must be absolute and look like a Claude Code configuration; anything else is ignored.
        foreach (var dir in (extraDirectories ?? []).Take(MaxExtraDirectories))
        {
            var full = dir.Trim().TrimEnd(Path.DirectorySeparatorChar);
            if (Path.IsPathFullyQualified(full) && File.Exists(Path.Combine(full, ".claude.json")))
            {
                lock (_extra)
                {
                    _extra.Add(full);
                }
            }
        }
        var accounts = new List<LocalAccount>();
        foreach (var dir in Directories())
        {
            // An account file without a credential is a login that ended: not an account to offer.
            if (!HasCredential(dir))
            {
                continue;
            }
            var config = ReadJson(Path.Combine(dir, ".claude.json"))
                         ?? (dir == DefaultDir ? ReadJson(Path.Combine(_home, ".claude.json")) : null);
            if (config is not { } root || !root.TryGetProperty("oauthAccount", out var account))
            {
                continue;
            }
            var email = Str(account, "emailAddress");
            accounts.Add(new LocalAccount(
                Id,
                dir,
                Str(account, "displayName") is { Length: > 0 } name && email is not null ? $"{name} ({email})" : email ?? "Claude Code",
                email,
                // .claude.json only knows the billing channel; the plan comes from `claude auth status`.
                Plan: Plan(dir),
                SubscriptionUntil: null,
                Str(account, "accountUuid")));
        }
        return accounts;
    }

    private IReadOnlyList<string> Directories()
    {
        var scanned = Scanned();
        lock (_extra)
        {
            return [.. _configDirs.Concat(_extra).Concat(scanned).Distinct(StringComparer.Ordinal)];
        }
    }

    private bool HasCredential(string dir) => _useKeychain
        ? _keychain.Exists(KeychainService(dir), Environment.UserName)
        : File.Exists(Path.Combine(dir, ".credentials.json"));

    /// <summary>
    /// Never: O_O does not read Claude Code's token. Plan usage comes from Claude Code's documented status line instead
    /// (<see cref="ClaudeStatusLineBridge"/>), and the plan from <c>claude auth status</c>.
    /// </summary>
    public override LocalCredential? ReadCredential(string accountKey) => null;

    /// <summary>The plan of a configuration directory, from <c>claude auth status --json</c> (cached).</summary>
    public string? Plan(string configDir)
    {
        lock (_plans)
        {
            if (_plans.TryGetValue(configDir, out var cached) && DateTime.UtcNow - cached.At < PlanCacheFor)
            {
                return cached.Plan;
            }
        }
        var plan = _planLookup(configDir, CliCommand);
        lock (_plans)
        {
            _plans[configDir] = (plan, DateTime.UtcNow);
        }
        return plan;
    }

    /// <summary>
    /// The command that runs Claude Code, as the user set it in the console (e.g. a path, or a wrapper with
    /// arguments); null uses <see cref="ClaudeCli.DefaultCommand"/>. O_O appends <c>auth status --json</c>.
    /// </summary>
    public string? CliCommand
    {
        get;
        set
        {
            field = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
            lock (_plans)
            {
                _plans.Clear();
            }
        }
    }

    private static readonly TimeSpan PlanCacheFor = TimeSpan.FromMinutes(30);
    private readonly Func<string, string?, string?> _planLookup;
    private readonly Dictionary<string, (string? Plan, DateTime At)> _plans = new(StringComparer.Ordinal);

    /// <summary>The Keychain service name Claude Code uses for a config directory.</summary>
    public string KeychainService(string configDir)
    {
        if (configDir == DefaultDir)
        {
            return "Claude Code-credentials";
        }
        var hash = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configDir)));
        return $"Claude Code-credentials-{hash[..8]}";
    }
}

/// <summary>
/// Asks the Claude Code CLI itself (documented: <c>claude auth status --json</c>) for the plan of a configuration
/// directory. Claude Code reads its own login to answer, so O_O never touches it.
/// The command is what the user would type: a name such as <c>claude</c> (the default) or a full path, optionally
/// followed by arguments (a wrapper). Apps started from the Finder get a minimal PATH, so a bare name is also looked
/// for in the usual install locations and, failing that, through the user's login shell.
/// </summary>
public static class ClaudeCli
{
    /// <summary>What an empty command setting means.</summary>
    public const string DefaultCommand = "claude";

    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);
    private static readonly Dictionary<string, string?> Resolved = new(StringComparer.Ordinal);

    /// <param name="command">The user's command (program plus optional arguments), or null for <see cref="DefaultCommand"/>.</param>
    public static string? Plan(string configDir, string? command = null)
    {
        var parts = Split(command ?? DefaultCommand);
        if (parts.Count == 0 || Resolve(parts[0]) is not { } program)
        {
            return null;
        }
        parts[0] = program;
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(parts[0])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var arg in parts.Skip(1))
            {
                info.ArgumentList.Add(arg);
            }
            info.ArgumentList.Add("auth");
            info.ArgumentList.Add("status");
            info.ArgumentList.Add("--json");
            info.Environment["CLAUDE_CONFIG_DIR"] = configDir;
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(Timeout))
            {
                process.Kill();
                return null;
            }
            using var doc = JsonDocument.Parse(output.Result);
            var root = doc.RootElement;
            var tier = root.TryGetProperty("planTier", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            var type = root.TryGetProperty("subscriptionType", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
            return Describe(tier ?? type);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Splits a command line on spaces, keeping "double" or 'single' quoted parts together.</summary>
    public static List<string> Split(string command)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        char? quote = null;
        var any = false;
        foreach (var ch in command)
        {
            if (quote is not null)
            {
                if (ch == quote)
                {
                    quote = null;
                }
                else
                {
                    current.Append(ch);
                }
            }
            else if (ch is '"' or '\'')
            {
                quote = ch;
                any = true;
            }
            else if (char.IsWhiteSpace(ch))
            {
                if (current.Length > 0 || any)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                    any = false;
                }
            }
            else
            {
                current.Append(ch);
            }
        }
        if (current.Length > 0 || any)
        {
            parts.Add(current.ToString());
        }
        return parts;
    }

    /// <summary>"max_20x" → "Max 20x", "pro" → "Pro".</summary>
    public static string? Describe(string? tier)
    {
        if (string.IsNullOrEmpty(tier))
        {
            return null;
        }
        var parts = tier.Split('_', StringSplitOptions.RemoveEmptyEntries);
        return string.Join(' ', parts.Select((p, i) => i == 0 ? char.ToUpperInvariant(p[0]) + p[1..] : p));
    }

    /// <summary>
    /// The executable a program name or path stands for, or null when there is none. A path (absolute, relative or
    /// starting with ~) is used as given; a bare name is searched for.
    /// </summary>
    public static string? Resolve(string program)
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (program.StartsWith('~'))
        {
            program = home + program[1..];
        }
        if (program.Contains(Path.DirectorySeparatorChar) || program.Contains(Path.AltDirectorySeparatorChar))
        {
            var full = Path.GetFullPath(program);
            return File.Exists(full) ? full : null;
        }
        lock (Resolved)
        {
            if (Resolved.TryGetValue(program, out var cached))
            {
                return cached;
            }
        }
        // The same program the user's terminal runs for that name: this process's PATH first, then the login shell's
        // (an app started from the Finder has a minimal PATH), and only then the usual install locations.
        var found = Search(program, home, usual: false) ?? FromLoginShell(program) ?? Search(program, home, usual: true);
        lock (Resolved)
        {
            Resolved[program] = found;
        }
        return found;
    }

    private static string? Search(string name, string home, bool usual)
    {
        var names = OperatingSystem.IsWindows() && !Path.HasExtension(name)
            ? new[] { name + ".exe", name + ".cmd", name + ".bat" }
            : new[] { name };
        string[] locations =
        [
            Path.Combine(home, ".local", "bin"),
            Path.Combine(home, ".npm-global", "bin"),
            Path.Combine(home, ".claude", "local"),
            Path.Combine(home, ".bun", "bin"),
            Path.Combine(home, ".volta", "bin"),
            Path.Combine(home, ".yarn", "bin"),
            "/opt/homebrew/bin",
            "/usr/local/bin",
        ];
        var dirs = usual ? locations : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        foreach (var dir in dirs.Where(d => d.Length > 0))
        {
            foreach (var candidate in names)
            {
                var path = Path.Combine(dir, candidate);
                if (File.Exists(path))
                {
                    return path;
                }
            }
        }
        return null;
    }

    /// <summary>
    /// Where the user's shell finds <paramref name="name"/>, as an interactive login shell (<c>-ilc</c>): a terminal
    /// reads both the login profile and the interactive configuration (~/.zshrc), and either may set the PATH, so
    /// only this matches what typing the name in a terminal runs. The Finder's PATH knows neither.
    /// </summary>
    private static string? FromLoginShell(string name)
    {
        if (OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("SHELL") is not { Length: > 0 } shell ||
            !name.All(c => char.IsLetterOrDigit(c) || c is '-' or '_' or '.'))
        {
            return null;
        }
        try
        {
            var info = new System.Diagnostics.ProcessStartInfo(shell)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            info.ArgumentList.Add("-ilc");
            info.ArgumentList.Add($"command -v {name}");
            using var process = System.Diagnostics.Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEndAsync();
            if (!process.WaitForExit(TimeSpan.FromSeconds(5)))
            {
                process.Kill();
                return null;
            }
            // An interactive shell may print greetings or prompts; the answer is the last line that is a real file.
            return output.Result.Split('\n')
                .Select(line => line.Trim())
                .LastOrDefault(line => line.Length > 0 && Path.IsPathRooted(line) && File.Exists(line));
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
