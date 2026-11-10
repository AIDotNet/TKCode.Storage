using System.Runtime.InteropServices;

namespace Island.Platform.MacOS;

/// <summary>
/// macOS: the main screen's wallpaper via <c>NSWorkspace desktopImageURLForScreen:</c>. The file may be HEIC (the
/// built-in wallpapers) or a format Avalonia cannot decode, so AppKit decodes it and hands back a small PNG
/// (<c>NSImage</c> → <c>NSBitmapImageRep</c> at a reduced size → PNG data).
/// </summary>
public sealed partial class MacWallpaper : IWallpaperSource
{
    private const nuint PngFileType = 4; // NSBitmapImageFileTypePNG
    private const double Thumb = 96;     // points: plenty to find the main colour

    public byte[]? ReadImage()
    {
        var pool = ObjC.Send(ObjC.Send(ObjC.Class("NSAutoreleasePool"), ObjC.Sel("alloc")), ObjC.Sel("init"));
        try
        {
            var workspace = ObjC.Send(ObjC.Class("NSWorkspace"), ObjC.Sel("sharedWorkspace"));
            var screen = ObjC.Send(ObjC.Class("NSScreen"), ObjC.Sel("mainScreen"));
            if (workspace == 0 || screen == 0)
            {
                return null;
            }
            var url = ObjC.Send(workspace, ObjC.Sel("desktopImageURLForScreen:"), screen);
            if (url == 0)
            {
                return null;
            }
            var image = ObjC.Send(ObjC.Send(ObjC.Class("NSImage"), ObjC.Sel("alloc")), ObjC.Sel("initWithContentsOfURL:"), url);
            if (image == 0)
            {
                return null;
            }
            try
            {
                // Draw the image into a small bitmap and encode that: decoding HEIC and shrinking happen in AppKit.
                var rep = ObjC.Send(ObjC.Class("NSBitmapImageRep"), ObjC.Sel("alloc"));
                rep = InitBitmap(rep, ObjC.Sel("initWithBitmapDataPlanes:pixelsWide:pixelsHigh:bitsPerSample:samplesPerPixel:hasAlpha:isPlanar:colorSpaceName:bytesPerRow:bitsPerPixel:"),
                    0, (nint)Thumb, (nint)Thumb, 8, 4, 1, 0, NSString("NSDeviceRGBColorSpace"), 0, 0);
                if (rep == 0)
                {
                    return null;
                }
                try
                {
                    var context = ObjC.Send(ObjC.Class("NSGraphicsContext"), ObjC.Sel("graphicsContextWithBitmapImageRep:"), rep);
                    if (context == 0)
                    {
                        return null;
                    }
                    ObjC.Send(ObjC.Class("NSGraphicsContext"), ObjC.Sel("saveGraphicsState"));
                    ObjC.SendVoid(ObjC.Class("NSGraphicsContext"), ObjC.Sel("setCurrentContext:"), context);
                    DrawInRect(image, ObjC.Sel("drawInRect:"), new Rect(0, 0, Thumb, Thumb));
                    ObjC.Send(context, ObjC.Sel("flushGraphics"));
                    ObjC.Send(ObjC.Class("NSGraphicsContext"), ObjC.Sel("restoreGraphicsState"));

                    var dictionary = ObjC.Send(ObjC.Class("NSDictionary"), ObjC.Sel("dictionary"));
                    var data = Represent(rep, ObjC.Sel("representationUsingType:properties:"), PngFileType, dictionary);
                    if (data == 0)
                    {
                        return null;
                    }
                    var length = (int)ObjC.Send(data, ObjC.Sel("length"));
                    var bytes = ObjC.Send(data, ObjC.Sel("bytes"));
                    if (length <= 0 || bytes == 0)
                    {
                        return null;
                    }
                    var result = new byte[length];
                    Marshal.Copy(bytes, result, 0, length);
                    return result;
                }
                finally
                {
                    ObjC.Send(rep, ObjC.Sel("release"));
                }
            }
            finally
            {
                ObjC.Send(image, ObjC.Sel("release"));
            }
        }
        finally
        {
            ObjC.Send(pool, ObjC.Sel("drain"));
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct Rect(double X, double Y, double Width, double Height);

    private static nint NSString(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    private const string Lib = "/usr/lib/libobjc.A.dylib";

    // BOOL is one byte on both Apple architectures.
    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial nint InitBitmap(nint receiver, nint selector, nint planes, nint wide, nint high, nint bitsPerSample,
        nint samplesPerPixel, byte hasAlpha, byte isPlanar, nint colorSpace, nint bytesPerRow, nint bitsPerPixel);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial void DrawInRect(nint receiver, nint selector, Rect rect);

    [LibraryImport(Lib, EntryPoint = "objc_msgSend")]
    private static partial nint Represent(nint receiver, nint selector, nuint type, nint properties);
}
