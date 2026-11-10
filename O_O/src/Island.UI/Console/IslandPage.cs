using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.VisualTree;
using Avalonia.Layout;
using Avalonia.Media;
using System.Collections.ObjectModel;
using Avalonia.Automation;
using Avalonia.Controls.Templates;
using Island.Core.Layout;
using Island.Core.Pages;
using Island.Plugins;
using Island.Plugins.Host;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcSlider = TKCode.Arc.Slider;

namespace Island.UI.Console;

/// <summary>
/// 岛体 (docs/design/09-island-v2.md §5): collapsible sections on the left — 收起时 (the ambient items), 活动 (order,
/// policies, pinned), 主页 (the home pages), 外观 (shape, dock, size, hover) — and on the right a preview drawn with the
/// plugins' real content. Every change is saved and applied to the island at once.
/// </summary>
public sealed class IslandPage : StackPanel
{
    private readonly IslandAppearanceStore _store;
    private readonly PagePreferencesStore _pages;
    private readonly PluginHost _plugins;
    private readonly ChipPreferencesStore _chips;
    private readonly Island.Core.Activities.ActivityPreferencesStore _activities;
    private readonly IslandPreview _preview;
    private readonly Border _activityHost = new();
    private Grid? _columns;

    /// <summary>Which sections are open, kept while the app runs (the page is drawn again after every layout).</summary>
    private static readonly Dictionary<string, bool> s_open = new(StringComparer.Ordinal)
    {
        ["collapsed"] = true, ["activities"] = true, ["home"] = false, ["look"] = false,
    };

    public IslandPage(IslandAppearanceStore store, PagePreferencesStore pages, ChipPreferencesStore chips, PluginHost plugins,
        Island.Core.Activities.ActivityPreferencesStore activities, Action? tryOnIsland = null)
    {
        _chips = chips;
        _store = store;
        _pages = pages;
        _plugins = plugins;
        _activities = activities;
        _preview = new IslandPreview(plugins, pages, chips, activities, tryOnIsland);
        // The preview stays in view while the settings scroll (two columns only).
        AttachedToVisualTree += (_, _) =>
        {
            if (this.FindAncestorOfType<ScrollViewer>() is { } scroll)
            {
                scroll.ScrollChanged += (_, _) => Stick(scroll);
            }
        };
        SizeChanged += (_, _) => Arrange2();
        _layouts = new Island.UI.Layouts.LayoutService(plugins, store, pages, chips);
        // A layout changes the shape, the lists and more: draw the page again from the stores.
        _layouts.Applied += () => Avalonia.Threading.Dispatcher.UIThread.Post(Build);
        Build();
    }

    private readonly Island.UI.Layouts.LayoutService _layouts;

