using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Island.Core.Layout;
using Island.Core.State;
using Island.Plugins.Host;
using Island.UI.Plugins;
using Island.UI.Views;

namespace Island.UI;

/// <summary>
/// Development aid: renders the island with live plugin content to a PNG, offscreen, for reviewing the island where
/// screen capture is unavailable. Uses a plain screen (no notch) and the default metrics.
/// </summary>
public static class IslandSnapshot
{
    public static void Render(PluginHost host, string output, IslandState state, TimeSpan settle, bool attached = false)
    {
        var metrics = new IslandMetrics();
        var screen = new ScreenTopology(new Core.Geometry.RectD(0, 0, 1440, 900), 1, Notch: null, TopInset: 0);
        var placement = attached ? IslandPlacement.Attached : IslandPlacement.Floating;
        var frame = IslandLayout.Compute(screen, placement, state, metrics);
        var expanded = IslandLayout.Compute(screen, placement, IslandState.Expanded, metrics);
        var compact = IslandLayout.Compute(screen, placement, IslandState.Compact, metrics);

        // Development aid: O_O_RENDER_THEME=plugin/theme draws the island in that theme's island layer.
        if (Environment.GetEnvironmentVariable("O_O_RENDER_THEME") is { Length: > 0 } themeKey &&
            host.Packages.SelectMany(p => p.Manifest.Contributes.Themes.Select(t => (Key: $"{p.Id}/{t.Id}", t.Island)))
                .FirstOrDefault(t => t.Key == themeKey) is { Key: not null } chosen)
        {
            Island.UI.Themes.IslandTheme.Apply(chosen.Island);
        }
        var view = new IslandView();
        // Default page settings (a file nobody writes), so the image shows what a new user sees.
        var pages = new Island.Core.Pages.PagePreferencesStore(Path.Combine(Path.GetTempPath(), $"o_o-render-pages-{Guid.NewGuid():N}.json"));
        var presenter = new IslandPresenter(view, (plugin, handler, payload) => host.Dispatch(plugin, handler, payload), pages, s =>
        {
            var declared = host.Packages.FirstOrDefault(p => p.Id == s.PluginId)?.Manifest.Contributes.Home.FirstOrDefault(p => p.Id == s.PageId);
            return new Island.Core.Pages.PageInfo(s.Key, declared?.Title ?? s.Title, declared?.Enabled ?? true, declared?.Icon);
        }, new Island.Core.Pages.ChipPreferencesStore(Path.Combine(Path.GetTempPath(), $"o_o-render-chips-{Guid.NewGuid():N}.json")));
        presenter.OnStateChanged(state);
        foreach (var page in host.LivePages)
        {
            presenter.UpdatePage(page);
        }
        host.LiveActivityUpdated += s => Dispatcher.UIThread.Post(() => presenter.UpdateLive(s));
        host.LiveActivityEnded += (_, k) => Dispatcher.UIThread.Post(() => presenter.EndLive(k));
        host.AmbientUpdated += a => Dispatcher.UIThread.Post(() => presenter.UpdateAmbient(a));
        foreach (var live in host.LiveActivities)
        {
            presenter.UpdateLive(live);
        }
        foreach (var ambient in host.LiveAmbient)
        {
            presenter.UpdateAmbient(ambient);
        }

        presenter.SetTabRoom(expanded.Surface.Width - 10 - 200);
        host.PageUpdated += s => Dispatcher.UIThread.Post(() => presenter.UpdatePage(s));
        host.PageEnded += (p, g) => Dispatcher.UIThread.Post(() => presenter.EndPage(p, g));

        view.Surface.Width = frame.Surface.Width;
        view.Surface.Height = frame.Surface.Height;
        // Attached: flush with the top edge of the image, with the shoulders reaching into the margin either side.
        view.Surface.Margin = attached ? new Thickness(16, 0, 16, 16) : new Thickness(16);
        var c = frame.Corners;
        if (!attached && Island.UI.Themes.IslandTheme.Current.CornerScale is var k and not 1)
        {
            var most = Math.Min(frame.Surface.Width, frame.Surface.Height) / 2;
            c = new Island.Core.Layout.Corners(Math.Min(c.TopLeft * k, most), Math.Min(c.TopRight * k, most), Math.Min(c.BottomRight * k, most), Math.Min(c.BottomLeft * k, most));
        }
        view.Surface.CornerRadius = new CornerRadius(c.TopLeft, c.TopRight, c.BottomRight, c.BottomLeft);
        if (Island.UI.Themes.IslandTheme.Current.Animated is { } animated)
        {
            view.Backdrop.Show(animated.Preset, animated.Colors, animated.Speed, still: true); // a picture: one frame
        }
        view.Surface.Shoulder = frame.Shoulder;
        view.CompactPanel.Width = compact.Surface.Width;
        view.CompactPanel.Height = compact.Surface.Height;
        view.ExpandedPanel.Width = expanded.Surface.Width;
        view.ExpandedHeader.Height = 40;
        view.CompactPanel.IsActive = state == IslandState.Compact;
        view.ExpandedPanel.IsActive = state == IslandState.Expanded;

        var width = frame.Surface.Width + 32;
        double height = frame.Surface.Height + (attached ? 16 : 32);
        // Shown, but far off-screen: Arc pauses animated controls (gauges, counters) in windows that are not visible,
        // which would leave them at their starting values in the image.
        var window = new TKCode.Arc.ArcWindow
        {
            IsTitleBarVisible = false,
            ExtendClientAreaToDecorationsHint = false,
            Content = view,
            Width = width,
            Height = height,
            Background = Brushes.Gray,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(-20000, -20000),
            ShowInTaskbar = false,
            ShowActivated = false,
            WindowDecorations = WindowDecorations.None,
        };
        window.Show();
        Pump(settle);
        if (state == IslandState.Expanded && Environment.GetEnvironmentVariable("O_O_RENDER_HOME") != "1")
        {
            // Opened again now that the plugins have started, as a click would: the first activity's card, or home.
            presenter.OnStateChanged(IslandState.Compact);
            presenter.OnStateChanged(IslandState.Expanded);
            Pump(TimeSpan.FromMilliseconds(300));
        }
        if (Environment.GetEnvironmentVariable("O_O_RENDER_ACTIVITY") is { Length: > 0 } plugin)
        {
            presenter.Pick(plugin); // e.g. com.o_o.ai-usage
            Pump(TimeSpan.FromMilliseconds(300));
        }
        if (state == IslandState.Compact)
        {
            // As wide as the chips, like the real island.
            view.CompactLeading.Measure(Size.Infinity);
            view.CompactTrailing.Measure(Size.Infinity);
            var chips = Math.Clamp(Math.Ceiling(view.CompactLeading.DesiredSize.Width + view.CompactTrailing.DesiredSize.Width + 20), metrics.IdleWidth, 420);
            view.Surface.Width = view.CompactPanel.Width = chips;
            var columns = view.CompactGrid.ColumnDefinitions;
            columns[0].Width = GridLength.Auto;
            columns[1].Width = new GridLength(1, GridUnitType.Star);
            columns[2].Width = GridLength.Auto;
            width = chips + 32;
            window.Width = width;
        }
        window.Measure(new Size(width, height));
        window.Arrange(new Rect(0, 0, width, height));
        Pump(TimeSpan.FromMilliseconds(400));
        if (state == IslandState.Expanded)
        {
            // As the controller does: the card is as tall as its content, within limits.
            view.ExpandedPanel.Measure(new Size(expanded.Surface.Width, double.PositiveInfinity));
            view.Surface.Height = Math.Clamp(Math.Ceiling(view.ExpandedPanel.DesiredSize.Height), metrics.MinExpandedHeight, metrics.MaxExpandedHeight);
            height = view.Surface.Height + (attached ? 16 : 32);
            window.Height = height;
        }
        view.Measure(new Size(width, height));
        view.Arrange(new Rect(0, 0, width, height));
        Pump(TimeSpan.FromMilliseconds(1500)); // let the gauges spring to their values

        if (Environment.GetEnvironmentVariable("O_O_DEBUG") == "1")
        {
            foreach (var g in view.GetVisualDescendants().OfType<TKCode.Arc.Gauge>())
            {
                System.Console.Error.WriteLine($"[snapshot] gauge value={g.Value} max={g.Maximum} label={g.Label} visible={g.IsEffectivelyVisible}");
            }
        }
        using var bitmap = new RenderTargetBitmap(new PixelSize((int)width, (int)height));
        bitmap.Render(view);
        bitmap.Save(output, PngBitmapEncoderOptions.Default);
        window.Close();
    }

    private static void Pump(TimeSpan duration)
    {
        var until = DateTime.UtcNow + duration;
        while (DateTime.UtcNow < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(15);
        }
    }
}
