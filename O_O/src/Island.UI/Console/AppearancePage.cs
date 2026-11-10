using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.UI.Themes;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Console;

/// <summary>
/// docs/design/04-console.md §3.2 and 05 §3: theme, appearance and accent, an automatic light/dark schedule, and the
/// accent from the wallpaper. Changes apply and are saved at once.
/// </summary>
public sealed class AppearancePage : StackPanel
{
    // The Apple look's blue first (the default), then Arc's presets.
    private static readonly (string Label, Color Seed)[] Accents =
    [
        ("Apple 蓝（默认）", AppleTheme.SystemBlue),
        ("中性", Color.Parse("#353535")),
        ("紫罗兰", Color.Parse("#7747FF")),
        ("蓝", Color.Parse("#0562EF")),
        ("绿", Color.Parse("#0DB879")),
        ("琥珀", Color.Parse("#F3AD20")),
        ("橙", Color.Parse("#F48120")),
        ("珊瑚", Color.Parse("#F15F55")),
        ("玫红", Color.Parse("#ED4E9D")),
    ];

    // Half-hour steps for the schedule.
    private static readonly TimeOnly[] Times = [.. Enumerable.Range(0, 48).Select(i => new TimeOnly(i / 2, i % 2 * 30))];

    private readonly ThemeService? _themes = ThemeService.Current;
    private bool _syncing;

    public AppearancePage()
    {
        Children.Add(PageTitle("外观", "所有窗口默认采用苹果风格。灵动岛本身始终是黑色，以便与刘海融为一体。"));
        if (_themes is { Themes.Count: > 0 })
        {
            Children.Add(SectionLabel("主题"));
            Children.Add(Group(ThemeCards(_themes)));
        }
        Children.Add(SectionLabel("外观与强调色"));
        Children.Add(Group(ModeAndAccent()));
        if (_themes is not null)
        {
            Children.Add(SectionLabel("自动"));
            Children.Add(Group(Automatic(_themes)));
        }
    }

