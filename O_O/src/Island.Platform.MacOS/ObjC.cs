using System.Runtime.InteropServices;

namespace Island.Platform.MacOS;

[StructLayout(LayoutKind.Sequential)]
internal struct NSPoint { public double X, Y; }

[StructLayout(LayoutKind.Sequential)]
internal struct NSRect
{
    public double X, Y, Width, Height;
    public readonly double MaxY => Y + Height;
}

[StructLayout(LayoutKind.Sequential)]
internal struct NSEdgeInsets { public double Top, Left, Bottom, Right; }

/// <summary>
/// Minimal Objective-C runtime calls, without Xamarin/.NET for macOS bindings (AOT friendly).
/// Structs larger than 16 bytes come back through objc_msgSend_stret on x86_64 and in registers on arm64.
/// </summary>
internal static partial class ObjC
{
    private const string Lib = "/usr/lib/libobjc.A.dylib";
    private static readonly bool UseStret = RuntimeInformation.ProcessArchitecture == Architecture.X64;

    [LibraryImport(Lib, EntryPoint = "objc_getClass", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Class(string name);

    [LibraryImport(Lib, EntryPoint = "sel_registerName", StringMarshalling = StringMarshalling.Utf8)]
    public static partial nint Sel(string name);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial nint Send(nint receiver, nint selector, nint arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, nint arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial void SendVoid(nint receiver, nint selector, byte arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial byte SendByte(nint receiver, nint selector, nint arg);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial double SendDouble(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    public static partial NSPoint SendPoint(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial NSRect SendRectRegs(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend_stret")]
    private static partial void SendRectStret(out NSRect result, nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial NSEdgeInsets SendInsetsRegs(nint receiver, nint selector);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend_stret")]
    private static partial void SendInsetsStret(out NSEdgeInsets result, nint receiver, nint selector);

    public static NSRect SendRect(nint receiver, nint selector)
    {
        if (!UseStret)
        {
            return SendRectRegs(receiver, selector);
        }
        SendRectStret(out var r, receiver, selector);
        return r;
    }

    public static NSEdgeInsets SendInsets(nint receiver, nint selector)
    {
        if (!UseStret)
        {
            return SendInsetsRegs(receiver, selector);
        }
        SendInsetsStret(out var r, receiver, selector);
        return r;
    }

    public static bool RespondsTo(nint receiver, nint selector) =>
        SendByte(receiver, Sel("respondsToSelector:"), selector) != 0;
}
