using System.Text.Json;
using System.Text.Json.Serialization;
using Avalonia.Controls;
using Avalonia.Media;
using Island.UI;
using Island.UI.Themes;
using TKCode.Arc;

namespace Island.App.Settings;

/// <summary>
/// The preferences kept between runs: on the desktop in <c>settings.json</c> under the user's application data folder
/// (<see cref="DefaultPath"/>), elsewhere wherever <see cref="Store"/> puts them. They are read once, the first time
/// <see cref="Current"/> is used, and written after every change. To keep something else, add a property and call
/// <see cref="Save"/> when it changes.
/// </summary>
public sealed class AppSettings
{
    private static AppSettings? s_current;
    private ISettingsStore _store = null!;

    /// <summary>The settings of this run, read from <see cref="Store"/> on first use.</summary>
    public static AppSettings Current => s_current ??= Load(Store);

    // DefaultPath comes before Store: static properties are initialized in the order they are written.
    /// <summary>The desktop settings file: <c>&lt;application data&gt;/O_O/settings.json</c>.
    /// <c>O_O_SETTINGS_DIR</c> replaces that folder, for development (a run that leaves the real data alone).</summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("O_O_SETTINGS_DIR") is { Length: > 0 } dir
            ? Path.GetFullPath(dir)
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData, Environment.SpecialFolderOption.DoNotVerify),
                "O_O"),
        "settings.json");

    /// <summary>
    /// Where the settings are read from and written to: <see cref="DefaultPath"/> unless a host sets another store
    /// before the app is built (a browser host keeps them in the page's local storage, tests in memory).
    /// </summary>
    public static ISettingsStore Store { get; set; } = new FileSettingsStore(DefaultPath);

    /// <summary>The default folder: holds the settings file, and the data too unless it was moved (<see cref="DataDirectory"/>).</summary>
    public static string DefaultRoot => Path.GetDirectoryName(DefaultPath)!;

    /// <summary>
    /// Where O_O keeps its data this run (plugin data, user plugins, preferences): the folder the user chose, or
    /// <see cref="DefaultRoot"/>. Settled by <see cref="Program"/> before the app starts (see Island.Core.Storage.DataLocation).
    /// </summary>
    public static string DataRoot { get; set; } = DefaultRoot;

    /// <summary>The data directory the user chose (when installing, or on the About page), or null for <see cref="DefaultRoot"/>.</summary>
    public string? DataDirectory { get; set; }

    /// <summary>A data directory to move the data to at the next start, or null. Set on the About page.</summary>
    public string? PendingDataDirectory { get; set; }

    /// <summary>Light, dark, or following the system.</summary>
    public ArcAppearance Appearance { get; set; } = ArcAppearance.System;

    /// <summary>The accent seed (<c>#AARRGGBB</c>), or null for the accent the app was created with.</summary>
    public string? Accent { get; set; }

    /// <summary>The main window's size and position when it was last closed, or null before the first close.</summary>
    public WindowPlacement? Window { get; set; }

    /// <summary>The kind of mascot (classic, pixel, ghost or cube). Chosen on the About page.</summary>
    public MascotKind Mascot { get; set; } = MascotKind.Classic;

    /// <summary>The mascot's colour (<c>#AARRGGBB</c>), or null for the theme's own. Chosen on the About page.</summary>
    public string? MascotColor { get; set; }

    /// <summary>Reads the settings from <paramref name="store"/>; nothing stored, or something unreadable, gives the defaults.</summary>
    public static AppSettings Load(ISettingsStore store)
    {
        AppSettings? settings = null;
        try
        {
            if (store.Read() is { Length: > 0 } json)
            {
                settings = JsonSerializer.Deserialize(json, AppSettingsJsonContext.Default.AppSettings);
            }
        }
        catch (JsonException)
        {
            // Damaged settings must not stop the app: start from the defaults and overwrite them on the next save.
        }
        settings ??= new AppSettings();
        settings._store = store;
        return settings;
    }

    /// <summary>Writes the settings to their store. Failures are ignored: preferences are a convenience.</summary>
    public void Save()
    {
        if (Design.IsDesignMode)
        {
            return;
        }
        Write();
    }

    /// <summary>Writes the settings without asking Avalonia anything: usable before the app is built.</summary>
    internal void Write() => _store.Write(JsonSerializer.Serialize(this, AppSettingsJsonContext.Default.AppSettings));

    /// <summary>The start-up theme: the saved appearance and accent (Program.cs passes this to <c>WithTheme</c>).</summary>
    public void ApplyTheme(ArcThemeBuilder theme)
    {
        // Every window is Apple style by default; a saved accent of the user's own choosing goes on top.
        theme.Appearance(Appearance).Apple();
        if (Accent is not null && Color.TryParse(Accent, out var seed))
        {
            theme.Accent(seed);
        }
    }

    /// <summary>Saves the appearance and the accent whenever the theme changes, from anywhere in the app.</summary>
    public void TrackTheme()
    {
        ArcTheme.Manager.StateChanged += (_, e) =>
        {
            var accent = e.NewState.Accent?.ToString();
            if (e.NewState.Appearance == Appearance && accent == Accent)
            {
                return;
            }
            Appearance = e.NewState.Appearance;
            Accent = accent;
            Save();
        };
    }

    /// <summary>Starts from the saved mascot and saves it whenever it changes, from anywhere in the app.</summary>
    public void TrackMascot()
    {
        MascotStyle.Set(Mascot, Color.TryParse(MascotColor ?? "", out var tint) ? tint : null);
        MascotStyle.Changed += (_, _) =>
        {
            var color = MascotStyle.Tint?.ToString();
            if (MascotStyle.Kind == Mascot && color == MascotColor)
            {
                return;
            }
            Mascot = MascotStyle.Kind;
            MascotColor = color;
            Save();
        };
    }

    /// <summary>Puts <paramref name="window"/> where it was last closed and saves its placement when it closes again.</summary>
    public void TrackWindow(Window window)
    {
        Window?.Restore(window);
        window.Closing += (_, _) =>
        {
            Window = WindowPlacement.Capture(window, Window);
            Save();
        };
    }
}

