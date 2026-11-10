using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Island.Core.Themes;
using Island.Platform;
using Island.Plugins;
using Island.Plugins.Host;
using TKCode.Arc;
using TKCode.Arc.Themes.Corners;

namespace Island.UI.Themes;

/// <summary>A theme an enabled plugin offers (docs/design/03-plugin-system.md §9).</summary>
/// <param name="Key"><c>plugin/theme</c>.</param>
public sealed record ThemeOption(string Key, string Title, string? Description, string Plugin, Color? Accent,
    ArcAppearance? Appearance, double? Corners);

/// <summary>
/// Themes for every O_O window (docs/design/05-builtin-plugins.md §3): theme packs from plugins, chosen in the
/// console (外观) or the island's menu; an optional light/dark schedule; an optional accent from the wallpaper.
/// A theme sets the appearance (when it names one), the accent and the corners through Arc's own levers; afterwards
/// the user may still change appearance and accent by hand — the theme stays chosen, the hand-made change wins until
/// another theme is picked. The island itself stays black whatever the theme.
/// </summary>
public sealed class ThemeService
{
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan WallpaperEvery = TimeSpan.FromMinutes(10);

    private readonly PluginHost _plugins;
    private readonly ThemePreferencesStore _store;
    private readonly IWallpaperSource? _wallpaper;
    private readonly DispatcherTimer _timer;
    private bool? _scheduledLight;
    private DateTime _wallpaperCheckedAt = DateTime.MinValue;
    private Color? _wallpaperAccent;

