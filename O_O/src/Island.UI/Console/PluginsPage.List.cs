using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Island.Plugins.Host;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Console;

/// <summary>
/// The plugin list: installed plugins grouped by category. A plugin that requires another installed plugin is that
/// plugin's data source (Claude 用量 → AI 用量, 飞书日历 → 日历) and is listed inside its card, so the one that shows
/// something on the island and the ones feeding it are seen together. A plugin the user turned off stays, dimmed.
/// </summary>
public sealed partial class PluginsPage
{
    private const double DimmedOpacity = 0.55;

    private void Rebuild()
    {
        _content.Children.Clear();
        if (_marketOpen)
        {
            BuildMarket();
        }
        else
        {
            BuildList();
        }
    }

    private void BuildList()
    {
        var market = new ArcButton { Content = "插件市场", Variant = ButtonVariant.Primary, Size = ControlSize.Small, VerticalAlignment = VerticalAlignment.Top };
        market.Click += (_, _) => OpenMarket(null);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(market, Dock.Right);
        header.Children.Add(market);
        header.Children.Add(PageTitle("插件", "灵动岛上的内容都来自插件。点卡片查看权限和设置；更多插件在「插件市场」。"));
        _content.Children.Add(header);

        var installed = _host.Packages;
        var sources = installed.Where(p => HostOf(p) is not null).ToLookup(p => HostOf(p)!.Id);
        // On, turned off by the user (kept, dimmed), or feeding a plugin that is: a bundled plugin nobody added waits
        // in 插件市场 instead.
        bool Listed(PluginPackage p) => _host.IsEnabled(p.Id) || _host.HasChoice(p.Id) || sources[p.Id].Any(s => _host.IsEnabled(s.Id));
        var shown = installed.Where(p => HostOf(p) is null && Listed(p)).ToList();
        if (shown.Count == 0)
        {
            _content.Children.Add(new EmptyState
            {
                Title = "还没有启用插件",
                Description = "在「插件市场」里启用内置插件、安装插件目录里的插件，或安装你自己的插件包。",
                Width = 480,
            });
            return;
        }

        foreach (var group in shown.GroupBy(p => PluginCategories.Of(p.Manifest)).OrderBy(g => PluginCategories.Order(g.Key)))
        {
            _content.Children.Add(SectionLabel(PluginCategories.Label(group.Key)));
            var grid = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 12 };
            var ordered = group
                .OrderBy(p => sources[p.Id].Any() ? 0 : 1)
                .ThenBy(p => _host.IsEnabled(p.Id) ? 0 : 1)
                .ThenBy(p => p.Bundled ? 0 : 1)
                .ThenBy(p => p.Manifest.Name, StringComparer.CurrentCulture);
            foreach (var package in ordered)
            {
                var feeding = sources[package.Id].ToList();
                grid.Children.Add(feeding.Count > 0 ? HostCard(package, feeding) : PluginCard(package));
            }
            _content.Children.Add(grid);
        }
    }

    /// <summary>The installed plugin this one feeds (the first of its requirements that is installed); null for most.</summary>
    private PluginPackage? HostOf(PluginPackage package) =>
        package.Manifest.Requires
            .Select(id => _host.Packages.FirstOrDefault(p => p.Id == id))
            .FirstOrDefault(p => p is not null && p.Manifest.Requires.Count == 0);

    private string StatusOf(PluginPackage package)
    {
        if (!_host.IsEnabled(package.Id))
        {
            return "已关闭";
        }
        if (MissingRequirements(package).FirstOrDefault() is { } missing)
        {
            return $"需启用{missing.Manifest.Name}";
        }
        return StateBadge(_host.GetState(package.Id)).Item1;
    }

    private ArcSwitch EnabledSwitch(PluginPackage package)
    {
        var toggle = new ArcSwitch { IsChecked = _host.IsEnabled(package.Id), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, $"启用{package.Manifest.Name}");
        toggle.IsCheckedChanged += (_, _) =>
        {
            var on = toggle.IsChecked == true;
            if (on != _host.IsEnabled(package.Id))
            {
                _host.SetEnabled(package.Id, on);
            }
        };
        return toggle;
    }

    private Control PluginCard(PluginPackage package)
    {
        var card = new Card
        {
            Width = CardWidth,
            Height = CardHeight,
            Title = package.Manifest.Name,
            Description = package.Manifest.Description ?? package.Id,
            Avatar = Icon(package, 18),
            Meta = package.Bundled ? $"内置 · {package.Manifest.Version}" : $"我的插件 · {package.Manifest.Version}",
            Status = StatusOf(package),
            Action = EnabledSwitch(package),
            Cursor = new Cursor(StandardCursorType.Hand),
            Opacity = _host.IsEnabled(package.Id) ? 1 : DimmedOpacity,
        };
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not ArcSwitch)
            {
                ShowPlugin(package.Id);
            }
        };
        return card;
    }

    /// <summary>A plugin with its data sources: two cards wide, each source a row with its own switch.</summary>
    private Control HostCard(PluginPackage host, IReadOnlyList<PluginPackage> feeding)
    {
        var on = _host.IsEnabled(host.Id);
        var body = new StackPanel { Spacing = 8 };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 10, Cursor = new Cursor(StandardCursorType.Hand) };
        var title = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text(host.Manifest.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.SemiBold),
                Text($"{(host.Bundled ? "内置" : "我的插件")} · {StatusOf(host)}", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary),
            },
        };
        var toggle = EnabledSwitch(host);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(toggle, 2);
        head.Children.Add(Icon(host, 20));
        head.Children.Add(title);
        head.Children.Add(toggle);
        head.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not ArcSwitch)
            {
                ShowPlugin(host.Id);
            }
        };
        body.Children.Add(head);
        if (host.Manifest.Description is { } description)
        {
            body.Children.Add(Text(description, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        }
        if (!on && feeding.Any(s => _host.IsEnabled(s.Id)))
        {
            body.Children.Add(Text($"{host.Manifest.Name}已关闭：下面的数据来源不会显示在岛上。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Danger, wrap: true));
        }

        var label = Text("数据来源", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.SemiBold);
        label.Margin = new Thickness(0, 4, 0, 0);
        body.Children.Add(label);
        foreach (var source in feeding.OrderBy(s => _host.IsEnabled(s.Id) ? 0 : 1).ThenBy(s => s.Manifest.Name, StringComparer.CurrentCulture))
        {
            body.Children.Add(SourceRow(source));
        }
        var more = new ArcButton { Content = "+ 添加来源", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
        more.Click += (_, _) => OpenMarket(PluginCategories.Of(host.Manifest));
        body.Children.Add(more);

        var card = Group(body);
        card.Margin = default;
        card.Width = CardWidth * 2 + 12;
        card.Opacity = on ? 1 : 0.8; // still readable: its sources may be on
        return card;
    }

    private Control SourceRow(PluginPackage source)
    {
        var row = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"),
            ColumnSpacing = 10,
            Cursor = new Cursor(StandardCursorType.Hand),
            Background = Brushes.Transparent, // the whole row is clickable
            Opacity = _host.IsEnabled(source.Id) ? 1 : DimmedOpacity,
            Margin = new Thickness(8, 0, 0, 0),
        };
        var name = Text(source.Manifest.Name, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        var status = Text(StatusOf(source), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary);
        var toggle = EnabledSwitch(source);
        Grid.SetColumn(name, 1);
        Grid.SetColumn(status, 2);
        Grid.SetColumn(toggle, 3);
        row.Children.Add(Icon(source, 16));
        row.Children.Add(name);
        row.Children.Add(status);
        row.Children.Add(toggle);
        row.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not ArcSwitch)
            {
                ShowPlugin(source.Id);
            }
        };
        return row;
    }
}
