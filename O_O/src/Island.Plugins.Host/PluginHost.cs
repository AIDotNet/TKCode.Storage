using System.Collections.Concurrent;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host;

/// <summary>
/// Finds plugins, runs each in its own <see cref="PluginRunner"/>, and reports what they show. Owns the user's
/// permission decisions and the plugins' settings, which the console edits.
/// Events are raised on plugin threads (or the caller's thread); subscribers marshal to their own thread.
/// </summary>
public sealed class PluginHost : IDisposable, IPluginRunnerSink
{
    private readonly PluginRunnerOptions _options;
    private readonly PluginServices _services;
    private readonly PermissionGrantStore _grants;
    private readonly ConcurrentDictionary<string, PluginRunner> _runners = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginState> _states = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PluginSettingsStore> _settings = new(StringComparer.Ordinal);
    private readonly List<PluginPackage> _packages = [];
    private readonly Lock _gate = new();
    private bool _started;

    public PluginHost(PluginServices services, PluginRunnerOptions? options = null)
    {
        _services = services;
        _options = options ?? new PluginRunnerOptions();
        _grants = new PermissionGrantStore(Path.Combine(services.DataDirectory, "grants.json"));
    }

    public event Action<ActivitySnapshot>? ActivityUpdated;
    public event Action<string, string>? ActivityEnded;
    public event Action<PageSnapshot>? PageUpdated;
    public event Action<string, string>? PageEnded;

    /// <summary>A chip changed; a snapshot without a view means it was removed.</summary>
    public event Action<ChipSnapshot>? ChipUpdated;
    public event Action<AlertSnapshot>? AlertRaised;

    /// <summary>A satellite notice appeared or was taken away (a snapshot without a view).</summary>
    public event Action<SatelliteSnapshot>? SatelliteUpdated;

    /// <summary>What plugins may see of other apps' notifications, edited in the console.</summary>
    public NotificationPreferences NotificationPreferences => _services.NotificationPrefs;

    /// <summary>The platform's notification reader (the console shows its access state); null where there is none.</summary>
    public Island.Platform.INotificationListener? NotificationListener => _services.NotificationListener;

    private readonly ConcurrentDictionary<string, byte[]?> _appIcons = new(StringComparer.Ordinal);

    /// <summary>The icon of an application as PNG bytes (cached), or null when the platform cannot give one.</summary>
    public byte[]? AppIcon(string appId) =>
        _services.NotificationListener is { } l
            ? _appIcons.GetOrAdd(appId, id => { try { return l.GetAppIcon(id, 64); } catch { return null; } })
            : null;

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, ChipSnapshot> _liveChips = new();

    /// <summary>The chips plugins show right now (for the console, which lists instances of multiple chips).</summary>
    public IReadOnlyList<ChipSnapshot> LiveChips => [.. _liveChips.Values];
    public event Action<string, PluginState, string?>? PluginStateChanged;
    public event Action<PluginLogEntry>? PluginLog;

    public IReadOnlyList<PluginPackage> Packages => _packages;

    /// <summary>The services plugins run with (the console uses the same ones, e.g. to connect Claude Code).</summary>
    public PluginServices Services => _services;

