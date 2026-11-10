namespace Island.Plugins.Host;

/// <summary>A plugin found on disk with a valid manifest, permissions and settings schema.</summary>
/// <param name="Permissions">What the manifest declares; the user's grants may restrict it.</param>
/// <param name="Bundled">Shipped with O_O (first party): its declared permissions are granted without asking.</param>
public sealed record PluginPackage(
    PluginManifest Manifest,
    string Directory,
    PluginPermissions Permissions,
    IReadOnlyList<SettingDefinition> Settings,
    bool Bundled)
{
    public string Id => Manifest.Id;
    public string EntryPath => Path.GetFullPath(Path.Combine(Directory, Manifest.Main));
}

public enum PluginState
{
    Loaded,

    /// <summary>Asks for permissions the user has not decided on yet; not started until they do.</summary>
    AwaitingConsent,
    Running,

    /// <summary>The engine faulted (budget exceeded, internal error); a restart is scheduled.</summary>
    Restarting,

    /// <summary>Faulted too often; stays off until the user re-enables it.</summary>
    Disabled,
    Stopped,
}

public enum PluginLogLevel
{
    Debug,
    Info,
    Warn,
    Error,
}

/// <summary>What a plugin asks for and what the user granted.</summary>
public sealed record PermissionStatus(IReadOnlyCollection<string> Declared, IReadOnlySet<string> Granted, bool Decided)
{
    public IEnumerable<string> Pending => Decided ? [] : Declared.Where(p => !Granted.Contains(p));
}

public sealed record PluginLogEntry(string PluginId, PluginLogLevel Level, string Message, DateTimeOffset Time);

/// <summary>Budgets and restart policy (docs/design/03-plugin-system.md §5.3).</summary>
public sealed record PluginRunnerOptions
{
    public TimeSpan ActivateTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Every later callback (timer, UI event) must return within this.</summary>
    public TimeSpan CallbackTimeout { get; init; } = TimeSpan.FromMilliseconds(50);

    public int MaxStackDepth { get; init; } = 256;
    public long MaxTrackedBytes { get; init; } = 64L * 1024 * 1024;
    public long MaxOutputBytes { get; init; } = 1024 * 1024;

    /// <summary>Delays before restarting a faulted plugin; one more fault after the last one disables it.</summary>
    public IReadOnlyList<TimeSpan> RestartBackoff { get; init; } =
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)];

    /// <summary>Running this long without a fault resets the backoff.</summary>
    public TimeSpan HealthyAfter { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>Shortest timer interval a plugin may ask for.</summary>
    public TimeSpan MinTimerInterval { get; init; } = TimeSpan.FromMilliseconds(16);

    public int MaxTimers { get; init; } = 256;
    public int MaxActivities { get; init; } = 2;
}

/// <summary>What the host gives plugins beyond the engine: where their data lives and the OS services they may use.</summary>
public sealed record PluginServices(string DataDirectory, Island.Platform.ISecretStore Secrets)
{
    private static readonly Lazy<HttpClient> SharedHttp = new(Services.PluginHttp.CreateClient);

    public HttpClient Http { get; init; } = SharedHttp.Value;
    public string UserAgent { get; init; } = "O_O/0.1";
    public long StorageQuotaBytes { get; init; } = 5L * 1024 * 1024;

    /// <summary>Local logins plugins may reuse with a localAuth permission; default: Codex CLI and Claude Code.</summary>
    public IReadOnlyList<Services.LocalAuthSource> LocalAuthSources
    {
        get
        {
            if (field is null)
            {
                field = Services.LocalAuthSource.CreateDefaults(Secrets);
                // The user's Claude Code command from the console applies from the start.
                foreach (var claude in field.OfType<Services.ClaudeCodeSource>())
                {
                    claude.CliCommand = Connections.ClaudeCommand;
                }
            }
            return field;
        }
        init;
    }

    /// <summary>The system clipboard for the clipboard.* permissions; null where the app offers none (tests, headless).</summary>
    public Services.IPluginClipboard? Clipboard { get; init; }

    /// <summary>What is playing and transport commands for the media permission; null where the app offers none.</summary>
    public Services.IPluginMedia? Media { get; init; }

    /// <summary>Sign-in providers for oauth:&lt;id&gt; (tests point them at a local server).</summary>
    public IReadOnlyDictionary<string, Services.OAuthProfile> OAuthProfiles { get; init; } = Services.OAuthProfile.Defaults;

    /// <summary>Opens the sign-in page; the system browser by default.</summary>
    public Func<Uri, Task> OpenBrowser { get; init; } = Services.OAuthSessions.OpenSystemBrowser;

    /// <summary>
    /// Tests only: opens the stream to an IMAP server (e.g. a plain one on this machine). Null in the app, where mail
    /// always goes over implicit TLS with the system's certificate validation.
    /// </summary>
    public Func<string, int, CancellationToken, Task<Stream>>? ImapConnect { get; init; }

    /// <summary>
    /// The loopback port the browser returns to after signing in (<c>http://127.0.0.1:&lt;port&gt;/oauth/callback</c>). Fixed,
    /// because some providers (Feishu) require the exact redirect address to be registered with the app.
    /// </summary>
    public int OAuthCallbackPort { get; init; } = 47821;

    /// <summary>System notifications for the notifications permission (tests capture them instead).</summary>
    public Services.IPluginNotifier Notifier { get; init; } = new Services.SystemNotifier();

    /// <summary>Where plugin packages the user installs go (the first plugin directory); null: no installing.</summary>
    public string? UserPluginDirectory { get; init; }

    /// <summary>Connection settings the user edits in the console.</summary>
    public Services.ConnectionPreferences Connections => field ??= new Services.ConnectionPreferences(DataDirectory);

    /// <summary>Claude Code's status line bridge: plan usage from Claude Code's documented status line data.</summary>
    public Services.ClaudeStatusLineBridge ClaudeStatusLine
    {
        get => field ??= new Services.ClaudeStatusLineBridge(DataDirectory);
        init;
    }

    /// <summary>Most capability calls (HTTP requests, keychain access) one plugin may have in flight.</summary>
    public int MaxPendingCalls { get; init; } = 16;
}
