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

/// <summary>
/// 添加插件 → 插件目录 (docs/design/08-updates-and-catalog.md): the plugins the O_O service offers for this system,
/// each with 安装 (what it requires is installed first). Installed plugins ask for their permissions like any plugin
/// the user installs; <paramref name="installed"/> opens the new plugin's drawer for that.
/// </summary>
public sealed class CatalogSection : StackPanel
{
    private readonly PluginHost _host;
    private readonly Action<string> _installed;
    private readonly StackPanel _rows = new() { Spacing = 4 };
    private string? _busy;
    private string? _error;

    public CatalogSection(PluginHost host, Action<string> installed)
    {
        _host = host;
        _installed = installed;
        Spacing = 8;
        Children.Add(SectionLabel("插件目录"));
        Children.Add(_rows);
        _rows.Children.Add(Note("正在读取插件目录…"));
        AttachedToVisualTree += async (_, _) => await LoadAsync();
    }

    private async Task LoadAsync()
    {
        if (CatalogState.For(_host) is not { } installer)
        {
            Show(Note("这里不能安装插件。"));
            return;
        }
        try
        {
            CatalogState.Last = await installer.FetchAsync(CancellationToken.None);
            _error = null;
        }
        catch (PluginCapabilityException e)
        {
            _error = e.Message;
        }
        Dispatcher.UIThread.Post(Fill);
    }

    private void Show(params Control[] controls)
    {
        _rows.Children.Clear();
        foreach (var c in controls)
        {
            _rows.Children.Add(c);
        }
    }

    private void Fill()
    {
        if (_error is { } error)
        {
            var retry = new ArcButton { Content = "重试", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += async (_, _) =>
            {
                Show(Note("正在读取插件目录…"));
                await LoadAsync();
            };
            Show(Note($"{error}稍后可以重试。"), retry);
            return;
        }
        var catalog = CatalogState.Last ?? new PluginCatalog();
        // Not offered again once installed (an update shows in the plugin's drawer), nor when built in.
        var offered = catalog.Plugins.Where(p => _host.Packages.All(x => x.Id != p.Id)).OrderBy(p => p.Name, StringComparer.CurrentCulture).ToList();
        if (offered.Count == 0)
        {
            Show(Note(catalog.Plugins.Count == 0 ? "插件目录暂时是空的。" : "插件目录里的插件都已安装。"));
            return;
        }
        _rows.Children.Clear();
        foreach (var plugin in offered)
        {
            _rows.Children.Add(Row(plugin, catalog));
        }
    }

    private Control Row(CatalogPlugin plugin, PluginCatalog catalog)
    {
        var install = new ArcButton
        {
            Content = _busy == plugin.Id ? "安装中…" : "安装",
            Variant = ButtonVariant.Secondary,
            Size = ControlSize.Small,
            VerticalAlignment = VerticalAlignment.Center,
            IsEnabled = _busy is null,
        };
        install.Click += async (_, _) => await InstallAsync(plugin, catalog);
        var requires = plugin.Requires.Where(id => _host.Packages.All(x => x.Id != id))
            .Select(id => catalog.Plugins.FirstOrDefault(p => p.Id == id)?.Name ?? id).ToList();
        var details = new List<string>();
        if (plugin.Permissions.Count > 0)
        {
            details.Add("权限：" + string.Join("、", plugin.Permissions.Select(PluginsPage.Describe)));
        }
        if (requires.Count > 0)
        {
            details.Add("会一并安装：" + string.Join("、", requires));
        }
        var words = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text($"{plugin.Name}  {plugin.Version}", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
                Text(plugin.Description ?? plugin.Id, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        };
        foreach (var line in details)
        {
            words.Children.Add(Text(line, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextMuted, wrap: true));
        }
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
        var icon = new TKCode.Arc.Icons.Lucide.LucideIcon
        {
            Kind = Enum.TryParse<TKCode.Arc.Icons.Lucide.LucideIconKind>(plugin.Icon, out var kind) ? kind : TKCode.Arc.Icons.Lucide.LucideIconKind.Puzzle,
            Size = 20,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(words, 1);
        Grid.SetColumn(install, 2);
        row.Children.Add(icon);
        row.Children.Add(words);
        row.Children.Add(install);
        return row;
    }

    private async Task InstallAsync(CatalogPlugin plugin, PluginCatalog catalog)
    {
        if (CatalogState.For(_host) is not { } installer)
        {
            return;
        }
        _busy = plugin.Id;
        Fill();
        try
        {
            await installer.InstallAsync(plugin, catalog, CancellationToken.None);
            _busy = null;
            _installed(plugin.Id);
        }
        catch (PluginCapabilityException e)
        {
            _busy = null;
            _error = null;
            Fill();
            _rows.Children.Insert(0, new Alert { Tone = AlertTone.Danger, Title = e.Message });
        }
    }

    private static TextBlock Note(string text) => Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
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
