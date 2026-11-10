using System.Diagnostics;

namespace Island.Platform.Linux;

/// <summary>
/// Linux (GNOME and derivatives): the wallpaper from <c>gsettings get org.gnome.desktop.background picture-uri</c>
/// (or <c>picture-uri-dark</c> when that one is set). Other desktops: none.
/// </summary>
public static class LinuxWallpaper
{
    public static IWallpaperSource Create() => new FileWallpaperSource(Path);

    private static string? Path()
    {
        foreach (var key in new[] { "picture-uri-dark", "picture-uri" })
        {
            if (Gsettings(key) is { } value && FromUri(value) is { } file && File.Exists(file))
            {
                return file;
            }
        }
        return null;
    }

    private static string? FromUri(string value)
    {
        var v = value.Trim().Trim('\'', '"');
        return Uri.TryCreate(v, UriKind.Absolute, out var uri) && uri.IsFile ? uri.LocalPath
            : System.IO.Path.IsPathFullyQualified(v) ? v
            : null;
    }

    private static string? Gsettings(string key)
    {
        try
        {
            var info = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, UseShellExecute = false };
            info.ArgumentList.Add("get");
            info.ArgumentList.Add("org.gnome.desktop.background");
            info.ArgumentList.Add(key);
            using var process = Process.Start(info);
            if (process is null)
            {
                return null;
            }
            var output = process.StandardOutput.ReadToEnd();
            return process.WaitForExit(2000) && process.ExitCode == 0 ? output : null;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return null; // no gsettings: not GNOME
        }
    }
}
