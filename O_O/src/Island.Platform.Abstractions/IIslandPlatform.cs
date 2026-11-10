using Island.Core.Geometry;
using Island.Core.Layout;

namespace Island.Platform;

/// <summary>
/// What the island needs from the operating system that Avalonia does not offer
/// (docs/design/02-island-shell.md §1 and §3). One implementation per OS.
/// </summary>
public interface IIslandPlatform
{
    string Name { get; }

    /// <summary>
    /// The notch of the screen whose bounds are <paramref name="screenBounds"/> (DIPs, as Avalonia reports them:
    /// <c>Screen.Bounds / Screen.Scaling</c>), or null when it has none.
    /// </summary>
    NotchInfo? DetectNotch(RectD screenBounds);

    /// <summary>Takes over a shown island window: stacking level, task switcher exclusion, click-through.</summary>
    /// <param name="handle">The window's <c>IPlatformHandle.Handle</c>.</param>
    /// <param name="descriptor">The window's <c>IPlatformHandle.HandleDescriptor</c> ("NSView", "HWND", "XID").</param>
    /// <param name="scale">Pixels per DIP of the window.</param>
    IIslandWindowBridge Attach(nint handle, string? descriptor, double scale);

    /// <summary>The OS credential store, used for plugin secrets and account tokens.</summary>
    ISecretStore Secrets { get; }
}

/// <summary>The native side of one island window.</summary>
public interface IIslandWindowBridge : IDisposable
{
    /// <summary>
    /// The part of the window that receives the pointer, in window DIPs. Everything outside it passes clicks, scrolls
    /// and hovers through to the windows underneath.
    /// </summary>
    void SetInteractiveRegion(RectD region);

    /// <summary>The pointer position relative to the window's top-left corner, in DIPs, wherever the pointer is.</summary>
    bool TryGetCursor(out PointD position);

    /// <summary>Called after every pointer sample with whether the pointer is on the interactive region.</summary>
    void OnPointerSample(bool insideRegion);

    void UpdateScale(double scale);

    /// <summary>Native window state for diagnostics logs.</summary>
    string Describe() => "";
}

/// <summary>Fallback for platforms without an implementation: no notch, no click-through.</summary>
public sealed class NullIslandPlatform : IIslandPlatform
{
    public string Name => "Unsupported";

    public NotchInfo? DetectNotch(RectD screenBounds) => null;

    public IIslandWindowBridge Attach(nint handle, string? descriptor, double scale) => new NullBridge();

    // No OS store: secrets live for this run only rather than being written somewhere unprotected.
    public ISecretStore Secrets { get; } = new InMemorySecretStore();

    private sealed class NullBridge : IIslandWindowBridge
    {
        public void SetInteractiveRegion(RectD region) { }
        public bool TryGetCursor(out PointD position) { position = default; return false; }
        public void OnPointerSample(bool insideRegion) { }
        public void UpdateScale(double scale) { }
        public void Dispose() { }
    }
}
