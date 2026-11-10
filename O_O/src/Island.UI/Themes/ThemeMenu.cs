using TKCode.Arc;

namespace Island.UI.Themes;

/// <summary>
/// The island menu's 外观 submenu (docs/design/05-builtin-plugins.md §3, quick switch): 跟随系统 / 浅色 / 深色 and the
/// installed themes. The island's menus are built once, possibly before the themes are known, so their rows are
/// refilled whenever themes, the chosen one or the appearance change.
/// </summary>
public static class ThemeMenu
{
    private static readonly List<WeakReference<MenuEntry>> s_menus = [];
    private static ThemeService? s_subscribed;

    /// <summary>Fills <paramref name="parent"/> now and keeps it current.</summary>
    public static void Attach(MenuEntry parent)
    {
        s_menus.Add(new WeakReference<MenuEntry>(parent));
        Fill(parent);
    }

    /// <summary>Called once the theme service runs: subscribes and refills every attached menu.</summary>
    public static void Refresh()
    {
        if (ThemeService.Current is { } service && service != s_subscribed)
        {
            s_subscribed = service;
            service.Changed += Refresh;
        }
        s_menus.RemoveAll(w => !w.TryGetTarget(out _));
        foreach (var weak in s_menus)
        {
            if (weak.TryGetTarget(out var parent))
            {
                Fill(parent);
            }
        }
    }

    private static void Fill(MenuEntry parent)
    {
        parent.Items.Clear();
        var appearance = ArcTheme.State.Appearance;
        var service = ThemeService.Current;
        var scheduled = service?.Preferences.Schedule == true;
        foreach (var (value, label) in new[] { (ArcAppearance.System, "跟随系统"), (ArcAppearance.Light, "浅色"), (ArcAppearance.Dark, "深色") })
        {
            var entry = new MenuEntry
            {
                Header = label,
                ToggleType = MenuEntryToggleType.Radio,
                GroupName = "appearance",
                IsChecked = !scheduled && appearance == value,
            };
            entry.Click += (_, _) =>
            {
                if (service is not null)
                {
                    service.SetAppearance(value); // also turns a schedule off: the user chose by hand
                }
                else
                {
                    ArcTheme.Apply(t => t.Appearance(value));
                }
            };
            parent.Items.Add(entry);
        }
        if (scheduled)
        {
            parent.Items.Add(new MenuEntry { Header = "按时间自动切换中", IsEnabled = false });
        }
        if (service?.Themes is { Count: > 0 } themes)
        {
            var first = true;
            foreach (var theme in themes)
            {
                var entry = new MenuEntry
                {
                    Header = theme.Title,
                    ToggleType = MenuEntryToggleType.Radio,
                    GroupName = "theme",
                    IsChecked = service.Selected?.Key == theme.Key,
                    HasSeparatorBefore = first,
                };
                first = false;
                var key = theme.Key;
                entry.Click += (_, _) => service.Apply(key);
                parent.Items.Add(entry);
            }
        }
    }
}
