using System.Runtime.InteropServices;

namespace Island.Platform.Linux;

internal static partial class X11
{
    public const int ShapeInput = 2;
    public const int ShapeSet = 0;
    public const int Unsorted = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct XRectangle { public short X, Y; public ushort Width, Height; }

    [LibraryImport("libX11.so.6")]
    public static partial nint XOpenDisplay(nint name);

    [LibraryImport("libX11.so.6")]
    public static partial int XCloseDisplay(nint display);

    [LibraryImport("libX11.so.6")]
    public static partial int XFlush(nint display);

    [LibraryImport("libX11.so.6")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool XQueryPointer(
        nint display, nint window, out nint root, out nint child,
        out int rootX, out int rootY, out int winX, out int winY, out uint mask);

    [LibraryImport("libXext.so.6")]
    public static unsafe partial void XShapeCombineRectangles(
        nint display, nint window, int kind, int xOff, int yOff,
        XRectangle* rectangles, int count, int op, int ordering);
}
