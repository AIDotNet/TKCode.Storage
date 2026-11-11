using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.LogicalTree;
using Avalonia.Media;
using Avalonia.Threading;
using Island.Core.Pages;
using ChipSide = Island.Core.Pages.ChipSide;
using Island.Core.State;
using Island.Plugins;
using Island.UI.Views;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Plugins;

/// <summary>
/// Puts plugin content on the island (docs/design/09-island-v2.md, 07-home-and-layout.md):
/// <list type="bullet">
/// <item>Collapsed: an alert, else the first live activity (IslandPresenter.Live.cs), else the mascot and the ambient
/// items the user arranged in 收起时显示.</item>
/// <item>Expanded: the first activity's card, or home: one page at a time, picked with the tabs in the header (overflow
/// behind "更多 ⌄"); which page home opens on follows <see cref="SmartHome"/>.</item>
/// </list>
/// Must be used on the UI thread; <see cref="Post"/> marshals plugin events.
/// </summary>
public sealed partial class IslandPresenter
{
    // Room the overflow trigger needs when some tabs do not fit.
    private const double OverflowRoom = 64;

    private readonly IslandView _view;
    private readonly Action<string, int, string?> _dispatch;
    private readonly PagePreferencesStore _preferences;
    private readonly Func<PageSnapshot, PageInfo> _describe;
    private readonly ChipPreferencesStore _chipPreferences;
    private readonly Dictionary<string, (VNodeRenderer Renderer, Control? Control)> _chipControls = [];
    private bool _hasCompact;
    private readonly VNodeRenderer _expandedRenderer;
    private readonly VNodeRenderer _statusRenderer;
    private readonly Dictionary<string, (PageSnapshot Snapshot, PageInfo Info)> _pages = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private string? _shown;
    private string? _pickedByUser;
    private TimeSpan? _collapsedAt;
    private IslandState _state = IslandState.Idle;
    private double _tabRoom = double.PositiveInfinity;
    private bool _updatingTabs;
    private string? _overflowSignature;

    public IslandPresenter(
        IslandView view,
        Action<string, int, string?> dispatch,
        PagePreferencesStore preferences,
        Func<PageSnapshot, PageInfo> describe,
        ChipPreferencesStore chipPreferences)
    {
        _chipPreferences = chipPreferences;
        chipPreferences.Changed += _ => Post(RefreshCompact);
        _view = view;
        _dispatch = dispatch;
        _preferences = preferences;
        _describe = describe;
        _expandedRenderer = new VNodeRenderer((h, p) => Send(_shown is { } k && _pages.TryGetValue(k, out var pg) ? pg.Snapshot.PluginId : null, h, p));
        _statusRenderer = new VNodeRenderer((h, p) => Send(_shown is { } k && _pages.TryGetValue(k, out var pg) ? pg.Snapshot.PluginId : null, h, p));
        view.PageTabs.Picked += key =>
        {
            if (!_updatingTabs)
            {
                PickByUser(key);
            }
        };
        preferences.Changed += _ => Post(Refresh);

    }

    /// <summary>Raised after the expanded content was re-rendered (its height may have changed).</summary>
    public event Action? ExpandedContentChanged;

    /// <summary>Raised when the collapsed island gets something to show, or has nothing left (it then rests in Idle).</summary>
    public event Action<bool>? HasCompactContentChanged;

    /// <summary>Raised after the collapsed content was re-rendered (its width may have changed).</summary>
    public event Action? CompactContentChanged;

    /// <summary>The page the expanded island shows (<c>plugin/page</c>).</summary>
    public string? ShownPage => _shown;

    public static void Post(Action action) => Dispatcher.UIThread.Post(action, DispatcherPriority.Normal);

    /// <summary>Shows the first page of a plugin, as picking its tab would.</summary>
    public void Pick(string pluginId)
    {
        if (VisiblePages().FirstOrDefault(p => p.Key.StartsWith(pluginId + "/", StringComparison.Ordinal)) is { } page)
        {
            PickByUser(page.Key);
        }
    }

    /// <summary>Shows the page <paramref name="key"/> (<c>plugin/page</c>) as if picked; false when it is not shown.</summary>
    public bool ShowPage(string key)
    {
        if (VisiblePages().All(p => p.Key != key))
        {
            return false;
        }
        PickByUser(key);
        return true;
    }

    /// <summary>How wide the tab area may be, in island units; the controller knows the header's geometry.</summary>
    public void SetTabRoom(double room)
    {
        if (Math.Abs(room - _tabRoom) > 0.5)
        {
            _tabRoom = room;
            UpdateTabs(VisiblePages());
        }
    }

