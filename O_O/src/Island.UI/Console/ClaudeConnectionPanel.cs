using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.Plugins.Host;
using Island.Plugins.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// Claude Code's connection, shown inside the card of every plugin that declares <c>localAuth:claude-code</c> (it is
/// what that permission works with, so it lives next to it): the command that runs Claude Code (customisable, the
/// default always shown), and each signed-in configuration directory with its status line connection (the source of
/// plan usage), which can be connected or restored.
/// </summary>
public sealed class ClaudeConnectionPanel : StackPanel
{
    private readonly PluginHost _host;
    private readonly ClaudeCodeSource? _claude;
    private readonly ClaudeStatusLineBridge _bridge;
    private readonly StackPanel _accounts = new() { Spacing = 12 };

    public ClaudeConnectionPanel(PluginHost host)
    {
        _host = host;
        _claude = host.Services.LocalAuthSources.OfType<ClaudeCodeSource>().FirstOrDefault();
        _bridge = host.Services.ClaudeStatusLine;
        Spacing = 14;
        Margin = new Thickness(0, 0, 0, 8);
        Children.Add(ClaudeSection());
        Reload();
    }

    private Control ClaudeSection()
    {
        var command = new Input
        {
            Label = "Claude Code 命令",
            Text = _host.Services.Connections.ClaudeCommand ?? "",
            Placeholder = ClaudeCli.DefaultCommand,
        };
        void Describe()
        {
            var typed = string.IsNullOrWhiteSpace(command.Text) ? ClaudeCli.DefaultCommand : command.Text.Trim();
            var program = ClaudeCli.Split(typed).FirstOrDefault() ?? "";
            command.ErrorMessage = null;
            command.Description = $"填命令名或完整路径都可以，留空即用默认的 {ClaudeCli.DefaultCommand}。O_O 用它查询套餐（auth status --json）。";
            // Finding it may ask the login shell: off the UI thread, then say whether it was found.
            Task.Run(() => ClaudeCli.Resolve(program)).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                if (t.Result is null)
                {
                    command.ErrorMessage = $"找不到 {program}：请检查拼写，或填写完整路径。";
                }
            }));
        }
        Describe();
        void Save()
        {
            var value = string.IsNullOrWhiteSpace(command.Text) ? null : command.Text.Trim();
            if (value == _host.Services.Connections.ClaudeCommand)
            {
                return;
            }
            _host.Services.Connections.ClaudeCommand = value;
            if (_claude is not null)
            {
                _claude.CliCommand = value;
            }
            Describe();
            Reload();
        }
        command.LostFocus += (_, _) => Save();
        command.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter)
            {
                Save();
            }
        };

        return new StackPanel
        {
            Spacing = 14,
            Children =
            {
                Text("用量来自 Claude Code 官方的状态栏数据：接入后，Claude Code 每次刷新状态栏都会把 5 小时与每周用量交给 O_O。" +
                     "不读取登录凭据，不调用非公开接口；你原来的状态栏照常显示，随时可以还原。",
                    ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
                command,
                _accounts,
            },
        };
    }

    /// <summary>Finds the signed-in configurations off the UI thread (it scans the home folder) and lists them.</summary>
    private void Reload()
    {
        _accounts.Children.Clear();
        _accounts.Children.Add(Text("正在查找…", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary));
        if (_claude is null)
        {
            return;
        }
        Task.Run(() => _claude.Accounts().Select(a => (Account: a, Connected: _bridge.IsInstalled(a.Key))).ToList())
            .ContinueWith(t => Dispatcher.UIThread.Post(() => Show(t.IsCompletedSuccessfully ? t.Result : [])));
    }

    private void Show(List<(LocalAccount Account, bool Connected)> rows)
    {
        _accounts.Children.Clear();
        if (rows.Count == 0)
        {
            _accounts.Children.Add(Text("没有找到已登录的 Claude Code。", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary));
            return;
        }
        foreach (var (account, connected) in rows)
        {
            _accounts.Children.Add(Row(account, connected));
        }
        if (rows.Count(r => !r.Connected) > 1)
        {
            var all = new ArcButton { Content = "全部接入", Variant = ButtonVariant.Primary, Size = ControlSize.Small };
            all.Click += (_, _) => Run(() =>
            {
                foreach (var (account, isConnected) in rows.Where(r => !r.Connected))
                {
                    _bridge.Install(account.Key);
                }
            });
            _accounts.Children.Add(all);
        }
    }

    private Control Row(LocalAccount account, bool connected)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        title.Children.Add(Text(account.Label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium));
        if (account.Plan is { } plan)
        {
            title.Children.Add(new Badge { Content = plan, Size = BadgeSize.Small });
        }
        title.Children.Add(new Badge
        {
            Content = connected ? "已接入" : "未接入",
            Tone = connected ? BadgeTone.Success : BadgeTone.Neutral,
            Size = BadgeSize.Small,
        });

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var path = account.Key.StartsWith(home, StringComparison.Ordinal) ? "~" + account.Key[home.Length..] : account.Key;
        var info = new StackPanel
        {
            Spacing = 2,
            Children = { title, Text(path, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary) },
        };

        var action = new ArcButton
        {
            Content = connected ? "还原" : "接入",
            Variant = connected ? ButtonVariant.Ghost : ButtonVariant.Secondary,
            Size = ControlSize.Small,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ToolTip.SetTip(action, connected ? "恢复接入之前的状态栏设置" : $"在 {path}/settings.json 中接入状态栏");
        action.Click += (_, _) => Run(() =>
        {
            if (connected)
            {
                _bridge.Restore(account.Key);
            }
            else
            {
                _bridge.Install(account.Key);
            }
        });

        var row = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(action, Dock.Right);
        row.Children.Add(action);
        row.Children.Add(info);
        return row;
    }

    /// <summary>Changes Claude Code's settings, then lists again and lets the plugins look for new data at once.</summary>
    private void Run(Action change)
    {
        try
        {
            change();
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Children.Insert(0, new Alert { Tone = AlertTone.Danger, Title = "没有完成", Content = e.Message, IsDismissible = true });
        }
        _host.RefreshProviders();
        Reload();
    }
}
