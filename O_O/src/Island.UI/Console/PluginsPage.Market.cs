using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Threading;
using Island.Core.Updates;
using Island.Plugins.Host;
using Island.Plugins.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// 插件市场: one searchable grid, filtered by category, of every plugin that can be added — the installed ones that are
/// off (启用) and the O_O plugin catalog (安装, or 更新 for an installed one with a newer version). Installing a .zip and
/// the AI plugin-writing skill sit in the header.
/// </summary>
public sealed partial class PluginsPage
{
    private bool _marketOpen;
    private string _category = PluginCategories.All;
    private string _query = "";
    private readonly WrapPanel _marketGrid = new() { Orientation = Orientation.Horizontal, ItemSpacing = 12, LineSpacing = 12 };
    private readonly StackPanel _catalogStatus = new() { Spacing = 8 };
    private bool _catalogLoading;
    private string? _catalogError;
    private string? _installing;   // the catalog plugin being installed
    private Control? _installError;

    /// <summary>A card of the market: what it is and the one action it offers.</summary>
    private sealed record MarketItem(string Name, string? Description, string Category, string Meta, string? Note, Control Icon, ArcButton Action, int Rank)
    {
        public bool Matches(string query) =>
            query.Length == 0 ||
            Name.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
            (Description?.Contains(query, StringComparison.CurrentCultureIgnoreCase) ?? false);
    }

    private void OpenMarket(string? category)
    {
        _drawer.IsOpen = false;
        _marketOpen = true;
        _category = category ?? PluginCategories.All;
        _query = "";
        _addError = null;
        _installError = null;
        Rebuild();
        if (!_catalogLoading)
        {
            _ = LoadCatalogAsync();
        }
    }

    private void CloseMarket()
    {
        _marketOpen = false;
        Rebuild();
    }

