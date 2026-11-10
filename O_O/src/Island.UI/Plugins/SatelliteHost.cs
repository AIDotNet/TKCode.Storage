using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using Island.Platform;
using Island.Plugins;
using TKCode.Arc;

namespace Island.UI.Plugins;

/// <summary>
/// A satellite's window: borderless, transparent, never activated, above everything like the island itself. It holds up
/// to <see cref="SatelliteHost.MaxVisible"/> notices side by side; <see cref="SatelliteHost"/> fills and places it.
/// </summary>
public sealed class SatelliteWindow : ArcWindow
{
    public SatelliteWindow()
    {
        Title = "O_O satellite";
        IsTitleBarVisible = false;
        TitleBar = null;
        ExtendClientAreaToDecorationsHint = false;
        WindowDecorations = WindowDecorations.None;
        TransparencyLevelHint = [WindowTransparencyLevel.Transparent];
        Background = Brushes.Transparent;
        CanResize = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = !OperatingSystem.IsMacOS(); // macOS: the platform layer sets a level above the menu bar
        WindowStartupLocation = WindowStartupLocation.Manual;
        Content = Row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = SatelliteHost.NoticeSpacing,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
        };
    }

    public StackPanel Row { get; }


    public IIslandWindowBridge? Bridge { get; set; }
}

/// <summary>
/// Satellite islands (docs/design/08 §4): small pills the plugins float beside the collapsed island. One window per
/// <c>plugin/satellite</c>, up to two notices side by side, the rest queued. Same key replaces in place and restarts the
/// timer; hovering pauses it; the notices step aside while the island is open or hidden for a full-screen app.
/// Everything is marshalled to the UI thread.
/// </summary>
public sealed class SatelliteHost
{
    public const int MaxVisible = 2;
    public const double NoticeSpacing = 6;
    public const double MaxNoticeWidth = 240;
    private static readonly TimeSpan SlideTime = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(100);

    private readonly IIslandPlatform _platform;
    private readonly SatellitePreferencesStore _prefs;
    private readonly Action<string, int, string?> _dispatch;
    private readonly Dictionary<string, Satellite> _satellites = new(StringComparer.Ordinal);
    private readonly DispatcherTimer _timer;
    private IslandAnchor? _anchor;

    public SatelliteHost(IIslandPlatform platform, SatellitePreferencesStore prefs, Action<string, int, string?> dispatch)
    {
        _platform = platform;
        _prefs = prefs;
        _dispatch = dispatch;
        _timer = new DispatcherTimer { Interval = Tick };
        _timer.Tick += (_, _) => OnTick();
        _prefs.Changed += () => IslandPresenter.Post(RefreshAll);
    }

    /// <summary>The preferences a console row edits.</summary>
    public SatellitePreferencesStore Preferences => _prefs;

    /// <summary>Follows the island: where it is and whether it is open or hidden.</summary>
    public void Follow(IslandController controller)
    {
        controller.AnchorChanged += a => IslandPresenter.Post(() => SetAnchor(a));
        if (controller.Anchor is { } now)
        {
            IslandPresenter.Post(() => SetAnchor(now));
        }
    }

    /// <summary>A notice was clicked: (satellite key <c>plugin/satellite</c>, notice key). Raised before the view's own handlers.</summary>
    public event Action<string, string>? NoticeClicked;

    /// <summary>Wire to <c>PluginHost.SatelliteUpdated</c>; may be called from any thread.</summary>
    public void OnUpdated(SatelliteSnapshot snapshot) => IslandPresenter.Post(() => Apply(snapshot));

    private bool Suppressed => _anchor is null || _anchor.Expanded || _anchor.Hidden;

    private void SetAnchor(IslandAnchor anchor)
    {
        _anchor = anchor;
        RefreshAll();
    }

    private void RefreshAll()
    {
        foreach (var s in _satellites.Values)
        {
            s.Refresh();
        }
        UpdateTimer();
    }

    private void Apply(SatelliteSnapshot snap)
    {
        var key = snap.SatelliteKey;
        if (snap.View is null)
        {
            if (_satellites.TryGetValue(key, out var existing))
            {
                existing.Remove(snap.Key);
            }
            UpdateTimer();
            return;
        }
        if (!_prefs.IsEnabled(key))
        {
            return;
        }
        if (!_satellites.TryGetValue(key, out var sat))
        {
            _satellites[key] = sat = new Satellite(this, snap.PluginId, key);
        }
        sat.Show(snap);
        UpdateTimer();
    }

    private void UpdateTimer()
    {
        var any = _satellites.Values.Any(s => s.Notices.Count > 0);
        if (any && !_timer.IsEnabled)
        {
            _timer.Start();
        }
        else if (!any && _timer.IsEnabled)
        {
            _timer.Stop();
        }
    }

    private void OnTick()
    {
        if (Suppressed)
        {
            return; // time stands still while the island is open: nothing is lost
        }
        foreach (var s in _satellites.Values)
        {
            s.Tick(Tick);
        }
        UpdateTimer();
    }

