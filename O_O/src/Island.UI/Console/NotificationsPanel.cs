using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Island.Platform;
using Island.Plugins.Host;
using Island.Plugins.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// What plugins get to see of other apps' notifications (docs/design/08 §7), and whether the system lets O_O read them:
/// a default (full / sender only / hidden), a choice for 微信 and QQ, and the listener's access state.
/// </summary>
public sealed class NotificationsPanel : StackPanel
{
    private static readonly (string Value, string Label)[] Modes =
    [
        (nameof(NotificationDisplay.Full), "完整内容"),
        (nameof(NotificationDisplay.SenderOnly), "仅发送人"),
        (nameof(NotificationDisplay.Hidden), "隐藏内容"),
    ];

    private const string FollowDefault = "Default";

    private static readonly (string AppId, string Name)[] Apps =
    [
        ("com.tencent.xinWeChat", "微信"),
        ("com.tencent.qq", "QQ"),
    ];

    private readonly PluginHost _host;

    public NotificationsPanel(PluginHost host)
    {
        _host = host;
        Spacing = 14;
        Margin = new Thickness(0, 0, 0, 8);
        var prefs = host.NotificationPreferences;

        Children.Add(Text("插件只能看到你允许的部分：内容在交给插件之前就被裁掉，插件无法读到更多。",
            ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        Children.Add(Line("消息显示", Choice(Modes, prefs.Default.ToString(), v =>
        {
            if (Enum.TryParse<NotificationDisplay>(v, out var d))
            {
                prefs.Default = d;
            }
        })));
        foreach (var (appId, name) in Apps)
        {
            var options = new List<(string, string)> { (FollowDefault, "跟随默认") };
            options.AddRange(Modes);
            var current = prefs.HasOverride(appId) ? prefs.For(appId).ToString() : FollowDefault;
            Children.Add(Line(name, Choice([.. options], current, v =>
                prefs.Set(appId, Enum.TryParse<NotificationDisplay>(v, out var d) ? d : null))));
        }
        Children.Add(AccessRow(host.NotificationListener));
    }

    private static Control Line(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        var text = Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground);
        grid.Children.Add(text);
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    private static Control AccessRow(INotificationListener? listener)
    {
        var panel = new StackPanel { Spacing = 8 };
        var access = listener?.Access ?? NotificationAccess.Unsupported;
        var (state, tone) = access switch
        {
            NotificationAccess.Granted => ("系统已允许 O_O 读取通知。", ArcDesignTokenKind.TextSecondary),
            NotificationAccess.NeedsPermission => ("需要在系统设置里允许 O_O 读取通知。", ArcDesignTokenKind.Foreground),
            _ => ("这个系统暂不支持读取其他应用的通知。", ArcDesignTokenKind.TextSecondary),
        };
        panel.Children.Add(Text(state, ArcDesignTokenKind.TypeSM, tone, wrap: true));
        if (access == NotificationAccess.NeedsPermission && listener is not null)
        {
            if (listener.AccessHint is { Length: > 0 } hint)
            {
                panel.Children.Add(Text(hint, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
            }
            var open = new ArcButton { Content = "打开系统设置", Variant = ButtonVariant.Primary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            open.Click += (_, _) =>
            {
                try
                {
                    listener.OpenAccessSettings();
                }
                catch (Exception e)
                {
                    System.Diagnostics.Trace.TraceError($"O_O: cannot open notification settings: {e.Message}");
                }
            };
            panel.Children.Add(open);
        }
        return panel;
    }

    private static SegmentedControl Choice((string Value, string Label)[] options, string selected, Action<string> picked)
    {
        var control = new SegmentedControl();
        foreach (var (value, label) in options)
        {
            control.Items.Add(new SegmentedOption { Value = value, Label = label });
        }
        control.SelectedValue = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is string v)
            {
                picked(v);
            }
        };
        return control;
    }
}