    /// <summary>Draws the whole page from the stores' current values.</summary>
    private void Build()
    {
        Children.Clear();
        var look = new StackPanel();
        var store = _store;
        var a = store.Current;
        Children.Add(PageTitle("岛体", "灵动岛收起和展开时显示什么、外观和位置。修改会立即生效，右侧是用插件当前内容画出的预览。"));
        if (LayoutPresets() is { } presets)
        {
            look.Children.Add(presets);
        }

        var shape = new SegmentedControl
        {
            Items =
            {
                new SegmentedOption { Value = nameof(IslandShape.Capsule), Label = "胶囊" },
                new SegmentedOption { Value = nameof(IslandShape.Notch), Label = "刘海" },
            },
            SelectedValue = a.Shape.ToString(),
        };
        var shapeNote = Note(ShapeNote(a.Shape));
        shape.SelectionChanged += (_, _) =>
        {
            if (shape.SelectedValue is string v && Enum.TryParse<IslandShape>(v, out var s))
            {
                shapeNote.Text = ShapeNote(s);
                Update(x => x with { Shape = s });
            }
        };

        var dock = new SegmentedControl
        {
            Items =
            {
                new SegmentedOption { Value = nameof(IslandDock.Top), Label = "顶部" },
                new SegmentedOption { Value = nameof(IslandDock.Left), Label = "左侧" },
                new SegmentedOption { Value = nameof(IslandDock.Right), Label = "右侧" },
            },
            SelectedValue = a.Dock.ToString(),
        };
        var dockNote = Note(DockNote(a.Dock));

        var size = new ArcSlider
        {
            Label = "大小",
            Minimum = IslandAppearance.MinScale * 100,
            Maximum = IslandAppearance.MaxScale * 100,
            SmallChange = 5,
            LargeChange = 10,
            IsValueVisible = true,
            ValueFormatter = v => $"{v:0}%",
            Value = Math.Round(a.Scale * 100),
            Marks = { new SliderMark { Value = 80, Label = "80%" }, new SliderMark { Value = 100, Label = "100%" }, new SliderMark { Value = 140, Label = "140%" } },
        };
        size.ValueCommitted += (_, _) => Update(x => x with { Scale = Math.Round(size.Value / 5) * 5 / 100 });

        var offset = new ArcSlider
        {
            Label = OffsetLabel(a.Dock),
            Minimum = -IslandAppearance.MaxOffset,
            Maximum = IslandAppearance.MaxOffset,
            SmallChange = 10,
            LargeChange = 50,
            IsValueVisible = true,
            ValueFormatter = v => Math.Abs(v) < 0.5 ? "居中"
                : _store.Current.Dock == IslandDock.Top ? (v < 0 ? $"左移 {-v:0}" : $"右移 {v:0}")
                : (v < 0 ? $"上移 {-v:0}" : $"下移 {v:0}"),
            Value = a.Offset,
            Marks = { new SliderMark { Value = 0, Label = "居中" } },
        };
        offset.ValueCommitted += (_, _) => Update(x => x with { Offset = Math.Round(offset.Value / 10) * 10 });
        dock.SelectionChanged += (_, _) =>
        {
            if (dock.SelectedValue is string v && Enum.TryParse<IslandDock>(v, out var d) && d != _store.Current.Dock)
            {
                dockNote.Text = DockNote(d);
                offset.Label = OffsetLabel(d);
                offset.Value = 0; // the old offset ran along another edge
                Update(x => x with { Dock = d, Offset = 0 });
            }
        };

        look.Children.Add(Group(new StackPanel
        {
            Spacing = 20,
            Children =
            {
                Field("形态", shape, shapeNote),
                Field("停靠", dock, dockNote),
                DisplayField(a),
                Field("全屏时", Choice(
                    [(nameof(FullScreenBehavior.Hide), "隐藏"), (nameof(FullScreenBehavior.ThinLine), "细线"), (nameof(FullScreenBehavior.Show), "显示")],
                    a.FullScreen.ToString(),
                    v => Update(x => x with { FullScreen = Enum.Parse<FullScreenBehavior>(v) })),
                    Note("其他应用全屏（视频、游戏、演示）时：隐藏岛；或缩成贴边的一条细线，指针移上去仍能展开；退出全屏后自动恢复。")),
                MinimalField(a),
                HoverField(a),
            },
        }));
        look.Children.Add(Group(new StackPanel
        {
            Spacing = 20,
            Children =
            {
                new StackPanel { Spacing = 6, Children = { size, Note("整个岛按比例缩放，文字一起变大或变小。") } },
                new StackPanel { Spacing = 6, Children = { offset, Note(OperatingSystem.IsMacOS()
                    ? "点刻度上的\"居中\"可回到正中。也可以按住 ⌥ 直接拖动岛。"
                    : "点刻度上的\"居中\"可回到正中。也可以按住 Alt 直接拖动岛。") } },
                Note("停在顶部时，在有刘海的 Mac 上岛要罩住刘海，大小和位置不起作用。"),
            },
        }));

        FillActivities();
        var settings = new StackPanel
        {
            Children =
            {
                Section("collapsed", "收起时", "没有活动时，岛的左右两侧显示的小项；有活动时它们让位。", Group(ChipList())),
                Section("activities", "活动", "正在进行的事（播放、计时、日程……）。最多同时显示两个：第一个在岛上，第二个是旁边的小圆泡。", _activityHost),
                Section("home", "主页", "没有活动时点岛，或按快捷键打开的带页签的仪表盘。", Group(PageList())),
                Section("look", "外观", "形态、停靠、大小、位置和展开方式。", look),
            },
        };
        if (_preview.Parent is Panel old)
        {
            old.Children.Remove(_preview);
        }
        _columns = new Grid();
        _columns.Children.Add(settings);
        _columns.Children.Add(_preview);
        Children.Add(_columns);
        Arrange2();
    }

