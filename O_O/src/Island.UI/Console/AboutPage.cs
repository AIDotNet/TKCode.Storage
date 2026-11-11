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
        if (Island.UI.Services.UpdateService.Shared is { } updates)
        {
            Children.Add(Group(new UpdatePanel(updates)));
        }
        Children.Add(Group(new StackPanel
        {
            Spacing = 16,
            Children =
            {
                new StackPanel { Spacing = 8, Children = { Text("吉祥物", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), MascotPicker() } },
                Text("选择控制台侧栏和这里显示的吉祥物，以及它的颜色。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
            },
        }));
        Children.Add(Group(DataDirectoryPanel(dataDirectory)));
    }

    /// <summary>
    /// 数据目录: where O_O keeps its data, and (where the app allows it) moving it to another folder. A move asks first,
    /// then restarts O_O, which copies the data over before using it (Island.Core.Storage.DataLocation).
    /// </summary>
    private StackPanel DataDirectoryPanel(string pluginDataDirectory)
    {
        var service = Island.UI.Services.DataDirectoryService.Shared;
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(Text("数据目录", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        panel.Children.Add(Text(service?.Current ?? pluginDataDirectory, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        panel.Children.Add(Text("插件、插件的存储与设置、授权记录和偏好都在这里；密钥保存在系统钥匙串中。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        if (service is null)
        {
            return panel;
        }
        if (service.Notice is { } notice)
        {
            panel.Children.Add(Text(notice, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, wrap: true));
        }

        var status = Text("", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, wrap: true);
        status.IsVisible = false;
        var actions = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 6, 0, 0) };
        var confirm = new StackPanel { Spacing = 8, IsVisible = false, Margin = new Thickness(0, 6, 0, 0) };
        var confirmText = Text("", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.Foreground, wrap: true);
        var move = new TKCode.Arc.Button { Content = "移动并重启", Size = ControlSize.Small, Variant = ButtonVariant.Primary };
        var cancel = new TKCode.Arc.Button { Content = "取消", Size = ControlSize.Small, Variant = ButtonVariant.Secondary };
        confirm.Children.Add(confirmText);
        confirm.Children.Add(new WrapPanel { ItemSpacing = 8, Children = { move, cancel } });
        string? chosen = null;

        void Show(string? problem)
        {
            status.Text = problem ?? "";
            status.IsVisible = problem is not null;
        }
        void Ask(string folder)
        {
            chosen = folder;
            Show(null);
            confirmText.Text = $"把数据移动到 {folder}（已有其他文件时放在其中新建的 O_O 文件夹里）。O_O 会重新启动，移动完成前请勿关闭。";
            confirm.IsVisible = true;
            actions.IsVisible = false;
        }
        void Reset()
        {
            chosen = null;
            confirm.IsVisible = false;
            actions.IsVisible = true;
        }

        var change = new TKCode.Arc.Button { Content = "更改…", Size = ControlSize.Small, Variant = ButtonVariant.Secondary };
        change.Click += async (_, _) =>
        {
            if (TopLevel.GetTopLevel(this)?.StorageProvider is not { CanPickFolder: true } storage)
            {
                Show("这个平台不能选择文件夹。");
                return;
            }
            var folders = await storage.OpenFolderPickerAsync(new Avalonia.Platform.Storage.FolderPickerOpenOptions
            {
                Title = "选择新的数据目录",
                AllowMultiple = false,
            });
            if (folders is [var folder] && Avalonia.Platform.Storage.StorageProviderExtensions.TryGetLocalPath(folder) is { } path)
            {
                Ask(path);
            }
        };
        actions.Children.Add(change);
        if (!string.Equals(service.Current, service.Default, StringComparison.Ordinal))
        {
            var reset = new TKCode.Arc.Button { Content = "恢复默认", Size = ControlSize.Small, Variant = ButtonVariant.Secondary };
            reset.Click += (_, _) => Ask(service.Default);
            actions.Children.Add(reset);
        }
        move.Click += (_, _) =>
        {
            if (chosen is null)
            {
                return;
            }
            var problem = service.MoveAndRestart(chosen);
            Reset();
            Show(problem);
        };
        cancel.Click += (_, _) => Reset();

        panel.Children.Add(actions);
        panel.Children.Add(confirm);
        panel.Children.Add(status);
        return panel;
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
