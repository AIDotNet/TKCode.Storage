using Island.Core.Geometry;
using Island.Core.Layout;

namespace Island.Platform.Linux;

/// <summary>
/// Linux under X11, including XWayland (the app forces the X11 backend on Wayland sessions,
/// docs/design/02-island-shell.md §1): the X Shape extension's input shape is the click-through mechanism.
/// </summary>
public sealed class LinuxIslandPlatform : IIslandPlatform
{
    public string Name => "Linux (X11)";

    public ISecretStore Secrets { get; } = new SecretToolSecretStore();

    public NotchInfo? DetectNotch(RectD screenBounds) => null;

    public IIslandWindowBridge Attach(nint handle, string? descriptor, double scale)
    {
        if (descriptor is not null && descriptor != "XID")
        {
            throw new NotSupportedException($"Expected an X11 window, got '{descriptor}'.");
        }
        // A connection of our own: shapes are server-side state, so they apply to Avalonia's window all the same.
        var display = X11.XOpenDisplay(0);
        if (display == 0)
        {
            throw new InvalidOperationException("Cannot open the X display.");
        }
        return new Bridge(display, handle, scale);
    }

    private sealed class Bridge(nint display, nint window, double scale) : IIslandWindowBridge
    {
        private double _scale = scale;
        private RectD? _region;

        public unsafe void SetInteractiveRegion(RectD region)
        {
            if (_region == region)
            {
                return;
            }
            _region = region;
            var x = (int)Math.Floor(region.X * _scale);
            var y = (int)Math.Floor(region.Y * _scale);
            var rect = new X11.XRectangle
            {
                X = (short)x,
                Y = (short)y,
                Width = (ushort)(Math.Ceiling(region.Right * _scale) - x),
                Height = (ushort)(Math.Ceiling(region.Bottom * _scale) - y),
            };
            X11.XShapeCombineRectangles(display, window, X11.ShapeInput, 0, 0, &rect, 1, X11.ShapeSet, X11.Unsorted);
            X11.XFlush(display);
        }

        public bool TryGetCursor(out PointD position)
        {
            var ok = X11.XQueryPointer(display, window, out _, out _, out _, out _, out var wx, out var wy, out _);
            position = new PointD(wx / _scale, wy / _scale);
            return ok;
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

        public void Dispose() => X11.XCloseDisplay(display);
    }
}