    /// <summary>Two columns (settings, preview) when there is room; otherwise the preview above the settings.</summary>
    private void Arrange2()
    {
        if (_columns is not { Children: [var settings, var preview] })
        {
            return;
        }
        var wide = Bounds.Width is 0 or >= 820;
        _columns.ColumnDefinitions = wide ? new ColumnDefinitions("*,24,340") : new ColumnDefinitions("*");
        _columns.RowDefinitions = wide ? new RowDefinitions("Auto") : new RowDefinitions("Auto,Auto");
        Grid.SetColumn(settings, 0);
        Grid.SetRow(settings, wide ? 0 : 1);
        Grid.SetColumn(preview, wide ? 2 : 0);
        Grid.SetRow(preview, 0);
        preview.VerticalAlignment = VerticalAlignment.Top;
        preview.Margin = wide ? default : new Thickness(0, 0, 0, 16);
        preview.RenderTransform = null;
    }

    private void Stick(ScrollViewer scroll)
    {
        if (_columns is null || _columns.ColumnDefinitions.Count < 3 || _columns.TranslatePoint(default, this) is not { } top)
        {
            return;
        }
        var room = Math.Max(0, _columns.Bounds.Height - _preview.Bounds.Height);
        var y = Math.Clamp(scroll.Offset.Y - top.Y - Bounds.Y + 16, 0, room);
        _preview.RenderTransform = new Avalonia.Media.TranslateTransform(0, y);
    }

