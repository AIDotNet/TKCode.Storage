using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Island.Plugins.Host.Services;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>Where a runner reports to. Called on the runner's thread; implementations marshal as they need.</summary>
internal interface IPluginRunnerSink
{
    void ActivityUpdated(ActivitySnapshot snapshot);

    /// <summary>Runs a provider method in the providing plugin; returns its JSON result.</summary>
    Task<string> CallProvider(string callerId, string iface, string id, string method, string argsJson, CancellationToken ct);

    /// <summary>The providers of <paramref name="iface"/> as a JSON array.</summary>
    string ListProviders(string iface);
    void ActivityEnded(string pluginId, string activityId);
    void PageUpdated(PageSnapshot snapshot);
    void PageEnded(string pluginId, string pageId);
    void ChipUpdated(ChipSnapshot snapshot);
    void Alert(AlertSnapshot alert);
    void StateChanged(string pluginId, PluginState state, string? reason);
    void Log(PluginLogEntry entry);
}

/// <summary>
/// One plugin's runtime (docs/design/03-plugin-system.md §5): a long-lived Jaunt engine on a dedicated thread with its
/// own work queue. Jaunt runs one execution at a time per engine, so everything that touches the engine, including
/// timers and UI events, goes through the queue. Every callback runs under a budget; a fault discards the engine and
/// restarts the plugin with backoff, and too many faults disable it.
/// </summary>
internal sealed partial class PluginRunner : IDisposable
{
    private readonly PluginPackage _package;
    private readonly IPluginRunnerSink _sink;
    private readonly PluginRunnerOptions _options;
    private readonly PluginServices _services;
    private readonly Services.SystemStats Stats = new();
    private readonly PluginPermissions _permissions;
    private readonly PluginSettingsStore _settings;
    private readonly PluginStorage _storage;
    private readonly PluginHttp _http;
    private readonly OAuthSessions _oauth;
    private readonly PluginNotifications _notifications;
    private long _lastGestureTicks = long.MinValue / 2;

    /// <summary>How long after a click on its view a plugin may still open a link (async work in between).</summary>
    public static readonly TimeSpan GestureWindow = TimeSpan.FromSeconds(3);
    private readonly Timer _flush;
    private int _pendingCalls;

    // Provider calls this plugin is serving: call id → completion. Completed by __island_reply on the runner thread.
    private readonly ConcurrentDictionary<int, TaskCompletionSource<string>> _serving = new();
    private int _nextServing;

    // localAuth handles given to this plugin: opaque id → (source, account key). Read from capability tasks.
    private readonly ConcurrentDictionary<string, (LocalAuthSource Source, string Key)> _authHandles = new(StringComparer.Ordinal);
    private readonly BlockingCollection<Action> _queue = new();
    private readonly CancellationTokenSource _stop = new();
    private readonly Thread _thread;

    // Owned by the runner thread.
    private JsEngine? _engine;
    private JsFunction? _dispatch;
    private readonly Dictionary<int, Timer> _timers = [];
    private readonly HashSet<string> _activities = [];
    private readonly HashSet<string> _pages = [];
    private readonly HashSet<(string Chip, string? Instance)> _chips = [];
    private int _generation;
    private int _faults;
    private long _sequence;
    private readonly Stopwatch _sinceStart = new();

