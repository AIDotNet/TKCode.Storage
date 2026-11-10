using System.Runtime.InteropServices;
using Island.Core.Geometry;

namespace Island.Platform.Windows;

/// <summary>
/// Windows: the foreground window belongs to another process, is not the desktop or the shell, and its rectangle
/// equals the whole monitor it is on (taskbar included) — what full-screen video, games and slide shows do.
/// </summary>
public sealed partial class WindowsFullScreenDetector : IFullScreenDetector
{
    private const uint MonitorDefaultToNearest = 2;
    private static readonly uint OwnPid = (uint)Environment.ProcessId;

    public bool IsFullScreen(RectD screen, double scale)
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == 0 || hwnd == GetDesktopWindow() || hwnd == GetShellWindow())
        {
            return false;
        }
        GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == OwnPid)
        {
            return false;
        }
        string name;
        unsafe
        {
            var cls = stackalloc char[64];
            var length = GetClassNameW(hwnd, cls, 64);
            name = new string(cls, 0, Math.Max(0, length));
        }
        if (name is "WorkerW" or "Progman" or "Shell_TrayWnd")
        {
            return false;
        }
        if (!GetWindowRect(hwnd, out var r))
        {
            return false;
        }
        var info = new MONITORINFO { Size = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(MonitorFromWindow(hwnd, MonitorDefaultToNearest), ref info))
        {
            return false;
        }
        var m = info.Monitor;
        // Only the island's screen matters: compare the monitor with it, in pixels.
        var sameScreen = Math.Abs(m.Left - screen.X * scale) < 2 && Math.Abs(m.Top - screen.Y * scale) < 2;
        return sameScreen && r.Left <= m.Left && r.Top <= m.Top && r.Right >= m.Right && r.Bottom >= m.Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int Size; public RECT Monitor; public RECT Work; public uint Flags; }

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetDesktopWindow();

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint pid);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    private static unsafe partial int GetClassNameW(nint hwnd, char* name, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out RECT rect);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MONITORINFO info);
}
