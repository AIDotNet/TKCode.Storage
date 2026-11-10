using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Island.Platform;
using Island.Plugins.Host;
using Island.UI.Console;
using TKCode.Arc;

namespace Island.App.Tools;

/// <summary>
/// <c>O_O --render-console &lt;png&gt; [plugins|appearance|about] [light|dark]</c>: renders the console offscreen, with the
/// bundled plugins running and the repository's sample plugins installed as user plugins (so the consent flow shows).
/// Uses a throwaway data directory and in-memory secrets. For reviewing the UI where screen capture is unavailable.
/// </summary>
internal static class ConsoleRenderer
{
    public static int Run(string output, string page, string appearance)
    {
        var h = double.TryParse(Environment.GetEnvironmentVariable("O_O_RENDER_HEIGHT"), out var hh) ? hh : 680;
        // Motion off: a still frame shows each control's final state rather than an entrance mid-flight.
        ArcTheme.Apply(t => t
            .Appearance(appearance == "dark" ? ArcAppearance.Dark : ArcAppearance.Light)
            .MotionLevel(TKCode.Arc.Motion.ArcMotionLevel.Off));
        var data = Directory.CreateTempSubdirectory("o_o-render-").FullName;
        // The real credential store, so the connections page shows this machine's logins; discovery only checks that an
        // item exists and never reads one.
        ISecretStore secrets = OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacKeychainSecretStore() : new InMemorySecretStore();
        using var host = new PluginHost(new PluginServices(data, secrets));
        if (FindSamples() is { } samples)
        {
            host.Discover(samples);
        }
        host.Discover(App.BundledPluginDirectory, bundled: true);
        host.StartAll();
        // Themes for the 外观 page, with throwaway preferences and every bundled theme pack offered.
        foreach (var themePack in host.Packages.Where(p => p.Manifest.Contributes.Themes.Count > 0 && !host.IsEnabled(p.Id)))
        {
            host.SetEnabled(themePack.Id, true);
        }
        Island.UI.Themes.ThemeService.Start(host, new Island.Core.Themes.ThemePreferencesStore(Path.Combine(data, "theme.json")), null);
        Pump(TimeSpan.FromSeconds(2)); // let the plugins start and report their states

        var view = new ConsoleView(host, data)
        {
            // The 快捷键 page with the defaults, registering nothing on this machine.
            Hotkeys = new Island.UI.Services.HotkeyService(
                new Island.Core.Input.HotkeyStore(Path.Combine(data, "hotkeys.json"), OperatingSystem.IsMacOS()),
                new Island.Platform.NullGlobalHotkeys(), new Dictionary<string, Action>()),
        };
        if (page == "market")
        {
            view.ShowPlugin("+add"); // 插件市场
        }
        else
        {
            view.ShowPage(page);
        }

        Control root = view;
        if (Environment.GetEnvironmentVariable("O_O_RENDER_GLASS") == "1")
        {
            // Stand-in for the system material, which offscreen rendering cannot capture: a colourful backdrop shows
            // how much of it each translucent layer lets through.
            view.Glass = true;
            root = new Panel
            {
                Background = new Avalonia.Media.LinearGradientBrush
                {
                    StartPoint = new Avalonia.RelativePoint(0, 0, Avalonia.RelativeUnit.Relative),
                    EndPoint = new Avalonia.RelativePoint(1, 1, Avalonia.RelativeUnit.Relative),
                    GradientStops = { new(Avalonia.Media.Color.Parse("#7B9CFF"), 0), new(Avalonia.Media.Color.Parse("#F2A6C8"), 1) },
                },
                Children = { view },
            };
        }
        var window = new ArcWindow { Content = root, Width = 1000, Height = h, IsTitleBarVisible = false };
        window.Measure(new Size(1000, h));
        window.Arrange(new Rect(0, 0, 1000, h));
        Pump(TimeSpan.FromMilliseconds(600)); // first layout, then let entrance motion settle
        // A plugin's drawer, or "+add" for the 添加插件 dialog: overlays live in the window's overlay layer, so they
        // open once the view is in the window, and the window itself is rendered.
        var overlay = Environment.GetEnvironmentVariable("O_O_RENDER_PLUGIN") is { Length: > 0 } plugin;
        if (overlay)
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = new PixelPoint(-20000, -20000);
            window.ShowInTaskbar = false;
            window.ShowActivated = false;
            window.Show();
            Pump(TimeSpan.FromMilliseconds(400));
            view.ShowPlugin(Environment.GetEnvironmentVariable("O_O_RENDER_PLUGIN")!);
            Pump(TimeSpan.FromMilliseconds(900));
            window.Measure(new Size(1000, h));
            window.Arrange(new Rect(0, 0, 1000, h));
        }
        root.Measure(new Size(1000, h));
        root.Arrange(new Rect(0, 0, 1000, h));
        Pump(TimeSpan.FromMilliseconds(600));

        // O_O_RENDER_SCALE=2 for a sharper image; some Arc composition layers are scaled twice offscreen at 2x.
        var scale = double.TryParse(Environment.GetEnvironmentVariable("O_O_RENDER_SCALE"), out var s) ? s : 1;
        using var bitmap = new RenderTargetBitmap(
            new PixelSize((int)(1000 * scale), (int)(h * scale)), new Vector(96 * scale, 96 * scale));
        bitmap.Render(overlay ? window : root);
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        window.Close();
        Directory.Delete(data, recursive: true);
        Console.WriteLine($"Rendered {page} ({appearance}) to {Path.GetFullPath(output)}");
        return 0;
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }

    private static string? FindSamples()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "samples", "plugins");
            if (Directory.Exists(candidate))
            {
                return candidate;
            }
        }
        return null;
    }
}