    private Control ThemeCards(ThemeService themes)
    {
        var cards = new RadioCards { MinColumnWidth = 180 };
        foreach (var theme in themes.Themes)
        {
            cards.Items.Add(new RadioCardOption
            {
                Value = theme.Key,
                Label = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children =
                    {
                        new Avalonia.Controls.Shapes.Ellipse
                        {
                            Width = 12,
                            Height = 12,
                            VerticalAlignment = VerticalAlignment.Center,
                            Fill = new SolidColorBrush(theme.Accent ?? AppleTheme.SystemBlue),
                        },
                        new TextBlock { Text = theme.Title, VerticalAlignment = VerticalAlignment.Center },
                    },
                },
                Description = theme.Description ?? Describe(theme),
            });
        }
        cards.SelectedValue = themes.Selected?.Key;
        cards.SelectionChanged += (_, _) =>
        {
            if (!_syncing && cards.SelectedValue is string key)
            {
                themes.Apply(key);
            }
        };
        return new StackPanel
        {
            Spacing = 10,
            Children =
            {
                cards,
                Text("主题设定强调色和圆角（有的也设定浅色或深色）。之后仍可在下面单独调整，直到再选一次主题。更多主题在 插件 › 添加插件 里。",
                    ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        };
    }

    private static string Describe(ThemeOption theme) => theme.Appearance switch
    {
        ArcAppearance.Dark => "深色",
        ArcAppearance.Light => "浅色",
        _ => "跟随系统的浅色或深色",
    };

    private Control ModeAndAccent()
    {
        var mode = new SegmentedControl
        {
            Items =
            {
                new SegmentedOption { Value = nameof(ArcAppearance.System), Label = "跟随系统" },
                new SegmentedOption { Value = nameof(ArcAppearance.Light), Label = "浅色" },
                new SegmentedOption { Value = nameof(ArcAppearance.Dark), Label = "深色" },
            },
            SelectedValue = ArcTheme.State.Appearance.ToString(),
        };
        mode.SelectionChanged += (_, _) =>
        {
            if (!_syncing && mode.SelectedValue is string value && Enum.TryParse<ArcAppearance>(value, out var appearance))
            {
                if (_themes is not null)
                {
                    _themes.SetAppearance(appearance); // choosing by hand turns a schedule off
                }
                else
                {
                    ArcTheme.Apply(t => t.Appearance(appearance));
                }
            }
        };

        var accent = new Select { Label = "强调色", Description = "按钮、开关和选中状态使用的颜色。" };
        foreach (var (label, seed) in Accents)
        {
            accent.Items.Add(new SelectItem { Content = label, Value = seed.ToString() });
        }
        accent.SelectedValue = ArcTheme.State.Accent?.ToString() ?? AppleTheme.SystemBlue.ToString();
        accent.SelectionChanged += (_, _) =>
        {
            if (!_syncing && accent.SelectedValue is string value && Color.TryParse(value, out var seed))
            {
                ArcTheme.Apply(t => t.Accent(seed));
            }
        };

        // The schedule or the island's menu may change the appearance while this page is open.
        void Sync(object? sender, ArcThemeChangedEventArgs e) => Dispatcher.UIThread.Post(() =>
        {
            _syncing = true;
            try
            {
                mode.SelectedValue = e.NewState.Appearance.ToString();
            }
            finally
            {
                _syncing = false;
            }
        });
        ArcTheme.Manager.StateChanged += Sync;
        DetachedFromVisualTree += (_, _) => ArcTheme.Manager.StateChanged -= Sync;

        return new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { Text("外观", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), mode } },
                accent,
            },
        };
    }

    private Control Automatic(ThemeService themes)
    {
        var prefs = themes.Preferences;
        var schedule = new ArcSwitch { Content = "按时间自动切换浅色和深色", IsChecked = prefs.Schedule };
        var light = TimeSelect("浅色开始", prefs.LightAt);
        var dark = TimeSelect("深色开始", prefs.DarkAt);
        var times = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, IsVisible = prefs.Schedule, Children = { light, dark } };
        void Commit()
        {
            times.IsVisible = schedule.IsChecked == true;
            themes.SetSchedule(schedule.IsChecked == true, Parse(light.SelectedValue, prefs.LightAt), Parse(dark.SelectedValue, prefs.DarkAt));
        }
        schedule.IsCheckedChanged += (_, _) => Commit();
        light.SelectionChanged += (_, _) => Commit();
        dark.SelectionChanged += (_, _) => Commit();

        var wallpaper = new ArcSwitch { Content = "从桌面壁纸取强调色", IsChecked = prefs.WallpaperAccent };
        var wallpaperNote = Text(WallpaperNote(themes), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
        wallpaper.IsCheckedChanged += (_, _) => themes.SetWallpaperAccent(wallpaper.IsChecked == true);
        // A theme may ship an alert sound (docs/design/09-island-v2.md §4); it plays only once turned on here.
        var sounds = new ArcSwitch { Content = "提醒时播放主题的提示音", IsChecked = prefs.AlertSounds };
        var soundsNote = Text(SoundsNote(), ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
        sounds.IsCheckedChanged += (_, _) => themes.SetAlertSounds(sounds.IsChecked == true);
        void Note() => Dispatcher.UIThread.Post(() =>
        {
            wallpaperNote.Text = WallpaperNote(themes);
            soundsNote.Text = SoundsNote();
        });
        themes.Changed += Note;
        DetachedFromVisualTree += (_, _) => themes.Changed -= Note;

        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        schedule,
                        times,
                        Text("手动选择浅色或深色会关闭自动切换。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
                    },
                },
                new StackPanel { Spacing = 6, Children = { wallpaper, wallpaperNote } },
                new StackPanel { Spacing = 6, Children = { sounds, soundsNote } },
            },
        };
    }

    private static string SoundsNote() => IslandTheme.Current.AlertSound is not null
        ? "当前主题带有提示音：有提醒弹出时播放一次。"
        : "当前主题没有提示音；换成带提示音的主题后才会播放。";

    private static string WallpaperNote(ThemeService themes) =>
        !themes.Preferences.WallpaperAccent ? "开启后取壁纸中最显眼的颜色作为强调色，每 10 分钟看一次壁纸是否变了。"
        : themes.WallpaperColor is { } c ? $"当前取到的颜色：{c.ToString().ToUpperInvariant()}（壁纸没有明显颜色时保持原来的强调色）。"
        : "正在读取壁纸……（壁纸没有明显颜色时保持原来的强调色）";

    private static Select TimeSelect(string label, TimeOnly current)
    {
        var select = new Select { Label = label, Width = 140 };
        foreach (var t in Times)
        {
            var text = t.ToString("HH:mm", CultureInfo.InvariantCulture);
            select.Items.Add(new SelectItem { Content = text, Value = text });
        }
        select.SelectedValue = current.ToString("HH:mm", CultureInfo.InvariantCulture);
        return select;
    }

    private static TimeOnly Parse(object? value, TimeOnly fallback) =>
        value is string s && TimeOnly.TryParseExact(s, "HH:mm", CultureInfo.InvariantCulture, DateTimeStyles.None, out var t) ? t : fallback;
}