    /// <summary>
    /// Adds every sub-folder of <paramref name="directory"/> that holds a <c>plugin.json</c>. A plugin id seen before
    /// is skipped, so earlier directories (user plugins) take precedence over later ones (bundled plugins).
    /// </summary>
    /// <param name="bundled">The directory holds plugins shipped with O_O, granted what they declare.</param>
    public IReadOnlyList<string> Discover(string directory, bool bundled = false)
    {
        var problems = new List<string>();
        if (!Directory.Exists(directory))
        {
            return problems;
        }
        foreach (var dir in Directory.EnumerateDirectories(directory).Order(StringComparer.Ordinal))
        {
            var manifestPath = Path.Combine(dir, "plugin.json");
            if (!File.Exists(manifestPath))
            {
                continue;
            }
            try
            {
                var manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
                var manifestProblems = new List<string>();
                var permissions = PluginPermissions.Parse(manifest.Permissions, manifestProblems);
                var settings = SettingsSchema.Parse(manifest.Contributes.Settings, manifestProblems);
                ThemeContribution.Validate(manifest.Contributes.Themes, manifestProblems);
                LayoutContribution.Validate(manifest.Contributes.Layouts, manifestProblems);
                manifest.ValidateTargets(manifestProblems);
                foreach (var key in permissions?.HostSettings ?? [])
                {
                    // network:@key names the site the user types into that setting: it must be a plain text setting.
                    if (!settings.Any(s => s.Key == key && s.Type == SettingType.String && !s.IsSecret))
                    {
                        manifestProblems.Add($"network:@{key} needs a string setting '{key}' (the site's address)");
                    }
                }
                if (permissions is null || manifestProblems.Count > 0)
                {
                    problems.Add($"{manifest.Id}: {string.Join("; ", manifestProblems)}");
                    continue;
                }
                var package = new PluginPackage(manifest, dir, permissions, settings, bundled);
                if (!File.Exists(package.EntryPath))
                {
                    problems.Add($"{manifest.Id}: entry '{manifest.Main}' not found.");
                }
                else if (!manifest.RunsOn(PluginManifest.CurrentPlatform))
                {
                    // Made for another system (e.g. macOS only): not offered here at all. Not a problem with the plugin.
                }
                else if (_packages.All(p => p.Id != manifest.Id))
                {
                    _packages.Add(package);
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidDataException)
            {
                problems.Add($"{Path.GetFileName(dir)}: {e.Message}");
            }
        }
        return problems;
    }

    /// <summary>Starts every plugin the user has decided on; the others wait in <see cref="PluginState.AwaitingConsent"/>.</summary>
    public void StartAll()
    {
        lock (_gate)
        {
            _started = true;
            foreach (var package in _packages)
            {
                StartLocked(package);
            }
        }
    }

    public PluginState? GetState(string pluginId) => _states.TryGetValue(pluginId, out var s) ? s : null;

    // ---- signed-in accounts (oauth:) -------------------------------------------------------------------------------

    /// <summary>An account a plugin signed in with <c>oauth.signIn</c>.</summary>
    public sealed record OAuthAccount(string Handle, string Provider, string ProviderName, string? Label);

    /// <summary>The accounts <paramref name="pluginId"/> has signed in (for the console).</summary>
    public IReadOnlyList<OAuthAccount> GetOAuthAccounts(string pluginId)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(Sessions(pluginId).Accounts());
        return [.. doc.RootElement.EnumerateArray().Select(a => new OAuthAccount(
            a.GetProperty("handle").GetString()!, a.GetProperty("provider").GetString()!,
            a.TryGetProperty("providerName", out var n) ? n.GetString()! : a.GetProperty("provider").GetString()!,
            a.TryGetProperty("label", out var l) ? l.GetString() : null))];
    }

    /// <summary>Signs an account out: its tokens are deleted; the plugin sees it gone on its next look.</summary>
    public void SignOut(string pluginId, string handle)
    {
        var payload = System.Text.Json.JsonSerializer.Serialize(handle, HostJsonContext.Default.String);
        Sessions(pluginId).SignOut($"{{\"handle\":{payload}}}");
        RefreshProviders();
    }

    private Services.OAuthSessions Sessions(string pluginId) =>
        new(Package(pluginId).Id, _services.DataDirectory, _services.Secrets, _services.Http, _services.OAuthProfiles,
            _services.OpenBrowser, _services.OAuthCallbackPort);

    // ---- enabling, installing, removing (the console's 插件 page) ------------------------------------------------

    private Dictionary<string, bool>? _enabled;
    private string EnabledPath => Path.Combine(_services.DataDirectory, "enabled.json");

