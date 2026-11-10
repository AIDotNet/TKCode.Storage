using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Island.Platform;
using Island.UI;
using TKCode.Arc;

namespace Island.App;

/// <summary>
/// What ties O_O to the desktop beyond the island window (docs/design/06-roadmap.md M1): the tray / menu bar icon
/// with the island's commands, the hand-off from a second launch, and full-screen detection for the platform.
/// </summary>
internal static class ShellIntegration
{
    // The tray icon is drawn from the mascot at runtime, like the app icon (Tools/IconRenderer.cs), so it always
    // matches the island; at 2x for sharp menu bars.
    private const double IconSize = 22;
    private const int IconScale = 2;

    public static void Install(Application app, IslandController island, Action openConsole, Action quit)
    {
        island.FullScreenDetector = CreateFullScreenDetector();
        Program.SecondLaunch += () => Dispatcher.UIThread.Post(() =>
        {
            if (Environment.GetEnvironmentVariable("O_O_DEBUG") == "1")
            {
                Console.Error.WriteLine("[O_O] another launch asked for the console");
            }
            openConsole();
        });

        var pin = new NativeMenuItem("保持展开") { ToggleType = MenuItemToggleType.CheckBox };
        pin.Click += (_, _) => island.Pinned = !island.Pinned;
        var console = new NativeMenuItem("打开控制台");
        console.Click += (_, _) => openConsole();
        var exit = new NativeMenuItem("退出 O_O");
        exit.Click += (_, _) => quit();
        var menu = new NativeMenu { console, pin, new NativeMenuItemSeparator(), exit };
        menu.Opening += (_, _) => pin.IsChecked = island.Pinned;

        var tray = new TrayIcon { ToolTipText = "O_O", Menu = menu, IsVisible = true };
        try
        {
            tray.Icon = RenderIcon();
        }
        catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
        {
            System.Diagnostics.Trace.TraceWarning($"O_O: tray icon not drawn: {e.Message}");
        }
        // The icon follows the mascot the user picks.
        Island.UI.MascotStyle.Changed += (_, _) => Dispatcher.UIThread.Post(() =>
        {
            try
            {
                tray.Icon = RenderIcon();
            }
            catch (Exception e) when (e is InvalidOperationException or NotSupportedException)
            {
                System.Diagnostics.Trace.TraceWarning($"O_O: tray icon not drawn: {e.Message}");
            }
        });
        // Windows and Linux open the menu on right-click; a left click opens the console, as apps there do.
        tray.Clicked += (_, _) =>
        {
            if (!OperatingSystem.IsMacOS())
            {
                openConsole();
            }
        };
        TrayIcon.SetIcons(app, [tray]);
    }

    /// <summary>
    /// Global hotkeys (docs 04, 快捷键): toggle the island, open it on the clipboard history, open the console. A
    /// platform that cannot do them gets a service that says so in the console instead of failing.
    /// </summary>
    public static Island.UI.Services.HotkeyService InstallHotkeys(IslandController island, Action openConsole, string dataDirectory)
    {
        var store = new Island.Core.Input.HotkeyStore(Path.Combine(dataDirectory, "hotkeys.json"), OperatingSystem.IsMacOS());
        IGlobalHotkeys platform;
        try
        {
            platform = OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacGlobalHotkeys()
                : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsGlobalHotkeys()
                : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxGlobalHotkeys()
                : new NullGlobalHotkeys();
        }
        catch (Exception e) when (e is InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            System.Diagnostics.Trace.TraceWarning($"O_O: global hotkeys unavailable: {e.Message}");
            platform = new NullGlobalHotkeys();
        }
        return new Island.UI.Services.HotkeyService(store, platform, new Dictionary<string, Action>
        {
            [Island.Core.Input.HotkeyActions.ToggleIsland] = island.ToggleFromHotkey,
            [Island.Core.Input.HotkeyActions.OpenClipboard] = () => island.OpenFromHotkey("com.o_o.clipboard/history"),
            [Island.Core.Input.HotkeyActions.OpenConsole] = openConsole,
        });
    }

    public static IFullScreenDetector CreateFullScreenDetector()
    {
        if (Environment.GetEnvironmentVariable("O_O_FAKE_FULLSCREEN") == "1")
        {
            return new AlwaysFullScreen(); // development aid: exercise the hiding without a full-screen app
        }
        if (OperatingSystem.IsMacOS())
        {
            return new Island.Platform.MacOS.MacFullScreenDetector();
        }
        if (OperatingSystem.IsWindows())
        {
            return new Island.Platform.Windows.WindowsFullScreenDetector();
        }
        if (OperatingSystem.IsLinux())
        {
            return new Island.Platform.Linux.LinuxFullScreenDetector();
        }
        return new NullFullScreenDetector();
    }

    /// <summary>The mascot, alone on a transparent square, as a window icon.</summary>
    private static WindowIcon RenderIcon()
    {
        // The look the user picked (About page), at its smallest drawing, scaled into the icon.
        var mascot = Island.UI.MascotStyle.Create(24);
        mascot.HorizontalAlignment = HorizontalAlignment.Center;
        mascot.VerticalAlignment = VerticalAlignment.Center;
        mascot.RenderTransform = new ScaleTransform(IconSize / 24, IconSize / 24);
        var root = new Panel { Width = IconSize, Height = IconSize, Background = Brushes.Transparent, Children = { mascot } };
        // Styles and templates apply once the control is in a top level; this window is never shown.
        var host = new ArcWindow { Content = root, SizeToContent = SizeToContent.WidthAndHeight, ShowInTaskbar = false };
        host.Measure(Size.Infinity);
        host.Arrange(new Rect(host.DesiredSize));
        Dispatcher.UIThread.RunJobs();
        root.Measure(new Size(IconSize, IconSize));
        root.Arrange(new Rect(0, 0, IconSize, IconSize));
        Dispatcher.UIThread.RunJobs();
        var px = (int)IconSize * IconScale;
        using var bitmap = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96 * IconScale, 96 * IconScale));
        bitmap.Render(root);
        using var stream = new MemoryStream();
        bitmap.Save(stream, PngBitmapEncoderOptions.Default);
        if (Environment.GetEnvironmentVariable("O_O_DEBUG_TRAY_PNG") is { Length: > 0 } png)
        {
            File.WriteAllBytes(png, stream.ToArray()); // development aid: the icon as drawn
        }
        stream.Position = 0;
        host.Content = null;
        return new WindowIcon(stream);
    }

    private sealed class AlwaysFullScreen : IFullScreenDetector
    {
        public bool IsFullScreen(Island.Core.Geometry.RectD screen, double scale) => true;
    }
}
