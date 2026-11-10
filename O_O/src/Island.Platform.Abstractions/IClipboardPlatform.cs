namespace Island.Platform;

/// <summary>
/// The native half of the clipboard capability (docs/design/05-builtin-plugins.md §8): cheap change detection,
/// recognising content other apps mark as secret, and "direct paste" into the app the user was working in.
/// Reading and writing the content itself goes through Avalonia's clipboard.
/// </summary>
public interface IClipboardPlatform
{
    /// <summary>A number that changes whenever the clipboard does; null where the platform has none (callers then compare content).</summary>
    long? ChangeCount { get; }

    /// <summary>The current content is marked as secret or transient (password managers): it must not be recorded.</summary>
    bool IsConcealed();

    /// <summary>
    /// Notes which app is in front, unless it is O_O itself. Called regularly, so that when the user clicks the island
    /// (which may bring O_O forward) the app they were working in is still known.
    /// </summary>
    void TrackForegroundApp();

    /// <summary>The app last tracked, for display ("粘贴到 Safari"); null when unknown.</summary>
    string? TargetAppName { get; }

    /// <summary>Brings the tracked app back to the front and sends the paste shortcut (Cmd/Ctrl+V).</summary>
    PasteResult Paste();
}

/// <param name="Pasted">The keystroke was sent.</param>
/// <param name="Reason">Why not, in words for the user (e.g. accessibility permission missing).</param>
public sealed record PasteResult(bool Pasted, string? Reason = null)
{
    public static PasteResult Done { get; } = new(true);
}

/// <summary>For tests and platforms without native support: content comparison only, no direct paste.</summary>
public sealed class NullClipboardPlatform : IClipboardPlatform
{
    public long? ChangeCount => null;
    public bool IsConcealed() => false;
    public void TrackForegroundApp() { }
    public string? TargetAppName => null;
    public PasteResult Paste() => new(false, "此平台不支持直接粘贴，已复制到剪贴板。");
}