    private sealed class Notice
    {
        public required string Key { get; init; }
        public required Border Pill { get; init; }
        public required Border Holder { get; init; }
        public required Border BadgeHost { get; init; }
        public required TextBlock BadgeText { get; init; }
        public Control? Content { get; set; }
        public SatelliteSnapshot Snapshot { get; set; } = null!;
        public TimeSpan Remaining { get; set; }
        public bool Hovered { get; set; }
        public bool Visible { get; set; }
        public bool Leaving { get; set; }
    }

    private sealed class Satellite
    {
        private readonly SatelliteHost _host;
        private readonly string _pluginId;
        private readonly string _key;
        private readonly VNodeRenderer _renderer;
        private SatelliteWindow? _window;

        public Satellite(SatelliteHost host, string pluginId, string key)
        {
            _host = host;
            _pluginId = pluginId;
            _key = key;
            _renderer = new VNodeRenderer((h, p) => _host._dispatch(_pluginId, h, p));
        }

        public List<Notice> Notices { get; } = [];

        public void Show(SatelliteSnapshot snap)
        {
            var notice = Notices.FirstOrDefault(n => n.Key == snap.Key && !n.Leaving);
            if (notice is null)
            {
                notice = NewNotice(snap.Key);
                Notices.Add(notice);
            }
            notice.Snapshot = snap;
            // Duration zero: stays until the plugin hides it (an unread count, say).
            notice.Remaining = snap.Duration <= TimeSpan.Zero ? TimeSpan.MaxValue : TimeSpan.FromSeconds(Math.Max(1, snap.Duration.TotalSeconds));
            notice.Content = _renderer.Render(snap.View, notice.Content);
            notice.Holder.Child = notice.Content;
            notice.BadgeHost.IsVisible = snap.Badge is > 0;
            notice.BadgeText.Text = snap.Badge is > 99 ? "99+" : snap.Badge?.ToString();
            Refresh();
        }

        public void Remove(string key)
        {
            foreach (var n in Notices.Where(n => key.Length == 0 || n.Key == key).ToList())
            {
                Leave(n);
            }
            Refresh();
        }

        public void Tick(TimeSpan elapsed)
        {
            foreach (var n in Notices.Where(n => n.Visible && !n.Leaving && !n.Hovered).ToList())
            {
                if (n.Snapshot.Duration <= TimeSpan.Zero)
                {
                    continue;
                }
                n.Remaining -= elapsed;
                if (n.Remaining <= TimeSpan.Zero)
                {
                    Leave(n);
                }
            }
            Refresh();
        }

        private void Leave(Notice n)
        {
            if (n.Leaving)
            {
                return;
            }
            if (!n.Visible || _host.Suppressed)
            {
                Notices.Remove(n); // never shown (or nobody sees it): nothing to slide back
                return;
            }
            n.Leaving = true;
            SlideOut(n);
            DispatcherTimer.RunOnce(() =>
            {
                Notices.Remove(n);
                Refresh();
                _host.UpdateTimer();
            }, SlideTime + TimeSpan.FromMilliseconds(30));
        }

        /// <summary>Brings the window in line with the notices, the anchor and the suppression rules.</summary>
        public void Refresh()
        {
            if (!_host._prefs.IsEnabled(_key))
            {
                foreach (var n in Notices.ToList())
                {
                    Notices.Remove(n);
                }
            }
            var anchor = _host._anchor;
            if (anchor is null || Notices.Count == 0 || _host.Suppressed)
            {
                if (_window is { IsVisible: true })
                {
                    _window.Hide();
                    _window.Row.Children.Clear();
                    foreach (var n in Notices)
                    {
                        n.Visible = false; // they slide in again when the island lets go
                    }
                }
                return;
            }

            // Promote queued notices into the free slots, oldest first.
            var shown = Notices.Count(n => n.Visible && !n.Leaving);
            var window = _window ??= new SatelliteWindow();
            foreach (var n in Notices.Where(n => !n.Visible && !n.Leaving).Take(Math.Max(0, MaxVisible - shown)))
            {
                n.Visible = true;
                n.Pill.Opacity = 0;
                n.Pill.RenderTransform = Offset(anchor, window, n);
                window.Row.Children.Add(n.Pill);
                var pill = n.Pill;
                Dispatcher.UIThread.Post(() =>
                {
                    pill.Opacity = 1;
                    pill.RenderTransform = TransformOperations.Parse("translateX(0px)");
                }, DispatcherPriority.Background);
            }
            window.Row.Children.RemoveAll(window.Row.Children.Where(c => !Notices.Any(n => n.Visible && n.Pill == c)).ToList());

            // As tall as the island, unless a notice's content needs more (it would be cut off at the bottom).
            var pillHeight = Math.Max(16, anchor.SurfacePx.Height / Math.Max(0.5, anchor.Scale));
            foreach (var n in Notices.Where(n => n.Visible))
            {
                n.Holder.Measure(new Size(MaxNoticeWidth, double.PositiveInfinity)); // the holder's margin is inside its desired size
                pillHeight = Math.Max(pillHeight, Math.Ceiling(n.Holder.DesiredSize.Height) + 8);
            }
            foreach (var n in Notices.Where(n => n.Visible))
            {
                n.Pill.Height = pillHeight;
                // The island's own shape: hanging from the screen edge like the notch, or a floating capsule.
                n.Pill.CornerRadius = anchor.Corners is { } k
                    ? new CornerRadius(k.TopLeft, k.TopRight, k.BottomRight, k.BottomLeft)
                    : new CornerRadius(pillHeight / 2);
            }

            var first = !window.IsVisible;
            if (first)
            {
                window.Show();
                TryAttach(window);
            }
            Place(window, anchor, pillHeight);
            if (first)
            {
                window.Bridge?.EnsureOnScreen();
            }
        }

