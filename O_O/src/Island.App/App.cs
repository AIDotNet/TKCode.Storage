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
    private SatelliteHost? _satellites;

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
    /// <summary>⌘-click on an activity (09 §2.2): its link in the browser, or its application brought forward or launched.</summary>
    private static void OpenSource(PluginHost plugins, Island.Plugins.LiveActivityOpen open)
    {
        if (open.Url is { } url && Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http")
        {
            _ = plugins.Services.OpenBrowser(uri);
            return;
        }
        if (open.App is not { } app || plugins.Services.NotificationListener?.Activate(app) == true)
        {
            return;
        }
        try
        {
            if (OperatingSystem.IsMacOS())
            {
                System.Diagnostics.Process.Start("/usr/bin/open", ["-b", app])?.Dispose();
            }
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // Not installed, or not an application id: nothing to open.
        }
    }

    public static Island.Core.Pages.PageInfo DescribePage(PluginHost plugins, string pluginId, string pageId, string title)
    {
        var declared = plugins.Packages.FirstOrDefault(p => p.Id == pluginId)?.Manifest.Contributes.Home
            .FirstOrDefault(p => p.Id == pageId);
        return new(Island.Plugins.PageSnapshot.PageKey(pluginId, pageId), declared?.Title ?? title, declared?.Enabled ?? true, declared?.Icon);
    }

    /// <summary>What the collapsed island shows: which chips, in what order, on which side.</summary>
    /// <summary>The user's say over activities: order, policies, pinned (岛体 → 活动).</summary>
    public static Island.Core.Activities.ActivityPreferencesStore Activities { get; } =
        new(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "activities.json"));

    public static Island.Core.Pages.ChipPreferencesStore Chips { get; } =
        new(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "chips.json"));

    public static string DataDirectory => Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "plugin-data");

    // The XAML previewer needs a parameterless constructor.
    public App() : this(new NullIslandPlatform())
    {
    }

    /// <summary>
    /// The first-party plugins shipped with O_O: next to the executable, or — inside a macOS app bundle, where
    /// Contents/MacOS may only hold code — in Contents/Resources/plugins.
    /// </summary>
    public static string BundledPluginDirectory
    {
        get
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "plugins");
            var resources = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "Resources", "plugins"));
            return !Directory.Exists(beside) && Directory.Exists(resources) ? resources : beside;
        }
    }

    /// <summary>User plugins first (they override bundled ones with the same id), then the bundled ones.</summary>
    public static IReadOnlyList<string> PluginDirectories { get; } =
    [
        Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "plugins"),
        BundledPluginDirectory,
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
        _console.View.ActivityPreferences = Activities;
        _console.View.TryOnIsland = () => _island?.Preview();
        _console.Closed += (_, _) => _console = null;
        _console.Show();
    }

    /// <summary>The native clipboard support for this OS (docs 05 §8).</summary>
    private static Island.Platform.IClipboardPlatform ClipboardPlatform() =>
        OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacClipboard()
        : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsClipboard()
        : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxClipboard()
        : new Island.Platform.NullClipboardPlatform();

    /// <summary>"O_O 有新版本 0.1.1001" on the island for a few seconds.</summary>
    private static Island.Plugins.LiveAlertSnapshot UpdateAlert(string version) => new(
        "o_o",
        null,
        Island.Plugins.LivePriority.Normal,
        new Island.Plugins.RegionView(
            Leading: new Island.Plugins.VNode("icon", null, new Dictionary<string, object> { ["name"] = "CircleArrowUp", ["tone"] = "accent" },
                new Dictionary<string, int>(), []),
            Center: new Island.Plugins.VNode("text", null, new Dictionary<string, object> { ["text"] = $"O_O 有新版本 {version}", ["size"] = "sm", ["weight"] = "semibold" },
                new Dictionary<string, int>(), [])),
        TimeSpan.FromSeconds(8),
        0);

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
            // ⌥-drag on the island moves it; the new position is saved (and the 岛体 page follows).
            _island.AppearanceEdited += a => Appearance.Set(a);
            if (Enum.TryParse<IslandPlacement>(Environment.GetEnvironmentVariable("O_O_PLACEMENT"), true, out var placement))
            {
                _island.PlacementOverride = placement; // development aid
            }

            // The clipboard's platform layer also knows the app the user works in: typing on the island hands the
            // keyboard back to it (docs 02 §7).
            var clipboardPlatform = ClipboardPlatform();
            IslandFocus.RestoreFocus = clipboardPlatform.RestoreFocus;
            _plugins = new PluginHost(new PluginServices(
                DataDirectory: DataDirectory,
                Secrets: platform.Secrets)
            {
                Clipboard = new Island.UI.Services.ClipboardService(window, clipboardPlatform,
                    Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "clipboard-images")),
                UserPluginDirectory = PluginDirectories[0],
                SystemCalendar = OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacCalendar() : null,
                // Other apps' notifications for the notifications.listen permission (docs/design/08).
                NotificationListener = OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacNotificationListener()
                    : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsNotificationListener()
                    : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxNotificationListener()
                    : null,
                // Music (docs 05 §1). MacMedia is created here, on the main thread, where its notifications arrive.
                Media = new Island.Plugins.Host.Services.PlatformMedia(
                    OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacNetEaseMedia(new Island.Platform.MacOS.MacMedia())
                    : OperatingSystem.IsWindows() ? new Island.Platform.Windows.WindowsMedia()
                    : OperatingSystem.IsLinux() ? new Island.Platform.Linux.LinuxMedia()
                    : new Island.Platform.NullMediaPlatform()),
            });
            // The music bars move by themselves: following an app's real loudness means tapping its audio, which lights
            // macOS's recording indicator (AudioLevels.Source stays the null source).
            var plugins = _plugins;
            var presenter = _island.CreatePresenter(
                (plugin, handler, payload) => plugins.Dispatch(plugin, handler, payload),
                Pages,
                s => DescribePage(plugins, s.PluginId, s.PageId, s.Title),
                Chips);
            _plugins.PageUpdated += s => IslandPresenter.Post(() => presenter.UpdatePage(s));
            _plugins.PageEnded += (p, g) => IslandPresenter.Post(() => presenter.EndPage(p, g));
            // The island (docs/design/09-island-v2.md): live activities, alerts, ambient items.
            presenter.ActivityPreferences = Activities;
            _plugins.LiveActivityUpdated += s => IslandPresenter.Post(() => presenter.UpdateLive(s));
            _plugins.LiveActivityEnded += (_, key) => IslandPresenter.Post(() => presenter.EndLive(key));
            _plugins.LiveAlertRaised += a => IslandPresenter.Post(() => presenter.RaiseLiveAlert(a));
            _plugins.AmbientUpdated += a => IslandPresenter.Post(() => presenter.UpdateAmbient(a));
            presenter.OpenRequested += open => OpenSource(plugins, open);
            // The theme's alert sound, when the user turned alert sounds on (docs/design/09-island-v2.md §4).
            presenter.AlertShown += _ =>
            {
                if (Island.UI.Themes.ThemeService.Current?.Preferences.AlertSounds == true &&
                    Island.UI.Themes.IslandTheme.Current.AlertSound is { } sound)
                {
                    Island.UI.Themes.SoundPlayer.Play(sound);
                }
            };
            // The satellite window beside the island: the second activity's bubble.
            VNodeRenderer.AppIconResolver = plugins.AppIcon;
            _satellites = new SatelliteHost(platform,
                new SatellitePreferencesStore(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "satellites.json")),
                (pid, handler, payload) => plugins.Dispatch(pid, handler, payload));
            _satellites.Follow(_island);
            // The second activity is a bubble beside the island (09 §2, 最小); clicking it brings it to the front.
            string? bubble = null;
            long bubbleSequence = 0;
            var satellites = _satellites;
            presenter.BubbleChanged += s =>
            {
                bubble = s?.Key;
                satellites.OnUpdated(new Island.Plugins.SatelliteSnapshot("o_o", "bubble", s is null ? "" : "bubble",
                    s is null ? null : s.View.Minimal ?? s.View.Compact.Leading ?? s.View.Compact.Trailing,
                    TimeSpan.Zero, null, "auto", ++bubbleSequence));
            };
            satellites.NoticeClicked += (satellite, _) =>
            {
                if (satellite == "o_o/bubble" && bubble is { } key)
                {
                    presenter.BringToFront(key);
                }
            };
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
            // Themes from theme packs, the light/dark schedule and the wallpaper accent (docs 05 §3).
            Island.UI.Themes.ThemeService.Start(_plugins,
                new Island.Core.Themes.ThemePreferencesStore(Path.Combine(Path.GetDirectoryName(AppSettings.DefaultPath)!, "theme.json")),
                OperatingSystem.IsMacOS() ? new Island.Platform.MacOS.MacWallpaper()
                : OperatingSystem.IsWindows() ? Island.Platform.Windows.WindowsWallpaper.Create()
                : OperatingSystem.IsLinux() ? Island.Platform.Linux.LinuxWallpaper.Create()
                : null);
            _island.SettingsRequested += OpenConsole;
            // Tray icon, hand-off from a second launch, full-screen detection (ShellIntegration.cs).
            ShellIntegration.Install(this, _island, OpenConsole, () => desktop.Shutdown());
            _hotkeys = ShellIntegration.InstallHotkeys(_island, OpenConsole, Path.GetDirectoryName(AppSettings.DefaultPath)!);
            _island.Show();
            Island.Core.StartupTrace.Mark("island shown");
            _plugins.StartAll();
            // Catalog plugins installed before island API 2 no longer load: put their catalog version in their place.
            if (_plugins.Services.UserPluginDirectory is { } userPlugins)
            {
                var installer = new Island.Plugins.Host.Services.CatalogInstaller(_plugins, userPlugins,
                    new HttpClient { Timeout = TimeSpan.FromSeconds(60) }, DataDirectory);
                _ = Task.Run(async () =>
                {
                    try
                    {
                        foreach (var id in await installer.UpgradeStrandedAsync(CancellationToken.None))
                        {
                            Console.Error.WriteLine($"[plugins] updated {id} for island API 2");
                        }
                    }
                    catch (Island.Plugins.Host.Services.PluginCapabilityException e)
                    {
                        Console.Error.WriteLine($"[plugins] could not update old catalog plugins: {e.Message}");
                    }
                });
            }
            // Update check (docs/design/08-updates-and-catalog.md): a newer O_O is announced once on the island; the
            // download is offered on the console's 关于 page.
            var updates = new Island.UI.Services.UpdateService(Path.GetDirectoryName(AppSettings.DefaultPath)!,
                latest => presenter.RaiseLiveAlert(UpdateAlert(latest.Version)));
            Island.UI.Services.UpdateService.Shared = updates;
            updates.Start();
            desktop.Exit += (_, _) => updates.Dispose();
            if (desktop.Args?.Contains("--console") == true)
            {
                OpenConsole();
            }
        }
        base.OnFrameworkInitializationCompleted();
    }
}
