using System.Diagnostics;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Platform;
using Avalonia.Threading;
using Island.Core.Geometry;
using Island.Core.Layout;
using Island.Core.State;
using Island.Platform;
using Island.UI.Plugins;

namespace Island.UI;

/// <summary>
/// Drives one island window: samples the pointer, runs the state machine, lays the shape out for the current screen
/// and keeps the native click-through region in step with the shape.
/// </summary>
public sealed class IslandController : IDisposable
{
    // Pointer sampling. AppKit and X11 report the pointer even while the window ignores it, which is what lets the
    // transparent parts of the window pass clicks through and still notice the pointer arriving.
    private static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(33);

    // Longer than Arc's Morph spring (0.42 s) so a collapsing shape is never clipped by the region while it shrinks.
    private static readonly TimeSpan RegionSettleDelay = TimeSpan.FromMilliseconds(700);

    // Long enough for the window's first frame to be on screen before the launch transition starts.
    private static readonly TimeSpan LaunchDelay = TimeSpan.FromMilliseconds(150);

    private static readonly bool Debug = Environment.GetEnvironmentVariable("O_O_DEBUG") == "1";

    private readonly IslandWindow _window;
    private readonly IIslandPlatform _platform;
    private readonly IslandMetrics _metrics;
    private readonly IslandStateMachine _states;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _sampler;
    private bool _launched;
    private bool _hasActivity;
    private IDisposable? _settle;
    private IIslandWindowBridge? _bridge;
    private Screen? _screen;
    private ScreenTopology? _topology;
    private IslandFrame? _frame;
    private RectD _region;
    private IslandPresenter? _presenter;