    /// <summary>
    /// Whether the plugin runs: the user's choice, else on — except bundled plugins marked optional, which wait in
    /// "添加插件" until the user adds them.
    /// </summary>
    public bool IsEnabled(string pluginId)
    {
        lock (_gate)
        {
            _enabled ??= LoadEnabled();
            return _enabled.TryGetValue(pluginId, out var on) ? on : !(Package(pluginId) is { Bundled: true, Manifest.Optional: true });
        }
    }

    /// <summary>Whether the user has turned this plugin on or off (rather than it being on or off by default).</summary>
    public bool HasChoice(string pluginId)
    {
        lock (_gate)
        {
            _enabled ??= LoadEnabled();
            return _enabled.ContainsKey(pluginId);
        }
    }

    /// <summary>
    /// Turns a plugin on (it starts, or asks for permission) or off (it stops; its data stays). Turning one on also turns
    /// on the installed plugins it <see cref="PluginManifest.Requires"/>: a usage provider shows nothing without the
    /// AI 用量 plugin, a calendar source nothing without 日历.
    /// </summary>
    public void SetEnabled(string pluginId, bool enabled)
    {
        var package = Package(pluginId);
        lock (_gate)
        {
            _enabled ??= LoadEnabled();
            _enabled[pluginId] = enabled;
            if (enabled)
            {
                EnableRequirementsLocked(package, []);
            }
            SaveEnabled();
            Restart(package);
        }
        RefreshProviders();
    }

    private void Restart(PluginPackage package)
    {
        if (_runners.TryRemove(package.Id, out var old))
        {
            old.Dispose();
        }
        if (_started)
        {
            StartLocked(package);
        }
    }

    /// <summary>Turns on (and starts) the installed requirements of <paramref name="package"/> that are off, depth first.</summary>
    private void EnableRequirementsLocked(PluginPackage package, HashSet<string> seen)
    {
        foreach (var id in package.Manifest.Requires)
        {
            if (!seen.Add(id) || _packages.FirstOrDefault(p => p.Id == id) is not { } required)
            {
                continue; // a cycle, or not installed (the catalog installs requirements first)
            }
            EnableRequirementsLocked(required, seen);
            if (!IsEnabled(id))
            {
                _enabled![id] = true;
                Restart(required);
            }
        }
    }

    /// <summary>
    /// Installs (or updates) a plugin from a <c>.zip</c> into <paramref name="userDirectory"/>. It is enabled and, as
    /// a user plugin, waits for the user's permission decision before it runs. A package may not replace a bundled
    /// plugin.
    /// </summary>
    /// <returns>The installed package.</returns>
    /// <exception cref="InvalidDataException">Not a valid plugin package; the message is for the user.</exception>
    public PluginPackage Install(string zipPath, string userDirectory)
    {
        var (unpacked, manifest) = PluginPackages.Unpack(zipPath);
        try
        {
            lock (_gate)
            {
                if (_packages.FirstOrDefault(p => p.Id == manifest.Id) is { Bundled: true })
                {
                    throw new InvalidDataException($"'{manifest.Id}' 是内置插件的 id，不能被替换。");
                }
                if (!manifest.RunsOn(PluginManifest.CurrentPlatform))
                {
                    throw new InvalidDataException($"这个插件只支持 {string.Join("、", manifest.Platforms)}，不能装在这台电脑上。");
                }
                if (_runners.TryRemove(manifest.Id, out var old))
                {
                    old.Dispose();
                }
                _packages.RemoveAll(p => p.Id == manifest.Id);
                var target = Path.Combine(userDirectory, manifest.Id);
                Directory.CreateDirectory(userDirectory);
                if (Directory.Exists(target))
                {
                    Directory.Delete(target, recursive: true);
                }
                MoveDirectory(unpacked, target);
                var problems = Discover(userDirectory);
                if (_packages.FirstOrDefault(p => p.Id == manifest.Id) is not { } package)
                {
                    throw new InvalidDataException(string.Join("；", problems.DefaultIfEmpty("安装后无法载入。")));
                }
                _enabled ??= LoadEnabled();
                _enabled[manifest.Id] = true;
                EnableRequirementsLocked(package, []);
                SaveEnabled();
                if (_started)
                {
                    StartLocked(package);
                }
                return package;
            }
        }
        finally
        {
            if (Directory.Exists(unpacked))
            {
                Directory.Delete(unpacked, recursive: true);
            }
        }
    }

