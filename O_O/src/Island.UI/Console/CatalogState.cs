using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.Core.Updates;
using Island.Plugins.Host;
using Island.Plugins.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>The catalog client shared by the console (one installer per plugin host, the last catalog fetched).</summary>
internal static class CatalogState
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(60) };
    private static CatalogInstaller? s_installer;
    private static PluginHost? s_host;

    /// <summary>The last catalog fetched, for update checks in the plugin drawer; null before the first.</summary>
    public static PluginCatalog? Last { get; set; }

    public static CatalogInstaller? For(PluginHost host)
    {
        if (host.Services.UserPluginDirectory is not { } userDirectory)
        {
            return null;
        }
        if (s_installer is null || !ReferenceEquals(s_host, host))
        {
            s_host = host;
            s_installer = new CatalogInstaller(host, userDirectory, Http, host.Services.DataDirectory);
        }
        return s_installer;
    }
}

/// <summary>In a catalog plugin's drawer: where it came from and, when the catalog has a newer version, 更新.</summary>
public static class CatalogDrawer
{
    public static Control? For(PluginHost host, PluginPackage package, Action updated)
    {
        if (package.Bundled || CatalogState.For(host) is not { } installer || !installer.IsFromCatalog(package.Id))
        {
            return null;
        }
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(Text("来自 O_O 插件目录", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        if (CatalogState.Last is { } catalog && installer.Updates(catalog).FirstOrDefault(p => p.Id == package.Id) is { } newer)
        {
            var update = new ArcButton { Content = $"更新到 {newer.Version}", Variant = ButtonVariant.Primary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            update.Click += async (_, _) =>
            {
                update.IsEnabled = false;
                update.Content = "更新中…";
                try
                {
                    await installer.InstallAsync(newer, catalog, CancellationToken.None);
                    updated();
                }
                catch (PluginCapabilityException e)
                {
                    update.Content = "更新失败";
                    panel.Children.Add(new Alert { Tone = AlertTone.Danger, Title = e.Message });
                }
            };
            panel.Children.Add(update);
        }
        return panel;
    }
}
