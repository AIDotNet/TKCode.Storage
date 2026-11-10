using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Island.App.Settings;
using Island.Core.Layout;
using Island.Core.State;
using Island.Platform;
using Island.Plugins.Host;
using Island.UI;
using Island.UI.Plugins;
using Island.UI.Themes;

namespace Island.App;

/// <summary>
/// The application: one island window whose content comes from plugins. The console window
/// (docs/design/04-console.md) comes in M3.
/// </summary>
public class App(IIslandPlatform platform) : Application
{
    private static readonly bool Debug = Environment.GetEnvironmentVariable("O_O_DEBUG") == "1";

    private IslandController? _island;
    private PluginHost? _plugins;
    private Island.UI.Console.ConsoleWindow? _console;
    private Island.UI.Services.HotkeyService? _hotkeys;

    /// <summary>The island's shape, size and position, shared by the island and the console's 岛体 page.</summary>
    public static IslandAppearanceStore Appearance { get; } =
        new(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "island.json"));

    /// <summary>Which pages the expanded island shows, in what order, and which is home.</summary>
    public static Island.Core.Pages.PagePreferencesStore Pages { get; } =
        new(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "pages.json"));

    /// <summary>A page as its plugin's manifest declares it (title, icon, on by default or not).</summary>
    public static Island.Core.Pages.PageInfo DescribePage(PluginHost plugins, string pluginId, string pageId, string title)
    {
        var declared = plugins.Packages.FirstOrDefault(p => p.Id == pluginId)?.Manifest.Contributes.Pages
            .FirstOrDefault(p => p.Id == pageId);
        return new(Island.Plugins.PageSnapshot.PageKey(pluginId, pageId), declared?.Title ?? title, declared?.Enabled ?? true, declared?.Icon);
    }

    /// <summary>What the collapsed island shows: which chips, in what order, on which side.</summary>
    public static Island.Core.Pages.ChipPreferencesStore Chips { get; } =
        new(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "chips.json"));

    /// <summary>A chip as its plugin's manifest declares it.</summary>
    public static Island.Core.Pages.ChipInfo DescribeChip(PluginHost plugins, Island.Plugins.ChipSnapshot s)
    {
        var declared = plugins.Packages.FirstOrDefault(p => p.Id == s.PluginId)?.Manifest.Contributes.Chips
            .FirstOrDefault(c => c.Id == s.ChipId);
        return new(s.Key, s.Title, declared?.Enabled ?? true,
            declared?.Side == "left" ? Island.Core.Pages.ChipSide.Left : Island.Core.Pages.ChipSide.Right);
    }

    public static string DataDirectory => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "plugin-data");

    // The XAML previewer needs a parameterless constructor.
    public App() : this(new NullIslandPlatform())
    {
    }

    /// <summary>User plugins first (they override bundled ones with the same id), then the bundled ones.</summary>
    public static IReadOnlyList<string> PluginDirectories { get; } =
    [
        Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "plugins"),
        Path.Combine(AppContext.BaseDirectory, "plugins"),
    ];

    /// <summary>Opens the console, or brings it to the front when it is already open.</summary>
    public void OpenConsole()
    {
        if (_plugins is null)
        {
            return;
        }
        if (_console is { IsVisible: true })
        {
            _console.Activate();
            return;
        }
        _console = new Island.UI.Console.ConsoleWindow(_plugins, DataDirectory, Appearance, Pages, Chips) { Hotkeys = _hotkeys };
        _console.Closed += (_, _) => _console = null;
        _console.Show();
    }

    /// <summary>The native clipboard support for this OS (docs 05 §8).</summary>
    private static Island.Platform.IClipboardPlatform ClipboardPlatform() =>
        OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacClipboard()
        : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsClipboard()
        : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxClipboard()
        : new Island.Platform.NullClipboardPlatform();

    public override void Initialize() => Resources.MergedDictionaries.Add(new IslandResources());

    public override void OnFrameworkInitializationCompleted()
    {
        Island.Core.StartupTrace.Mark("framework initialised");
        AppSettings.Current.TrackTheme();
        AppSettings.Current.TrackMascot();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var metrics = new IslandMetrics();
            var window = new IslandWindow(metrics);
            _island = new IslandController(window, platform, metrics, new HoverOptions()) { Appearance = Appearance.Current };
            Appearance.Changed += a => Dispatcher.UIThread.Post(() => _island.Appearance = a);
            if (Enum.TryParse<IslandPlacement>(Environment.GetEnvironmentVariable("O_O_PLACEMENT"), true, out var placement))
            {
                _island.PlacementOverride = placement; // development aid
            }

            _plugins = new PluginHost(new PluginServices(
                DataDirectory: DataDirectory,
                Secrets: platform.Secrets)
            {
                Clipboard = new Island.UI.Services.ClipboardService(window, ClipboardPlatform(),
                    Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "clipboard-images")),
                UserPluginDirectory = PluginDirectories[0],
                // Music (docs 05 §1). MacMedia is created here, on the main thread, where its notifications arrive.
                Media = new Island.Plugins.Host.Services.PlatformMedia(
                    OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacMedia()
                    : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsMedia()
                    : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxMedia()
                    : new Island.Platform.NullMediaPlatform()),
            });
            var plugins = _plugins;
            var presenter = _island.CreatePresenter(
                (plugin, handler, payload) => plugins.Dispatch(plugin, handler, payload),
                Pages,
                s => DescribePage(plugins, s.PluginId, s.PageId, s.Title),
                Chips,
                s => DescribeChip(plugins, s));
            _plugins.ActivityUpdated += s => IslandPresenter.Post(() => presenter.Update(s));
            _plugins.ActivityEnded += (p, a) => IslandPresenter.Post(() => presenter.End(p, a));
            _plugins.PageUpdated += s => IslandPresenter.Post(() => presenter.UpdatePage(s));
            _plugins.PageEnded += (p, g) => IslandPresenter.Post(() => presenter.EndPage(p, g));
            _plugins.ChipUpdated += s => IslandPresenter.Post(() => presenter.UpdateChip(s));
            _plugins.AlertRaised += a => IslandPresenter.Post(() => presenter.ShowAlert(a));
            // Files dropped on the island go to the file shelf (docs 05 §7).
            IslandDropTarget.Attach(window.View, () => plugins.AcceptsFiles, plugins.DropFiles);
            _plugins.PluginLog += e =>
            {
                if (Debug || e.Level >= PluginLogLevel.Warn)
                {
                    Console.Error.WriteLine($"[{e.PluginId}] {e.Level}: {e.Message}");
                }
            };
            _plugins.PluginStateChanged += (id, state, reason) =>
                Console.Error.WriteLine($"[{id}] {state}{(reason is null ? "" : ": " + reason)}");
            for (var i = 0; i < PluginDirectories.Count; i++)
            {
                // The last directory is the one shipped next to the executable: first-party, granted what it declares.
                var bundled = i == PluginDirectories.Count - 1;
                foreach (var problem in _plugins.Discover(PluginDirectories[i], bundled))
                {
                    Console.Error.WriteLine($"[plugins] skipped {problem}");
                }
            }

            if (Debug)
            {
                Console.Error.WriteLine($"[plugins] found {string.Join(", ", _plugins.Packages.Select(p => p.Id))} in " +
                                        string.Join(" | ", PluginDirectories));
            }

            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            desktop.Exit += (_, _) =>
            {
                _plugins.Dispose();
                _hotkeys?.Dispose();
                _island.Dispose();
            };
            Island.Core.StartupTrace.Mark("plugins discovered");
            _island.SettingsRequested += OpenConsole;
            // Tray icon, hand-off from a second launch, full-screen detection (ShellIntegration.cs).
            ShellIntegration.Install(this, _island, OpenConsole, () => desktop.Shutdown());
            _hotkeys = ShellIntegration.InstallHotkeys(_island, OpenConsole, Path.GetDirectoryName(AppSettings.DefaultPath)!);
            _island.Show();
            Island.Core.StartupTrace.Mark("island shown");
            _plugins.StartAll();
            if (desktop.Args?.Contains("--console") == true)
            {
                OpenConsole();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
