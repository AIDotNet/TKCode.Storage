using Island.Core.Geometry;
using Island.Core.Layout;

namespace Island.Platform.MacOS;

/// <summary>macOS: notch from NSScreen, window above the menu bar, click-through by toggling ignoresMouseEvents.</summary>
public sealed class MacIslandPlatform : IIslandPlatform
{
    // Above NSMainMenuWindowLevel (24) so the island can sit over the menu bar around the notch.
    private const long IslandWindowLevel = 27;

    // NSWindowCollectionBehavior: CanJoinAllSpaces | Stationary | IgnoresCycle | FullScreenAuxiliary.
    private const ulong CollectionBehavior = (1 << 0) | (1 << 4) | (1 << 6) | (1 << 8);

    public string Name => "macOS";

    public ISecretStore Secrets { get; } = new MacKeychainSecretStore();

    public NotchInfo? DetectNotch(RectD screenBounds)
    {
        var screens = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("screens"));
        var count = (long)ObjC.Send(screens, ObjC.Sel("count"));
        for (long i = 0; i < count; i++)
        {
            var screen = ObjC.Send(screens, ObjC.Sel("objectAtIndex:"), (nint)i);
            var frame = ObjC.SendRect(screen, ObjC.Sel("frame"));
            // Avalonia and AppKit agree on x and on sizes (both in points); AppKit's y axis points up, so y is not compared.
            if (Math.Abs(frame.X - screenBounds.X) > 1 || Math.Abs(frame.Width - screenBounds.Width) > 1 ||
                Math.Abs(frame.Height - screenBounds.Height) > 1)
            {
                continue;
            }
            return NotchOf(screen, frame);
        }
        return null;
    }

    private static NotchInfo? NotchOf(nint screen, NSRect frame)
    {
        var safeArea = ObjC.Sel("safeAreaInsets");
        if (!ObjC.RespondsTo(screen, safeArea))
        {
            return null;
        }
        var insets = ObjC.SendInsets(screen, safeArea);
        if (insets.Top <= 0)
        {
            return null;
        }
        // The two auxiliary areas are the menu bar either side of the notch; the notch is what lies between them.
        // Only their widths are used, which does not depend on which coordinate space AppKit reports them in.
        var left = ObjC.SendRect(screen, ObjC.Sel("auxiliaryTopLeftArea"));
        var right = ObjC.SendRect(screen, ObjC.Sel("auxiliaryTopRightArea"));
        var width = frame.Width - left.Width - right.Width;
        return width > 0 ? new NotchInfo(left.Width, width, insets.Top) : null;
    }

    public IIslandWindowBridge Attach(nint handle, string? descriptor, double scale)
    {
        var window = descriptor == "NSWindow" ? handle : ObjC.Send(handle, ObjC.Sel("window"));
        if (window == 0)
        {
            throw new InvalidOperationException($"No NSWindow behind handle '{descriptor}'.");
        }
        ObjC.SendVoid(window, ObjC.Sel("setLevel:"), (nint)IslandWindowLevel);
        ObjC.SendVoid(window, ObjC.Sel("setCollectionBehavior:"), (nint)CollectionBehavior);
        ObjC.SendVoid(window, ObjC.Sel("setHasShadow:"), (byte)0);
        return new Bridge(window);
    }

    private sealed class Bridge(nint window) : IIslandWindowBridge
    {
        private bool? _ignoring;

        public void SetInteractiveRegion(RectD region)
        {
            // AppKit has no input region: the window ignores the mouse whenever the pointer is off the region
            // (see OnPointerSample), which the controller samples continuously.
        }

        public bool TryGetCursor(out PointD position)
        {
            var mouse = ObjC.SendPoint(ObjC.Class("NSEvent"), ObjC.Sel("mouseLocation"));
            var frame = ObjC.SendRect(window, ObjC.Sel("frame"));
            // Both are in global points with the origin at the bottom-left; flip to window-relative top-left.
            position = new PointD(mouse.X - frame.X, frame.MaxY - mouse.Y);
            return true;
        }

        public void OnPointerSample(bool insideRegion)
        {
            var ignore = !insideRegion;
            if (_ignoring == ignore)
            {
                return;
            }
            _ignoring = ignore;
            System.Console.Error.WriteLine($"[zz] island ignoresMouse={ignore}"); // TEMP
            ObjC.SendVoid(window, ObjC.Sel("setIgnoresMouseEvents:"), ignore ? (byte)1 : (byte)0);
        }

        public void UpdateScale(double scale) { }

        public void EnsureOnScreen()
        {
            // Re-assert the level and order the window front without making it key or activating O_O.
            ObjC.SendVoid(window, ObjC.Sel("setLevel:"), (nint)IslandWindowLevel);
            _ = ObjC.Send(window, ObjC.Sel("orderFrontRegardless")); // returns void; the register value is ignored
        }

        public string Describe()
        {
            var f = ObjC.SendRect(window, ObjC.Sel("frame"));
            var visible = ObjC.Send(window, ObjC.Sel("isVisible")) != 0;
            var level = (long)ObjC.Send(window, ObjC.Sel("level"));
            var opaque = ObjC.Send(window, ObjC.Sel("isOpaque")) != 0;
            return $"frame=({f.X},{f.Y},{f.Width}x{f.Height}) visible={visible} level={level} opaque={opaque}";
        }

        public void Dispose() { }
    }
}