    /// <summary>Removes a plugin the user installed: stops it, deletes its folder and its permission decisions.</summary>
    /// <param name="deleteData">Also delete what it stored (storage, settings); otherwise reinstalling keeps them.</param>
    public void Uninstall(string pluginId, bool deleteData = false)
    {
        var package = Package(pluginId);
        if (package.Bundled)
        {
            throw new InvalidOperationException("Bundled plugins can be turned off, not removed.");
        }
        lock (_gate)
        {
            if (_runners.TryRemove(pluginId, out var old))
            {
                old.Dispose();
            }
            _packages.Remove(package);
            _states.TryRemove(pluginId, out _);
            _enabled ??= LoadEnabled();
            _enabled.Remove(pluginId);
            SaveEnabled();
        }
        _grants.Remove(pluginId);
        Directory.Delete(package.Directory, recursive: true);
        if (deleteData && Directory.Exists(Path.Combine(_services.DataDirectory, pluginId)))
        {
            Directory.Delete(Path.Combine(_services.DataDirectory, pluginId), recursive: true);
        }
        PluginStateChanged?.Invoke(pluginId, PluginState.Stopped, "uninstalled");
        RefreshProviders();
    }

    private static void MoveDirectory(string from, string to)
    {
        try
        {
            Directory.Move(from, to);
        }
        catch (IOException)
        {
            // Across volumes (the temp folder may be elsewhere): copy, then the caller deletes the source.
            foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(to, Path.GetRelativePath(from, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest);
            }
        }
    }