    private void BuildMarket()
    {
        var back = new ArcButton { Content = "‹ 插件", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
        back.Click += (_, _) => CloseMarket();
        var zip = new ArcButton { Content = "安装插件包…", Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        zip.Click += async (_, _) => await PickAndInstall();
        var skill = new ArcButton { Content = "让 AI 写插件", Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        skill.Click += (_, _) => OpenSkill();
        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Top };
        tools.Children.Add(zip);
        tools.Children.Add(skill);
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        header.Children.Add(PageTitle("插件市场", "内置但还没启用的插件，以及 O_O 插件目录里的插件。"));
        _content.Children.Add(back);
        _content.Children.Add(header);
        if (_addError is not null)
        {
            _content.Children.Add(_addError);
        }

        var search = new SearchField { Text = _query, Watermark = "搜索插件…", Margin = new Thickness(0, 0, 0, 12) };
        // The Text property itself, not TextChanged: that one only follows typing, not a cleared or set text.
        search.PropertyChanged += (_, e) =>
        {
            if (e.Property.Name == "Text" && (search.Text ?? "").Trim() is var query && query != _query)
            {
                _query = query;
                FillMarket();
            }
        };
        _content.Children.Add(search);

        var filters = new SegmentedControl { Margin = new Thickness(0, 0, 0, 16), HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (key, label) in PluginCategories.Filters)
        {
            filters.Items.Add(new SegmentedOption { Value = key, Label = label });
        }
        filters.SelectedValue = _category;
        filters.SelectionChanged += (_, _) =>
        {
            if (filters.SelectedValue is string key && key != _category)
            {
                _category = key;
                FillMarket();
            }
        };
        _content.Children.Add(filters);

        DetachFromParent(_marketGrid);
        DetachFromParent(_catalogStatus);
        _content.Children.Add(_catalogStatus);
        _content.Children.Add(_marketGrid);
        FillMarket();
    }

    private static void DetachFromParent(Control control)
    {
        if (control.Parent is Panel parent)
        {
            parent.Children.Remove(control);
        }
    }

    /// <summary>Refills the grid (search, filter, catalog loaded) without rebuilding the header, so the search keeps focus.</summary>
    private void FillMarket()
    {
        _catalogStatus.Children.Clear();
        if (_installError is not null)
        {
            _catalogStatus.Children.Add(_installError);
        }
        if (_catalogLoading)
        {
            _catalogStatus.Children.Add(Text("正在读取插件目录…", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        }
        else if (_catalogError is { } error)
        {
            var retry = new ArcButton { Content = "重试", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            retry.Click += async (_, _) => await LoadCatalogAsync();
            _catalogStatus.Children.Add(new Alert { Tone = AlertTone.Warning, Title = $"插件目录暂时打不开：{error}" });
            _catalogStatus.Children.Add(retry);
        }

        _marketGrid.Children.Clear();
        var items = MarketItems()
            .Where(i => (_category == PluginCategories.All || i.Category == _category) && i.Matches(_query))
            .OrderBy(i => i.Rank)
            .ThenBy(i => PluginCategories.Order(i.Category))
            .ThenBy(i => i.Name, StringComparer.CurrentCulture)
            .ToList();
        foreach (var item in items)
        {
            _marketGrid.Children.Add(new Card
            {
                Width = CardWidth,
                Height = CardHeight,
                Title = item.Name,
                Description = item.Description,
                Avatar = item.Icon,
                Meta = item.Meta,
                Status = item.Note,
                Action = item.Action,
            });
        }
        if (items.Count == 0 && !_catalogLoading)
        {
            _marketGrid.Children.Add(new EmptyState
            {
                Title = _query.Length > 0 || _category != PluginCategories.All ? "没有找到插件" : "没有可添加的插件",
                Description = _query.Length > 0 || _category != PluginCategories.All ? "换个关键词或分类试试。" : "内置插件都已启用，插件目录里的插件也都装好了。",
                Width = 480,
            });
        }
    }

    private IEnumerable<MarketItem> MarketItems()
    {
        // Installed but off: bundled ones nobody added, and ones the user turned off.
        foreach (var package in _host.Packages.Where(p => !_host.IsEnabled(p.Id)))
        {
            var enable = SmallButton("启用", primary: false);
            enable.Click += (_, _) =>
            {
                _host.SetEnabled(package.Id, true);
                ShowPlugin(package.Id); // its permission request, if any, is the next step
            };
            yield return new MarketItem(package.Manifest.Name, package.Manifest.Description, PluginCategories.Of(package.Manifest),
                package.Bundled ? $"内置 · {package.Manifest.Version}" : $"我的插件 · {package.Manifest.Version}",
                FeedsNote(package.Manifest.Requires, null), Icon(package, 18), enable, Rank: 1);
        }

        if (CatalogState.Last is not { } catalog || CatalogState.For(_host) is not { } installer)
        {
            yield break;
        }
        foreach (var newer in installer.Updates(catalog))
        {
            var update = SmallButton(_installing == newer.Id ? "更新中…" : $"更新到 {newer.Version}", primary: true);
            update.Click += async (_, _) => await InstallFromCatalogAsync(newer, catalog);
            yield return new MarketItem(newer.Name, newer.Description,
                PluginCategories.Of(newer.Id, newer.Requires, newer.Permissions), $"已安装 · 有新版本 {newer.Version}", null,
                CatalogIcon(newer), update, Rank: 0);
        }
        foreach (var plugin in catalog.Plugins.Where(p => _host.Packages.All(x => x.Id != p.Id)))
        {
            var install = SmallButton(_installing == plugin.Id ? "安装中…" : "安装", primary: false);
            install.Click += async (_, _) => await InstallFromCatalogAsync(plugin, catalog);
            yield return new MarketItem(plugin.Name, plugin.Description,
                PluginCategories.Of(plugin.Id, plugin.Requires, plugin.Permissions), $"插件目录 · {plugin.Version}",
                FeedsNote(plugin.Requires, catalog), CatalogIcon(plugin), install, Rank: 2);
        }
    }

    /// <summary>"为「AI 用量」提供数据", saying when that plugin comes along (turned on, or installed first).</summary>
    private string? FeedsNote(IReadOnlyCollection<string> requires, PluginCatalog? catalog)
    {
        var parts = new List<string>();
        foreach (var id in requires)
        {
            if (_host.Packages.FirstOrDefault(p => p.Id == id) is { } local)
            {
                parts.Add(_host.IsEnabled(id) ? $"为「{local.Manifest.Name}」提供数据" : $"会一并启用「{local.Manifest.Name}」");
            }
            else if (catalog?.Plugins.FirstOrDefault(p => p.Id == id) is { } remote)
            {
                parts.Add($"会一并安装「{remote.Name}」");
            }
        }
        return parts.Count == 0 ? null : string.Join("；", parts);
    }

    private ArcButton SmallButton(string content, bool primary) => new()
    {
        Content = content,
        Variant = primary ? ButtonVariant.Primary : ButtonVariant.Secondary,
        Size = ControlSize.Small,
        VerticalAlignment = VerticalAlignment.Center,
        IsEnabled = _installing is null,
    };

    private static Control CatalogIcon(CatalogPlugin plugin) => new TKCode.Arc.Icons.Lucide.LucideIcon
    {
        Kind = Enum.TryParse<TKCode.Arc.Icons.Lucide.LucideIconKind>(plugin.Icon, out var kind) ? kind : TKCode.Arc.Icons.Lucide.LucideIconKind.Puzzle,
        Size = 18,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private async Task LoadCatalogAsync()
    {
        if (CatalogState.For(_host) is not { } installer)
        {
            return;
        }
        _catalogLoading = true;
        _catalogError = null;
        RefreshMarket();
        try
        {
            CatalogState.Last = await installer.FetchAsync(CancellationToken.None);
        }
        catch (PluginCapabilityException e)
        {
            _catalogError = e.Message;
        }
        _catalogLoading = false;
        Dispatcher.UIThread.Post(RefreshMarket);
    }

    private void RefreshMarket()
    {
        if (_marketOpen && _marketGrid.Parent is not null)
        {
            FillMarket();
        }
    }

    private async Task InstallFromCatalogAsync(CatalogPlugin plugin, PluginCatalog catalog)
    {
        if (CatalogState.For(_host) is not { } installer)
        {
            return;
        }
        _installing = plugin.Id;
        _installError = null;
        FillMarket();
        try
        {
            await installer.InstallAsync(plugin, catalog, CancellationToken.None);
            _installing = null;
            Rebuild();
            ShowPlugin(plugin.Id); // a new plugin asks for its permissions in its drawer
        }
        catch (PluginCapabilityException e)
        {
            _installing = null;
            _installError = new Alert { Tone = AlertTone.Danger, Title = e.Message };
            FillMarket();
        }
    }
}
