using Island.Core.Input;

namespace Island.Platform;

/// <summary>
/// System-wide keyboard shortcuts that reach O_O whichever app is in front (docs/design/04-console.md, 快捷键).
/// </summary>
public interface IGlobalHotkeys : IDisposable
{
    /// <summary>Raised with the id of the hotkey pressed. May be raised on any thread.</summary>
    event Action<string>? Pressed;

    /// <summary>Claims <paramref name="hotkey"/> for <paramref name="id"/>, replacing what that id had.</summary>
    /// <returns>Null on success; otherwise why not, in words for the user (e.g. another app already uses it).</returns>
    string? Register(string id, Hotkey hotkey);

    void Unregister(string id);
}

/// <summary>Where global hotkeys are not supported (Wayland without XWayland, tests).</summary>
public sealed class NullGlobalHotkeys : IGlobalHotkeys
{
    public event Action<string>? Pressed { add { } remove { } }
    public string? Register(string id, Hotkey hotkey) => "此平台不支持全局快捷键。";
    public void Unregister(string id) { }
    public void Dispose() { }
}