    public PluginRunner(
        PluginPackage package,
        IPluginRunnerSink sink,
        PluginRunnerOptions options,
        PluginServices services,
        PluginPermissions permissions,
        PluginSettingsStore settings)
    {
        _package = package;
        _permissions = permissions;
        _settings = settings;
        _settings.Changed += OnSettingChanged;
        if (services.Clipboard is { } clipboard)
        {
            clipboard.Changed += OnClipboardChanged;
        }
        if (services.Media is { } media)
        {
            media.Changed += OnMediaChanged;
        }
        _sink = sink;
        _options = options;
        _services = services;
        // Storage belongs to the plugin, not to one engine: it survives restarts.
        _storage = new PluginStorage(Path.Combine(services.DataDirectory, package.Id, "storage.json"), services.StorageQuotaBytes);
        // The user's grants (not just the manifest) decide the hosts; network:@setting sites come from what the user
        // typed into those settings, read on every request so a changed address applies at once.
        _notifications = new PluginNotifications(package.Manifest.Name, services.Notifier);
        _oauth = new OAuthSessions(package.Id, services.DataDirectory, services.Secrets, services.Http,
            services.OAuthProfiles, services.OpenBrowser, services.OAuthCallbackPort);
        _http = new PluginHttp(services.Http, permissions, $"{services.UserAgent} (plugin {package.Id})",
            permissions.HostSettings.Count == 0 ? null : () => permissions.HostSettings.Select(SettingText));
        _flush = new Timer(_ => FlushStorage(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        _thread = new Thread(Loop) { IsBackground = true, Name = $"plugin:{package.Id}" };
    }

    public string Id => _package.Id;

    public PluginState State { get; private set; } = PluginState.Loaded;

    public void Start()
    {
        _thread.Start();
        Post(StartEngine);
    }

    /// <summary>A UI event for handler <paramref name="handlerId"/>; <paramref name="payloadJson"/> may be null.</summary>
    public void Dispatch(int handlerId, string? payloadJson)
    {
        var generation = Volatile.Read(ref _generation);
        Post(() =>
        {
            if (generation == _generation)
            {
                // A UI event is a user gesture: for a moment the plugin may do what needs one (open a link). The moment
                // counts from when the plugin gets the event, not when it was queued, so a busy plugin keeps it.
                Volatile.Write(ref _lastGestureTicks, Environment.TickCount64);
                Call("event", handlerId, payloadJson);
            }
        });
    }

    private void Post(Action work)
    {
        if (!_queue.IsAddingCompleted)
        {
            try
            {
                _queue.Add(work);
            }
            catch (InvalidOperationException)
            {
                // Completed between the check and the add: the runner is stopping.
            }
        }
    }

    private void Loop()
    {
        try
        {
            foreach (var work in _queue.GetConsumingEnumerable(_stop.Token))
            {
                work();
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            TearDown();
        }
    }

    // ---- lifecycle -----------------------------------------------------------------------------------------------

    private void StartEngine()
    {
        var generation = ++_generation;
        Island.Core.StartupTrace.Mark($"{Id}: engine start");
        try
        {
            var engine = new JsEngine(new JsEngineOptions
            {
                Profile = JsProfile.Hardened,
                TimeZone = TimeZoneInfo.Local,
                Limits = new JsLimits
                {
                    MaxStackDepth = _options.MaxStackDepth,
                    MaxTrackedBytes = _options.MaxTrackedBytes,
                    MaxOutputBytes = _options.MaxOutputBytes,
                },
            });
            _engine = engine;
            RegisterNatives(engine, generation);
            engine.SetValue("__island_permissions", PermissionsJson());

            // The prelude takes the natives into a closure and removes them from globalThis; the host then keeps its
            // own reference to the dispatcher and removes that global too. Plugin code never sees the natives.
            using (var x = engine.BeginExecution(Budget(_options.ActivateTimeout), _stop.Token))
            {
                x.Evaluate(EmbeddedRuntime.Read("prelude.js"));
                x.Complete();
            }
            _dispatch = engine.GetFunction("__island_dispatch");
            engine.Execute("delete globalThis.__island_dispatch;", _stop.Token);

            // @island/api is loaded before any plugin code, so it receives the one-shot handoff of the natives before
            // a plugin module could reach for it. The plugin's own import resolves to this same instance.
            var resolver = new PluginModuleResolver(_package.Directory);
            engine.LoadModule(PluginModuleResolver.ApiModule, resolver, PluginModuleResolver.ApiSpecifier, _stop.Token);
            Island.Core.StartupTrace.Mark($"{Id}: prelude done");

            var source = File.ReadAllText(_package.EntryPath);
            var module = JsCompiler.CompileModule(source, new JsCompileOptions { SourceName = _package.EntryPath }, _stop.Token);
            var instance = engine.LoadModule(module, resolver, _package.EntryPath, _stop.Token);

            Island.Core.StartupTrace.Mark($"{Id}: module loaded");
            if (instance.ExportNames.Contains("activate"))
            {
                using var x = engine.BeginExecution(Budget(_options.ActivateTimeout), _stop.Token);
                x.Invoke(instance.GetFunction("activate"), []);
                x.Complete();
            }
            else
            {
                Log(PluginLogLevel.Warn, "The module exports no activate() function.");
            }

            _sinceStart.Restart();
            Island.Core.StartupTrace.Mark($"{Id}: activated");
            SetState(PluginState.Running, null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            Fault(e);
        }
    }

    private static JsExecutionOptions Budget(TimeSpan timeout) => new() { Timeout = timeout };

    /// <summary>Runs a JS callback through <c>__island_dispatch</c> under the callback budget.</summary>
    private void Call(string kind, int id, string? payload)
    {
        if (_engine is null || _dispatch is null || State != PluginState.Running)
        {
            return;
        }
        try
        {
            using var x = _engine.BeginExecution(Budget(_options.CallbackTimeout), _stop.Token);
            x.Invoke(_dispatch, [JsValue.From(kind), JsValue.From(id), payload is null ? JsValue.Undefined : JsValue.From(payload)]);
            x.Complete();
            if (_faults > 0 && _sinceStart.Elapsed > _options.HealthyAfter)
            {
                _faults = 0;
            }
        }
        catch (JsException e)
        {
            // A JS throw: the engine stays usable (Jaunt), so the plugin keeps running.
            Log(PluginLogLevel.Error, $"{kind} handler threw {e.Name}: {e.ErrorMessage}\n{e.JsStackTrace}");
        }
        catch (Exception e) when (e is not OperationCanceledException || !_stop.IsCancellationRequested)
        {
            Fault(e);
        }
    }

    private void Fault(Exception e)
    {
        var reason = e switch
        {
            JsLimitExceededException => $"exceeded its budget: {e.Message}",
            JsCompileException => $"failed to compile: {e.Message}",
            JsException js => $"threw during start-up {js.Name}: {js.ErrorMessage}",
            _ => $"{e.GetType().Name}: {e.Message}",
        };
        Log(PluginLogLevel.Error, $"Plugin {reason}");
        DisposeEngine();

        if (_faults >= _options.RestartBackoff.Count)
        {
            SetState(PluginState.Disabled, reason);
            return;
        }
        var delay = _options.RestartBackoff[_faults++];
        SetState(PluginState.Restarting, reason);
        var generation = _generation;
        _ = Task.Delay(delay, _stop.Token).ContinueWith(
            _ => Post(() =>
            {
                if (generation == _generation && State == PluginState.Restarting)
                {
                    StartEngine();
                }
            }),
            TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    private void DisposeEngine()
    {
        _authHandles.Clear();
        foreach (var callId in _serving.Keys)
        {
            Fail(callId, $"{Id} stopped while answering.");
        }
        Interlocked.Increment(ref _generation);
        foreach (var timer in _timers.Values)
        {
            timer.Dispose();
        }
        _timers.Clear();
        foreach (var activity in _activities)
        {
            _sink.ActivityEnded(Id, activity);
        }
        _activities.Clear();
        foreach (var page in _pages)
        {
            _sink.PageEnded(Id, page);
        }
        _pages.Clear();
        foreach (var (chip, instance) in _chips)
        {
            _sink.ChipUpdated(new ChipSnapshot(Id, chip, instance, "", null, ++_sequence));
        }
        _chips.Clear();
        _dispatch = null;
        _engine?.Dispose();
        _engine = null;
    }

    private void TearDown()
    {
        _settings.Changed -= OnSettingChanged;
        if (_services.Clipboard is { } clipboard)
        {
            clipboard.Changed -= OnClipboardChanged;
        }
        if (_services.Media is { } media)
        {
            media.Changed -= OnMediaChanged;
        }
        DisposeEngine();
        _flush.Dispose();
        FlushStorage();
        SetState(PluginState.Stopped, null);
    }

    private void SetState(PluginState state, string? reason)
    {
        // Repeating a state without a reason is noise; a repeated Restarting carries a new fault and is reported.
        if (State == state && reason is null)
        {
            return;
        }
        State = state;
        _sink.StateChanged(Id, state, reason);
    }

    private void Log(PluginLogLevel level, string message) =>
        _sink.Log(new PluginLogEntry(Id, level, message, DateTimeOffset.Now));

    /// <summary>A provider plugin started, stopped or changed state: let this plugin's listeners look again.</summary>
    public void NotifyProvidersChanged()
    {
        var generation = Volatile.Read(ref _generation);
        Post(() =>
        {
            if (generation == _generation)
            {
                Call("providers", 0, null);
            }
        });
    }

    /// <summary>A setting changed in the console: tell the plugin (its onChange listeners) which key.</summary>
    private void OnSettingChanged(string key)
    {
        var generation = Volatile.Read(ref _generation);
        var payload = JsonSerializer.Serialize(key, HostJsonContext.Default.String);
        Post(() =>
        {
            if (generation == _generation)
            {
                Call("settings", 0, payload);
            }
        });
    }

    public bool AcceptsFiles => _permissions.Has("files.shelf") && State == PluginState.Running;

    /// <summary>The user dropped files on the island: plugins holding files.shelf get the paths (files.onDrop).</summary>
    public bool DeliverDrop(IReadOnlyList<string> paths)
    {
        if (!_permissions.Has("files.shelf") || State != PluginState.Running)
        {
            return false;
        }
        var generation = Volatile.Read(ref _generation);
        var payload = PluginFiles.DropJson(paths);
        Post(() =>
        {
            if (generation == _generation)
            {
                Call("drop", 0, payload);
            }
        });
        return true;
    }

    /// <summary>The clipboard changed: plugins holding clipboard.watch get the entry (their clipboard.onChange listeners).</summary>
    private void OnClipboardChanged(Services.ClipboardEntry entry)
    {
        if (!_permissions.Has("clipboard.watch"))
        {
            return;
        }
        var generation = Volatile.Read(ref _generation);
        var payload = entry.ToJson();
        Post(() =>
        {
            if (generation == _generation)
            {
                Call("clipboard", 0, payload);
            }
        });
    }

    private string PermissionsJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var p in _permissions.All.Order(StringComparer.Ordinal))
            {
                w.WriteStringValue(p);
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void FlushStorage()
    {
        try
        {
            _storage.Flush();
        }
        catch (IOException e)
        {
            Log(PluginLogLevel.Error, $"Could not save storage: {e.Message}");
        }
    }

    // ---- capability calls --------------------------------------------------------------------------------------

    /// <summary>
    /// Starts an asynchronous capability call. The work runs off the plugin thread; its result is delivered through
    /// the queue as a <c>settle</c> dispatch that resolves or rejects the promise on the JS side.
    /// </summary>
    private void StartCall(int callId, string op, string payload, int generation, ref JsCallContext c)
    {
        if (_pendingCalls >= _services.MaxPendingCalls)
        {
            throw c.TypeError($"Too many calls in flight (limit {_services.MaxPendingCalls}).");
        }
        Func<CancellationToken, Task<string>> work = op switch
        {
            "http.fetch" => _permissions.HasNetwork
                ? ct => _http.FetchAsync(payload, ct,
                    _permissions.All.Any(p => p.StartsWith("localAuth:", StringComparison.Ordinal) || p.StartsWith("oauth:", StringComparison.Ordinal))
                        ? CredentialFor
                        : null)
                : throw c.TypeError("This plugin has no network: permission."),
            "settings.secret" => _ => Task.Run(() => SettingSecret(payload)),
            "localAuth.accounts" => _ => Task.Run(() => LocalAccounts(payload)),
            "localAuth.statusLine" => _ => Task.Run(() => StatusLine(payload)),
            "providers.list" => _ => Task.Run(() => _sink.ListProviders(ProviderInterface(payload))),
            "providers.call" => ct => CallProvider(payload, ct),
            "clipboard.read" => Clipboard("clipboard.read", ref c) is { } read
                ? async ct => await read.ReadAsync(ct) is { } entry ? entry.ToJson() : "null"
                : throw new UnreachableException(),
            "clipboard.write" => Clipboard("clipboard.write", ref c) is { } write
                ? async ct =>
                {
                    await write.WriteAsync(Services.ClipboardEntry.Parse(payload, write.ImageFolder), ct);
                    return "true";
                }
                : throw new UnreachableException(),
            "clipboard.paste" => Clipboard("clipboard.paste", ref c) is { } paste
                ? async ct =>
                {
                    var (pasted, reason) = await paste.PasteAsync(Services.ClipboardEntry.Parse(payload, paste.ImageFolder), ct);
                    return PasteJson(pasted, reason);
                }
                : throw new UnreachableException(),
            "media.nowPlaying" or "media.command" => MediaCall(op, payload, ref c),
            "system.stats" => _permissions.Has("system.info")
                ? ct => Task.FromResult(Stats.Sample())
                : throw c.TypeError("This plugin has no system.info permission."),
            "mail.check" => MailCall(payload, ref c),
            "files.info" or "files.reveal" or "files.open" => _permissions.Has("files.shelf")
                ? _ => Task.Run(() => op switch
                {
                    "files.info" => PluginFiles.Info(payload),
                    "files.reveal" => PluginFiles.Reveal(payload),
                    _ => PluginFiles.Open(payload),
                })
                : throw c.TypeError("This plugin has no files.shelf permission."),
            "oauth.signIn" => HasOAuth
                ? ct => _oauth.SignInAsync(payload, provider => _permissions.Has("oauth:" + provider), ct)
                : throw c.TypeError("This plugin has no oauth: permission."),
            "oauth.accounts" or "oauth.signOut" or "oauth.setLabel" => HasOAuth
                ? _ => Task.Run(() => op switch
                {
                    "oauth.accounts" => _oauth.Accounts(),
                    "oauth.signOut" => _oauth.SignOut(payload),
                    _ => _oauth.SetLabel(payload),
                })
                : throw c.TypeError("This plugin has no oauth: permission."),
            "notify.show" => _permissions.Has("notifications")
                ? ct => _notifications.ShowAsync(payload, ct)
                : throw c.TypeError("This plugin has no notifications permission."),
            "shell.openUrl" => _permissions.Has("shell.openUrl")
                ? Environment.TickCount64 - Volatile.Read(ref _lastGestureTicks) <= (long)GestureWindow.TotalMilliseconds
                    ? _ => PluginShell.OpenUrl(payload, _services.OpenBrowser)
                    : throw c.TypeError("shell.openUrl only works right after the user clicks something in this plugin.")
                : throw c.TypeError("This plugin has no shell.openUrl permission."),
            "secrets.get" or "secrets.set" or "secrets.delete" => _permissions.Has("secrets")
                ? _ => Task.Run(() => Secret(op, payload))
                : throw c.TypeError("This plugin has no secrets permission."),
            _ => throw c.TypeError($"Unknown capability '{op}'."),
        };
        _pendingCalls++;
        _ = Run();

        async Task Run()
        {
            string settlement;
            try
            {
                var value = await work(_stop.Token);
                settlement = Settlement(ok: true, value);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e) when (e is PluginCapabilityException or Island.Platform.SecretStoreException or JsonException)
            {
                settlement = Settlement(ok: false, e.Message);
            }
            catch (Exception e)
            {
                Log(PluginLogLevel.Error, $"{op} failed: {e}");
                settlement = Settlement(ok: false, $"{op} failed.");
            }
            Post(() =>
            {
                _pendingCalls--;
                if (generation == _generation)
                {
                    Call("settle", callId, settlement);
                }
            });
        }
    }

    /// <summary>The clipboard, if this plugin holds <paramref name="permission"/> and the app offers one.</summary>
    private Services.IPluginClipboard Clipboard(string permission, ref JsCallContext c)
    {
        if (!_permissions.Has(permission))
        {
            throw c.TypeError($"This plugin has no {permission} permission.");
        }
        return _services.Clipboard ?? throw c.TypeError("The clipboard is not available here.");
    }

    private static string PasteJson(bool pasted, string? reason)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("pasted", pasted);
            if (reason is not null)
            {
                w.WriteString("reason", reason);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>{"ok":true,"value":&lt;json&gt;} or {"ok":false,"error":"…"}; a successful value is already JSON.</summary>
    private static string Settlement(bool ok, string valueOrError)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            if (ok)
            {
                w.WritePropertyName("value");
                w.WriteRawValue(valueOrError);
            }
            else
            {
                w.WriteString("error", valueOrError);
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private static string ProviderInterface(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty("interface").GetString() ?? "";
    }

    private Task<string> CallProvider(string payload, CancellationToken ct)
    {
        using var doc = JsonDocument.Parse(payload);
        var r = doc.RootElement;
        return _sink.CallProvider(
            Id,
            r.GetProperty("interface").GetString() ?? "",
            r.GetProperty("id").GetString() ?? "",
            r.GetProperty("method").GetString() ?? "",
            r.TryGetProperty("args", out var args) ? args.GetRawText() : "[]",
            ct);
    }

    /// <summary>
    /// Serves a provider call from another plugin: runs the registered implementation in this engine and completes
    /// when it replies (it may be asynchronous). Fails if the call takes too long or this engine goes away.
    /// </summary>
    public Task<string> ServeProvider(string iface, string id, string method, string argsJson, TimeSpan timeout)
    {
        var callId = Interlocked.Increment(ref _nextServing);
        var completion = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _serving[callId] = completion;
        var generation = Volatile.Read(ref _generation);
        string payload;
        using (var buffer = new MemoryStream())
        {
            using (var w = new Utf8JsonWriter(buffer))
            {
                w.WriteStartObject();
                w.WriteString("interface", iface);
                w.WriteString("id", id);
                w.WriteString("method", method);
                w.WritePropertyName("args");
                w.WriteRawValue(argsJson);
                w.WriteEndObject();
            }
            payload = Encoding.UTF8.GetString(buffer.ToArray());
        }
        Post(() =>
        {
            if (generation != _generation || State != PluginState.Running)
            {
                Fail(callId, $"{Id} is not running.");
                return;
            }
            Call("provide", callId, payload);
        });
        var timer = new CancellationTokenSource(timeout);
        timer.Token.Register(() => Fail(callId, $"{Id} did not answer {method} within {timeout.TotalSeconds:0} s."));
        completion.Task.ContinueWith(_ => timer.Dispose(), TaskScheduler.Default);
        return completion.Task;
    }

    private void Fail(int callId, string message)
    {
        if (_serving.TryRemove(callId, out var completion))
        {
            completion.TrySetException(new PluginCapabilityException(message));
        }
    }

    /// <summary>Accounts of a local client's login; each gets an opaque handle, never the credential itself.</summary>
    private string LocalAccounts(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var id = doc.RootElement.GetProperty("source").GetString() ?? "";
        if (!_permissions.Has($"localAuth:{id}"))
        {
            throw new PluginCapabilityException($"This plugin has no localAuth:{id} permission.");
        }
        var source = _services.LocalAuthSources.FirstOrDefault(s => s.Id == id)
                     ?? throw new PluginCapabilityException($"本机不支持读取 {id} 的登录。");
        var extra = doc.RootElement.TryGetProperty("configDirs", out var dirs) && dirs.ValueKind == JsonValueKind.Array
            ? dirs.EnumerateArray().Where(d => d.ValueKind == JsonValueKind.String).Select(d => d.GetString()!).ToList()
            : null;
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var account in source.Discover(extra))
            {
                var handle = $"{id}:{Guid.NewGuid():N}";
                _authHandles[handle] = (source, account.Key);
                w.WriteStartObject();
                w.WriteString("handle", handle);
                w.WriteString("source", id);
                w.WriteString("label", account.Label);
                w.WriteString("email", account.Email);
                w.WriteString("plan", account.Plan);
                w.WriteString("accountId", account.AccountId);
                if (account.SubscriptionUntil is { } until)
                {
                    w.WriteString("subscriptionUntil", until);
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// Plan usage of a Claude Code account from its status line (see <see cref="ClaudeStatusLineBridge"/>):
    /// {"connected", "updatedAt", "fiveHour": {"usedPercent","resetsAt"}, "sevenDay": …}.
    /// </summary>
    private string StatusLine(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var handle = doc.RootElement.GetProperty("handle").GetString() ?? "";
        if (!_authHandles.TryGetValue(handle, out var entry) || entry.Source is not ClaudeCodeSource)
        {
            throw new PluginCapabilityException("账户句柄无效或不是 Claude Code 账户，请重新调用 localAuth.accounts()。");
        }
        var bridge = _services.ClaudeStatusLine;
        var configDir = entry.Key;
        var usage = bridge.Read(configDir);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteBoolean("connected", bridge.IsInstalled(configDir));
            if (usage is not null)
            {
                w.WriteString("updatedAt", usage.UpdatedAt);
                foreach (var (name, window) in new[] { ("fiveHour", usage.FiveHour), ("sevenDay", usage.SevenDay) })
                {
                    if (window is null)
                    {
                        continue;
                    }
                    w.WriteStartObject(name);
                    w.WriteNumber("usedPercent", window.UsedPercentage);
                    if (window.ResetsAt is { } resets)
                    {
                        w.WriteString("resetsAt", resets);
                    }
                    w.WriteEndObject();
                }
            }
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>
    /// The token for <paramref name="handle"/>, only for the source's own hosts. Read-only: an expired login is
    /// reported, never refreshed, because refreshing would sign the client itself out.
    /// </summary>
    private bool HasOAuth => _permissions.All.Any(p => p.StartsWith("oauth:", StringComparison.Ordinal));

    private LocalCredential CredentialFor(string handle, Uri uri)
    {
        if (OAuthSessions.IsHandle(handle))
        {
            return HasOAuth ? _oauth.CredentialFor(handle, uri) : throw new PluginCapabilityException("This plugin has no oauth: permission.");
        }
        if (!_authHandles.TryGetValue(handle, out var entry))
        {
            throw new PluginCapabilityException("账户句柄无效或已失效，请重新调用 localAuth.accounts()。");
        }
        var (source, key) = entry;
        if (!source.AllowsHost(uri.IdnHost))
        {
            throw new PluginCapabilityException(
                $"{source.Name} 的登录凭据只能发送到 {string.Join("、", source.Hosts)}，不能发送到 '{uri.IdnHost}'。");
        }
        var credential = source.ReadCredential(key)
                         ?? throw new PluginCapabilityException($"{source.Name} 已在本机退出登录。");
        if (credential.ExpiresAt is { } expires && expires <= DateTimeOffset.UtcNow)
        {
            throw new PluginCapabilityException(
                $"{source.Name} 的登录已过期：请运行一次 {source.Name} 刷新登录。O_O 不会自行刷新，以免让 {source.Name} 掉线。");
        }
        return credential;
    }

    /// <summary>A string setting's current value, or null.</summary>
    private string? SettingText(string key)
    {
        if (_settings.GetJson(key) is not { } json)
        {
            return null;
        }
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.ValueKind == JsonValueKind.String ? doc.RootElement.GetString() : null;
    }

    /// <summary>A password setting's value: declaring it in the manifest is what lets the plugin read it.</summary>
    private string SettingSecret(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var key = doc.RootElement.GetProperty("key").GetString() ?? "";
        if (_settings.Find(key) is not { IsSecret: true })
        {
            throw new PluginCapabilityException($"'{key}' is not a password setting of this plugin.");
        }
        return _settings.GetSecret(key) is { } secret ? JsonSerializer.Serialize(secret, HostJsonContext.Default.String) : "null";
    }

    /// <summary>Keychain access, scoped to this plugin: service "O_O/&lt;plugin id&gt;", account = the key.</summary>
    private string Secret(string op, string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var key = doc.RootElement.GetProperty("key").GetString() ?? "";
        if (key.Length is 0 or > 128)
        {
            throw new PluginCapabilityException("Secret keys must be 1 to 128 characters.");
        }
        var service = $"O_O/{Id}";
        var store = _services.Secrets;
        switch (op)
        {
            case "secrets.get":
                return store.Get(service, key) is { } secret ? JsonSerializer.Serialize(secret, HostJsonContext.Default.String) : "null";
            case "secrets.set":
                store.Set(service, key, doc.RootElement.GetProperty("value").GetString() ?? "");
                return "true";
            default:
                return store.Delete(service, key) ? "true" : "false";
        }
    }

    // ---- natives: the only things JS can ask the host for ---------------------------------------------------------

    private void RegisterNatives(JsEngine engine, int generation)
    {
        engine.SetFunction("__island_log", (ref JsCallContext c) =>
        {
            var level = Arg(ref c, 0) switch
            {
                "debug" => PluginLogLevel.Debug,
                "warn" => PluginLogLevel.Warn,
                "error" => PluginLogLevel.Error,
                _ => PluginLogLevel.Info,
            };
            Log(level, Arg(ref c, 1));
            return JsValue.Undefined;
        }, 2);

        engine.SetFunction("__island_setTimer", (ref JsCallContext c) =>
        {
            var id = c.GetInt32(0);
            var ms = Math.Max(c.GetNumber(1), 0);
            var repeat = c.GetBoolean(2);
            if (_timers.Count >= _options.MaxTimers)
            {
                throw c.TypeError($"Too many timers (limit {_options.MaxTimers}).");
            }
            var due = TimeSpan.FromMilliseconds(ms);
            if (repeat && due < _options.MinTimerInterval)
            {
                due = _options.MinTimerInterval;
            }
            var timer = new Timer(_ => Post(() =>
            {
                if (generation != _generation)
                {
                    return;
                }
                if (!repeat && _timers.Remove(id, out var t))
                {
                    t.Dispose();
                }
                Call("timer", id, null);
            }));
            _timers[id] = timer;
            timer.Change(due, repeat ? due : Timeout.InfiniteTimeSpan);
            return JsValue.Undefined;
        }, 3);

        engine.SetFunction("__island_clearTimer", (ref JsCallContext c) =>
        {
            if (_timers.Remove(c.GetInt32(0), out var timer))
            {
                timer.Dispose();
            }
            return JsValue.Undefined;
        }, 1);

        engine.SetFunction("__island_storage", (ref JsCallContext c) =>
        {
            var op = Arg(ref c, 0);
            var key = Arg(ref c, 1);
            try
            {
                switch (op)
                {
                    case "get":
                        return _storage.Get(key) is { } json ? JsValue.From(json) : JsValue.Null;
                    case "set":
                        _storage.Set(key, Arg(ref c, 2));
                        return JsValue.Undefined;
                    case "delete":
                        return JsValue.From(_storage.Delete(key));
                    case "keys":
                        return JsValue.From(JsonSerializer.Serialize(_storage.Keys(), HostJsonContext.Default.IReadOnlyListString));
                    default:
                        throw c.TypeError($"Unknown storage operation '{op}'.");
                }
            }
            catch (InvalidOperationException e)
            {
                throw c.TypeError(e.Message);
            }
        }, 3);

        engine.SetFunction("__island_settings", (ref JsCallContext c) =>
        {
            var op = Arg(ref c, 0);
            return op switch
            {
                "get" => _settings.GetJson(Arg(ref c, 1)) is { } json ? JsValue.From(json) : JsValue.Null,
                "all" => JsValue.From(_settings.AllJson()),
                _ => throw c.TypeError($"Unknown settings operation '{op}'."),
            };
        }, 2);

        engine.SetFunction("__island_reply", (ref JsCallContext c) =>
        {
            var callId = c.GetInt32(0);
            var ok = c.GetBoolean(1);
            var value = Arg(ref c, 2);
            if (_serving.TryRemove(callId, out var completion))
            {
                if (ok)
                {
                    completion.TrySetResult(value);
                }
                else
                {
                    completion.TrySetException(new PluginCapabilityException(value));
                }
            }
            return JsValue.Undefined;
        }, 3);

        engine.SetFunction("__island_call", (ref JsCallContext c) =>
        {
            StartCall(c.GetInt32(0), Arg(ref c, 1), Arg(ref c, 2), generation, ref c);
            return JsValue.Undefined;
        }, 3);

        engine.SetFunction("__island_activity", (ref JsCallContext c) =>
        {
            var op = Arg(ref c, 0);
            var activityId = Arg(ref c, 1);
            if (op is "chip" or "chipEnd")
            {
                return Chip(ref c, op == "chipEnd", activityId);
            }
            if (op == "alert")
            {
                ActivityView alert;
                try
                {
                    alert = VNodeJson.ParseView(Arg(ref c, 4));
                }
                catch (InvalidDataException e)
                {
                    throw c.TypeError($"Invalid view: {e.Message}");
                }
                var seconds = double.TryParse(Arg(ref c, 3), System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 3;
                _sink.Alert(new AlertSnapshot(Id, alert.CompactLeading, alert.CompactTrailing,
                    TimeSpan.FromSeconds(Math.Clamp(double.IsFinite(s) ? seconds : 3, 1, 10)), ++_sequence));
                return JsValue.Undefined;
            }
            if (op == "page")
            {
                // A page: declared in plugin.json, titled there, and only its expanded view is used.
                if (_package.Manifest.Contributes.Pages.FirstOrDefault(p => p.Id == activityId) is not { } declared)
                {
                    throw c.TypeError($"Page '{activityId}' is not declared in plugin.json (contributes.pages).");
                }
                ActivityView pageView;
                try
                {
                    pageView = VNodeJson.ParseView(Arg(ref c, 4));
                }
                catch (InvalidDataException e)
                {
                    throw c.TypeError($"Invalid view: {e.Message}");
                }
                _pages.Add(activityId);
                _sink.PageUpdated(new PageSnapshot(Id, activityId, declared.Title ?? activityId, pageView.Expanded, ++_sequence, pageView.Status));
                return JsValue.Undefined;
            }
            if (op == "end")
            {
                if (_activities.Remove(activityId))
                {
                    _sink.ActivityEnded(Id, activityId);
                }
                return JsValue.Undefined;
            }
            if (!_activities.Contains(activityId) && _activities.Count >= _options.MaxActivities)
            {
                throw c.TypeError($"A plugin may show at most {_options.MaxActivities} activities at once.");
            }
            ActivityView view;
            try
            {
                view = VNodeJson.ParseView(Arg(ref c, 4));
            }
            catch (InvalidDataException e)
            {
                throw c.TypeError($"Invalid view: {e.Message}");
            }
            var priority = Arg(ref c, 3) switch
            {
                "ambient" => ActivityPriority.Ambient,
                "active" => ActivityPriority.Active,
                "critical" => ActivityPriority.Critical,
                _ => ActivityPriority.Passive,
            };
            _activities.Add(activityId);
            _sink.ActivityUpdated(new ActivitySnapshot(Id, activityId, Arg(ref c, 2), priority, view, ++_sequence));
            return JsValue.Undefined;
        }, 5);
    }

    /// <summary>
    /// <c>chip</c> / <c>chipEnd</c>: (op, chip id, instance or "", title or "", view JSON). The chip must be declared;
    /// an instance only for chips declared <c>multiple</c>.
    /// </summary>
    private JsValue Chip(ref JsCallContext c, bool end, string chipId)
    {
        if (_package.Manifest.Contributes.Chips.FirstOrDefault(x => x.Id == chipId) is not { } declared)
        {
            throw c.TypeError($"Chip '{chipId}' is not declared in plugin.json (contributes.chips).");
        }
        var instance = Arg(ref c, 2) is { Length: > 0 } i ? i : null;
        if (declared.Multiple != (instance is not null))
        {
            throw c.TypeError(declared.Multiple
                ? $"Chip '{chipId}' is declared multiple: use island.chip('{chipId}', instance)."
                : $"Chip '{chipId}' is not declared multiple: use island.chip('{chipId}').");
        }
        if (instance is { Length: > 64 })
        {
            throw c.TypeError("A chip instance id is at most 64 characters.");
        }
        var key = (chipId, instance);
        if (end)
        {
            if (_chips.Remove(key))
            {
                _sink.ChipUpdated(new ChipSnapshot(Id, chipId, instance, "", null, ++_sequence));
            }
            return JsValue.Undefined;
        }
        if (!_chips.Contains(key) && _chips.Count >= MaxChips)
        {
            throw c.TypeError($"A plugin may show at most {MaxChips} chips at once.");
        }
        VNode? view;
        try
        {
            view = VNodeJson.ParseView(Arg(ref c, 4)).Expanded;
        }
        catch (InvalidDataException e)
        {
            throw c.TypeError($"Invalid view: {e.Message}");
        }
        _chips.Add(key);
        var title = Arg(ref c, 3) is { Length: > 0 } t ? t : declared.Title ?? chipId;
        _sink.ChipUpdated(new ChipSnapshot(Id, chipId, instance, title, view, ++_sequence));
        return JsValue.Undefined;
    }

    private const int MaxChips = 32;

    /// <summary>A string argument; missing or non-string arguments read as empty.</summary>
    private static string Arg(ref JsCallContext c, int index) =>
        index < c.ArgumentCount ? c.GetString(index) ?? "" : "";

    public void Dispose()
    {
        _queue.CompleteAdding();
        _stop.Cancel();
        if (Thread.CurrentThread != _thread && _thread.IsAlive)
        {
            // Interrupt a script that is still running so the thread can finish.
            _engine?.Interrupt();
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }
}
