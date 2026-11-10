namespace Island.Platform;

/// <summary>
/// The desktop wallpaper of the main screen, for "从壁纸取色" (docs/design/05-builtin-plugins.md §3): the image bytes in a
/// format Avalonia decodes (PNG / JPEG / BMP). Read-only; the wallpaper is never changed.
/// </summary>
public interface IWallpaperSource
{
    /// <summary>The wallpaper image, or null when there is none (a solid colour, a dynamic wallpaper we cannot read…).</summary>
    byte[]? ReadImage();
}

/// <summary>Reads the wallpaper from a file path (Windows, Linux); the platform supplies the path.</summary>
public sealed class FileWallpaperSource(Func<string?> path) : IWallpaperSource
{
    private const long MaxBytes = 64L * 1024 * 1024;

    public byte[]? ReadImage()
    {
        try
        {
            return path() is { } file && File.Exists(file) && new FileInfo(file).Length <= MaxBytes ? File.ReadAllBytes(file) : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
