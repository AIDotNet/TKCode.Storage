using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Island.UI.Themes;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;

namespace Island.UI.Console;

/// <summary>docs/design/04-console.md §3.2 (first part): appearance and accent. Changes apply and are saved at once.</summary>
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

    public AppearancePage()
    {
        Children.Add(PageTitle("外观", "所有窗口默认采用苹果风格。灵动岛本身始终是黑色，以便与刘海融为一体。"));

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
            if (mode.SelectedValue is string value && Enum.TryParse<ArcAppearance>(value, out var appearance))
            {
                ArcTheme.Apply(t => t.Appearance(appearance));
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
            if (accent.SelectedValue is string value && Color.TryParse(value, out var seed))
            {
                ArcTheme.Apply(t => t.Accent(seed));
            }
        };

        Children.Add(Group(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { Text("外观", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), mode } },
                accent,
            },
        }));
    }
}