        private void Place(SatelliteWindow window, IslandAnchor anchor, double pillHeight)
        {
            window.Row.Measure(new Size(MaxNoticeWidth * MaxVisible + NoticeSpacing, pillHeight));
            var desired = window.Row.DesiredSize;
            var width = Math.Max(1, Math.Ceiling(desired.Width));
            window.Width = width;
            window.Height = pillHeight;
            var scale = window.IsVisible ? window.RenderScaling : anchor.Scale;
            var side = _host._prefs.SideOf(_key) ?? Notices.FirstOrDefault()?.Snapshot.Side ?? "auto";
            var wPx = (int)Math.Round(width * scale);
            var hPx = (int)Math.Round(pillHeight * scale);
            window.Position = SatellitePlacement.Choose(side, anchor.SurfacePx, anchor.ScreenBoundsPx, anchor.Dock != Island.Core.Layout.IslandDock.Top, wPx, hPx);
        }

        private void TryAttach(SatelliteWindow window)
        {
            if (window.Bridge is not null || window.TryGetPlatformHandle() is not { } handle)
            {
                return;
            }
            try
            {
                window.Bridge = _host._platform.Attach(handle.Handle, handle.HandleDescriptor, window.RenderScaling);
            }
            catch (Exception e)
            {
                System.Diagnostics.Trace.TraceError($"O_O: satellite platform bridge unavailable: {e.Message}");
            }
        }

        private TransformOperations Offset(IslandAnchor anchor, SatelliteWindow window, Notice n)
        {
            var side = _host._prefs.SideOf(_key) ?? n.Snapshot.Side;
            var dir = SatellitePlacement.SlideDirection(side, anchor.SurfacePx, anchor.ScreenBoundsPx, anchor.Dock != Island.Core.Layout.IslandDock.Top,
                (int)Math.Round(MaxNoticeWidth * anchor.Scale));
            // Slide out of the island edge: start toward the island, end in place; downwards when docked on a side.
            return dir == 0
                ? TransformOperations.Parse("translateY(-24px)")
                : TransformOperations.Parse($"translateX({(-dir * 36).ToString(System.Globalization.CultureInfo.InvariantCulture)}px)");
        }

        private void SlideOut(Notice n)
        {
            if (_host._anchor is not { } anchor || _window is null)
            {
                return;
            }
            n.Pill.Opacity = 0;
            n.Pill.RenderTransform = Offset(anchor, _window, n);
        }

        private Notice NewNotice(string key)
        {
            var holder = new Border { Margin = new Thickness(12, 0), VerticalAlignment = VerticalAlignment.Center, MaxWidth = MaxNoticeWidth - 24 };
            var badgeText = new TextBlock
            {
                FontSize = 10,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brushes.White,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var badge = new Border
            {
                Background = new SolidColorBrush(Color.Parse("#FF453A")),
                CornerRadius = new CornerRadius(8),
                MinWidth = 16,
                Height = 16,
                Padding = new Thickness(4, 0),
                Margin = new Thickness(0, 3, 6, 0),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                IsVisible = false,
                IsHitTestVisible = false,
                Child = badgeText,
            };
            var inner = new Grid();
            inner.Children.Add(holder);
            inner.Children.Add(badge);
            var pill = new Border
            {
                Background = Resource(Island.UI.Themes.IslandTheme.SurfaceBrushKey) ?? Brushes.Black,
                ClipToBounds = true,
                MaxWidth = MaxNoticeWidth,
                Child = inner,
                Opacity = 0,
                Transitions =
                [
                    new DoubleTransition { Property = Visual.OpacityProperty, Duration = SlideTime, Easing = new CubicEaseOut() },
                    new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = SlideTime, Easing = new CubicEaseOut() },
                ],
            };
            var notice = new Notice { Key = key, Pill = pill, Holder = holder, BadgeHost = badge, BadgeText = badgeText };
            pill.PointerEntered += (_, _) => notice.Hovered = true;
            pill.AddHandler(InputElement.PointerReleasedEvent, (_, e) =>
            {
                if (e.InitialPressMouseButton == Avalonia.Input.MouseButton.Left)
                {
                    _host.NoticeClicked?.Invoke(_key, notice.Key);
                }
            }, Avalonia.Interactivity.RoutingStrategies.Tunnel, handledEventsToo: true);
            pill.PointerExited += (_, _) => notice.Hovered = false;
            return notice;
        }

        private static IBrush? Resource(string key) =>
            Application.Current?.TryFindResource(key, out var value) == true ? value as IBrush : null;
    }
}
