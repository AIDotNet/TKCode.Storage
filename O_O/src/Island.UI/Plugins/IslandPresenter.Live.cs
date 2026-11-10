using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.Core.Activities;
using Island.Core.Pages;
using Island.Core.State;
using Island.Plugins;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Plugins;

/// <summary>
/// The 2.0 island (docs/design/09-island-v2.md §2): live activities arbitrated by priority, the first beside the notch
/// and the second as a bubble, alerts one at a time, ambient items beside the idle island, and the expanded island as
/// either the first activity's card (fixed regions) or home (the pages).
/// </summary>
public sealed partial class IslandPresenter
{
    private readonly Dictionary<string, LiveActivitySnapshot> _live = new(StringComparer.Ordinal);
    private readonly Dictionary<string, AmbientSnapshot> _ambientItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (VNodeRenderer Renderer, Control? Control)> _liveCompact = new(StringComparer.Ordinal);
    private readonly HashSet<string> _setAside = new(StringComparer.Ordinal);
    private readonly AlertQueue _alerts = new();
    private readonly Dictionary<string, LiveAlertSnapshot> _alertViews = new(StringComparer.Ordinal);
    private DispatcherTimer? _alertTick;
    private ActivityPreferencesStore? _activityPreferences;
    private ActivityArrangement _arrangement = ActivityArrangement.Empty;
    private string? _front;
    private bool _wantHome;
    private bool _showingCard;
    private string? _cardKey;
    private readonly VNodeRenderer[] _cardRenderers = new VNodeRenderer[4];
    private readonly Control?[] _cardControls = new Control?[4];
    private Grid? _card;

    /// <summary>The user's say over activities; null: every activity is allowed, in the plugins' own order.</summary>
    public ActivityPreferencesStore? ActivityPreferences
    {
        get => _activityPreferences;
        set
        {
            _activityPreferences = value;
            if (value is not null)
            {
                value.Changed += _ => Post(Rearrange);
            }
            Rearrange();
        }
    }

    /// <summary>The activity shown as the bubble beside the island changed (null: no bubble).</summary>
    public event Action<LiveActivitySnapshot?>? BubbleChanged;

    /// <summary>The user asked to open an activity's source (⌘-click): the application, or a link.</summary>
    public event Action<LiveActivityOpen>? OpenRequested;

    /// <summary>Which activity is in the compact place and which is the bubble.</summary>
    public ActivityArrangement Arrangement => _arrangement;

    public void UpdateLive(LiveActivitySnapshot snapshot)
    {
        if (_live.TryGetValue(snapshot.Key, out var known) && snapshot.Sequence < known.Sequence)
        {
            return;
        }
        _live[snapshot.Key] = snapshot;
        Rearrange();
    }

    public void EndLive(string key)
    {
        if (_live.Remove(key))
        {
            _liveCompact.Remove(key);
            _setAside.Remove(key);
            if (_front == key)
            {
                _front = null;
            }
            _alerts.Remove(key, _clock.Elapsed);
            Rearrange();
        }
    }

    /// <summary>Shows an alert in its turn (09 §2, 提醒), unless the user turned its kind off.</summary>
    public void RaiseLiveAlert(LiveAlertSnapshot alert)
    {
        if (_activityPreferences?.Current.MayAlert(alert.Kind) == false)
        {
            return;
        }
        _alertViews[alert.Key] = alert;
        _alerts.Enqueue(new IslandAlert(alert.Key, alert.Kind, Priority(alert.Priority), alert.Duration), _clock.Elapsed);
        EnsureAlertTick();
        RefreshCompact();
    }

    public void UpdateAmbient(AmbientSnapshot snapshot)
    {
        if (snapshot.View is null)
        {
            _ambientItems.Remove(snapshot.Key);
            _chipControls.Remove(AmbientChipKey(snapshot.Key));
        }
        else
        {
            if (_ambientItems.TryGetValue(snapshot.Key, out var known) && snapshot.Sequence < known.Sequence)
            {
                return;
            }
            _ambientItems[snapshot.Key] = snapshot;
        }
        RefreshCompact();
    }

    /// <summary>Brings an activity to the compact place (the user clicked its bubble or scrolled to it).</summary>
    public void BringToFront(string key)
    {
        if (!_live.ContainsKey(key))
        {
            return;
        }
        _front = key;
        _setAside.Remove(key);
        Rearrange();
    }

    /// <summary>Moves to the next (+1) or previous (-1) activity: horizontal scrolling on the island.</summary>
    public void Cycle(int direction)
    {
        var all = _arrangement.All.ToList();
        if (all.Count < 2 || _arrangement.Primary is not { } primary)
        {
            return;
        }
        var i = all.FindIndex(a => a.Key == primary.Key);
        BringToFront(all[((i + direction) % all.Count + all.Count) % all.Count].Key);
    }