    /// <summary>The island changed state: opening it applies the smart home rule (docs 07 §3.1).</summary>
    public void OnStateChanged(IslandState state)
    {
        var was = _state;
        _state = state;
        if (was == IslandState.Expanded && state != IslandState.Expanded)
        {
            _collapsedAt = _clock.Elapsed;
        }
        else if (was != IslandState.Expanded && state == IslandState.Expanded)
        {
            if (OpenLive())
            {
                return; // the first activity's card (09 §2); home is one click away
            }
            var visible = VisiblePages();
            var picked = _pickedByUser == _shown ? _pickedByUser : null;
            _shown = SmartHome.Choose(visible, _preferences.Current, picked, _collapsedAt is { } at ? _clock.Elapsed - at : TimeSpan.MaxValue, []);
            if (_shown != picked)
            {
                _pickedByUser = null;
            }
            RenderPage(visible);
        }
    }

    public void UpdatePage(PageSnapshot snapshot)
    {
        if (_pages.TryGetValue(snapshot.Key, out var known) && snapshot.Sequence < known.Snapshot.Sequence)
        {
            return;
        }
        _pages[snapshot.Key] = (snapshot, _describe(snapshot));
        Refresh();
    }

    public void EndPage(string pluginId, string pageId)
    {
        if (_pages.Remove(PageSnapshot.PageKey(pluginId, pageId)))
        {
            Refresh();
        }
    }

    private void PickByUser(string key)
    {
        _shown = key;
        _pickedByUser = key;
        RenderPage(VisiblePages());
    }

    private void Send(string? pluginId, int handler, string? payload)
    {
        if (pluginId is not null)
        {
            _dispatch(pluginId, handler, payload);
        }
    }

    private IEnumerable<(PageSnapshot Snapshot, PageInfo Info)> AllPages() => _pages.Values;

    private IReadOnlyList<PageInfo> VisiblePages() => _preferences.Current.Visible(AllPages().Select(p => p.Info));

    private void Refresh()
    {
        RefreshCompact();

        var visible = VisiblePages();
        if (_shown is null || visible.All(p => p.Key != _shown))
        {
            // Nothing chosen yet, or the page went away (plugin stopped, page hidden): choose again.
            _shown = SmartHome.Choose(visible, _preferences.Current, null, TimeSpan.MaxValue, []);
            _pickedByUser = null;
        }
        RenderPage(visible);
    }

    /// <summary>
    /// The collapsed island, in order of precedence (09 §2): an alert, else the first live activity; otherwise the
    /// mascot and the ambient items the user arranged, left and right.
    /// </summary>
    private void RefreshCompact()
    {
        Control? leading, trailing;
        SetAccent(_view.CompactPanel, null); // the first activity sets its own below; chips and alerts use the app's
        if (LiveCompact() is { } live)
        {
            (leading, trailing) = live;
        }
        else if (_minimal)
        {
            // 极简模式: only the time (alerts and urgent activities above still take its place).
            List<(VNode View, string PluginId, ChipInfo Info)> time = [(TimeView(), "o_o", TimeChip)];
            leading = null;
            trailing = ChipRow([TimeChip], time, HorizontalAlignment.Right);
        }
        else
        {
            var offered = AllChips().ToList();
            var (left, right) = _chipPreferences.Current.Visible(offered.Select(c => c.Info));
            // Docked at a side the island is an upright pill: the two groups stack, and fewer chips fit (docs 07 §5).
            if (_vertical)
            {
                left = [.. left.Take(SidePerGroup)];
                right = [.. right.Take(SidePerGroup)];
            }
            leading = ChipRow(left, offered, HorizontalAlignment.Left);
            trailing = ChipRow(right, offered, HorizontalAlignment.Right);
        }
        Unclip(leading);
        Unclip(trailing);
        if (!ReferenceEquals(_view.CompactLeading.Content, leading))
        {
            _view.CompactLeading.Content = leading;
        }
        if (!ReferenceEquals(_view.CompactTrailing.Content, trailing))
        {
            _view.CompactTrailing.Content = trailing;
        }

        var has = leading is not null || trailing is not null;
        if (has && !_hasCompact)
        {
            Island.Core.StartupTrace.Mark("first compact content rendering");
        }
        CompactContentChanged?.Invoke();
        if (has != _hasCompact)
        {
            _hasCompact = has;
            HasCompactContentChanged?.Invoke(has);
        }
    }

