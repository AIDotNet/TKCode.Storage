using Avalonia.Threading;
using Island.Core.Input;
using Island.Platform;

namespace Island.UI.Services;

/// <summary>
/// Keeps the platform's global hotkeys in step with the user's (<see cref="HotkeyStore"/>) and runs the action of a
/// pressed one on the UI thread. Remembers why a hotkey could not be registered, for the console.
/// </summary>
public sealed class HotkeyService : IDisposable
{
    private readonly IGlobalHotkeys _platform;
    private readonly IReadOnlyDictionary<string, Action> _actions;
    private readonly Dictionary<string, string?> _problems = new(StringComparer.Ordinal);
    private bool _suspended;

    public HotkeyService(HotkeyStore store, IGlobalHotkeys platform, IReadOnlyDictionary<string, Action> actions)
    {
        Store = store;
        _platform = platform;
        _actions = actions;
        platform.Pressed += id => Dispatcher.UIThread.Post(() =>
        {
            if (!_suspended && _actions.TryGetValue(id, out var action))
            {
                action();
            }
        });
        store.Changed += action => Dispatcher.UIThread.Post(() => Apply(action));
        foreach (var (id, _, _) in HotkeyActions.All)
        {
            Apply(id);
        }
    }

    public HotkeyStore Store { get; }

    /// <summary>Raised after an action's hotkey was (re)registered; <see cref="Problem"/> says how it went.</summary>
    public event Action<string>? StatusChanged;

    /// <summary>Why the action's hotkey is not working (taken by another app, unsupported), or null.</summary>
    public string? Problem(string action) => _problems.GetValueOrDefault(action);

    /// <summary>
    /// While the console records a new shortcut, every hotkey is released so pressing an existing one reaches the
    /// recorder instead of running its action; afterwards they are claimed again.
    /// </summary>
    public void Suspend(bool suspended)
    {
        if (_suspended == suspended)
        {
            return;
        }
        _suspended = suspended;
        foreach (var (id, _, _) in HotkeyActions.All)
        {
            Apply(id);
        }
    }

    private void Apply(string action)
    {
        _platform.Unregister(action);
        string? problem = null;
        if (!_suspended && Store.Get(action) is { } hotkey)
        {
            problem = _platform.Register(action, hotkey);
        }
        _problems[action] = problem;
        if (Environment.GetEnvironmentVariable("O_O_DEBUG") == "1")
        {
            System.Console.Error.WriteLine($"[hotkeys] {action} = {Store.Get(action)?.ToString() ?? "(none)"}{(_suspended ? " (suspended)" : "")}: {problem ?? "ok"}");
        }
        StatusChanged?.Invoke(action);
    }

    public void Dispose() => _platform.Dispose();
}