    /// <summary>Sets the first activity aside (it keeps running, at the end of the queue): scrolling up on the island.</summary>
    public void SetAsideCurrent()
    {
        if (_arrangement.Primary is { } primary && _arrangement.Secondary is not null)
        {
            _setAside.Add(primary.Key);
            if (_front == primary.Key)
            {
                _front = null;
            }
            Rearrange();
        }
    }

    /// <summary>⌘-click: asks to open the first activity's source; false when it declared none.</summary>
    public bool OpenCurrentSource()
    {
        if (_arrangement.Primary is { } primary && _live.TryGetValue(primary.Key, out var s) && s.Open is { } open)
        {
            OpenRequested?.Invoke(open);
            return true;
        }
        return false;
    }

    /// <summary>The next time the island opens, it opens home rather than the first activity's card.</summary>
    public void OpenHomeNext() => _wantHome = true;

    private static IslandPriority Priority(LivePriority p) => p switch
    {
        LivePriority.Passive => IslandPriority.Passive,
        LivePriority.High => IslandPriority.High,
        LivePriority.Critical => IslandPriority.Critical,
        _ => IslandPriority.Normal,
    };

    private void Rearrange()
    {
        var entries = _live.Values.Select(s => new ActivityEntry(s.Key, s.Kind, Priority(s.Priority), s.Sequence));
        var before = _arrangement;
        _arrangement = ActivityArbiter.Arrange(entries, _activityPreferences?.Current ?? new ActivityPreferences(), _front, _setAside);
        if (before.Secondary?.Key != _arrangement.Secondary?.Key ||
            (_arrangement.Secondary is { } s && before.Secondary is { } b && s.Updated != b.Updated))
        {
            BubbleChanged?.Invoke(_arrangement.Secondary is { } second ? _live[second.Key] : null);
        }
        RefreshCompact();
        if (_state == IslandState.Expanded && _showingCard)
        {
            if (_arrangement.Primary is { } primary)
            {
                RenderCard(_live[primary.Key]);
            }
            else
            {
                ShowHome(); // the activity ended while its card was open
            }
        }
    }

