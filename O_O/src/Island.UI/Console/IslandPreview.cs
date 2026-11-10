using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.Core.Activities;
using Island.Core.Layout;
using Island.Core.Pages;
using Island.Core.State;
using Island.Plugins;
using Island.Plugins.Host;
using Island.UI.Plugins;
using Island.UI.Themes;
using Island.UI.Views;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// The 岛体 page's preview (docs/design/09-island-v2.md §5): the island collapsed and opened, drawn with the plugins'
/// real content and the user's settings as they are now, so a change shows at once. Clicks in it do not reach plugins
/// (only the home tabs switch); 在岛上试一下 opens the real island.
/// </summary>
public sealed class IslandPreview : StackPanel
{
    private readonly PluginHost _host;
    private readonly IslandMetrics _metrics = new();
    private readonly IslandView _compact = new();
    private readonly IslandView _expanded = new();
    private readonly IslandPresenter _compactPresenter;
    private readonly IslandPresenter _expandedPresenter;
    private readonly IslandFrame _compactFrame;
    private readonly IslandFrame _expandedFrame;

    public IslandPreview(PluginHost host, PagePreferencesStore pages, ChipPreferencesStore chips, ActivityPreferencesStore activities, Action? tryOnIsland)
    {
        _host = host;
        Spacing = 8;
        var screen = new ScreenTopology(new Core.Geometry.RectD(0, 0, 1440, 900), 1, Notch: null, TopInset: 0);
        _compactFrame = IslandLayout.Compute(screen, IslandPlacement.Floating, IslandState.Compact, _metrics);
        _expandedFrame = IslandLayout.Compute(screen, IslandPlacement.Floating, IslandState.Expanded, _metrics);

        _compactPresenter = Presenter(_compact, pages, chips, activities);
        _expandedPresenter = Presenter(_expanded, pages, chips, activities);
        _compactPresenter.OnStateChanged(IslandState.Compact);
        _compactPresenter.CompactContentChanged += () => Dispatcher.UIThread.Post(SizeCompact, DispatcherPriority.Background);
        _expandedPresenter.ExpandedContentChanged += () => Dispatcher.UIThread.Post(SizeExpanded, DispatcherPriority.Background);
        Prepare(_compact, _compactFrame, compact: true);
        Prepare(_expanded, _expandedFrame, compact: false);

        Children.Add(Text("预览", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.SemiBold));
        Children.Add(Stage("收起", _compact));
        Children.Add(Stage("展开", _expanded));
        if (tryOnIsland is not null)
        {
            var tryIt = new ArcButton { Content = "在岛上试一下", Variant = ButtonVariant.Secondary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            tryIt.Click += (_, _) => tryOnIsland();
            Children.Add(tryIt);
        }
        Children.Add(Text("用插件当前的内容画出；点展开预览里的页签可以切换主页的页面。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));

        Action<LiveActivitySnapshot> live = s => Post(() => Both(p => p.UpdateLive(s)));
        Action<string, string> ended = (_, key) => Post(() => Both(p => p.EndLive(key)));
        Action<AmbientSnapshot> ambient = a => Post(() => Both(p => p.UpdateAmbient(a)));
        Action<PageSnapshot> page = s => Post(() => Both(p => p.UpdatePage(s)));
        Action<string, string> pageEnded = (p, g) => Post(() => Both(x => x.EndPage(p, g)));
        Action themeChanged = () => Post(ApplyTheme);
        WhileAttached(this, () =>
        {
            // What is on show now, then what changes.
            foreach (var s in host.LivePages)
            {
                Both(p => p.UpdatePage(s));
            }
            foreach (var s in host.LiveActivities)
            {
                Both(p => p.UpdateLive(s));
            }
            foreach (var a in host.LiveAmbient)
            {
                Both(p => p.UpdateAmbient(a));
            }
            _expandedPresenter.OnStateChanged(IslandState.Compact);
            _expandedPresenter.OnStateChanged(IslandState.Expanded);
            host.LiveActivityUpdated += live;
            host.LiveActivityEnded += ended;
            host.AmbientUpdated += ambient;
            host.PageUpdated += page;
            host.PageEnded += pageEnded;
            IslandTheme.Changed += themeChanged;
            ApplyTheme();
        }, () =>
        {
            host.LiveActivityUpdated -= live;
            host.LiveActivityEnded -= ended;
            host.AmbientUpdated -= ambient;
            host.PageUpdated -= page;
            host.PageEnded -= pageEnded;
            IslandTheme.Changed -= themeChanged;
        });
    }

    private static void Post(Action action) => Dispatcher.UIThread.Post(action);

    private void Both(Action<IslandPresenter> action)
    {
        action(_compactPresenter);
        action(_expandedPresenter);
    }

    private static IslandPresenter Presenter(IslandView view, PagePreferencesStore pages, ChipPreferencesStore chips, ActivityPreferencesStore activities)
    {
        // Clicks do not reach plugins from the preview: it shows, it does not act.
        var presenter = new IslandPresenter(view, (_, _, _) => { }, pages, s => new PageInfo(s.Key, s.Title), chips)
        {
            ActivityPreferences = activities,
        };
        return presenter;
    }

    private void Prepare(IslandView view, IslandFrame frame, bool compact)
    {
        view.Surface.Width = frame.Surface.Width;
        view.Surface.Height = frame.Surface.Height;
        var c = frame.Corners;
        view.Surface.CornerRadius = new CornerRadius(c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft);
        view.CompactPanel.Width = _compactFrame.Surface.Width;
        view.CompactPanel.Height = _compactFrame.Surface.Height;
        view.ExpandedPanel.Width = _expandedFrame.Surface.Width;
        view.ExpandedHeader.Height = 40;
        view.CompactPanel.IsActive = compact;
        view.ExpandedPanel.IsActive = !compact;
        if (compact)
        {
            var columns = view.CompactGrid.ColumnDefinitions;
            columns[0].Width = GridLength.Auto;
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            columns[2].Width = GridLength.Auto;
        }
        if (!compact)
        {
            _expandedPresenter.SetTabRoom(_expandedFrame.Surface.Width - 10 - 200);
        }
    }

    /// <summary>As wide as what it shows, like the real island.</summary>
    private void SizeCompact()
    {
        _compact.CompactLeading.Measure(Size.Infinity);
        _compact.CompactTrailing.Measure(Size.Infinity);
        var width = Math.Clamp(Math.Ceiling(_compact.CompactLeading.DesiredSize.Width + _compact.CompactTrailing.DesiredSize.Width + 36), _metrics.IdleWidth, 420);
        _compact.Surface.Width = _compact.CompactPanel.Width = width;
        _compact.Surface.CornerRadius = new CornerRadius(_compactFrame.Surface.Height / 2 * Math.Min(1, IslandTheme.Current.CornerScale));
    }

    /// <summary>As tall as its content, within the island's limits.</summary>
    private void SizeExpanded()
    {
        _expanded.ExpandedPanel.Measure(new Size(_expandedFrame.Surface.Width, double.PositiveInfinity));
        _expanded.Surface.Height = Math.Clamp(Math.Ceiling(_expanded.ExpandedPanel.DesiredSize.Height), _metrics.MinExpandedHeight, _metrics.MaxExpandedHeight);
        var c = _expandedFrame.Corners;
        var k = IslandTheme.Current.CornerScale;
        var most = Math.Min(_expanded.Surface.Width, _expanded.Surface.Height) / 2;
        _expanded.Surface.CornerRadius = new CornerRadius(Math.Min(c.TopLeft * k, most), Math.Min(c.TopRight * k, most), Math.Min(c.BottomRight * k, most), Math.Min(c.BottomLeft * k, most));
    }

    private void ApplyTheme()
    {
        var animated = IslandTheme.Current.Animated;
        foreach (var view in new[] { _compact, _expanded })
        {
            view.Backdrop.Show(animated?.Preset, animated?.Colors ?? [], animated?.Speed ?? 1, still: false);
        }
        SizeCompact();
        SizeExpanded();
    }

    /// <summary>A labelled, sunken stage with the island scaled down to fit.</summary>
    private static Control Stage(string label, IslandView view)
    {
        var stage = new Border
        {
            Padding = new Thickness(12),
            Child = new Viewbox { Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly, Child = view, HorizontalAlignment = HorizontalAlignment.Center },
        };
        ArcTokens.Bind(stage, Border.BackgroundProperty, ArcDesignTokenKind.SurfaceMuted);
        ArcTokens.BindCornerRadius(stage, ArcDesignTokenKind.RadiusPanel);
        return new StackPanel
        {
            Spacing = 4,
            Children = { Text(label, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary), stage },
        };
    }
}
