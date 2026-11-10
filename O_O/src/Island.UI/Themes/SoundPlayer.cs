using System.Diagnostics;

namespace Island.UI.Themes;

/// <summary>
/// Plays a short sound file with the system's own player (docs/design/09-island-v2.md §4, a theme's alert sound):
/// afplay on macOS, PowerShell's SoundPlayer on Windows (wav), paplay or aplay on Linux. Fire and forget; a missing
/// player means no sound.
/// </summary>
public static class SoundPlayer
{
    public static void Play(string path)
    {
        try
        {
            ProcessStartInfo info;
            if (OperatingSystem.IsMacOS())
            {
                info = new ProcessStartInfo("/usr/bin/afplay");
                info.ArgumentList.Add(path);
            }
            else if (OperatingSystem.IsWindows())
            {
                info = new ProcessStartInfo("powershell.exe");
                info.ArgumentList.Add("-NoProfile");
                info.ArgumentList.Add("-Command");
                info.ArgumentList.Add($"(New-Object Media.SoundPlayer '{path.Replace("'", "''", StringComparison.Ordinal)}').PlaySync()");
            }
            else
            {
                info = new ProcessStartInfo(File.Exists("/usr/bin/paplay") ? "/usr/bin/paplay" : "aplay");
                info.ArgumentList.Add(path);
            }
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            Process.Start(info)?.Dispose();
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // No player here: the alert shows without a sound.
        }
    }
}
