using Avalonia.Controls;
using Island.Platform;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Console;

/// <summary>系统: how O_O fits into the operating system. For now, whether it starts when the user logs in.</summary>
public sealed class SystemPage : StackPanel
{
    public SystemPage()
    {
        Children.Add(PageTitle("系统", "O_O 与操作系统的配合。"));

        var toggle = new ArcSwitch { Content = "开机时自动启动 O_O", IsChecked = LaunchAtLogin.IsEnabled };
        var note = Text("", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
        var available = LaunchAtLogin.IsAvailable;
        toggle.IsEnabled = available;
        note.Text = available
            ? "登录系统后自动在后台启动，不需要管理员权限。"
            : "当前是开发模式运行（dotnet run），请从发布的 O_O 启动后再设置。";

        var applying = false;
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (applying)
            {
                return;
            }
            var want = toggle.IsChecked == true;
            if (!LaunchAtLogin.TrySet(want))
            {
                applying = true;
                toggle.IsChecked = !want;
                applying = false;
                note.Text = "设置失败：无法写入系统的启动项。";
            }
        };
        Children.Add(Group(new StackPanel { Spacing = 8, Children = { toggle, note } }));
    }
}
