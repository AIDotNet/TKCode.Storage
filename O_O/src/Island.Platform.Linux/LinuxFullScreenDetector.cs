using System.Runtime.InteropServices;
using Island.Core.Geometry;

namespace Island.Platform.Linux;

/// <summary>
/// Linux (X11 / XWayland, see docs 01): the active window (_NET_ACTIVE_WINDOW on the root window) carries
/// _NET_WM_STATE_FULLSCREEN. Windows of native Wayland clients are invisible to X11; then this says false.
/// </summary>
public sealed partial class LinuxFullScreenDetector : IFullScreenDetector, IDisposable
{
    private const string Lib = "libX11.so.6";
    private const nint AnyPropertyType = 0;
    private readonly nint _display;
    private readonly nint _active;
    private readonly nint _state;
    private readonly nint _fullScreen;

    public LinuxFullScreenDetector()
    {
        try
        {
            _display = XOpenDisplay(0);
        }
        catch (DllNotFoundException)
        {
            _display = 0;
        }
        if (_display != 0)
        {
            _active = XInternAtom(_display, "_NET_ACTIVE_WINDOW", 0);
            _state = XInternAtom(_display, "_NET_WM_STATE", 0);
            _fullScreen = XInternAtom(_display, "_NET_WM_STATE_FULLSCREEN", 0);
        }
    }

    public unsafe bool IsFullScreen(RectD screen, double scale)
    {
        if (_display == 0)
        {
            return false;
        }
        var root = XDefaultRootWindow(_display);
        if (XGetWindowProperty(_display, root, _active, 0, 1, 0, AnyPropertyType, out _, out var format, out var items, out _, out var data) != 0 ||
            data == 0)
        {
            return false;
        }
        var window = items > 0 && format == 32 ? *(nint*)data : 0;
        XFree(data);
        if (window == 0)
        {
            return false;
        }
        if (XGetWindowProperty(_display, window, _state, 0, 64, 0, AnyPropertyType, out _, out format, out items, out _, out data) != 0 ||
            data == 0)
        {
            return false;
        }
        try
        {
            // Format-32 properties come back as an array of C longs.
            for (nint i = 0; i < items; i++)
            {
                if (((nint*)data)[i] == _fullScreen)
                {
                    return true;
                }
            }
            return false;
        }
        finally
        {
            XFree(data);
        }
    }

    public void Dispose()
    {
        if (_display != 0)
        {
            XCloseDisplay(_display);
        }
    }

    [LibraryImport(Lib)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(Lib)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Lib)]
    private static partial nint XDefaultRootWindow(nint display);

    [LibraryImport(Lib, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint XInternAtom(nint display, string name, int onlyIfExists);

    [LibraryImport(Lib)]
    private static partial int XGetWindowProperty(
        nint display, nint window, nint property, nint offset, nint length, int delete, nint reqType,
        out nint actualType, out int actualFormat, out nint items, out nint bytesAfter, out nint data);

    [LibraryImport(Lib)]
    private static partial int XFree(nint data);
}