    private void EnsureAlertTick()
    {
        _alertTick ??= new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Normal, (_, _) =>
        {
            var shown = _alerts.Current?.Key;
            _alerts.Advance(_clock.Elapsed);
            if (_alerts.Current is null && _alerts.Waiting == 0)
            {
                _alertTick!.Stop();
            }
            if (_alerts.Current?.Key != shown)
            {
                RefreshCompact();
            }
        });
        _alertTick.Start();
    }

    // ---- the collapsed island ------------------------------------------------------------------------------------

    /// <summary>
    /// What the 2.0 model puts on the collapsed island: the current alert, else the first activity's compact regions.
    /// Null when it has nothing to say (the chips and ambient items show).
    /// </summary>
    private (Control? Leading, Control? Trailing)? LiveCompact()
    {
        if (_alerts.Current is { } current && _alertViews.TryGetValue(current.Key, out var alert))
        {
            var r = Renderer(alert.Key + ":alert", alert.PluginId);
            var leading = r.Renderer.Render(alert.View.Leading, null);
            var trailing = Row(r.Renderer.Render(alert.View.Center, null), r.Renderer.Render(alert.View.Trailing, null));
            return (leading, trailing);
        }
        if (_arrangement.Primary is { } primary && _live.TryGetValue(primary.Key, out var s))
        {
            var lead = Renderer(s.Key + ":leading", s.PluginId);
            var trail = Renderer(s.Key + ":trailing", s.PluginId);
            var compact = s.View.Compact;
            // An activity with only a minimal view shows that, beside the notch.
            var leading = Remember(s.Key + ":leading", lead.Renderer.Render(compact.Leading ?? (compact.Trailing is null ? s.View.Minimal : null), lead.Control));
            var trailing = Remember(s.Key + ":trailing", trail.Renderer.Render(compact.Trailing, trail.Control));
            return (leading, trailing);
        }
        return null;
    }

    private (VNodeRenderer Renderer, Control? Control) Renderer(string key, string pluginId)
    {
        if (!_liveCompact.TryGetValue(key, out var entry))
        {
            entry = (new VNodeRenderer((h, p) => Send(pluginId, h, p)), null);
            _liveCompact[key] = entry;
        }
        return entry;
    }

    private Control? Remember(string key, Control? control)
    {
        _liveCompact[key] = (_liveCompact[key].Renderer, control);
        return control;
    }

    private static Control? Row(Control? a, Control? b)
    {
        if (a is null || b is null)
        {
            return a ?? b;
        }
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        row.Children.Add(a);
        row.Children.Add(b);
        return row;
    }

    /// <summary>
    /// An ambient item is arranged under the same key (<c>plugin/id</c>) as the chip it replaces, so the user's order,
    /// sides and switches in 收起时显示 — and layout presets — carry over to the 2.0 plugins unchanged.
    /// </summary>
    private static string AmbientChipKey(string key) => key;

    /// <summary>Ambient items, offered with the chips (the user arranges both in 收起时显示).</summary>
    private IEnumerable<(VNode View, string PluginId, ChipInfo Info)> AmbientChips() =>
        _ambientItems.Values.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => (a.View!, a.PluginId,
            new ChipInfo(AmbientChipKey(a.Key), a.Title ?? a.AmbientId, a.EnabledByDefault, a.Side == "left" ? ChipSide.Left : ChipSide.Right)));

    // ---- the expanded island: the activity's card, or home --------------------------------------------------------

    /// <summary>Called as the island opens: the first activity's card unless home was asked for or nothing runs.</summary>
    private bool OpenLive()
    {
        var home = _wantHome;
        _wantHome = false;
        if (home || _arrangement.Primary is not { } primary || !_live.TryGetValue(primary.Key, out var s) || s.View.Expanded.IsEmpty)
        {
            _showingCard = false;
            SetHomeChrome(true);
            return false;
        }
        RenderCard(s);
        return true;
    }

    private void ShowHome()
    {
        _showingCard = false;
        SetHomeChrome(true);
        RenderPage(VisiblePages());
    }

    private void SetHomeChrome(bool home)
    {
        _view.PageTabs.IsVisible = home;
        _view.PageOverflow.IsVisible = home && _view.PageOverflow.Items.Count > 0;
        if (home)
        {
            _view.ExpandedTitle.Text = "O_O";
        }
    }

    /// <summary>The card (09 §3): leading | center | trailing on top, bottom underneath; a 主页 button in the header.</summary>
    private void RenderCard(LiveActivitySnapshot s)
    {
        _showingCard = true;
        if (_cardKey != s.Key)
        {
            _cardKey = s.Key;
            for (var i = 0; i < _cardRenderers.Length; i++)
            {
                var pluginId = s.PluginId;
                _cardRenderers[i] = new VNodeRenderer((h, p) => Send(pluginId, h, p));
                _cardControls[i] = null;
            }
            _card = null;
        }
        var regions = s.View.Expanded;
        VNode?[] nodes = [regions.Leading, regions.Center, regions.Trailing, regions.Bottom];
        for (var i = 0; i < nodes.Length; i++)
        {
            _cardControls[i] = _cardRenderers[i].Render(nodes[i], _cardControls[i]);
        }

        _card ??= new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 14, RowSpacing = 14 };
        _card.Children.Clear();
        Place(_cardControls[0], 0, 0, VerticalAlignment.Center);
        Place(_cardControls[1], 0, 1, VerticalAlignment.Center);
        Place(_cardControls[2], 0, 2, VerticalAlignment.Center);
        if (_cardControls[3] is { } bottom)
        {
            Detach(bottom);
            Grid.SetRow(bottom, 1);
            Grid.SetColumn(bottom, 0);
            Grid.SetColumnSpan(bottom, 3);
            _card.Children.Add(bottom);
        }
        _card.RowDefinitions[1].Height = _cardControls[3] is null ? new GridLength(0) : GridLength.Auto;
        _card.RowSpacing = _cardControls[3] is null ? 0 : 14;
        _view.ExpandedContent.Content = _card;

        SetHomeChrome(false);
        _view.ExpandedTitle.Text = s.Title;
        _view.ExpandedTitle.IsVisible = true;
        var home = new ArcButton { Content = "主页", Variant = TKCode.Arc.ButtonVariant.Ghost, Size = TKCode.Arc.ControlSize.Small };
        home.Click += (_, _) => ShowHome();
        _view.HeaderStatus.Content = home;
        ExpandedContentChanged?.Invoke();

        void Place(Control? c, int row, int column, VerticalAlignment align)
        {
            if (c is null)
            {
                return;
            }
            Detach(c);
            c.VerticalAlignment = align;
            Grid.SetRow(c, row);
            Grid.SetColumn(c, column);
            Grid.SetColumnSpan(c, 1);
            _card!.Children.Add(c);
        }

        static void Detach(Control c)
        {
            if (c.Parent is Panel p)
            {
                p.Children.Remove(c);
            }
        }
    }
}