    /// <summary>
    /// O_O's own chip: the mascot, on the left, shown unless the user turns it off in 收起时显示 (it belongs to the
    /// shell, not to a plugin, so it is there whatever plugins are on).
    /// </summary>
    public static readonly ChipInfo MascotChip = new("o_o/mascot", "吉祥物", EnabledByDefault: true, DefaultSide: ChipSide.Left);

    private static readonly VNode MascotView = new("mascot", null,
        new Dictionary<string, object> { ["size"] = 24.0, ["status"] = "idle" }, new Dictionary<string, int>(), []);

    private IEnumerable<(VNode View, string PluginId, ChipInfo Info)> AllChips()
    {
        yield return (MascotView, "o_o", MascotChip);
        // In key order, so items nobody has ordered yet appear the same way here and in the console.
        foreach (var ambient in AmbientChips())
        {
            yield return ambient;
        }
    }

    // Docked at a side: at most four chips in all, two from each group.
    private const int SidePerGroup = 2;
    private bool _vertical;

    /// <summary>Stacks the collapsed island's chips vertically (docked to a side edge) or lays them out in a row.</summary>
    /// <summary>The time chip of 极简模式, drawn by O_O itself.</summary>
    public static readonly ChipInfo TimeChip = new("o_o/time", "时间", EnabledByDefault: true, DefaultSide: ChipSide.Right);

    private static VNode TimeView() => new("text", null,
        new Dictionary<string, object>
        {
            ["text"] = DateTime.Now.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
            ["size"] = "sm",
            ["weight"] = "semibold",
            ["tabular"] = true,
        },
        new Dictionary<string, int>(), []);

    private bool _minimal;
    private DispatcherTimer? _minuteTimer;

    /// <summary>
    /// 极简模式 (docs 07 §5): collapsed, the island shows only the time and alerts — no chips. The time is O_O's own,
    /// whatever plugins are on.
    /// </summary>
    public bool Minimal
    {
        get => _minimal;
        set
        {
            if (_minimal == value)
            {
                return;
            }
            _minimal = value;
            _minuteTimer?.Stop();
            _minuteTimer = null;
            if (value)
            {
                // Refresh on every minute boundary while the time is shown.
                _minuteTimer = new DispatcherTimer(TimeSpan.FromSeconds(60 - DateTime.Now.Second + 0.2), DispatcherPriority.Background, (s, _) =>
                {
                    ((DispatcherTimer)s!).Interval = TimeSpan.FromSeconds(60 - DateTime.Now.Second + 0.2);
                    RefreshCompact();
                });
                _minuteTimer.Start();
            }
            _chipControls.Clear();
            RefreshCompact();
        }
    }

    public bool Vertical
    {
        get => _vertical;
        set
        {
            if (_vertical != value)
            {
                _vertical = value;
                _chipControls.Clear(); // controls belong to rows of the old orientation
                RefreshCompact();
            }
        }
    }