    public IslandController(IslandWindow window, IIslandPlatform platform, IslandMetrics metrics, HoverOptions hover)
    {
        _window = window;
        _platform = platform;
        _metrics = metrics;
        _states = new IslandStateMachine(hover);
        _states.StateChanged += s =>
        {
            if (s != IslandState.Expanded)
            {
                IslandFocus.End(restore: true); // collapsing ends typing and gives the keyboard back
            }
            _presenter?.OnStateChanged(s); // before the layout, so the card is measured with the page it opens on
            ApplyLayout();
        };
        // Typing keeps the island open; clicking elsewhere (the island loses activation) ends typing — focus has
        // already gone where the user clicked.
        IslandFocus.EditingChanged += _ => UpdatePin();
        window.Deactivated += (_, _) => IslandFocus.End(restore: false);

        _sampler = new DispatcherTimer(SampleInterval, DispatcherPriority.Input, (_, _) => Sample());

        var view = window.View;
        // Only the primary button expands; the secondary one opens the island's menu. With ⌥ (Alt) held, the primary
        // button drags the island along its edge instead (docs 07 §5, 位置).
        view.Surface.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(view.Surface).Properties.IsLeftButtonPressed)
            {
                return;
            }
            if (e.KeyModifiers.HasFlag(KeyModifiers.Alt) && BeginDrag(e))
            {
                e.Handled = true;
                return;
            }
            _states.Click();
        };
        view.Surface.PointerMoved += (_, e) => Drag(e);
        view.Surface.PointerReleased += (_, e) => EndDrag(e);
        view.Surface.PointerCaptureLost += (_, _) => EndDrag(null);

        var menu = new IslandMenu(
            openConsole: () =>
            {
                SetUserPinned(false);
                _states.Collapse();
                SettingsRequested?.Invoke();
            },
            isPinned: () => _userPinned,
            setPinned: SetUserPinned,
            quit: () => (Application.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.Shutdown());
        foreach (var entry in menu.Entries())
        {
            view.MoreMenu.Items.Add(entry);
        }
        var context = new TKCode.Arc.MenuSurface();
        foreach (var entry in menu.Entries())
        {
            context.Items.Add(entry);
        }
        TKCode.Arc.ContextMenu.SetMenu(view.Surface, context);
        // While a menu is open the pointer is over the menu, not the island: keep the island as it is meanwhile.
        view.MoreMenu.Opened += (_, _) => SetMenuOpen(true);
        view.MoreMenu.Closed += (_, _) => SetMenuOpen(false);
        context.Opened += (_, _) => SetMenuOpen(true);
        context.Closed += (_, _) => SetMenuOpen(false);

        window.Opened += (_, _) => OnOpened();
        window.Screens.Changed += (_, _) => Relayout();
        window.ScalingChanged += (_, _) => _bridge?.UpdateScale(window.RenderScaling);
    }

    private IslandAppearance _appearance = new();
    private IslandPlacement? _placementOverride;

    /// <summary>
    /// Development aid (<c>O_O_PLACEMENT</c>): a placement that wins over the one the appearance's shape implies.
    /// </summary>
    public IslandPlacement? PlacementOverride
    {
        get => _placementOverride;
        set
        {
            _placementOverride = value;
            Relayout();
        }
    }

    private IslandPlacement Placement => _placementOverride ?? _appearance.Placement;

    /// <summary>
    /// Raised when the user changed the appearance on the island itself (⌥-drag moved it); the app saves it.
    /// </summary>
    public event Action<IslandAppearance>? AppearanceEdited;

    // ⌥-drag: where the drag started (desktop DIPs) and the offset at that moment.
    private PointD? _dragStart;
    private double _dragStartOffset;

    /// <summary>Starts moving the island with the pointer, unless it wraps a physical notch (that cannot move).</summary>
    private bool BeginDrag(PointerPressedEventArgs e)
    {
        if (_topology is null || (Dock == IslandDock.Top && IslandLayout.AroundNotch(IslandLayout.Resolve(_topology, Placement))))
        {
            return false;
        }
        _dragStart = DesktopPoint(e);
        _dragStartOffset = _appearance.Offset;
        e.Pointer.Capture(_window.View.Surface);
        return true;
    }

    private void Drag(PointerEventArgs e)
    {
        if (_dragStart is not { } start || _topology is null)
        {
            return;
        }
        var now = DesktopPoint(e);
        var delta = Dock == IslandDock.Top ? now.X - start.X : now.Y - start.Y;
        // Keep the island on its screen: at most half the edge's length from the centre, less a margin.
        var edge = Dock == IslandDock.Top ? _topology.Bounds.Width : _topology.Bounds.Height - _topology.TopInset;
        var limit = Math.Max(0, edge / 2 - 80);
        var offset = Math.Clamp(Math.Round(_dragStartOffset + delta), -limit, limit);
        if (Math.Abs(offset - _appearance.Offset) >= 1)
        {
            _appearance = (_appearance with { Offset = offset }).Normalized();
            ApplyLayout(animate: false);
        }
    }

    private void EndDrag(PointerReleasedEventArgs? e)
    {
        if (_dragStart is null)
        {
            return;
        }
        _dragStart = null;
        e?.Pointer.Capture(null);
        if (Math.Abs(_appearance.Offset - _dragStartOffset) >= 1)
        {
            AppearanceEdited?.Invoke(_appearance);
        }
    }

    /// <summary>The pointer in desktop DIPs: steady while the window moves under it.</summary>
    private PointD DesktopPoint(PointerEventArgs e)
    {
        var p = e.GetPosition(_window);
        var scale = _topology?.Scale ?? 1;
        return new PointD(_window.Position.X / scale + p.X, _window.Position.Y / scale + p.Y);
    }

    /// <summary>Shape, size and position (docs/design/07-home-and-layout.md §5). Applies at once.</summary>
    public IslandAppearance Appearance
    {
        get => _appearance;
        set
        {
            _appearance = value.Normalized();
            if (_presenter is not null)
            {
                _presenter.Minimal = _appearance.Minimal;
            }
            Relayout();
            if (_launched)
            {
                CheckFullScreen(); // e.g. 全屏时 switched to 显示 while hidden
            }
        }
    }

    /// <summary>The scale the island is drawn at on the current screen: 1 while it wraps a physical notch.</summary>
    private double Scale => _topology is null ? 1 : IslandLayout.EffectiveScale(_topology, Placement, Dock, _appearance.Scale);

    private IslandDock Dock => _placementOverride is null ? _appearance.Dock : IslandDock.Top;

    /// <summary>Around a physical notch the island cannot move: it has to cover the notch exactly.</summary>
    private double Offset => _topology is not null && Dock == IslandDock.Top && IslandLayout.AroundNotch(IslandLayout.Resolve(_topology, Placement))
        ? 0
        : _appearance.Offset;

    private bool _userPinned;
    private bool _menuOpen;

    /// <summary>"保持展开": the island stays open until unpinned (the island's menu and the tray menu).</summary>
    public bool Pinned
    {
        get => _userPinned;
        set => SetUserPinned(value);
    }

    /// <summary>Finds full-screen windows of other applications (docs 07 §5, 全屏时); none by default.</summary>
    public IFullScreenDetector FullScreenDetector { get; set; } = new NullFullScreenDetector();

    // How often to look for a full-screen window, and for the screen under the pointer (跟随鼠标).
    private static readonly TimeSpan FullScreenInterval = TimeSpan.FromSeconds(1);
    private const int FollowMouseEverySamples = 15; // ~0.5 s at the sampling rate
    private static readonly TimeSpan FadeDuration = TimeSpan.FromMilliseconds(200);
    private DispatcherTimer? _fullScreenTimer;
    private bool _hidden;
    private bool _thinLine;

    /// <summary>How thick the island is while it is a thin line for a full-screen app (DIPs).</summary>
    private const double ThinLineThickness = 4;
    private int _followCountdown;

    /// <summary>
    /// Hides the island (fades out and lets every click through) while another application is full screen on its
    /// screen, if the user asked for that; shows it again afterwards.
    /// </summary>
    private void CheckFullScreen()
    {
        var fullScreen = _appearance.FullScreen != FullScreenBehavior.Show && _topology is { } t && SafeIsFullScreen(t);
        // 贴边细线: a thin line at the edge that still opens on hover, instead of hiding.
        var thin = fullScreen && _appearance.FullScreen == FullScreenBehavior.ThinLine;
        if (thin != _thinLine)
        {
            _thinLine = thin;
            Log($"full screen app: {(thin ? "thin line" : "normal island")}");
            if (_launched)
            {
                _states.SetRestingState(thin || !_hasActivity ? IslandState.Idle : IslandState.Compact);
            }
            ApplyLayout(animate: true);
        }
        var hide = fullScreen && _appearance.FullScreen == FullScreenBehavior.Hide;
        if (hide == _hidden)
        {
            return;
        }
        _hidden = hide;
        Log($"full screen app: {(hide ? "hiding" : "showing")} the island");
        var view = _window.View;
        view.Transitions ??=
        [
            new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = FadeDuration },
        ];
        view.Opacity = hide ? 0 : 1;
        if (hide)
        {
            _states.Collapse();
            SetRegion(default);
            _bridge?.OnPointerSample(false);
        }
        else
        {
            ApplyLayout(animate: false);
        }
    }

    private bool SafeIsFullScreen(ScreenTopology t)
    {
        try
        {
            return FullScreenDetector.IsFullScreen(t.Bounds, t.Scale);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
        {
            FullScreenDetector = new NullFullScreenDetector(); // the platform cannot tell; stop asking
            Log($"full screen detection unavailable: {e.Message}");
            return false;
        }
    }

    /// <summary>
    /// The screen the island should be on: the primary one, the one under the pointer (跟随鼠标), or the one the user
    /// chose (指定屏) — the primary one while that is not connected.
    /// </summary>
    private Screen? ChooseScreen()
    {
        if (_appearance.Display == IslandDisplay.FollowMouse && PointerOnDesktop() is { } point &&
            _window.Screens.ScreenFromPoint(point) is { } underPointer)
        {
            return underPointer;
        }
        if (_appearance.Display == IslandDisplay.Specific && _appearance.ScreenId is { } id)
        {
            var all = _window.Screens.All;
            var index = ScreenIdentity.Find(id, [.. all.Select(s => (s.DisplayName, new RectD(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)))]);
            if (index >= 0)
            {
                return all[index];
            }
        }
        return _window.Screens.Primary ?? _window.Screens.All.FirstOrDefault();
    }

    /// <summary>The pointer in desktop pixels, from the bridge's window-relative position.</summary>
    private PixelPoint? PointerOnDesktop()
    {
        if (_bridge is null || _topology is null || !_bridge.TryGetCursor(out var p))
        {
            return null;
        }
        var scale = _topology.Scale;
        return new PixelPoint(_window.Position.X + (int)Math.Round(p.X * scale), _window.Position.Y + (int)Math.Round(p.Y * scale));
    }

    /// <summary>跟随鼠标: moves the island when the pointer is on another screen (never while it is open).</summary>
    private void FollowMouse()
    {
        if (_appearance.Display != IslandDisplay.FollowMouse || _states.State == IslandState.Expanded || --_followCountdown > 0)
        {
            return;
        }
        _followCountdown = FollowMouseEverySamples;
        if (ChooseScreen() is { } screen && _screen is { } current && screen.Bounds != current.Bounds)
        {
            Relayout();
        }
    }

    private void SetUserPinned(bool pinned)
    {
        _userPinned = pinned;
        UpdatePin();
        if (pinned)
        {
            _states.Click(); // pinning from the compact island's menu also opens it
        }
    }

    private void SetMenuOpen(bool open)
    {
        _menuOpen = open;
        UpdatePin();
    }

    // A hotkey opens the island where the pointer is not: it stays open until the pointer has been on it and left,
    // or, if the pointer never comes, for HotkeyGrace; pressing the toggle again closes it.
    private static readonly TimeSpan HotkeyGrace = TimeSpan.FromSeconds(10);
    private bool _hotkeyHeld;
    private bool _hotkeyVisited;
    private TimeSpan _hotkeyAt;

    /// <summary>
    /// Opens the island from a global hotkey (docs 04, 快捷键), on <paramref name="page"/> (<c>plugin/page</c>) when
    /// given and shown.
    /// </summary>
    public void OpenFromHotkey(string? page = null)
    {
        if (_hidden)
        {
            return; // a full-screen app is in front: the island stays out of the way
        }
        _hotkeyHeld = true;
        _hotkeyVisited = false;
        _hotkeyAt = _clock.Elapsed;
        _states.Pin(true);
        _states.Click();
        if (page is not null)
        {
            _presenter?.ShowPage(page); // after expanding, so the smart home rule does not override it
        }
    }

    /// <summary>The toggle hotkey: opens the island, or closes it when it is open.</summary>
    public void ToggleFromHotkey()
    {
        if (_states.State == IslandState.Expanded)
        {
            ReleaseHotkeyHold();
            _userPinned = false;
            _states.Collapse();
        }
        else
        {
            OpenFromHotkey();
        }
    }

    private void ReleaseHotkeyHold()
    {
        _hotkeyHeld = false;
        UpdatePin();
    }

    /// <summary>The island stays open while pinned, a menu is open, a hotkey holds it, or the user is typing on it.</summary>
    private void UpdatePin() => _states.Pin(_userPinned || _menuOpen || _hotkeyHeld || IslandFocus.IsEditing);

    /// <summary>Lets go of a hotkey-opened island once the pointer has visited and left it, or never came.</summary>
    private void TrackHotkeyHold(bool onShape)
    {
        if (!_hotkeyHeld)
        {
            return;
        }
        if (onShape)
        {
            _hotkeyVisited = true;
        }
        else if (_hotkeyVisited || _clock.Elapsed - _hotkeyAt > HotkeyGrace)
        {
            ReleaseHotkeyHold();
        }
    }

    /// <summary>The island's 设置 button: open the console.</summary>
    public event Action? SettingsRequested;

    /// <summary>
    /// Creates the presenter that renders plugin pages and activities into this island, and lets the island rest in
    /// Compact while it has an activity. <paramref name="dispatch"/> receives (plugin id, handler id, payload JSON).
    /// </summary>
    public IslandPresenter CreatePresenter(
        Action<string, int, string?> dispatch,
        Island.Core.Pages.PagePreferencesStore pages,
        Func<Island.Plugins.PageSnapshot, Island.Core.Pages.PageInfo> describe,
        Island.Core.Pages.ChipPreferencesStore chips,
        Func<Island.Plugins.ChipSnapshot, Island.Core.Pages.ChipInfo> describeChip)
    {
        var presenter = new IslandPresenter(_window.View, dispatch, pages, describe, chips, describeChip);
        _presenter = presenter;
        presenter.Vertical = _vertical ?? false;
        presenter.HasCompactContentChanged += SetHasActivity;
        // The collapsed island is as wide as its chips: re-lay out when they change size.
        presenter.CompactContentChanged += () => Dispatcher.UIThread.Post(() =>
        {
            if (MeasureCompact() != _compactContent)
            {
                ApplyLayout();
            }
        }, DispatcherPriority.Background);
        // New content may need a taller or shorter card; re-lay out once it has been rendered.
        presenter.ExpandedContentChanged += () =>
        {
            if (_states.State == IslandState.Expanded)
            {
                Dispatcher.UIThread.Post(ApplyLayout, DispatcherPriority.Background);
            }
        };
        // After the subscriptions: turning minimal mode on renders the time right away, and the island must hear that.
        presenter.Minimal = _appearance.Minimal;
        return presenter;
    }

    /// <summary>
    /// Lays the island out and shows it. The native bridge is attached first where the platform allows it, so on macOS
    /// the window is above the menu bar before AppKit first orders it on screen and is never constrained below it.
    /// </summary>
    public void Show()
    {
        // The island opens in Idle and grows into its resting state once the window is up (see OnOpened).
        AttachBridge();
        Relayout();
        _window.Show();
    }

    /// <summary>
    /// Whether any plugin activity is running: the island rests in Compact while one is, and in Idle otherwise.
    /// </summary>
    public void SetHasActivity(bool hasActivity)
    {
        _hasActivity = hasActivity;
        if (_launched)
        {
            // While a full-screen app is in front the island rests as a thin line, whatever it has to show.
            _states.SetRestingState(hasActivity && !_thinLine ? IslandState.Compact : IslandState.Idle);
        }
    }

    private void AttachBridge()
    {
        if (_bridge is not null || _window.TryGetPlatformHandle() is not { } handle)
        {
            return;
        }
        try
        {
            _bridge = _platform.Attach(handle.Handle, handle.HandleDescriptor, _window.RenderScaling);
            _bridge.SetInteractiveRegion(_region);
        }
        catch (Exception e)
        {
            // Without the bridge the island still works, it just blocks clicks on its transparent area.
            Trace.TraceError($"O_O: platform bridge unavailable: {e.Message}");
        }
        Log($"platform={_platform.Name} handle={handle.HandleDescriptor}");
    }

    private static readonly TimeSpan[] RepresentDelays = [TimeSpan.FromSeconds(1.2), TimeSpan.FromSeconds(3)];

    /// <summary>R16 mitigation: re-order the native window and draw a few fresh frames (opacity 1 → 0.99 → 1).</summary>
    private void Represent()
    {
        if (_hidden)
        {
            return;
        }
        _bridge?.EnsureOnScreen();
        var surface = _window.View.Surface;
        surface.Transitions ??= [];
        var fade = new Avalonia.Animation.DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(120) };
        surface.Transitions.Add(fade);
        surface.Opacity = 0.99;
        DispatcherTimer.RunOnce(() =>
        {
            surface.Opacity = 1;
            DispatcherTimer.RunOnce(() => surface.Transitions?.Remove(fade), TimeSpan.FromMilliseconds(200));
        }, TimeSpan.FromMilliseconds(130));
        Log($"represent: native={_bridge?.Describe()}");
    }

    private void OnOpened()
    {
        // Platforms that create the native window only when it is shown are attached here instead.
        if (_bridge is null)
        {
            AttachBridge();
            Relayout();
        }
        _sampler.Start();
        _fullScreenTimer = new DispatcherTimer(FullScreenInterval, DispatcherPriority.Background, (_, _) => CheckFullScreen());
        _fullScreenTimer.Start();

        // Grow in from Idle. Besides being the launch animation, this is needed on macOS: a static first frame drawn
        // at the very top of the screen (y = 0, the notch and "attached" placements) is not presented, while frames
        // produced by a running transition are.
        DispatcherTimer.RunOnce(() =>
        {
            _launched = true;
            SetHasActivity(_hasActivity);
        }, LaunchDelay);

        // R16 (docs 06): occasionally the island was not drawn after launch at the very top of a macOS screen. The
        // grow-in above mostly avoids it; as a second line, once the window has surely been ordered on screen, put it
        // in front again (without activating) and run a short, invisible transition so a fresh frame is presented.
        foreach (var delay in RepresentDelays)
        {
            DispatcherTimer.RunOnce(Represent, delay);
        }

        if (Environment.GetEnvironmentVariable("O_O_DEBUG_WINDOW_PNG") is { Length: > 0 } png)
        {
            // Development aid: the island window exactly as it composes (template included), to check transparency.
            DispatcherTimer.RunOnce(() =>
            {
                var size = new PixelSize((int)_window.Bounds.Width, (int)_window.Bounds.Height);
                using var bitmap = new Avalonia.Media.Imaging.RenderTargetBitmap(size);
                bitmap.Render(_window);
                bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
                Log($"window rendered to {png}");
            }, TimeSpan.FromSeconds(double.TryParse(Environment.GetEnvironmentVariable("O_O_DEBUG_WINDOW_PNG_DELAY"),
                CultureInfo.InvariantCulture, out var delay) ? delay : 2));
        }

        if (Debug)
        {
            DispatcherTimer.RunOnce(() => Log(
                $"after 1s: position={_window.Position} surfaceBounds={_window.View.Surface.Bounds} " +
                $"native={_bridge?.Describe()}"), TimeSpan.FromSeconds(1));
        }

        if (Environment.GetEnvironmentVariable("O_O_DEBUG_FOCUS") is { Length: > 0 } focusPage)
        {
            // Development aid: open a page, start typing in its first text box, and log whether the island really
            // became the key window; then give the keyboard back (docs 02 §7, typing on the island).
            DispatcherTimer.RunOnce(() =>
            {
                SetUserPinned(true);
                _presenter?.ShowPage(focusPage);
                DispatcherTimer.RunOnce(() =>
                {
                    var box = Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(_window.View).OfType<TextBox>().FirstOrDefault();
                    if (box is null)
                    {
                        System.Console.Error.WriteLine("[focus] no text box on that page");
                        return;
                    }
                    IslandFocus.Begin(box);
                    DispatcherTimer.RunOnce(() =>
                    {
                        System.Console.Error.WriteLine($"[focus] editing: window active={_window.IsActive} box focused={box.IsFocused} editing={IslandFocus.IsEditing}");
                        IslandFocus.End(restore: true);
                        DispatcherTimer.RunOnce(() =>
                            System.Console.Error.WriteLine($"[focus] after Esc: window active={_window.IsActive} editing={IslandFocus.IsEditing}"),
                            TimeSpan.FromMilliseconds(800));
                    }, TimeSpan.FromMilliseconds(800));
                }, TimeSpan.FromMilliseconds(800));
            }, TimeSpan.FromSeconds(4));
        }

        if (Environment.GetEnvironmentVariable("O_O_DEMO") == "1")
        {
            // Development aid: cycle through the states every two seconds, for screenshots and animation checks.
            IslandState[] cycle = [IslandState.Expanded, IslandState.Compact, IslandState.Idle, IslandState.Compact];
            _launched = false; // the demo owns the resting state
            var step = 0;
            _sampler.Stop();
            DispatcherTimer.Run(() =>
            {
                var next = cycle[step++ % cycle.Length];
                if (next == IslandState.Expanded) { _states.Click(); } else { _states.SetRestingState(next); _states.Collapse(); }
                return true;
            }, TimeSpan.FromSeconds(2));
        }

        if (Environment.GetEnvironmentVariable("O_O_STATE") is { } forced &&
            Enum.TryParse<IslandState>(forced, ignoreCase: true, out var state) && state == IslandState.Expanded)
        {
            SetUserPinned(true);
        }
    }

    /// <summary>Screen added, removed or rearranged: find our screen again and place the window on it.</summary>
    private void Relayout()
    {
        // docs 02 §4: the primary screen by default, or the one under the pointer.
        _screen = ChooseScreen();
        if (_screen is null)
        {
            return;
        }
        var scale = _screen.Scaling;
        var bounds = new RectD(
            _screen.Bounds.X / scale, _screen.Bounds.Y / scale, _screen.Bounds.Width / scale, _screen.Bounds.Height / scale);
        var topInset = (_screen.WorkingArea.Y - _screen.Bounds.Y) / scale;
        _topology = new ScreenTopology(bounds, scale, FakeNotch(bounds) ?? _platform.DetectNotch(bounds), topInset);
        Log($"screen bounds={bounds} scale={scale} topInset={topInset} notch={_topology.Notch}");
        ApplyLayout(animate: false);
    }

    /// <summary>
    /// Development aid: <c>O_O_FAKE_NOTCH=185x32</c> pretends the screen has a centred notch of that size, so the
    /// notch layout can be checked on machines without one.
    /// </summary>
    private static NotchInfo? FakeNotch(RectD screen)
    {
        var spec = Environment.GetEnvironmentVariable("O_O_FAKE_NOTCH")?.Split('x');
        if (spec is not [var w, var h] ||
            !double.TryParse(w, CultureInfo.InvariantCulture, out var width) ||
            !double.TryParse(h, CultureInfo.InvariantCulture, out var height))
        {
            return null;
        }
        return new NotchInfo((screen.Width - width) / 2, width, height);
    }

    private void ApplyLayout() => ApplyLayout(animate: true);

    private void ApplyLayout(bool animate)
    {
        if (_topology is null || _screen is null)
        {
            return;
        }
        var state = _states.State;
        var view = _window.View;

        // The expanded card is as tall as its content (docs 02 §2): lay the content out at the card's width first and
        // use its height, within limits. Content panels keep their final size so text does not reflow mid-spring.
        var islandScale = Scale;
        var offset = Offset;
        var dock = Dock;
        var (windowWidth, windowHeight) = IslandLayout.WindowSize(dock, _metrics);
        _window.SetIslandSize(islandScale, windowWidth, windowHeight);
        ArrangeCompact(vertical: dock != IslandDock.Top);
        var widthFrame = IslandLayout.Compute(_topology, Placement, dock, IslandState.Expanded, _metrics, offset, islandScale);
        view.ExpandedPanel.Width = widthFrame.Surface.Width;
        view.ExpandedHeader.Height = widthFrame.ContentTopInset > 0 ? widthFrame.ContentTopInset : 40;
        view.ExpandedNotchGap.Width = widthFrame.NotchGap;
        // Without a notch the tabs take everything the date and the menu leave; around one, they have the left wing.
        var header = view.ExpandedHeader.ColumnDefinitions;
        header[0].Width = new GridLength(1, GridUnitType.Star);
        header[2].Width = widthFrame.NotchGap > 0 ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        var right = (Control)view.HeaderStatus.Parent!;
        right.Measure(Size.Infinity);
        var rightWidth = right.DesiredSize.Width + 14;
        _presenter?.SetTabRoom(widthFrame.NotchGap > 0
            ? (widthFrame.Surface.Width - widthFrame.NotchGap) / 2 - 10 - 8
            : widthFrame.Surface.Width - 10 - rightWidth - 16);
        view.ExpandedPanel.Measure(new Size(widthFrame.Surface.Width, double.PositiveInfinity));
        var contentHeight = view.ExpandedPanel.DesiredSize.Height - widthFrame.ContentTopInset;
        // The collapsed island is as wide as its chips (docs 07 §4): a pill around both groups, or wings beside a notch
        // as wide as the wider group.
        _compactContent = MeasureCompact();
        var (chipsLeft, chipsRight) = _compactContent;
        // At a side the groups stack: the pill is as wide as the widest chip and as tall as both groups.
        var metrics = _metrics with
        {
            ExpandedHeight = Math.Clamp(Math.Ceiling(contentHeight), _metrics.MinExpandedHeight,
                dock == IslandDock.Top ? _metrics.MaxExpandedHeight : _metrics.SideMaxExpandedHeight),
            CompactWidth = Math.Clamp(Math.Ceiling(chipsLeft.Width + chipsRight.Width + CompactGap), _metrics.IdleWidth, MaxCompactWidth),
            WingWidth = Math.Clamp(Math.Ceiling(Math.Max(chipsLeft.Width, chipsRight.Width) + WingGap), MinWingWidth, MaxWingWidth),
            SideCompactWidth = Math.Clamp(Math.Ceiling(Math.Max(chipsLeft.Width, chipsRight.Width) + SidePadding * 2), MinSideWidth, MaxSideWidth),
            SideCompactHeight = Math.Clamp(Math.Ceiling(chipsLeft.Height + chipsRight.Height + CompactGap), _metrics.SideIdleHeight, MaxSideHeight),
        };
        if (_thinLine)
        {
            // 贴边细线: at rest the island is a thin pill against the edge (it still opens on hover).
            metrics = metrics with
            {
                IdleHeight = ThinLineThickness,
                IdleWidth = 120,
                SideIdleWidth = ThinLineThickness,
                FloatingGap = 0,
            };
        }

        var frame = IslandLayout.Compute(_topology, Placement, dock, state, metrics, offset, islandScale);
        var previous = _frame;
        _frame = frame;

        // Window: converted relative to its own screen, so mixed-DPI desktops do not skew the position.
        var scale = _topology.Scale;
        _window.Position = new PixelPoint(
            _screen.Bounds.X + (int)Math.Round((frame.Window.X - _topology.Bounds.X) * scale),
            _screen.Bounds.Y + (int)Math.Round((frame.Window.Y - _topology.Bounds.Y) * scale));

        var surface = view.Surface;
        if (!animate)
        {
            // The first frame never animates (Arc's motion classes arrive after load anyway).
            surface.Transitions = null;
        }
        surface.Width = frame.Surface.Width;
        surface.Height = frame.Surface.Height;
        surface.Margin = new Thickness(frame.Surface.X, frame.Surface.Y, 0, 0);
        var c = frame.Corners;
        surface.CornerRadius = new CornerRadius(c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft);
        surface.Shoulder = frame.Shoulder;
        if (!animate)
        {
            surface.ClearValue(Avalonia.Animation.Animatable.TransitionsProperty);
        }

        var compact = IslandLayout.Compute(_topology, Placement, dock, IslandState.Compact, metrics, offset, islandScale);
        view.CompactPanel.Width = compact.Surface.Width;
        view.CompactPanel.Height = compact.Surface.Height;
        view.CompactNotchGap.Width = compact.NotchGap;
        // Beside a notch the wings are equal so the notch stays centred; a capsule gives each group what it needs.
        if (dock == IslandDock.Top)
        {
            var columns = view.CompactGrid.ColumnDefinitions;
            var aroundNotch = compact.NotchGap > 0;
            columns[0].Width = aroundNotch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            columns[1].Width = aroundNotch ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
            columns[2].Width = aroundNotch ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        }
        view.CompactPanel.IsActive = state == IslandState.Compact;
        view.ExpandedPanel.IsActive = state == IslandState.Expanded;

        // While the shape animates, the region covers both the old and the new shape; afterwards just the new one.
        var target = HitArea(frame);
        SetRegion(animate && previous is not null ? target.Union(HitArea(previous)) : target);
        _settle?.Dispose();
        if (animate)
        {
            _settle = DispatcherTimer.RunOnce(() => SetRegion(target), RegionSettleDelay);
        }

        Log($"state={state} dock={dock} placement={frame.Placement} window={frame.Window} surface={frame.Surface} chips={_compactContent}");
    }

    // Collapsed sizing: space between the two groups, and between a wing's content and the notch.
    private const double CompactGap = 20;
    private const double WingGap = 10;
    private const double MaxCompactWidth = 420;
    private const double MinWingWidth = 44;
    private const double MaxWingWidth = 220;
    private const double SidePadding = 8;
    private const double MinSideWidth = 36;
    private const double MaxSideWidth = 84;
    private const double MaxSideHeight = 320;
    private (Size Left, Size Right) _compactContent;
    private bool? _vertical;

    /// <summary>The sizes of the two chip groups, margins included.</summary>
    private (Size Left, Size Right) MeasureCompact()
    {
        var view = _window.View;
        view.CompactLeading.Measure(Size.Infinity);
        view.CompactTrailing.Measure(Size.Infinity);
        static Size Round(Size s) => new(Math.Ceiling(s.Width), Math.Ceiling(s.Height));
        return (Round(view.CompactLeading.DesiredSize), Round(view.CompactTrailing.DesiredSize));
    }

    /// <summary>
    /// The collapsed content grid: a row (left group · notch · right group) at the top edge, a column (first group
    /// above the second) at a side edge. The grid keeps three rows and three columns; the unused ones collapse.
    /// </summary>
    private void ArrangeCompact(bool vertical)
    {
        if (_vertical == vertical)
        {
            return;
        }
        _vertical = vertical;
        var view = _window.View;
        var grid = view.CompactGrid;
        var star = new GridLength(1, GridUnitType.Star);
        var none = new GridLength(0);
        if (grid.RowDefinitions.Count == 0)
        {
            grid.RowDefinitions.Add(new RowDefinition(star));
            grid.RowDefinitions.Add(new RowDefinition(none));
            grid.RowDefinitions.Add(new RowDefinition(none));
        }
        var rows = grid.RowDefinitions;
        var columns = grid.ColumnDefinitions;
        Control[] cells = [view.CompactLeading, view.CompactNotchGap, view.CompactTrailing];
        for (var i = 0; i < 3; i++)
        {
            Grid.SetRow(cells[i], vertical ? i : 0);
            Grid.SetColumn(cells[i], vertical ? 0 : i);
        }
        if (vertical)
        {
            columns[0].Width = star;
            columns[1].Width = none;
            columns[2].Width = none;
            rows[0].Height = GridLength.Auto;
            rows[1].Height = star;
            rows[2].Height = GridLength.Auto;
            view.CompactLeading.Margin = new Thickness(SidePadding, 12, SidePadding, 0);
            view.CompactTrailing.Margin = new Thickness(SidePadding, 0, SidePadding, 12);
            view.CompactLeading.HorizontalAlignment = view.CompactTrailing.HorizontalAlignment = HorizontalAlignment.Center;
            view.CompactLeading.VerticalAlignment = VerticalAlignment.Top;
            view.CompactTrailing.VerticalAlignment = VerticalAlignment.Bottom;
        }
        else
        {
            rows[0].Height = star;
            rows[1].Height = none;
            rows[2].Height = none;
            // Inside the pill's rounded ends, not at their curve: the bottom corner was cutting into the trailing value.
            view.CompactLeading.Margin = new Thickness(22, 0, 0, 0);
            view.CompactTrailing.Margin = new Thickness(0, 0, 22, 0);
            view.CompactLeading.HorizontalAlignment = HorizontalAlignment.Left;
            view.CompactTrailing.HorizontalAlignment = HorizontalAlignment.Right;
            view.CompactLeading.VerticalAlignment = view.CompactTrailing.VerticalAlignment = VerticalAlignment.Center;
        }
        if (_presenter is not null)
        {
            _presenter.Vertical = vertical;
        }
    }

    /// <summary>
    /// Where the pointer counts as on the island: the shape plus a little slop and, docked to a side, the strip between
    /// the shape and the screen edge, so pushing the pointer against the edge reaches the island.
    /// </summary>
    private RectD HitArea(IslandFrame frame)
    {
        var area = frame.Surface.Inflate(_metrics.HitSlop);
        if (_thinLine && frame.Dock == IslandDock.Top)
        {
            // A thin line is hard to hit: pushing the pointer against the top edge above it counts too.
            area = area.Union(new RectD(area.X, 0, area.Width, Math.Max(1, area.Y)));
        }
        return frame.Dock switch
        {
            IslandDock.Left => area.Union(new RectD(0, area.Y, 1, area.Height)),
            IslandDock.Right => area.Union(new RectD(frame.Window.Width / _window.IslandScale - 1, area.Y, 1, area.Height)),
            _ => area,
        };
    }

    /// <summary><paramref name="region"/> is in island units; the native region is in window DIPs.</summary>
    private void SetRegion(RectD region)
    {
        var k = _window.IslandScale;
        if (_hidden)
        {
            region = default; // keep letting every click through until the full-screen app goes away
        }
        _region = new RectD(region.X * k, region.Y * k, region.Width * k, region.Height * k);
        _bridge?.SetInteractiveRegion(_region);
    }

    private void Sample()
    {
        if (_hidden || _bridge is null || !_bridge.TryGetCursor(out var p))
        {
            return; // hidden for a full-screen app: the island takes no pointer at all
        }
        FollowMouse();
        var k = _window.IslandScale;
        var onShape = _frame is { } f && HitArea(f).Contains(new PointD(p.X / k, p.Y / k));
        // The window takes the pointer anywhere on the region (which may be larger than the shape mid-animation),
        // but only the shape itself counts as hovering the island.
        _bridge.OnPointerSample(_region.Contains(p));
        TrackHotkeyHold(onShape);
        _states.Update(onShape, _clock.Elapsed);
    }

    private static void Log(string message)
    {
        if (Debug)
        {
            System.Console.Error.WriteLine($"[O_O] {message}");
        }
    }

    public void Dispose()
    {
        _fullScreenTimer?.Stop();
        _sampler.Stop();
        _settle?.Dispose();
        _bridge?.Dispose();
    }
}
