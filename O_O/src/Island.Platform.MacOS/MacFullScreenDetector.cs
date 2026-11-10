using System.Runtime.InteropServices;
using Island.Core.Geometry;

namespace Island.Platform.MacOS;

/// <summary>
/// macOS: the frontmost normal window (layer 0) of another process, from the on-screen window list
/// (CGWindowListCopyWindowInfo, front to back), covers the whole screen. Bounds and layer are available without the
/// screen recording permission; window titles, which need it, are never asked for. Global window coordinates have
/// their origin at the top left of the primary display, in points, which is what Avalonia's screen DIPs are.
/// </summary>
public sealed partial class MacFullScreenDetector : IFullScreenDetector
{
    private const string CoreGraphics = "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";
    private const string CoreFoundation = "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";
    private const uint OnScreenOnly = 1 << 0;
    private const uint ExcludeDesktopElements = 1 << 4;
    private const int CFNumberSInt64Type = 4;
    private const uint Utf8 = 0x08000100;

    private static readonly nint LayerKey = CFString("kCGWindowLayer");
    private static readonly nint BoundsKey = CFString("kCGWindowBounds");
    private static readonly nint OwnerKey = CFString("kCGWindowOwnerPID");
    private static readonly long OwnPid = Environment.ProcessId;

    public bool IsFullScreen(RectD screen, double scale)
    {
        var list = CGWindowListCopyWindowInfo(OnScreenOnly | ExcludeDesktopElements, 0);
        if (list == 0)
        {
            return false;
        }
        try
        {
            var count = CFArrayGetCount(list);
            for (nint i = 0; i < count; i++)
            {
                var window = CFArrayGetValueAtIndex(list, i);
                if (Number(window, LayerKey) != 0)
                {
                    continue; // menu bar, Dock, overlays, our own island (level 27)…
                }
                if (Number(window, OwnerKey) == OwnPid)
                {
                    continue; // our console
                }
                var boundsDict = CFDictionaryGetValue(window, BoundsKey);
                if (boundsDict == 0 || !CGRectMakeWithDictionaryRepresentation(boundsDict, out var r))
                {
                    continue;
                }
                if (r.Width < 50 || r.Height < 50)
                {
                    continue; // invisible helper windows
                }
                // The frontmost normal window that is on this screen decides.
                var onScreen = r.X < screen.Right && r.X + r.Width > screen.X && r.Y < screen.Bottom && r.Y + r.Height > screen.Y;
                if (!onScreen)
                {
                    continue;
                }
                return Math.Abs(r.X - screen.X) < 1 && Math.Abs(r.Y - screen.Y) < 1 &&
                       Math.Abs(r.Width - screen.Width) < 1 && Math.Abs(r.Height - screen.Height) < 1;
            }
            return false;
        }
        finally
        {
            CFRelease(list);
        }
    }

    private static long Number(nint dict, nint key)
    {
        var n = CFDictionaryGetValue(dict, key);
        return n != 0 && CFNumberGetValue(n, CFNumberSInt64Type, out var v) ? v : -1;
    }

    private static nint CFString(string s) => CFStringCreateWithCString(0, s, Utf8);

    [StructLayout(LayoutKind.Sequential)]
    private struct CGRect { public double X, Y, Width, Height; }

    [LibraryImport(CoreGraphics)]
    private static partial nint CGWindowListCopyWindowInfo(uint option, uint relativeToWindow);

    [LibraryImport(CoreGraphics)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CGRectMakeWithDictionaryRepresentation(nint dict, out CGRect rect);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetCount(nint array);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFArrayGetValueAtIndex(nint array, nint index);

    [LibraryImport(CoreFoundation)]
    private static partial nint CFDictionaryGetValue(nint dict, nint key);

    [LibraryImport(CoreFoundation)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool CFNumberGetValue(nint number, int type, out long value);

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint CFStringCreateWithCString(nint allocator, string s, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(nint obj);
}
