using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;

namespace Island.UI.Console;

/// <summary>docs/design/04-console.md §3.10 (first part): version and where O_O keeps its data.</summary>
public sealed class AboutPage : StackPanel
{
    public AboutPage(string dataDirectory)
    {
        var version = Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                      ?? "dev";
        Children.Add(new StackPanel
        {
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 24, 0, 32),
            Children =
            {
                new MascotHost { Size = 96, Wander = true, HorizontalAlignment = HorizontalAlignment.Center },
                CenteredText("O_O", ArcDesignTokenKind.Type2XL, ArcDesignTokenKind.Foreground, FontWeight.SemiBold),
                CenteredText($"版本 {version.Split('+')[0]}", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary),
            },
        });
        Children.Add(Group(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { Text("吉祥物", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), MascotPicker() } },
                Text("选择控制台侧栏和这里显示的吉祥物，以及它的颜色。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        }));
        Children.Add(Group(new StackPanel
        {
            Spacing = 6,
            Children =
            {
                Text("数据目录", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
                Text(dataDirectory, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
                Text("插件的存储、设置和授权记录都在这里；密钥保存在系统钥匙串中。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        }));
    }

    /// <summary>
    /// The kind of mascot and its colour. The choice is saved and used everywhere a mascot is shown. The swatches and
    /// the kind follow <see cref="MascotStyle.Changed"/>, so a change made anywhere is shown here too.
    /// </summary>
    private static StackPanel MascotPicker()
    {
        var kinds = new SegmentedControl
        {
            Items =
            {
                new SegmentedOption { Value = nameof(MascotKind.Classic), Label = "经典" },
                new SegmentedOption { Value = nameof(MascotKind.Pixel), Label = "像素" },
                new SegmentedOption { Value = nameof(MascotKind.Ghost), Label = "幽灵" },
                new SegmentedOption { Value = nameof(MascotKind.Cube), Label = "方块" },
            },
        };
        kinds.SelectionChanged += (_, _) =>
        {
            if (kinds.SelectedValue is string value && Enum.TryParse<MascotKind>(value, out var kind))
            {
                MascotStyle.Set(kind, MascotStyle.Tint);
            }
        };

        var defaultColor = new TKCode.Arc.Button { Content = "默认", Size = ControlSize.Small, Variant = ButtonVariant.Secondary };
        defaultColor.Click += (_, _) => MascotStyle.Set(MascotStyle.Kind, null);
        var swatches = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var swatchBorders = new List<(Color Value, Border Border)>();
        foreach (var (label, value) in MascotStyle.Swatches)
        {
            var swatch = new Border
            {
                Width = 26,
                Height = 26,
                CornerRadius = new CornerRadius(13),
                Background = new SolidColorBrush(value),
                BorderThickness = new Thickness(2),
                Cursor = new Cursor(StandardCursorType.Hand),
            };
            ToolTip.SetTip(swatch, label);
            swatch.PointerPressed += (_, _) => MascotStyle.Set(MascotStyle.Kind, value);
            swatchBorders.Add((value, swatch));
            swatches.Children.Add(swatch);
        }
        swatches.Children.Insert(0, defaultColor);

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(kinds);
        panel.Children.Add(swatches);

        void Show(object? sender, EventArgs e)
        {
            kinds.SelectedValue = MascotStyle.Kind.ToString();
            defaultColor.Variant = MascotStyle.Tint is null ? ButtonVariant.Primary : ButtonVariant.Secondary;
            foreach (var (value, border) in swatchBorders)
            {
                border.BorderBrush = MascotStyle.Tint == value
                    ? new SolidColorBrush(Colors.White)
                    : Brushes.Transparent;
            }
        }
        Show(null, EventArgs.Empty);
        MascotStyle.Changed += Show;
        panel.DetachedFromVisualTree += (_, _) => MascotStyle.Changed -= Show;
        return panel;
    }

    private static TextBlock CenteredText(string text, ArcDesignTokenKind size, ArcDesignTokenKind color, FontWeight? weight = null)
    {
        var t = Text(text, size, color, weight, display: size == ArcDesignTokenKind.Type2XL);
        t.HorizontalAlignment = HorizontalAlignment.Center;
        return t;
    }
}
