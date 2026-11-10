using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Island.UI.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Console;

/// <summary>
/// 关于 → 更新 (docs/design/08-updates-and-catalog.md): the version this is, what the O_O service offers, a 检查更新
/// button, 下载 (opens the download page in the browser — O_O never downloads or installs itself), and the
/// 自动检查更新 switch.
/// </summary>
public sealed class UpdatePanel : StackPanel
{
    private readonly UpdateService _updates;
    private readonly StackPanel _status = new() { Spacing = 6 };

    public UpdatePanel(UpdateService updates)
    {
        _updates = updates;
        Spacing = 12;
        var check = new ArcButton { Content = "检查更新", Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        check.Click += async (_, _) => await _updates.CheckAsync();
        var auto = new ArcSwitch { Content = "自动检查更新", IsChecked = updates.Preferences.AutoCheck };
        auto.IsCheckedChanged += (_, _) => _updates.Preferences.SetAutoCheck(auto.IsChecked == true);
        Children.Add(new DockPanel
        {
            LastChildFill = false,
            Children = { Text("更新", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium), Right(check) },
        });
        Children.Add(_status);
        Children.Add(auto);
        if (UpdateInstaller.Supported)
        {
            var install = new ArcSwitch { Content = "自动下载并安装更新", IsChecked = updates.Preferences.AutoInstall };
            install.IsCheckedChanged += (_, _) =>
            {
                _updates.Preferences.SetAutoInstall(install.IsChecked == true);
                if (install.IsChecked == true && _updates.UpdateAvailable)
                {
                    _ = _updates.DownloadAsync();
                }
                Fill();
            };
            Children.Add(install);
            Children.Add(Note("打开后，新版本会在后台下载，退出 O_O 时自动安装；也可以随时「立即重启并更新」。"));
        }
        Fill();
        updates.Changed += OnChanged;
        DetachedFromVisualTree += (_, _) => updates.Changed -= OnChanged;
    }

    private static Control Right(Control c)
    {
        DockPanel.SetDock(c, Dock.Right);
        return c;
    }

    private void OnChanged() => Dispatcher.UIThread.Post(Fill);

    private void Fill()
    {
        _status.Children.Clear();
        var u = _updates;
        if (u.IsChecking)
        {
            _status.Children.Add(Note("正在检查…"));
            return;
        }
        if (u.Problem is { } problem)
        {
            _status.Children.Add(Note($"{problem}。当前版本 {u.Current}。"));
            return;
        }
        if (u.Latest is not { } latest)
        {
            _status.Children.Add(Note($"当前版本 {u.Current}。"));
            return;
        }
        if (!u.UpdateAvailable)
        {
            _status.Children.Add(Note($"已是最新版本 {u.Current}。"));
            return;
        }
        _status.Children.Add(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children =
            {
                new Badge { Content = "有新版本", Tone = BadgeTone.Info, Size = BadgeSize.Small },
                Text($"{latest.Version}（当前 {u.Current}）", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
            },
        });
        if (!string.IsNullOrWhiteSpace(latest.Notes))
        {
            _status.Children.Add(Note(latest.Notes!));
        }
        if (u.Ready)
        {
            var restart = new ArcButton { Content = "立即重启并更新", Variant = ButtonVariant.Primary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            restart.Click += (_, _) => u.RestartToUpdate();
            _status.Children.Add(restart);
            _status.Children.Add(Note(u.Preferences.AutoInstall ? "已下载，退出 O_O 时会自动安装。" : "已下载。"));
            if (u.Installer.Problem is { } installProblem)
            {
                _status.Children.Add(Note(installProblem));
            }
            return;
        }
        if (u.IsDownloading)
        {
            _status.Children.Add(Note("正在下载新版本…"));
            return;
        }
        if (u.Installer.Problem is { } downloadProblem)
        {
            _status.Children.Add(Note($"{downloadProblem}。也可以在浏览器里下载安装。"));
        }
        else if (UpdateInstaller.Supported && !u.Preferences.AutoInstall)
        {
            var get = new ArcButton { Content = "下载并安装", Variant = ButtonVariant.Primary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
            get.Click += async (_, _) => await u.DownloadAsync();
            _status.Children.Add(get);
        }
        if (latest.Download is { Length: > 0 } link && Uri.TryCreate(link, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps)
        {
            var download = new ArcButton
            {
                Content = "在浏览器中下载",
                Variant = UpdateInstaller.Supported ? ButtonVariant.Secondary : ButtonVariant.Primary,
                Size = ControlSize.Small,
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            download.Click += (_, _) => OpenInBrowser(url);
            _status.Children.Add(download);
            _status.Children.Add(Note("在浏览器中下载安装包，退出 O_O 后安装即可。"));
        }
        else
        {
            _status.Children.Add(Note("这个平台暂时没有可下载的安装包。"));
        }
    }

    /// <summary>Opens an https link with the system's browser.</summary>
    public static void OpenInBrowser(Uri url)
    {
        var info = OperatingSystem.IsWindows()
            ? new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true }
            : new System.Diagnostics.ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open") { ArgumentList = { url.AbsoluteUri } };
        try
        {
            System.Diagnostics.Process.Start(info)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // No browser to hand the link to: nothing else to do.
        }
    }

    private static TextBlock Note(string text) => Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);
}
