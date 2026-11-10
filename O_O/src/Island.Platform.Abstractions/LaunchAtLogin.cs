#pragma warning disable CA1416 // registry calls only run behind OperatingSystem.IsWindows() in WindowsRun
using System.Security;

namespace Island.Platform;

/// <summary>
/// Starting O_O when the user logs in: a LaunchAgent on macOS, the per-user <c>Run</c> registry key on Windows, an XDG
/// autostart entry on Linux. Nothing here needs administrator rights, and the entry points at the running executable.
/// </summary>
public static class LaunchAtLogin
{
    private const string Label = "com.o_o.island";

    /// <summary>Whether the running build can register itself (a <c>dotnet run</c> session cannot: its executable is the SDK host).</summary>
    public static bool IsAvailable =>
        ExecutablePath is { } path && !Path.GetFileNameWithoutExtension(path).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
        && (OperatingSystem.IsMacOS() || OperatingSystem.IsWindows() || OperatingSystem.IsLinux());

    private static string? ExecutablePath => Environment.ProcessPath;

    private static string MacPlist => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Library", "LaunchAgents", Label + ".plist");

    private static string LinuxDesktop
    {
        get
        {
            var config = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg
                ? xdg
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(config, "autostart", "O_O.desktop");
        }
    }

    /// <summary>Whether an entry exists for this user.</summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                if (OperatingSystem.IsMacOS()) return File.Exists(MacPlist);
                if (OperatingSystem.IsLinux()) return File.Exists(LinuxDesktop);
                if (OperatingSystem.IsWindows()) return WindowsRun(static key => key.GetValue("O_O") is not null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
            {
            }
            return false;
        }
    }

    /// <summary>Adds or removes the entry; returns false (and changes nothing) when that fails.</summary>
    public static bool TrySet(bool enabled)
    {
        try
        {
            if (enabled && !IsAvailable) return false;
            var exe = ExecutablePath;
            if (OperatingSystem.IsMacOS()) SetFile(MacPlist, enabled, () => MacContent(exe!));
            else if (OperatingSystem.IsLinux()) SetFile(LinuxDesktop, enabled, () => LinuxContent(exe!));
            else if (OperatingSystem.IsWindows()) WindowsRun(key =>
            {
                if (enabled) key.SetValue("O_O", $"\"{exe}\"");
                else key.DeleteValue("O_O", throwOnMissingValue: false);
                return true;
            });
            else return false;
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SecurityException)
        {
            return false;
        }
    }

    private static void SetFile(string path, bool enabled, Func<string> content)
    {
        if (enabled)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content());
        }
        else if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string MacContent(string exe) => $"""
        <?xml version="1.0" encoding="UTF-8"?>
        <!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
        <plist version="1.0">
        <dict>
            <key>Label</key><string>{Label}</string>
            <key>ProgramArguments</key><array><string>{SecurityElement.Escape(exe)}</string></array>
            <key>RunAtLoad</key><true/>
            <key>ProcessType</key><string>Interactive</string>
        </dict>
        </plist>

        """;

    private static string LinuxContent(string exe) => $"""
        [Desktop Entry]
        Type=Application
        Name=O_O
        Exec="{exe.Replace("\\", "\\\\").Replace("\"", "\\\"")}"
        X-GNOME-Autostart-enabled=true

        """;

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static bool WindowsRunCore(Func<Microsoft.Win32.RegistryKey, bool> use)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run");
        return use(key);
    }

    private static bool WindowsRun(Func<Microsoft.Win32.RegistryKey, bool> use) =>
        OperatingSystem.IsWindows() && WindowsRunCore(use);
}