/// <summary>A window's size, position and maximized state.</summary>
public sealed class WindowPlacement
{
    /// <summary>The left edge, in screen pixels.</summary>
    public int X { get; set; }

    /// <summary>The top edge, in screen pixels.</summary>
    public int Y { get; set; }

    /// <summary>The client width, in device-independent pixels.</summary>
    public double Width { get; set; }

    /// <summary>The client height, in device-independent pixels.</summary>
    public double Height { get; set; }

    /// <summary>Whether the window was maximized.</summary>
    public bool IsMaximized { get; set; }

    /// <summary>
    /// Reads the placement of <paramref name="window"/>. A maximized window keeps the size and position it had before
    /// (<paramref name="previous"/>), so it un-maximizes to them next time.
    /// </summary>
    public static WindowPlacement Capture(Window window, WindowPlacement? previous)
    {
        var maximized = window.WindowState == WindowState.Maximized;
        if (maximized && previous is not null)
        {
            return new WindowPlacement
            {
                X = previous.X, Y = previous.Y, Width = previous.Width, Height = previous.Height, IsMaximized = true
            };
        }
        return new WindowPlacement
        {
            X = window.Position.X,
            Y = window.Position.Y,
            Width = window.ClientSize.Width,
            Height = window.ClientSize.Height,
            IsMaximized = maximized
        };
    }

    /// <summary>Applies the placement before the window is shown; a position on a screen that is gone is skipped.</summary>
    public void Restore(Window window)
    {
        if (Width >= window.MinWidth && Height >= window.MinHeight)
        {
            window.Width = Width;
            window.Height = Height;
        }
        var position = new Avalonia.PixelPoint(X, Y);
        foreach (var screen in window.Screens.All)
        {
            if (screen.WorkingArea.Contains(position))
            {
                window.WindowStartupLocation = WindowStartupLocation.Manual;
                window.Position = position;
                break;
            }
        }
        if (IsMaximized)
        {
            window.WindowState = WindowState.Maximized;
        }
    }
}

/// <summary>The settings file's serializer, generated at build time (no reflection, trimming and AOT safe).</summary>
[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
internal sealed partial class AppSettingsJsonContext : JsonSerializerContext
{
}