    private Dictionary<string, bool> LoadEnabled()
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(EnabledPath));
            return doc.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False)
                .ToDictionary(p => p.Name, p => p.Value.GetBoolean(), StringComparer.Ordinal);
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            return new(StringComparer.Ordinal);
        }
    }

    private void SaveEnabled()
    {
        using var buffer = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            foreach (var (id, on) in _enabled!.OrderBy(e => e.Key, StringComparer.Ordinal))
            {
                w.WriteBoolean(id, on);
            }
            w.WriteEndObject();
        }
        Directory.CreateDirectory(_services.DataDirectory);
        File.WriteAllBytes(EnabledPath + ".tmp", buffer.ToArray());
        File.Move(EnabledPath + ".tmp", EnabledPath, overwrite: true);
    }

    /// <summary>
    /// Reading another application's login is never granted without asking, not even to bundled plugins: it is
    /// personal, and on macOS reading it shows the system's Keychain prompt.
    /// </summary>
    public static bool AlwaysAsks(string permission) =>
        permission.StartsWith("localAuth:", StringComparison.Ordinal) ||
        permission.StartsWith(PluginPermissions.ListenPrefix, StringComparison.Ordinal);

    public PermissionStatus GetPermissions(string pluginId)
    {
        var package = Package(pluginId);
        var declared = package.Permissions.All;
        var stored = _grants.Get(pluginId);
        if (package.Bundled)
        {
            // Bundled plugins get what they declare, except the permissions that always ask.
            var granted = new HashSet<string>(declared.Where(p => !AlwaysAsks(p)), StringComparer.Ordinal);
            if (stored is not null)
            {
                granted.UnionWith(stored.Where(AlwaysAsks));
            }
            return new PermissionStatus(declared, granted, Decided: stored is not null || !declared.Any(AlwaysAsks));
        }
        return new PermissionStatus(declared, stored ?? new HashSet<string>(), Decided: stored is not null);
    }

    /// <summary>Whether the user may change <paramref name="permission"/> for this plugin in the console.</summary>
    public bool IsChangeable(string pluginId, string permission) => !Package(pluginId).Bundled || AlwaysAsks(permission);

    /// <summary>
    /// Records the user's decision (any subset of the declared permissions, possibly none) and restarts the plugin
    /// with exactly that. Bundled plugins follow their manifest and cannot be changed here.
    /// </summary>
    public void SetGranted(string pluginId, IEnumerable<string> granted)
    {
        var package = Package(pluginId);
        // For bundled plugins only the always-ask permissions are the user's to decide; the rest follow the manifest.
        _grants.Set(pluginId, granted.Where(p => package.Permissions.Has(p) && (!package.Bundled || AlwaysAsks(p))));
        lock (_gate)
        {
            if (_runners.TryRemove(pluginId, out var old))
            {
                old.Dispose();
            }
            if (_started)
            {
                StartLocked(package);
            }
        }
    }

    /// <summary>Tells every plugin that provider data may have changed (e.g. a connection was set up in the console).</summary>
    public void RefreshProviders()
    {
        foreach (var runner in _runners.Values)
        {
            runner.NotifyProvidersChanged();
        }
    }

    /// <summary>The plugin's settings, for the console to read and change.</summary>
    public PluginSettingsStore GetSettings(string pluginId) => Settings(Package(pluginId));

    /// <summary>Delivers a UI event to the plugin that rendered handler <paramref name="handlerId"/>.</summary>
    /// <summary>
    /// Files dropped on the island (docs 05 §7): handed to every running plugin that holds files.shelf.
    /// </summary>
    /// <returns>Whether any plugin took them.</returns>
    public bool DropFiles(IReadOnlyList<string> paths)
    {
        var files = paths.Where(Path.IsPathFullyQualified).Take(Island.Plugins.Host.Services.PluginFiles.MaxPaths).ToList();
        if (files.Count == 0)
        {
            return false;
        }
        var taken = false;
        foreach (var runner in _runners.Values)
        {
            taken |= runner.DeliverDrop(files);
        }
        return taken;
    }

    /// <summary>Whether a running plugin would take dropped files (so the island can show it accepts them).</summary>
    public bool AcceptsFiles => _runners.Values.Any(r => r.AcceptsFiles);

    public void Dispatch(string pluginId, int handlerId, string? payloadJson = null)
    {
        if (_runners.TryGetValue(pluginId, out var runner))
        {
            runner.Dispatch(handlerId, payloadJson);
        }
    }

    private void StartLocked(PluginPackage package)
    {
        if (!IsEnabled(package.Id))
        {
            ((IPluginRunnerSink)this).StateChanged(package.Id, PluginState.Stopped, null);
            return;
        }
        var status = GetPermissions(package.Id);
        if (status.Pending.Any())
        {
            ((IPluginRunnerSink)this).StateChanged(package.Id, PluginState.AwaitingConsent,
                $"needs permission: {string.Join(", ", status.Pending)}");
            return;
        }
        if (package.HasNoCode)
        {
            // A theme or layout pack: everything is in plugin.json. An engine, thread and timers would run nothing.
            ((IPluginRunnerSink)this).StateChanged(package.Id, PluginState.Running, null);
            return;
        }
        var runner = new PluginRunner(
            package, this, _options, _services, package.Permissions.Restrict(status.Granted), Settings(package));
        if (_runners.TryAdd(package.Id, runner))
        {
            runner.Start();
        }
    }

    private PluginSettingsStore Settings(PluginPackage package) => _settings.GetOrAdd(package.Id, _ =>
        new PluginSettingsStore(
            package.Id, package.Settings,
            Path.Combine(_services.DataDirectory, package.Id, "settings.json"), _services.Secrets));

    private PluginPackage Package(string pluginId) =>
        _packages.FirstOrDefault(p => p.Id == pluginId) ?? throw new ArgumentException($"Unknown plugin '{pluginId}'.");

    void IPluginRunnerSink.ActivityUpdated(ActivitySnapshot snapshot) => ActivityUpdated?.Invoke(snapshot);

    /// <summary>How long a provider may take to answer one call.</summary>
    public static readonly TimeSpan ProviderTimeout = TimeSpan.FromSeconds(60);

    Task<string> IPluginRunnerSink.CallProvider(string callerId, string iface, string id, string method, string argsJson, CancellationToken ct)
    {
        var provider = _packages.FirstOrDefault(p => p.Manifest.Contributes.Providers.Any(c => c.Interface == iface && c.Id == id))
            ?? throw new PluginCapabilityException($"No plugin provides {iface}/{id}.");
        if (!_runners.TryGetValue(provider.Id, out var runner) || runner.State != PluginState.Running)
        {
            throw new PluginCapabilityException($"{provider.Manifest.Name} is not running ({GetState(provider.Id)?.ToString() ?? "not started"}).");
        }
        return runner.ServeProvider(iface, id, method, argsJson, ProviderTimeout);
    }

    string IPluginRunnerSink.ListProviders(string iface)
    {
        using var buffer = new MemoryStream();
        using (var w = new System.Text.Json.Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var package in _packages)
            {
                foreach (var c in package.Manifest.Contributes.Providers.Where(c => c.Interface == iface))
                {
                    w.WriteStartObject();
                    w.WriteString("id", c.Id);
                    w.WriteString("title", c.Title ?? c.Id);
                    w.WriteString("plugin", package.Id);
                    w.WriteBoolean("running", GetState(package.Id) == PluginState.Running);
                    w.WriteEndObject();
                }
            }
            w.WriteEndArray();
        }
        return System.Text.Encoding.UTF8.GetString(buffer.ToArray());
    }

    void IPluginRunnerSink.ActivityEnded(string pluginId, string activityId) => ActivityEnded?.Invoke(pluginId, activityId);

    void IPluginRunnerSink.PageUpdated(PageSnapshot snapshot)
    {
        _livePages[snapshot.Key] = snapshot;
        PageUpdated?.Invoke(snapshot);
    }

    void IPluginRunnerSink.PageEnded(string pluginId, string pageId)
    {
        _livePages.TryRemove(PageSnapshot.PageKey(pluginId, pageId), out _);
        PageEnded?.Invoke(pluginId, pageId);
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, PageSnapshot> _livePages = new();

    /// <summary>The latest view of every page plugins show now (for views that start listening late).</summary>
    public IReadOnlyList<PageSnapshot> LivePages => [.. _livePages.Values];

    void IPluginRunnerSink.ChipUpdated(ChipSnapshot snapshot)
    {
        if (snapshot.View is null)
        {
            _liveChips.TryRemove(snapshot.Key, out _);
        }
        else
        {
            _liveChips[snapshot.Key] = snapshot;
        }
        ChipUpdated?.Invoke(snapshot);
    }

    void IPluginRunnerSink.Alert(AlertSnapshot alert) => AlertRaised?.Invoke(alert);

    void IPluginRunnerSink.Satellite(SatelliteSnapshot snapshot) => SatelliteUpdated?.Invoke(snapshot);

    void IPluginRunnerSink.StateChanged(string pluginId, PluginState state, string? reason)
    {
        _states[pluginId] = state;
        PluginStateChanged?.Invoke(pluginId, state, reason);
        // Consumers of a provider learn when it becomes available or goes away, instead of polling for it.
        if (_packages.FirstOrDefault(p => p.Id == pluginId) is { } package && package.Manifest.Contributes.Providers.Count > 0)
        {
            foreach (var runner in _runners.Values.Where(r => r.Id != pluginId))
            {
                runner.NotifyProvidersChanged();
            }
        }
    }

    void IPluginRunnerSink.Log(PluginLogEntry entry) => PluginLog?.Invoke(entry);

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var runner in _runners.Values)
            {
                runner.Dispose();
            }
            _runners.Clear();
        }
    }
}
