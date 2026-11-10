using Island.Core.Geometry;
using Island.Core.Layout;

namespace Island.Platform.Windows;

/// <summary>
/// Windows: a tool window that never takes focus, with a window region as the click-through mechanism.
/// A region clips hit-testing and drawing alike, which is fine because everything outside the island shape is
/// transparent anyway, and unlike WS_EX_TRANSPARENT it does not depend on the window being layered.
/// </summary>
public sealed class WindowsIslandPlatform : IIslandPlatform
{
    public string Name => "Windows";

    public ISecretStore Secrets { get; } = new WindowsCredentialSecretStore();

    // No notched displays on Windows today; the top inset of a top-docked taskbar comes from Avalonia's working area.
    public NotchInfo? DetectNotch(RectD screenBounds) => null;

    public IIslandWindowBridge Attach(nint handle, string? descriptor, double scale)
    {
        var style = (long)Win32.GetWindowLongPtr(handle, Win32.GWL_EXSTYLE);
        style = (style | Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE) & ~Win32.WS_EX_APPWINDOW;
        Win32.SetWindowLongPtr(handle, Win32.GWL_EXSTYLE, (nint)style);
        return new Bridge(handle, scale);
    }

    private sealed class Bridge(nint hwnd, double scale) : IIslandWindowBridge
    {
        private double _scale = scale;
        private RectD? _region;

        public void SetInteractiveRegion(RectD region)
        {
            if (_region == region)
            {
                return;
            }
            _region = region;
            // Round outwards so the shape's anti-aliased edge is never clipped.
            var rgn = Win32.CreateRectRgn(
                (int)Math.Floor(region.X * _scale),
                (int)Math.Floor(region.Y * _scale),
                (int)Math.Ceiling(region.Right * _scale),
                (int)Math.Ceiling(region.Bottom * _scale));
            if (Win32.SetWindowRgn(hwnd, rgn, true) == 0)
            {
                Win32.DeleteObject(rgn);
            }
        }

        public bool TryGetCursor(out PointD position)
        {
            position = default;
            if (!Win32.GetCursorPos(out var p) || !Win32.ScreenToClient(hwnd, ref p))
            {
                return false;
            }
            position = new PointD(p.X / _scale, p.Y / _scale);
            return true;
        }

        public void OnPointerSample(bool insideRegion) { }

        public void UpdateScale(double scale)
        {
            _scale = scale;
            if (_region is { } r)
            {
                _region = null;
                SetInteractiveRegion(r);
            }
        }

        public void Dispose() { }
    }
}