    /// <summary>A section with a header that opens and closes it.</summary>
    private static Control Section(string key, string title, string note, Control content)
    {
        var open = s_open.GetValueOrDefault(key, true);
        var chevron = new TKCode.Arc.Icons.Lucide.LucideIcon { Kind = TKCode.Arc.Icons.Lucide.LucideIconKind.ChevronDown, Size = 16, VerticalAlignment = VerticalAlignment.Center };
        var body = new StackPanel { Spacing = 8, IsVisible = open, Margin = new Thickness(0, 4, 0, 8), Children = { Note(note), content } };
        chevron.RenderTransform = new Avalonia.Media.RotateTransform(open ? 0 : -90);
        var header = new ArcButton
        {
            Variant = ButtonVariant.Ghost,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(-8, 8, 0, 0),
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Spacing = 6,
                Children = { chevron, Text(title, ArcDesignTokenKind.TypeLG, ArcDesignTokenKind.Foreground, FontWeight.SemiBold) },
            },
        };
        header.Click += (_, _) =>
        {
            body.IsVisible = !body.IsVisible;
            s_open[key] = body.IsVisible;
            chevron.RenderTransform = new Avalonia.Media.RotateTransform(body.IsVisible ? 0 : -90);
        };
        return new StackPanel { Children = { header, body } };
    }

    private sealed record ActivityKind(string Kind, string Title, string Plugin);

    private static readonly (string Value, string Label)[] Policies = [("allow", "允许"), ("alertOnly", "只提醒"), ("off", "关闭")];

    /// <summary>
    /// 活动 (09 §2.1, §5): every activity the enabled plugins declare, dragged into order (it decides between activities
    /// of the same importance), each allowed, alert-only or off, one of them pinned to the front.
    /// </summary>
    private void FillActivities()
    {
        var kinds = _plugins.Packages.Where(p => _plugins.IsEnabled(p.Id))
            .SelectMany(p => p.Manifest.Contributes.Activities.Select(a => new ActivityKind($"{p.Id}/{a.Id}", a.Title ?? a.Id, p.Manifest.Name)))
            .ToList();
        var panel = new StackPanel { Spacing = 12 };
        if (kinds.Count == 0)
        {
            panel.Children.Add(Note("启用的插件里还没有活动。"));
            _activityHost.Child = Group(panel);
            return;
        }
        var prefs = _activities.Current;
        var order = prefs.Order.Select((k, i) => (k, i)).ToDictionary(x => x.k, x => x.i, StringComparer.Ordinal);
        var rows = new ObservableCollection<ActivityKind>(kinds
            .Select((k, i) => (k, i))
            .OrderBy(x => order.TryGetValue(x.k.Kind, out var r) ? r : int.MaxValue)
            .ThenBy(x => x.i)
            .Select(x => x.k));
        var list = new ReorderableList { Label = "活动", ItemsSource = rows };
        list.ItemTemplate = new FuncDataTemplate<ActivityKind>((kind, _) => kind is null ? new Panel() : ActivityRow(kind));
        list.Reordered += (_, _) => _activities.Set(_activities.Current with { Order = [.. rows.Select(r => r.Kind)] });
        panel.Children.Add(list);
        panel.Children.Add(Note("拖动调整顺序：同样重要的活动，排在前面的先上岛。「只提醒」的活动不留在岛上，但仍会短暂弹出提醒；「关闭」则都不显示。置顶的活动总是第一个。"));
        _activityHost.Child = Group(panel);
    }

    private Control ActivityRow(ActivityKind kind)
    {
        var prefs = _activities.Current;
        var policy = new SegmentedControl { VerticalAlignment = VerticalAlignment.Center };
        foreach (var (value, label) in Policies)
        {
            policy.Items.Add(new SegmentedOption { Value = value, Label = label });
        }
        policy.SelectedValue = prefs.PolicyOf(kind.Kind) switch
        {
            Island.Core.Activities.ActivityPolicy.AlertOnly => "alertOnly",
            Island.Core.Activities.ActivityPolicy.Off => "off",
            _ => "allow",
        };
        policy.SelectionChanged += (_, _) =>
        {
            var chosen = policy.SelectedValue switch
            {
                "alertOnly" => Island.Core.Activities.ActivityPolicy.AlertOnly,
                "off" => Island.Core.Activities.ActivityPolicy.Off,
                _ => Island.Core.Activities.ActivityPolicy.Allow,
            };
            _activities.Set(_activities.Current.WithPolicy(kind.Kind, chosen));
        };
        var pinned = prefs.Pinned == kind.Kind;
        var pin = new ArcButton
        {
            Content = pinned ? "已置顶" : "置顶",
            Variant = pinned ? ButtonVariant.Secondary : ButtonVariant.Ghost,
            Size = ControlSize.Small,
            VerticalAlignment = VerticalAlignment.Center,
        };
        pin.Click += (_, _) =>
        {
            _activities.Set(_activities.Current with { Pinned = pinned ? null : kind.Kind });
            FillActivities(); // one pinned at a time: the other rows change too
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        var titles = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Text(kind.Title, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium) },
        };
        if (kind.Plugin != kind.Title)
        {
            titles.Children.Add(Text(kind.Plugin, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        }
        Grid.SetColumn(pin, 1);
        Grid.SetColumn(policy, 2);
        grid.Children.Add(titles);
        grid.Children.Add(pin);
        grid.Children.Add(policy);
        return grid;
    }

    /// <summary>悬停展开: off (default) — hovering makes the island a little larger, a click opens it (09 §2.2).</summary>
    private Control HoverField(IslandAppearance a)
    {
        var toggle = new ArcSwitch { IsChecked = a.HoverToExpand, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, "悬停展开");
        toggle.IsCheckedChanged += (_, _) => Update(x => x with { HoverToExpand = toggle.IsChecked == true });
        return Field("悬停展开", toggle, Note("关闭时（默认）指针移到岛上，岛会稍微变大，点一下才展开；打开后指针停留片刻就展开。全屏时的细线总是悬停展开。"));
    }

    /// <summary>The layouts enabled plugins offer, one button each; null when there are none.</summary>
    private Control? LayoutPresets()
    {
        var options = _layouts.Layouts;
        if (options.Count == 0)
        {
            return null;
        }
        var wrap = new WrapPanel { Orientation = Orientation.Horizontal };
        foreach (var option in options)
        {
            var button = new TKCode.Arc.Button { Content = option.Title, Variant = TKCode.Arc.ButtonVariant.Secondary, Margin = new Thickness(0, 0, 8, 8) };
            ToolTip.SetTip(button, option.Description is null ? option.Plugin : $"{option.Description}（{option.Plugin}）");
            var key = option.Key;
            button.Click += (_, _) => _layouts.Apply(key);
            wrap.Children.Add(button);
        }
        return Group(new StackPanel
        {
            Spacing = 8,
            Children = { wrap, Note("插件提供的整套岛布局：形态、停靠、大小、页面和小项一次设好。应用后仍可以在下面逐项修改。") },
        });
    }

    /// <summary>
    /// docs 07 §6, 收起时显示: every chip — the ones plugins declare, plus each instance of "multiple" chips that is
    /// showing now (e.g. one per signed-in AI account) — dragged into order, switched on or off, put left or right.
    /// </summary>
    private Control ChipList()
    {
        var offered = new List<(ChipInfo Info, string Plugin)> { (Island.UI.Plugins.IslandPresenter.MascotChip, "O_O") };
        // Only running plugins: a stopped plugin never sends its chip, so switching it on here would show nothing.
        foreach (var package in _plugins.Packages.Where(p => _plugins.IsEnabled(p.Id)))
        {
            foreach (var chip in package.Manifest.Contributes.Ambient)
            {
                var side = chip.Side == "left" ? ChipSide.Left : ChipSide.Right;
                if (!chip.Multiple)
                {
                    offered.Add((new ChipInfo(ItemKeys.Of(package.Id, chip.Id, null), chip.Title ?? chip.Id, chip.Enabled ?? true, side), package.Manifest.Name));
                    continue;
                }
                // One row per instance shown now (one per account, say).
                var instances = _plugins.LiveAmbient.Where(a => a.PluginId == package.Id && a.AmbientId == chip.Id).Select(a => (a.Key, Title: a.Title ?? a.AmbientId));
                foreach (var (key, title) in instances.OrderBy(x => x.Title))
                {
                    offered.Add((new ChipInfo(key, title, chip.Enabled ?? true, side), package.Manifest.Name));
                }
            }
        }
        var panel = new StackPanel { Spacing = 12 };
        if (offered.Count == 0)
        {
            panel.Children.Add(Note("还没有插件提供小项。"));
            return panel;
        }
        var plugins = offered.ToDictionary(o => o.Info.Key, o => o.Plugin);
        var rows = new ObservableCollection<ChipInfo>(_chips.Current.Arrange(offered.Select(o => o.Info).OrderBy(c => c.Key, StringComparer.Ordinal)));
        var list = new ReorderableList { Label = "收起时显示", ItemsSource = rows };
        list.ItemTemplate = new FuncDataTemplate<ChipInfo>((chip, _) => chip is null ? new Panel() : ChipRow(chip, plugins[chip.Key]));
        list.Reordered += (_, _) => _chips.Set(_chips.Current with { Order = [.. rows.Select(r => r.Key)] });
        panel.Children.Add(list);
        panel.Children.Add(Note($"岛收起时在左右两侧显示打开的小项，每侧最多 {ChipPreferences.MaxPerSide} 个；这里只列出已启用插件的小项，有的小项只在有内容时出现（如有待办、正在专注）；在刘海两侧时，左、右即刘海的左翼和右翼。提醒会临时占据这里几秒。"));
        return panel;
    }

    private Control ChipRow(ChipInfo chip, string plugin)
    {
        var prefs = _chips.Current;
        var side = new SegmentedControl
        {
            Items =
            {
                new SegmentedOption { Value = nameof(ChipSide.Left), Label = "左" },
                new SegmentedOption { Value = nameof(ChipSide.Right), Label = "右" },
            },
            SelectedValue = prefs.SideOf(chip).ToString(),
            VerticalAlignment = VerticalAlignment.Center,
        };
        side.SelectionChanged += (_, _) =>
        {
            if (side.SelectedValue is string v && Enum.TryParse<ChipSide>(v, out var s))
            {
                _chips.Set(_chips.Current.WithSide(chip.Key, s));
            }
        };
        var toggle = new ArcSwitch { IsChecked = prefs.IsEnabled(chip), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, $"显示{chip.Title}");
        toggle.IsCheckedChanged += (_, _) => _chips.Set(_chips.Current.WithEnabled(chip.Key, toggle.IsChecked == true));

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        var titles = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { Text(chip.Title, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium) },
        };
        if (plugin != chip.Title)
        {
            titles.Children.Add(Text(plugin, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        }
        grid.Children.Add(titles);
        Grid.SetColumn(side, 1);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(side);
        grid.Children.Add(toggle);
        return grid;
    }

    /// <summary>
    /// docs 07 §6, 页面: every page the plugins offer, dragged into order, switched on or off, one of them home.
    /// Hiding a page does not stop its plugin.
    /// </summary>
    private Control PageList()
    {
        // Only running plugins: a stopped plugin's page never appears on the island.
        var offered = _plugins.Packages
            .Where(p => _plugins.IsEnabled(p.Id))
            .SelectMany(p => p.Manifest.Contributes.Home.Select(page => (Package: p, Page: page)))
            .ToDictionary(
                x => PageSnapshot.PageKey(x.Package.Id, x.Page.Id),
                x => (Info: new PageInfo(PageSnapshot.PageKey(x.Package.Id, x.Page.Id), x.Page.Title ?? x.Page.Id, x.Page.Enabled ?? true, x.Page.Icon),
                      Plugin: x.Package.Manifest.Name));
        var panel = new StackPanel { Spacing = 12 };
        if (offered.Count == 0)
        {
            panel.Children.Add(Note("还没有插件提供页面。"));
            return panel;
        }
        var rows = new ObservableCollection<PageInfo>(_pages.Current.Arrange(offered.Values.Select(o => o.Info)));
        var list = new ReorderableList { Label = "页面", ItemsSource = rows };
        list.ItemTemplate = new FuncDataTemplate<PageInfo>((page, _) => page is null ? new Panel() : PageRow(page, offered[page.Key].Plugin));
        list.Reordered += (_, _) => _pages.Set(_pages.Current with { Order = [.. rows.Select(r => r.Key)] });
        // Bound to this list: Build() replaces it, and the old list must stop listening (and stop being kept alive).
        Action<PagePreferences> pagesChanged = _ => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            // Re-template the rows so the home marks follow the change. The rows are rebuilt, which would otherwise
            // move the console's scroll position (it jumped to the bottom): keep the offset and put it back.
            var scroll = this.FindAncestorOfType<ScrollViewer>();
            var offset = scroll?.Offset ?? default;
            list.ItemTemplate = new FuncDataTemplate<PageInfo>((page, _) => page is null ? new Panel() : PageRow(page, offered[page.Key].Plugin));
            if (scroll is not null)
            {
                scroll.Offset = offset;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => scroll.Offset = offset, Avalonia.Threading.DispatcherPriority.Loaded);
                Avalonia.Threading.Dispatcher.UIThread.Post(() => scroll.Offset = offset, Avalonia.Threading.DispatcherPriority.Background);
            }
        });
        WhileAttached(list, () => _pages.Changed += pagesChanged, () => _pages.Changed -= pagesChanged);
        panel.Children.Add(list);
        panel.Children.Add(Note("拖动左侧把手调整标签顺序。点开岛时，如果有正在进行的活动就显示它的页面，否则显示首页。关闭页面不会停用插件。"));
        return panel;
    }

    private Control PageRow(PageInfo page, string plugin)
    {
        var prefs = _pages.Current;
        var visible = prefs.Visible(_plugins.Packages.Where(p => _plugins.IsEnabled(p.Id)).SelectMany(p => p.Manifest.Contributes.Home
            .Select(g => new PageInfo(PageSnapshot.PageKey(p.Id, g.Id), g.Title ?? g.Id, g.Enabled ?? true))));
        var isHome = prefs.HomeOf(visible) == page.Key;
        var toggle = new ArcSwitch { IsChecked = prefs.IsEnabled(page), VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, $"显示{page.Title}");
        toggle.IsCheckedChanged += (_, _) => _pages.Set(_pages.Current.WithEnabled(page.Key, toggle.IsChecked == true));

        Control home;
        if (isHome)
        {
            home = new Badge { Content = "首页", Size = BadgeSize.Small, VerticalAlignment = VerticalAlignment.Center };
        }
        else
        {
            var set = new ArcButton { Content = "设为首页", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, VerticalAlignment = VerticalAlignment.Center };
            set.IsVisible = prefs.IsEnabled(page); // a hidden page cannot be home
            set.Click += (_, _) => _pages.Set(_pages.Current with { Home = page.Key });
            home = set;
        }

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto"), ColumnSpacing = 12 };
        grid.Children.Add(new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                Text(page.Title, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
            },
        });
        if (plugin != page.Title)
        {
            ((StackPanel)grid.Children[0]).Children.Add(Text(plugin, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        }
        Grid.SetColumn(home, 1);
        Grid.SetColumn(toggle, 2);
        grid.Children.Add(home);
        grid.Children.Add(toggle);
        return grid;
    }

    private static string OffsetLabel(IslandDock dock) => dock == IslandDock.Top ? "水平位置" : "垂直位置";

    private static string DockNote(IslandDock dock) => dock == IslandDock.Top
        ? "停在屏幕顶部正中。"
        : "贴在屏幕边缘、上下居中：收起时是竖向胶囊（最多 4 个小项），展开时卡片从边缘展开。刘海形态在侧边时紧贴屏幕边。";

    /// <summary>A segmented choice that reports the picked value.</summary>
    private static SegmentedControl Choice((string Value, string Label)[] options, string selected, Action<string> picked)
    {
        var control = new SegmentedControl();
        foreach (var (value, label) in options)
        {
            control.Items.Add(new SegmentedOption { Value = value, Label = label });
        }
        control.SelectedValue = selected; // after the options exist, or the selection is lost
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is string v)
            {
                picked(v);
            }
        };
        return control;
    }

    private void Update(Func<IslandAppearance, IslandAppearance> change) => _store.Set(change(_store.Current));

    /// <summary>显示器: 主屏 / 跟随鼠标 / 指定屏, with a list of the connected screens for the last.</summary>
    private Control DisplayField(IslandAppearance a)
    {
        var screens = new Select { Label = "屏幕", IsVisible = a.Display == IslandDisplay.Specific, HorizontalAlignment = HorizontalAlignment.Left, MinWidth = 280 };
        var note = Note(DisplayNote(a.Display));
        var choice = Choice(
            [(nameof(IslandDisplay.Primary), "主屏"), (nameof(IslandDisplay.FollowMouse), "跟随鼠标"), (nameof(IslandDisplay.Specific), "指定屏")],
            a.Display.ToString(),
            v =>
            {
                var display = Enum.Parse<IslandDisplay>(v);
                screens.IsVisible = display == IslandDisplay.Specific;
                note.Text = DisplayNote(display);
                // Choosing 指定屏 without picking yet keeps the island where it is: the first screen in the list.
                Update(x => x with { Display = display, ScreenId = display == IslandDisplay.Specific ? x.ScreenId ?? FirstScreenId() : x.ScreenId });
            });
        screens.SelectionChanged += (_, _) =>
        {
            if (screens.SelectedValue is string id && id != _store.Current.ScreenId)
            {
                Update(x => x with { ScreenId = id });
            }
        };
        // The screens are known once the page is in a window.
        screens.AttachedToVisualTree += (_, _) => FillScreens(screens);
        return Field("显示器", choice, new StackPanel { Spacing = 8, Children = { screens, note } });
    }

    private static string DisplayNote(IslandDisplay display) => display switch
    {
        IslandDisplay.FollowMouse => "跟随鼠标：指针移到另一块屏幕时，岛随之移过去（展开时不移动）。",
        IslandDisplay.Specific => "岛固定在选中的屏幕上；那块屏幕没有连接时暂时显示在主屏上，接回后自动回去。",
        _ => "岛显示在系统的主屏幕上。",
    };

    private string? FirstScreenId() => TopLevel.GetTopLevel(this)?.Screens is { } s && (s.Primary ?? s.All.FirstOrDefault()) is { } screen
        ? ScreenIdentity.Of(screen.DisplayName, new Island.Core.Geometry.RectD(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height))
        : null;

    private void FillScreens(Select select)
    {
        if (TopLevel.GetTopLevel(this)?.Screens is not { } screens)
        {
            return;
        }
        select.Items.Clear();
        var n = 0;
        foreach (var screen in screens.All)
        {
            n++;
            var id = ScreenIdentity.Of(screen.DisplayName, new Island.Core.Geometry.RectD(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height));
            var name = string.IsNullOrWhiteSpace(screen.DisplayName) ? $"屏幕 {n}" : screen.DisplayName;
            select.Items.Add(new SelectItem
            {
                Content = $"{name}（{screen.Bounds.Width}×{screen.Bounds.Height}{(screen.IsPrimary ? "，主屏" : "")}）",
                Value = id,
            });
        }
        var all = screens.All.Select(s => (s.DisplayName, new Island.Core.Geometry.RectD(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height))).ToList();
        var index = ScreenIdentity.Find(_store.Current.ScreenId, all);
        select.SelectedValue = index >= 0 ? ((SelectItem)select.Items[index]!).Value : null;
    }

    /// <summary>极简模式: collapsed, only the time and alerts.</summary>
    private Control MinimalField(IslandAppearance a)
    {
        var toggle = new ArcSwitch { Content = "收起时只显示时间与提醒", IsChecked = a.Minimal };
        toggle.IsCheckedChanged += (_, _) => Update(x => x with { Minimal = toggle.IsChecked == true });
        return Field("极简模式", toggle, Note("开启后，收起的岛不显示其他小项（\"收起时显示\"里的设置保留，关闭后恢复）。"));
    }

    private static string ShapeNote(IslandShape shape) => shape == IslandShape.Capsule
        ? "四角圆润的胶囊。在有刘海的 Mac 上它罩住刘海、比刘海更宽，内容显示在刘海两侧；其他屏幕上悬浮在顶边下方。"
        : "贴住屏幕顶边，两侧带内凹肩部；在有刘海的 Mac 上包住刘海，内容分在左右两翼。";

    private static Control Field(string label, Control control, Control? note = null)
    {
        var panel = new StackPanel { Spacing = 8, Children = { Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), control } };
        if (note is not null)
        {
            panel.Children.Add(note);
        }
        return panel;
    }

    private static TextBlock Note(string text) => Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
}