    private Control? ChipRow(IReadOnlyList<ChipInfo> chips, List<(VNode View, string PluginId, ChipInfo Info)> offered, HorizontalAlignment align)
    {
        if (chips.Count == 0)
        {
            return null;
        }
        var row = _vertical
            ? new StackPanel { Orientation = Orientation.Vertical, Spacing = 10, HorizontalAlignment = HorizontalAlignment.Center }
            : new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, HorizontalAlignment = align, VerticalAlignment = VerticalAlignment.Center };
        foreach (var chip in chips)
        {
            var (view, plugin, _) = offered.First(o => o.Info.Key == chip.Key);
            if (!_chipControls.TryGetValue(chip.Key, out var entry))
            {
                var pluginId = plugin;
                entry = (new VNodeRenderer((h, p) => Send(pluginId, h, p)), null);
            }
            var control = entry.Renderer.Render(view, entry.Control);
            _chipControls[chip.Key] = (entry.Renderer, control);
            if (control is null)
            {
                continue;
            }
            // A chip is small (docs 07 §4): at most 96 pt (an icon and five CJK characters), and never wrapped.
            if (control.Parent is Panel old)
            {
                old.Children.Remove(control);
            }
            control.MaxWidth = 96;
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
        }
        return row.Children.Count == 0 ? null : row;
    }

    /// <summary>
    /// The collapsed content paints its glyphs whole. A text block clips to its layout box by default, and a semibold
    /// "%" overhangs its advance: the trailing glyph of "0%" at the island's right end was being cut. Unclipping the
    /// chip alone was not enough; the text inside it was doing the clipping.
    /// </summary>
    private static void Unclip(Control? content)
    {
        if (content is null)
        {
            return;
        }
        foreach (var c in content.GetSelfAndLogicalDescendants().OfType<TextBlock>())
        {
            c.ClipToBounds = false;
        }
    }

    private void RenderPage(IReadOnlyList<PageInfo> visible)
    {
        if (_showingCard && _state == IslandState.Expanded)
        {
            return; // an activity's card is open; home renders when the user goes there
        }
        var page = AllPages().FirstOrDefault(p => p.Info.Key == _shown);
        _view.ExpandedContent.Content = _expandedRenderer.Render(page.Snapshot?.View, _view.ExpandedContent.Content as Control);
        // The header says what the page has to say about itself; a page with nothing to say leaves it empty.
        _view.HeaderStatus.Content = _statusRenderer.Render(page.Snapshot?.Status, _view.HeaderStatus.Content as Control);
        UpdateTabs(visible);
        ExpandedContentChanged?.Invoke();
    }

    private void UpdateTabs(IReadOnlyList<PageInfo> visible)
    {
        _updatingTabs = true;
        try
        {
            _view.ExpandedTitle.IsVisible = visible.Count == 0;
            // As many tabs as fit, in order; the rest behind "更多 ⌄". The page on show is always a tab or the
            // overflow's label, so the header always says where the user is.
            var widths = visible.Select(p => TabWidth(p.Title)).ToList();
            var fit = visible.Count;
            if (widths.Sum() > _tabRoom)
            {
                fit = 0;
                for (double used = 0; fit < visible.Count && used + widths[fit] <= _tabRoom - OverflowRoom; fit++)
                {
                    used += widths[fit];
                }
            }
            _view.PageTabs.SetItems([.. visible.Take(fit).Select(p => (p.Key, p.Title))]);
            _view.PageTabs.Selected = _shown;
            UpdateOverflow([.. visible.Skip(fit)]);
        }
        finally
        {
            _updatingTabs = false;
        }
    }

    private void UpdateOverflow(IReadOnlyList<PageInfo> rest)
    {
        var menu = _view.PageOverflow;
        menu.IsVisible = rest.Count > 0;
        // Pages re-render often (some every second); rebuilding the trigger each time made it flicker. Only rebuild
        // when what it shows changes: the pages behind it, or which one is on show.
        var signature = string.Join("\n", rest.Select(p => p.Key + "\t" + p.Title)) + "\n" + _shown;
        if (rest.Count == 0 || signature == _overflowSignature)
        {
            _overflowSignature = rest.Count == 0 ? null : signature;
            return;
        }
        _overflowSignature = signature;
        var current = rest.FirstOrDefault(p => p.Key == _shown);
        if (menu.Trigger is not ArcButton trigger)
        {
            trigger = new ArcButton { Variant = ButtonVariant.Ghost, Size = ControlSize.Small, Padding = new Thickness(8, 0) };
            menu.Trigger = trigger;
        }
        // Like the tabs: the page on show is white and semibold, otherwise the trigger is a quiet grey "更多".
        trigger.FontWeight = current is null ? FontWeight.Normal : FontWeight.SemiBold;
        trigger.Bind(Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty,
            trigger.GetResourceObservable(current is null ? "IslandSecondaryBrush" : "IslandForegroundBrush"));
        trigger.Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children =
            {
                new TextBlock { Text = current?.Title ?? "更多", VerticalAlignment = VerticalAlignment.Center },
                new LucideIcon { Kind = LucideIconKind.ChevronDown, Size = 14, VerticalAlignment = VerticalAlignment.Center },
            },
        };
        menu.Items.Clear();
        foreach (var page in rest)
        {
            var entry = new MenuEntry
            {
                Header = page.Title,
                ToggleType = MenuEntryToggleType.Radio,
                GroupName = "pages",
                IsChecked = page.Key == _shown,
            };
            var key = page.Key;
            entry.Click += (_, _) => PickByUser(key);
            menu.Items.Add(entry);
        }
    }

    /// <summary>The width of a tab: its title at the tab's size and weight, plus the button's padding and spacing.</summary>
    private double TabWidth(string title)
    {
        var text = new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, FontSize = _view.ExpandedTitle.FontSize };
        text.Measure(Size.Infinity);
        return Math.Ceiling(text.DesiredSize.Width) + 16 + _view.PageTabs.Spacing;
    }

}
