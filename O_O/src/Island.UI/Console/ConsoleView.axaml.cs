using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using Avalonia.Threading;
using Island.Plugins.Host;

namespace Island.UI.Console;

public partial class ConsoleView : UserControl
{
    private readonly PluginHost _plugins = null!;
    private readonly string _dataDirectory = "";
    private readonly Island.Core.Layout.IslandAppearanceStore? _appearance;
    private readonly Island.Core.Pages.PagePreferencesStore? _pages;
    private readonly Island.Core.Pages.ChipPreferencesStore? _chips;
    private PluginsPage? _pluginsPage;

    /// <summary>Resource key of the brush behind grouped surfaces (cards) on the console pages.</summary>
    public const string GroupBrushKey = "ConsoleGroupBrush";

    // How much of the window's system material shows through each layer when the window is glass.
    private const double ContentOpacity = 0.55;
    private const double GroupOpacity = 0.7;

    private bool _glass;

    /// <summary>For the XAML previewer only: the shell without pages.</summary>
    public ConsoleView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => ApplyGlass();
    }

    public ConsoleView(PluginHost plugins, string dataDirectory, Island.Core.Layout.IslandAppearanceStore? appearance = null, Island.Core.Pages.PagePreferencesStore? pages = null, Island.Core.Pages.ChipPreferencesStore? chips = null)
    {
        _chips = chips;
        _plugins = plugins;
        _appearance = appearance;
        _pages = pages;
        _dataDirectory = dataDirectory;
        InitializeComponent();
        Nav.PropertyChanged += (_, e) =>
        {
            if (e.Property == TKCode.Arc.Sidebar.SelectedKeyProperty)
            {
                ShowPage(Nav.SelectedKey);
            }
        };
        plugins.PluginStateChanged += (_, _, _) => Dispatcher.UIThread.Post(UpdateBadge);
        ArcTheme.Manager.StateChanged += (_, _) => Dispatcher.UIThread.Post(ApplyGlass);
        // Token colours resolve only once the view is in a window; before that they read as transparent.
        AttachedToVisualTree += (_, _) => ApplyGlass();
        ShowPage(Nav.SelectedKey);
        UpdateBadge();
    }

    /// <summary>The global hotkeys the 快捷键 page edits; null shows the page without them.</summary>
    public Island.UI.Services.HotkeyService? Hotkeys { get; set; }

    /// <summary>Opens the plugins page with one plugin's drawer ("+add": the 添加插件 dialog).</summary>
    public void ShowPlugin(string pluginId)
    {
        ShowPage("plugins");
        if (Page.Content is PluginsPage page)
        {
            if (pluginId == "+add")
            {
                page.ShowAdd();
            }
            else
            {
                page.ShowPlugin(pluginId);
            }
        }
    }

    /// <summary>Puts drawn window controls (Windows, Linux) at the top left, where macOS shows its own.</summary>
    public void ShowTrafficLights(Control lights) => TrafficLightsSlot.Content = lights;

    /// <summary>Opens a page by key: "island", "plugins", "appearance" or "about".</summary>
    public void ShowPage(string? key)
    {
        if (Nav.SelectedKey != key)
        {
            Nav.SelectedKey = key;
            return; // the property change shows it
        }
        Page.Content = key switch
        {
            "appearance" => new AppearancePage(),
            "island" => new IslandPage(
                _appearance ?? new Island.Core.Layout.IslandAppearanceStore(Path.Combine(_dataDirectory, "..", "island.json")),
                _pages ?? new Island.Core.Pages.PagePreferencesStore(Path.Combine(_dataDirectory, "..", "pages.json")),
                _chips ?? new Island.Core.Pages.ChipPreferencesStore(Path.Combine(_dataDirectory, "..", "chips.json")),
                _plugins),
            "shortcuts" => new HotkeysPage(Hotkeys),
            "about" => new AboutPage(_dataDirectory),
            _ => _pluginsPage ??= new PluginsPage(_plugins),
        };
    }

    /// <summary>
    /// Glass: the window shows a system material (macOS vibrancy, Windows Mica or Acrylic), the sidebar lets it show
    /// through fully as on macOS, and the content pane and cards are tinted with Arc's own colours. Without a material
    /// (or with Arc's reduced-transparency setting) everything is opaque, exactly the tokens.
    /// </summary>
    public bool Glass
    {
        get => _glass;
        set
        {
            _glass = value;
            ApplyGlass();
        }
    }

    private void ApplyGlass()
    {
        var glass = _glass && !ReducedTransparency();
        var background = Color(ArcDesignTokenKind.Background);
        var surface = Color(ArcDesignTokenKind.Surface);
        Background = glass ? Brushes.Transparent : new SolidColorBrush(background);
        Nav.Background = glass ? Brushes.Transparent : new SolidColorBrush(Color(ArcDesignTokenKind.SurfaceMuted));
        ContentPane.Background = new SolidColorBrush(glass ? WithAlpha(background, ContentOpacity) : background);
        Resources[GroupBrushKey] = new SolidColorBrush(glass ? WithAlpha(surface, GroupOpacity) : surface);
    }

    private Color Color(ArcDesignTokenKind kind) => ArcTokens.GetColor(this, kind);

    private static Color WithAlpha(Color c, double opacity) => new((byte)(c.A * opacity), c.R, c.G, c.B);

    private bool ReducedTransparency()
    {
        try
        {
            return ArcTokens.Get<bool>(this, ArcDesignTokenKind.ReduceTransparency);
        }
        catch (Exception e) when (e is InvalidCastException or InvalidOperationException or KeyNotFoundException)
        {
            return false;
        }
    }

    /// <summary>The plugins row shows how many plugins are waiting for permission.</summary>
    private void UpdateBadge()
    {
        var waiting = _plugins.Packages.Count(p => _plugins.GetState(p.Id) == PluginState.AwaitingConsent);
        // SidebarItem is not a named control, so it is found through the logical tree.
        if (Nav.GetLogicalDescendants().OfType<TKCode.Arc.SidebarItem>().FirstOrDefault(i => i.Key == "plugins") is { } item)
        {
            item.Badge = waiting > 0 ? waiting.ToString() : null;
            item.IsUnread = waiting > 0;
        }
    }
}
