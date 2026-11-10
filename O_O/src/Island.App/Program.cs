using Avalonia;
using Island.Platform;
using Island.Platform.Linux;
using Island.Platform.MacOS;
using Island.Platform.Windows;

namespace Island.App;

internal static class Program
{
    // Avalonia is not ready until Main runs: do not use any Avalonia or Arc API before BuildAvaloniaApp().
    [STAThread]
    public static void Main(string[] args)
    {
        Island.Core.StartupTrace.Mark("main");
        if (OperatingSystem.IsLinux())
        {
            ForceX11();
        }
        // Plugin authors: checks a plugin folder like installing would and packs it into an installable .zip.
        if (args is ["--pack-plugin", var pluginDir, ..])
        {
            var dir = Path.GetFullPath(pluginDir);
            var problems = Island.Plugins.Host.PluginPackages.Validate(dir, out var manifest);
            var zip = Path.GetFullPath(args.ElementAtOrDefault(2) ?? $"{manifest?.Id ?? Path.GetFileName(dir)}-{manifest?.Version ?? "0"}.zip");
            if (problems.Count == 0)
            {
                problems = Island.Plugins.Host.PluginPackages.Pack(dir, zip);
            }
            if (problems.Count > 0)
            {
                Console.Error.WriteLine("插件有问题，未打包：");
                problems.ForEach(p => Console.Error.WriteLine("  - " + p));
                Environment.Exit(1);
            }
            Console.WriteLine($"已打包 {manifest!.Name} {manifest.Version} → {zip}");
            Environment.Exit(0);
        }

        if (args is ["--render-icon", var output])
        {
            // Build-time tool: draws the app icon from the mascot (Tools/IconRenderer.cs) and exits.
            BuildAvaloniaApp().SetupWithoutStarting();
            Environment.Exit(Tools.IconRenderer.Run(output));
        }
        if (args is ["--render-island", var islandPng, ..])
        {
            // Development tool: renders the island with the bundled plugins running, offscreen, and exits.
            BuildAvaloniaApp().SetupWithoutStarting();
            TKCode.Arc.ArcTheme.Apply(t => t.MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
            var data = Directory.CreateTempSubdirectory("o_o-render-").FullName;
            using (var host = new Island.Plugins.Host.PluginHost(new Island.Plugins.Host.PluginServices(data, new InMemorySecretStore())))
            {
                for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
                {
                    if (Directory.Exists(Path.Combine(dir.FullName, "samples", "plugins")))
                    {
                        host.Discover(Path.Combine(dir.FullName, "samples", "plugins")); // sample plugins, as user plugins
                        break;
                    }
                }
                host.Discover(App.BundledPluginDirectory, bundled: true);
                host.StartAll();
                if (Environment.GetEnvironmentVariable("O_O_RENDER_DROP") is { Length: > 0 } drop)
                {
                    // Development aid: files dropped on the island (the file shelf) before the picture is taken.
                    for (var i = 0; i < 40 && !host.AcceptsFiles; i++)
                    {
                        Thread.Sleep(100);
                    }
                    host.DropFiles(drop.Split(Path.PathSeparator));
                }
                var state = args.ElementAtOrDefault(2) == "compact" ? Island.Core.State.IslandState.Compact : Island.Core.State.IslandState.Expanded;
                Island.UI.IslandSnapshot.Render(host, islandPng, state, TimeSpan.FromSeconds(3), attached: args.Contains("attached"));
            }
            Directory.Delete(data, recursive: true);
            Console.WriteLine($"Rendered island to {Path.GetFullPath(islandPng)}");
            Environment.Exit(0);
        }
        if (args is ["--render-console", var png, ..] )
        {
            // Development tool: renders the console offscreen (Tools/ConsoleRenderer.cs) and exits.
            BuildAvaloniaApp().SetupWithoutStarting();
            Environment.Exit(Tools.ConsoleRenderer.Run(png, args.ElementAtOrDefault(2) ?? "plugins", args.ElementAtOrDefault(3) ?? "light"));
        }
        if (args is ["--probe-fullscreen", var x, var y, var w, var h])
        {
            // Development tool: is another app's frontmost window covering this rectangle (DIPs)? Prints and exits.
            var rect = new Island.Core.Geometry.RectD(double.Parse(x), double.Parse(y), double.Parse(w), double.Parse(h));
            Console.WriteLine(ShellIntegration.CreateFullScreenDetector().IsFullScreen(rect, 1) ? "fullscreen" : "not fullscreen");
            return;
        }
        // One O_O per user: a second launch brings the running one forward (its console) and exits.
        var dataDirectory = Path.GetDirectoryName(Settings.AppSettings.DefaultPath)!;
        using var instance = SingleInstance.TryAcquire(dataDirectory);
        if (instance is null)
        {
            if (!SingleInstance.ActivateRunning())
            {
                Console.Error.WriteLine("O_O is already running but did not answer.");
            }
            return;
        }
        instance.Listen(() => SecondLaunch?.Invoke());
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    /// <summary>Raised on a background thread when another launch asked this instance to come forward.</summary>
    public static event Action? SecondLaunch;

    // Also used by the XAML previewer of your IDE. UseArcApp() (AppSetup.cs) sets up the fonts, Arc and the theme.
    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure(() => new App(CreatePlatform()))
                  .UsePlatformDetect()
                  // The island is an accessory: no Dock icon, no menu bar of its own.
                  .With(new MacOSPlatformOptions { ShowInDock = false })
                  // The island and console are small; Skia's default GPU resource cache (sized for full-screen apps)
                  // only keeps glyph atlases and old layer textures around.
                  .With(new SkiaOptions { MaxGpuResourceSizeBytes = 24 * 1024 * 1024 })
                  .UseArcApp()
                  .LogToTrace();

    private static IIslandPlatform CreatePlatform()
    {
        if (OperatingSystem.IsMacOS())
        {
            return new MacIslandPlatform();
        }
        if (OperatingSystem.IsWindows())
        {
            return new WindowsIslandPlatform();
        }
        if (OperatingSystem.IsLinux())
        {
            return new LinuxIslandPlatform();
        }
        return new NullIslandPlatform();
    }

    /// <summary>
    /// Wayland does not let a client place, stack or shape its own windows, so on a Wayland session the island runs
    /// through XWayland (decided in docs/design/06-roadmap.md §3).
    /// </summary>
    private static void ForceX11()
    {
        if (Environment.GetEnvironmentVariable("WAYLAND_DISPLAY") is null)
        {
            return;
        }
        if (Environment.GetEnvironmentVariable("DISPLAY") is null)
        {
            Console.Error.WriteLine("O_O needs XWayland on Wayland sessions, and DISPLAY is not set. Install or enable XWayland.");
            Environment.Exit(1);
        }
        Environment.SetEnvironmentVariable("WAYLAND_DISPLAY", null);
    }
}
