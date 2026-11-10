using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Island.Plugins.Host;
using TKCode.Arc;

namespace Island.UI.Console;

/// <summary>
/// The console (docs/design/04-console.md): an ArcWindow, Apple style. macOS keeps its own traffic lights; Windows and
/// Linux get <see cref="TrafficLights"/> at the left of the bar instead of the drawn caption buttons on the right.
/// </summary>
public sealed class ConsoleWindow : ArcWindow
{
    public ConsoleWindow(PluginHost plugins, string dataDirectory, Island.Core.Layout.IslandAppearanceStore? appearance = null, Island.Core.Pages.PagePreferencesStore? pages = null, Island.Core.Pages.ChipPreferencesStore? chips = null)
    {
        Title = "O_O 控制台";
        Width = 1000;
        Height = 680;
        MinWidth = 760;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        View = new ConsoleView(plugins, dataDirectory, appearance, pages, chips);
        Content = View;

        // Glass (docs/design/README.md §6): ask for the best system material the platform has; ConsoleView turns its
        // own surfaces translucent only if one is actually granted.
        TransparencyLevelHint =
        [
            WindowTransparencyLevel.Mica,        // Windows 11
            WindowTransparencyLevel.AcrylicBlur, // Windows 10/11; macOS vibrancy
            WindowTransparencyLevel.Blur,
            WindowTransparencyLevel.None,
        ];
        Background = Brushes.Transparent;
        PropertyChanged += (_, e) =>
        {
            if (e.Property == ActualTransparencyLevelProperty)
            {
                UpdateGlass();
            }
        };
        Opened += (_, _) => UpdateGlass();

        // A unified window, as macOS apps such as System Settings: no title bar band; the content (and the sidebar's
        // glass) reaches the top edge, the traffic lights float over the sidebar, and invisible strips move the window.
        // The client area still extends into the decorations, so macOS keeps its own traffic lights there; Windows and
        // Linux get drawn ones in the same place.
        IsTitleBarVisible = false;
        TitleBar = null;
        ExtendClientAreaToDecorationsHint = true;
        if (!OperatingSystem.IsMacOS())
        {
            View.ShowTrafficLights(new TrafficLights(this));
        }
    }

    public ConsoleView View { get; }

    /// <summary>The global hotkeys, for the 快捷键 page; null where the app has none (offscreen renders).</summary>
    public Island.UI.Services.HotkeyService? Hotkeys
    {
        get => View.Hotkeys;
        init => View.Hotkeys = value;
    }

    private void UpdateGlass()
    {
        var level = ActualTransparencyLevel;
        if (Environment.GetEnvironmentVariable("O_O_DEBUG") == "1")
        {
            System.Console.Error.WriteLine($"[console] transparency level granted: {level}");
        }
        View.Glass = level != WindowTransparencyLevel.None && level != WindowTransparencyLevel.Transparent;
        if (View.Glass)
        {
            Background = Brushes.Transparent;
        }
        else
        {
            ArcTokens.Bind(this, BackgroundProperty, TKCode.Arc.DesignTokens.ArcDesignTokenKind.Background);
        }
    }
}
