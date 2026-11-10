using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
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
/// Puts plugin content on the island (docs/design/07-home-and-layout.md):
/// <list type="bullet">
/// <item>Collapsed: the user's chips (docs 07 §4), unless an alert or an active or critical activity takes their place
/// (the highest priority, and among equals the one that started first).</item>
/// <item>Expanded: one page at a time, picked with the tabs in the header (overflow behind "更多 ⌄"); which page the
/// island opens on follows <see cref="SmartHome"/>. An activity that still sends an <c>expanded</c> view and names no
/// page gets a page of its own while it runs (compatibility, docs 07 §7).</item>
/// </list>
/// Must be used on the UI thread; <see cref="Post"/> marshals plugin events.
/// </summary>
public sealed class IslandPresenter
{
    // Room the overflow trigger needs when some tabs do not fit.
    private const double OverflowRoom = 64;

    private readonly IslandView _view;
    private readonly Action<string, int, string?> _dispatch;
    private readonly PagePreferencesStore _preferences;
    private readonly Func<PageSnapshot, PageInfo> _describe;
    private readonly ChipPreferencesStore _chipPreferences;
    private readonly Func<ChipSnapshot, ChipInfo> _describeChip;
    private readonly Dictionary<string, (ChipSnapshot Snapshot, ChipInfo Info)> _chips = [];
    private readonly Dictionary<string, (VNodeRenderer Renderer, Control? Control)> _chipControls = [];
    private readonly VNodeRenderer _alertRenderer;
    private AlertSnapshot? _alert;
    private IDisposable? _alertTimer;
    private bool _hasCompact;
    private (string, string)? _ownerShown;
    private readonly VNodeRenderer _compactRenderer;
    private readonly VNodeRenderer _expandedRenderer;
    private readonly VNodeRenderer _statusRenderer;
    private readonly Dictionary<(string Plugin, string Activity), (ActivitySnapshot Snapshot, long Order)> _activities = [];
    private readonly Dictionary<string, (PageSnapshot Snapshot, PageInfo Info)> _pages = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private long _order;
    private ActivitySnapshot? _current;
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
        ChipPreferencesStore chipPreferences,
        Func<ChipSnapshot, ChipInfo> describeChip)
    {
        _chipPreferences = chipPreferences;
        _describeChip = describeChip;
        _alertRenderer = new VNodeRenderer((h, p) => Send(_alert?.PluginId, h, p));
        chipPreferences.Changed += _ => Post(RefreshCompact);
        _view = view;
        _dispatch = dispatch;
        _preferences = preferences;
        _describe = describe;
        // Each half dispatches to the plugin whose view it shows.
        _compactRenderer = new VNodeRenderer((h, p) => Send(_current?.PluginId, h, p));
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

    public ActivitySnapshot? Current => _current;

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
            var visible = VisiblePages();
            var picked = _pickedByUser == _shown ? _pickedByUser : null;
            _shown = SmartHome.Choose(visible, _preferences.Current, picked, _collapsedAt is { } at ? _clock.Elapsed - at : TimeSpan.MaxValue, UrgentPages());
            if (_shown != picked)
            {
                _pickedByUser = null;
            }
            RenderPage(visible);
        }
    }

    public void Update(ActivitySnapshot snapshot)
    {
        var key = (snapshot.PluginId, snapshot.ActivityId);
        if (_activities.TryGetValue(key, out var known))
        {
            if (snapshot.Sequence < known.Snapshot.Sequence)
            {
                return; // out of order: a newer view is already shown
            }
            _activities[key] = (snapshot, known.Order);
        }
        else
        {
            _activities[key] = (snapshot, ++_order);
        }
        Refresh();
    }

    public void End(string pluginId, string activityId)
    {
        if (_activities.Remove((pluginId, activityId)))
        {
            Refresh();
        }
    }

    public void UpdateChip(ChipSnapshot snapshot)
    {
        if (snapshot.View is null)
        {
            _chips.Remove(snapshot.Key);
            _chipControls.Remove(snapshot.Key);
        }
        else
        {
            if (_chips.TryGetValue(snapshot.Key, out var known) && snapshot.Sequence < known.Snapshot.Sequence)
            {
                return;
            }
            _chips[snapshot.Key] = (snapshot, _describeChip(snapshot));
        }
        RefreshCompact();
    }

    /// <summary>Shows an alert in place of the chips for its duration (docs 07 §4); a newer alert replaces it.</summary>
    public void ShowAlert(AlertSnapshot alert)
    {
        _alert = alert;
        _alertTimer?.Dispose();
        _alertTimer = DispatcherTimer.RunOnce(() =>
        {
            _alert = null;
            RefreshCompact();
        }, alert.Duration);
        RefreshCompact();
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

    /// <summary>Legacy pages: activities that send an expanded view and name no page of their own.</summary>
    private static string LegacyKey(ActivitySnapshot s) => PageSnapshot.PageKey(s.PluginId, "@" + s.ActivityId);

    private IEnumerable<(PageSnapshot Snapshot, PageInfo Info)> AllPages()
    {
        foreach (var page in _pages.Values)
        {
            yield return page;
        }
        foreach (var (snapshot, _) in _activities.Values.OrderBy(a => a.Order))
        {
            if (snapshot.View.Expanded is not null && snapshot.View.Page is null)
            {
                var key = LegacyKey(snapshot);
                yield return (new PageSnapshot(snapshot.PluginId, "@" + snapshot.ActivityId, snapshot.Title, snapshot.View.Expanded, snapshot.Sequence),
                    new PageInfo(key, snapshot.Title));
            }
        }
    }

    private IReadOnlyList<PageInfo> VisiblePages() => _preferences.Current.Visible(AllPages().Select(p => p.Info));

    /// <summary>Pages of active or critical activities, most important and then most recent first.</summary>
    private IEnumerable<string> UrgentPages() => _activities.Values
        .Where(a => a.Snapshot.Priority >= ActivityPriority.Active)
        .OrderByDescending(a => a.Snapshot.Priority)
        .ThenByDescending(a => a.Order)
        .Select(a => a.Snapshot.View.Page is { } page ? PageSnapshot.PageKey(a.Snapshot.PluginId, page)
            : a.Snapshot.View.Expanded is not null ? LegacyKey(a.Snapshot) : null)
        .OfType<string>();

    private void Refresh()
    {
        RefreshCompact();

        var visible = VisiblePages();
        if (_shown is null || visible.All(p => p.Key != _shown))
        {
            // Nothing chosen yet, or the page went away (plugin stopped, page hidden): choose again.
            _shown = SmartHome.Choose(visible, _preferences.Current, null, TimeSpan.MaxValue, UrgentPages());
            _pickedByUser = null;
        }
        RenderPage(visible);
    }

    /// <summary>
    /// The collapsed island, in order of precedence: an alert; an active or critical activity with a compact view
    /// (it owns the island while it lasts); otherwise the user's chips, left and right. Activities that still send
    /// a compact view at a lower priority show as chips of their own (compatibility, docs 07 §7).
    /// </summary>
    private void RefreshCompact()
    {
        _current = _activities.Values
            .Where(a => a.Snapshot.Priority >= ActivityPriority.Active && HasCompact(a.Snapshot))
            .OrderByDescending(a => a.Snapshot.Priority)
            .ThenBy(a => a.Order)
            .Select(a => a.Snapshot)
            .FirstOrDefault();

        Control? leading, trailing;
        if (_alert is { } alert)
        {
            leading = _alertRenderer.Render(alert.Leading, null);
            trailing = _alertRenderer.Render(alert.Trailing, null);
        }
        else if (_current is { } owner)
        {
            // Update in place only what this renderer drew last time; chips and alerts are other renderers' controls.
            var mine = _ownerShown == (owner.PluginId, owner.ActivityId);
            leading = _compactRenderer.Render(owner.View.CompactLeading, mine ? _view.CompactLeading.Content as Control : null);
            trailing = _compactRenderer.Render(owner.View.CompactTrailing, mine ? _view.CompactTrailing.Content as Control : null);
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
        _ownerShown = _alert is null && _current is { } shown ? (shown.PluginId, shown.ActivityId) : null;
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

    private static bool HasCompact(ActivitySnapshot s) => s.View.CompactLeading is not null || s.View.CompactTrailing is not null;

    /// <summary>Plugin chips, then the compact halves of lower-priority activities as chips (leading left, trailing right).</summary>
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
        // In key order, so chips nobody has ordered yet appear the same way here and in the console.
        foreach (var (snapshot, info) in _chips.Values.OrderBy(c => c.Info.Key, StringComparer.Ordinal))
        {
            yield return (snapshot.View!, snapshot.PluginId, info);
        }
        foreach (var (s, _) in _activities.Values.OrderBy(a => a.Order))
        {
            var key = LegacyKey(s);
            if (s.View.CompactLeading is { } l)
            {
                yield return (l, s.PluginId, new ChipInfo(key + ":leading", s.Title, true, ChipSide.Left));
            }
            if (s.View.CompactTrailing is { } t)
            {
                yield return (t, s.PluginId, new ChipInfo(key + ":trailing", s.Title, true, ChipSide.Right));
            }
        }
    }

    // Docked at a side: at most four chips in all, two from each group.
    private const int SidePerGroup = 2;
    private bool _vertical;

    /// <summary>Stacks the collapsed island's chips vertically (docked to a side edge) or lays them out in a row.</summary>
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
            // A chip is small (docs 07 §4): at most 64 pt, and never wrapped. Not clipped either: the trailing glyph of
            // a value such as "43%" was being cut at the chip's edge.
            if (control.Parent is Panel old)
            {
                old.Children.Remove(control);
            }
            control.MaxWidth = 64;
            control.ClipToBounds = false;
            control.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(control);
        }
        return row.Children.Count == 0 ? null : row;
    }

    private void RenderPage(IReadOnlyList<PageInfo> visible)
    {
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