    private ThemeService(PluginHost plugins, ThemePreferencesStore store, IWallpaperSource? wallpaper)
    {
        _plugins = plugins;
        _store = store;
        _wallpaper = wallpaper;
        _timer = new DispatcherTimer(Tick, DispatcherPriority.Background, (_, _) => Evaluate());
        plugins.PluginStateChanged += (_, _, _) => Dispatcher.UIThread.Post(() => Changed?.Invoke());
        ArcTheme.Manager.StateChanged += (_, _) => Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>The app's theme service, once started (null in tools that render without the app).</summary>
    public static ThemeService? Current { get; private set; }

    /// <summary>Starts the service: re-applies the chosen theme's corners, then the schedule and wallpaper accent.</summary>
    public static ThemeService Start(PluginHost plugins, ThemePreferencesStore store, IWallpaperSource? wallpaper)
    {
        var service = new ThemeService(plugins, store, wallpaper);
        Current = service;
        // Appearance and accent come back from the app settings; the corners are the theme's alone.
        if (service.Selected is { Corners: { } corners })
        {
            ArcTheme.Apply(t => t.Corners(ArcCornerStyle.Scale(corners)));
        }
        service.Evaluate();
        service._timer.Start();
        ThemeMenu.Refresh();
        if (Environment.GetEnvironmentVariable("O_O_DEBUG_WALLPAPER") == "1" && wallpaper is not null)
        {
            // Development aid: what "从壁纸取色" would pick, logged only — nothing is applied or saved.
            _ = Task.Run(() =>
            {
                var bytes = wallpaper.ReadImage();
                System.Console.Error.WriteLine($"[theme] wallpaper image: {(bytes is null ? "none" : $"{bytes.Length} bytes")}; " +
                                               $"accent: {WallpaperColorNow(wallpaper)?.ToString() ?? "(none)"}");
            });
        }
        return service;
    }

    /// <summary>Raised when the themes on offer, the chosen one, the preferences or the app's appearance change.</summary>
    public event Action? Changed;

    public ThemePreferences Preferences => _store.Current;

    /// <summary>The themes of the enabled plugins, the default (Apple) look first.</summary>
    public IReadOnlyList<ThemeOption> Themes => [.. _plugins.Packages
        .Where(p => p.Manifest.Contributes.Themes.Count > 0 && _plugins.IsEnabled(p.Id))
        .OrderBy(p => p.Id == "com.o_o.theme.default" ? 0 : 1)
        .ThenBy(p => p.Manifest.Name, StringComparer.CurrentCulture)
        .SelectMany(p => p.Manifest.Contributes.Themes.Select(t => Option(p, t)))];

    /// <summary>The chosen theme; before the user chooses one, the default pack's (it is the look in effect).</summary>
    public ThemeOption? Selected =>
        Themes.FirstOrDefault(t => t.Key == _store.Current.Theme) ??
        (_store.Current.Theme is null ? Themes.FirstOrDefault(t => t.Key.StartsWith("com.o_o.theme.default/", StringComparison.Ordinal)) : null);

    /// <summary>Applies a theme now and remembers it.</summary>
    public void Apply(string key)
    {
        if (Themes.FirstOrDefault(t => t.Key == key) is not { } theme)
        {
            return;
        }
        _store.Set(_store.Current with { Theme = key });
        ArcTheme.Apply(t =>
        {
            if (theme.Appearance is { } appearance && !_store.Current.Schedule)
            {
                t.Appearance(appearance); // a schedule keeps deciding light and dark
            }
            t.Accent(_store.Current.WallpaperAccent && _wallpaperAccent is { } fromWallpaper ? fromWallpaper : theme.Accent ?? AppleTheme.SystemBlue);
            t.Corners(ArcCornerStyle.Scale(theme.Corners ?? 0.6));
        });
        Changed?.Invoke();
    }

    /// <summary>Light, dark or following the system, by hand (this turns the schedule off).</summary>
    public void SetAppearance(ArcAppearance appearance)
    {
        if (_store.Current.Schedule)
        {
            _store.Set(_store.Current with { Schedule = false });
        }
        ArcTheme.Apply(t => t.Appearance(appearance));
        Changed?.Invoke();
    }

    public void SetSchedule(bool on, TimeOnly lightAt, TimeOnly darkAt)
    {
        _store.Set(_store.Current with { Schedule = on, LightAt = lightAt, DarkAt = darkAt });
        _scheduledLight = null; // decide again now
        Evaluate();
        Changed?.Invoke();
    }

    public void SetWallpaperAccent(bool on)
    {
        _store.Set(_store.Current with { WallpaperAccent = on });
        _wallpaperCheckedAt = DateTime.MinValue;
        if (!on)
        {
            _wallpaperAccent = null;
            var accent = Selected?.Accent ?? AppleTheme.SystemBlue;
            ArcTheme.Apply(t => t.Accent(accent));
        }
        Evaluate();
        Changed?.Invoke();
    }

    /// <summary>The colour the wallpaper gave last, for the console to show; null before the first look or without one.</summary>
    public Color? WallpaperColor => _wallpaperAccent;

    private void Evaluate()
    {
        var prefs = _store.Current;
        if (prefs.Schedule)
        {
            // Only switch when the schedule's answer changes, so a hand-made change holds until the next boundary.
            var light = prefs.IsLightAt(TimeOnly.FromDateTime(DateTime.Now));
            if (_scheduledLight != light)
            {
                _scheduledLight = light;
                ArcTheme.Apply(t => t.Appearance(light ? ArcAppearance.Light : ArcAppearance.Dark));
            }
        }
        else
        {
            _scheduledLight = null;
        }
        if (prefs.WallpaperAccent && _wallpaper is not null && DateTime.UtcNow - _wallpaperCheckedAt >= WallpaperEvery)
        {
            _wallpaperCheckedAt = DateTime.UtcNow;
            _ = UpdateWallpaperAccent();
        }
    }

    private async Task UpdateWallpaperAccent()
    {
        var color = await Task.Run(() => WallpaperColorNow(_wallpaper!));
        if (Environment.GetEnvironmentVariable("O_O_DEBUG") == "1")
        {
            System.Console.Error.WriteLine($"[theme] wallpaper accent: {color?.ToString() ?? "(none)"}");
        }
        if (color is not { } accent || !_store.Current.WallpaperAccent)
        {
            return;
        }
        _wallpaperAccent = accent;
        ArcTheme.Apply(t => t.Accent(accent));
        Changed?.Invoke();
    }

    /// <summary>Reads the wallpaper and picks its accent (<see cref="WallpaperAccent"/>); null when there is none.</summary>
    public static Color? WallpaperColorNow(IWallpaperSource source)
    {
        try
        {
            if (source.ReadImage() is not { Length: > 0 } bytes)
            {
                return null;
            }
            using var stream = new MemoryStream(bytes);
            using var bitmap = Bitmap.DecodeToWidth(stream, 64);
            var size = bitmap.PixelSize;
            var stride = size.Width * 4;
            var pixels = new byte[stride * size.Height];
            var handle = GCHandle.Alloc(pixels, GCHandleType.Pinned);
            try
            {
                bitmap.CopyPixels(new PixelRect(size), handle.AddrOfPinnedObject(), pixels.Length, stride);
            }
            finally
            {
                handle.Free();
            }
            // Decoded bitmaps are 32-bit BGRA on every desktop Skia backend; RGBA (some Linux builds) swaps red and blue.
            if (bitmap.Format == PixelFormats.Rgba8888)
            {
                for (var i = 0; i + 2 < pixels.Length; i += 4)
                {
                    (pixels[i], pixels[i + 2]) = (pixels[i + 2], pixels[i]);
                }
            }
            return WallpaperAccent.Pick(pixels) is var (r, g, b) ? Color.FromRgb(r, g, b) : null;
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or ArgumentException or NotSupportedException or COMException)
        {
            return null;
        }
    }

    private static ThemeOption Option(PluginPackage package, ThemeContribution t) => new(
        $"{package.Id}/{t.Id}",
        t.Title ?? t.Id,
        t.Description,
        package.Manifest.Name,
        t.Accent is { } a && Color.TryParse(a, out var color) ? color : null,
        t.Appearance switch { "light" => ArcAppearance.Light, "dark" => ArcAppearance.Dark, "system" => ArcAppearance.System, _ => null },
        t.Corners);
}
