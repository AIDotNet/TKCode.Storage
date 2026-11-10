using System.Runtime.InteropServices;

namespace Island.Platform.Windows;

/// <summary>Windows: the wallpaper file from <c>SystemParametersInfo(SPI_GETDESKWALLPAPER)</c>.</summary>
public static partial class WindowsWallpaper
{
    private const uint SpiGetDeskWallpaper = 0x0073;
    private const int MaxPath = 260;

    public static IWallpaperSource Create() => new FileWallpaperSource(Path);

    private static unsafe string? Path()
    {
        var buffer = stackalloc char[MaxPath + 1];
        return SystemParametersInfoW(SpiGetDeskWallpaper, MaxPath, buffer, 0) && buffer[0] != '\0'
            ? new string(buffer)
            : null;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool SystemParametersInfoW(uint action, uint param, char* value, uint winIni);
}
