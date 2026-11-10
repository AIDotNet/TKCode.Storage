using System.Runtime.InteropServices;

namespace Island.Platform.Linux;

/// <summary>
/// Linux (X11 / XWayland) clipboard support. There is no cheap change counter here, so the caller compares content;
/// direct paste sends Ctrl+V through the XTest extension to whatever has the focus (the island does not take it).
/// Native Wayland windows do not receive XTest events: then the paste silently does nothing, which is reported as
/// a possibility rather than detected.
/// </summary>
public sealed partial class LinuxClipboard : IClipboardPlatform
{
    private const string Xlib = "libX11.so.6";
    private const string Xtst = "libXtst.so.6";
    private const ulong XkControlL = 0xffe3;
    private const ulong XkV = 0x0076;

    public long? ChangeCount => null;

    // KDE marks password-manager content with x-kde-passwordManagerHint; reading MIME types needs a selection
    // round trip, so content from those apps is not recognised yet.
    public bool IsConcealed() => false;

    public void TrackForegroundApp() { }

    public string? TargetAppName => null;

    public PasteResult Paste()
    {
        nint display;
        try
        {
            display = XOpenDisplay(0);
        }
        catch (DllNotFoundException)
        {
            return new(false, "缺少 X11 / XTest 库，无法直接粘贴。已复制到剪贴板。");
        }
        if (display == 0)
        {
            return new(false, "无法连接 X 服务器，无法直接粘贴。已复制到剪贴板。");
        }
        try
        {
            var control = XKeysymToKeycode(display, XkControlL);
            var v = XKeysymToKeycode(display, XkV);
            XTestFakeKeyEvent(display, control, 1, 0);
            XTestFakeKeyEvent(display, v, 1, 0);
            XTestFakeKeyEvent(display, v, 0, 0);
            XTestFakeKeyEvent(display, control, 0, 0);
            XFlush(display);
            return PasteResult.Done;
        }
        catch (DllNotFoundException)
        {
            return new(false, "缺少 libXtst，无法直接粘贴。已复制到剪贴板。");
        }
        finally
        {
            XCloseDisplay(display);
        }
    }

    [LibraryImport(Xlib)]
    private static partial nint XOpenDisplay(nint name);

    [LibraryImport(Xlib)]
    private static partial int XCloseDisplay(nint display);

    [LibraryImport(Xlib)]
    private static partial byte XKeysymToKeycode(nint display, ulong keysym);

    [LibraryImport(Xlib)]
    private static partial int XFlush(nint display);

    [LibraryImport(Xtst)]
    private static partial int XTestFakeKeyEvent(nint display, uint keycode, int isPress, ulong delay);
}
