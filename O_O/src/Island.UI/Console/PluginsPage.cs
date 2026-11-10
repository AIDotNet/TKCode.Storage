using System.Globalization;
using System.Text;
using System.Text.Json;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Island.Plugins;
using Island.Plugins.Host;
using Island.Plugins.Host.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;
using ArcSwitch = TKCode.Arc.Switch;

namespace Island.UI.Console;

/// <summary>
/// docs/design/04-console.md §3.5 and §4: the plugins that are on, as a grid of cards (Arc Card); a card opens a
/// drawer with everything about that plugin — permissions (consent for user plugins, switches to revoke), its
/// connection, a settings form generated from its schema, turning it off or removing it. "添加插件" lists the bundled
/// plugins that are off and installs plugin packages (.zip). Changes apply immediately.
/// </summary>
public sealed class PluginsPage : Panel
{
    private const double CardWidth = 212;
    private const double CardHeight = 196;

    private readonly PluginHost _host;
    private readonly WrapPanel _grid = new() { Orientation = Orientation.Horizontal };
    private readonly Drawer _drawer = new() { Side = DrawerSide.Right };
    private readonly Dialog _addDialog = new() { Title = "添加插件", Size = ControlSize.Large };
    private string? _drawerPlugin;
    private Control? _addError;

    // One panel, reused when the drawer is rebuilt (state changes), so its discovery does not run again each time.
    private ClaudeConnectionPanel? _claudePanel;

    public PluginsPage(PluginHost host)
    {
        _host = host;
        var add = new ArcButton { Content = "添加插件", Variant = ButtonVariant.Primary, Size = ControlSize.Small, VerticalAlignment = VerticalAlignment.Top };
        add.Click += (_, _) => OpenAdd();
        var header = new DockPanel { LastChildFill = true };
        DockPanel.SetDock(add, Dock.Right);
        header.Children.Add(add);
        header.Children.Add(PageTitle("插件", "灵动岛上的内容都来自插件。点卡片查看权限和设置；没用到的内置插件和你自己的插件在\"添加插件\"里。"));
        _grid.ItemSpacing = 12;
        _grid.LineSpacing = 12;
        Children.Add(new StackPanel { Children = { header, _grid } });
        Children.Add(_drawer);
        Children.Add(_addDialog);
        _drawer.Closed += (_, _) => _drawerPlugin = null;
        Rebuild();
        host.PluginStateChanged += (id, _, _) => Dispatcher.UIThread.Post(() =>
        {
            Rebuild();
            if (_drawerPlugin == id)
            {
                FillDrawer(id);
            }
        });
    }

    /// <summary>Opens the "添加插件" dialog.</summary>
    public void ShowAdd() => OpenAdd();

    /// <summary>Opens the drawer of a plugin, e.g. from elsewhere in the console.</summary>
    public void ShowPlugin(string pluginId)
    {
        if (_host.Packages.Any(p => p.Id == pluginId))
        {
            FillDrawer(pluginId);
            _drawer.IsOpen = true;
        }
    }

    private void Rebuild()
    {
        _grid.Children.Clear();
        var shown = _host.Packages
            .Where(p => _host.IsEnabled(p.Id))
            .OrderBy(p => p.Bundled ? 0 : 1)
            .ThenBy(p => p.Manifest.Name, StringComparer.CurrentCulture)
            .ToList();
        foreach (var package in shown)
        {
            _grid.Children.Add(PluginCard(package));
        }
        if (shown.Count == 0)
        {
            _grid.Children.Add(new EmptyState
            {
                Title = "还没有启用插件",
                Description = "点\"添加插件\"启用内置插件，或安装你自己的插件包。",
                Width = 480,
            });
        }
        if (_addDialog.IsOpen)
        {
            FillAdd();
        }
    }

    private Control PluginCard(PluginPackage package)
    {
        var state = _host.GetState(package.Id);
        var (label, _) = StateBadge(state);
        var toggle = new ArcSwitch { IsChecked = true, VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetName(toggle, $"启用{package.Manifest.Name}");
        toggle.IsCheckedChanged += (_, _) =>
        {
            if (toggle.IsChecked == false)
            {
                _host.SetEnabled(package.Id, false);
            }
        };
        var card = new Card
        {
            Width = CardWidth,
            Height = CardHeight,
            Title = package.Manifest.Name,
            Description = package.Manifest.Description ?? package.Id,
            Avatar = Icon(package, 18),
            Meta = package.Bundled ? $"内置 · {package.Manifest.Version}" : $"我的插件 · {package.Manifest.Version}",
            Status = label,
            Action = toggle,
            Cursor = new Cursor(StandardCursorType.Hand),
        };
        card.PointerReleased += (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Left && e.Source is not ArcSwitch)
            {
                ShowPlugin(package.Id);
            }
        };
        return card;
    }

    /// <summary>
    /// The plugin's icon: <c>logo:&lt;name&gt;</c> draws a vendor's mark the host knows (Claude, Codex), anything else is a
    /// Lucide icon name.
    /// </summary>
    private static Control Icon(PluginPackage package, double size)
    {
        if (package.Manifest.Icon is { } icon && icon.StartsWith("logo:", StringComparison.Ordinal) &&
            Island.UI.Plugins.BrandLogos.Get(icon["logo:".Length..]) is { } logo)
        {
            var path = new Avalonia.Controls.Shapes.Path { Data = logo, Stretch = Stretch.Uniform, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
            ArcTokens.Bind(path, Avalonia.Controls.Shapes.Shape.FillProperty, ArcDesignTokenKind.Foreground);
            return path;
        }
        return LucideIcon(package, size);
    }

    private static Control LucideIcon(PluginPackage package, double size) => new TKCode.Arc.Icons.Lucide.LucideIcon
    {
        Kind = Enum.TryParse<TKCode.Arc.Icons.Lucide.LucideIconKind>(package.Manifest.Icon, out var kind) ? kind : TKCode.Arc.Icons.Lucide.LucideIconKind.Puzzle,
        Size = size,
        VerticalAlignment = VerticalAlignment.Center,
    };

    // ---- the drawer: everything about one plugin ----------------------------------------------------------------

    private void FillDrawer(string pluginId)
    {
        if (_host.Packages.FirstOrDefault(p => p.Id == pluginId) is not { } package)
        {
            _drawer.IsOpen = false;
            return;
        }
        _drawerPlugin = pluginId;
        var state = _host.GetState(package.Id);
        var permissions = _host.GetPermissions(package.Id);
        _drawer.Title = package.Manifest.Name;
        _drawer.Description = $"{package.Id} · {package.Manifest.Version}";

        var body = new StackPanel { Spacing = 4, Width = 400 };
        var (label, tone) = StateBadge(state);
        var badges = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        badges.Children.Add(new Badge { Content = package.Bundled ? "内置" : "我的插件", Size = BadgeSize.Small });
        badges.Children.Add(new Badge { Content = label, Tone = tone, Size = BadgeSize.Small });
        body.Children.Add(badges);
        if (package.Manifest.Description is { } description)
        {
            body.Children.Add(Text(description, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary, wrap: true));
        }

        if (permissions.Declared.Count > 0)
        {
            body.Children.Add(SectionLabel("权限"));
            body.Children.Add(state == PluginState.AwaitingConsent ? ConsentRequest(package, permissions) : PermissionSwitches(package, permissions));
        }

        // Setup that a permission works with sits next to it.
        if (permissions.Declared.Any(p => p.StartsWith("oauth:", StringComparison.Ordinal)))
        {
            body.Children.Add(SectionLabel("已登录的账户"));
            body.Children.Add(new OAuthAccountsPanel(_host, package.Id));
        }
        if (permissions.Declared.Contains("localAuth:claude-code"))
        {
            body.Children.Add(SectionLabel("Claude Code"));
            if (_claudePanel?.Parent is Panel old)
            {
                old.Children.Remove(_claudePanel);
            }
            body.Children.Add(_claudePanel ??= new ClaudeConnectionPanel(_host));
        }

        var settings = _host.GetSettings(package.Id);
        if (settings.Definitions.Count > 0)
        {
            body.Children.Add(SectionLabel("设置"));
            var form = new StackPanel { Spacing = 14 };
            foreach (var definition in settings.Definitions)
            {
                form.Children.Add(SettingField(settings, definition));
            }
            body.Children.Add(form);
        }

        body.Children.Add(SectionLabel("管理"));
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var off = new ArcButton { Content = "停用", Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        off.Click += (_, _) =>
        {
            _drawer.IsOpen = false;
            _host.SetEnabled(package.Id, false);
        };
        actions.Children.Add(off);
        if (!package.Bundled)
        {
            var remove = new ArcButton { Content = "移除插件", Variant = ButtonVariant.Danger, Size = ControlSize.Small };
            remove.Click += (_, _) =>
            {
                _drawer.IsOpen = false;
                _host.Uninstall(package.Id);
            };
            actions.Children.Add(remove);
        }
        body.Children.Add(actions);
        body.Children.Add(Text(package.Bundled
                ? "停用后它会回到\"添加插件\"里，设置和数据都会保留。"
                : "停用会保留它的设置和数据；移除会删除插件本身（数据保留，重新安装即可恢复）。",
            ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));

        _drawer.Content = new ScrollViewer { Content = body, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }

    // ---- 添加插件 -------------------------------------------------------------------------------------------

    private void OpenAdd()
    {
        _addError = null;
        FillAdd();
        _addDialog.IsOpen = true;
    }

    private void FillAdd()
    {
        var body = new StackPanel { Spacing = 8 };
        var available = _host.Packages.Where(p => !_host.IsEnabled(p.Id))
            .OrderBy(p => p.Bundled ? 0 : 1).ThenBy(p => p.Manifest.Name, StringComparer.CurrentCulture).ToList();
        body.Children.Add(SectionLabel("可添加的插件"));
        if (available.Count == 0)
        {
            body.Children.Add(Text("所有插件都已启用。", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary));
        }
        foreach (var package in available)
        {
            var add = new ArcButton { Content = "添加", Variant = ButtonVariant.Secondary, Size = ControlSize.Small, VerticalAlignment = VerticalAlignment.Center };
            add.Click += (_, _) => _host.SetEnabled(package.Id, true);
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), ColumnSpacing = 12, Margin = new Thickness(0, 4) };
            var icon = Icon(package, 20);
            var words = new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Text(package.Bundled ? package.Manifest.Name : $"{package.Manifest.Name}（我的插件）", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
                    Text(package.Manifest.Description ?? package.Id, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true),
                },
            };
            Grid.SetColumn(words, 1);
            Grid.SetColumn(add, 2);
            row.Children.Add(icon);
            row.Children.Add(words);
            row.Children.Add(add);
            body.Children.Add(row);
        }

        body.Children.Add(SectionLabel("安装插件包"));
        body.Children.Add(Text("选择插件作者提供的 .zip 插件包。安装后它会请求所需的权限，你同意之前它不会运行。",
            ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        var pick = new ArcButton { Content = "选择 .zip 插件包…", Variant = ButtonVariant.Secondary, Size = ControlSize.Small, HorizontalAlignment = HorizontalAlignment.Left };
        pick.Click += async (_, _) => await PickAndInstall();
        body.Children.Add(pick);
        if (_addError is not null)
        {
            body.Children.Add(_addError);
        }
        _addDialog.Content = new ScrollViewer { Content = body, MaxHeight = 480 };
    }

    private async Task PickAndInstall()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var files = await storage.OpenFilePickerAsync(new Avalonia.Platform.Storage.FilePickerOpenOptions
        {
            Title = "选择插件包",
            AllowMultiple = false,
            FileTypeFilter = [new Avalonia.Platform.Storage.FilePickerFileType("O_O 插件包") { Patterns = ["*.zip"] }],
        });
        if (files is not [var file] || file.TryGetLocalPath() is not { } path)
        {
            return;
        }
        if (_host.Services.UserPluginDirectory is not { } userDirectory)
        {
            ShowAddError("这里不能安装插件。");
            return;
        }
        try
        {
            var package = await Task.Run(() => _host.Install(path, userDirectory));
            _addError = null;
            _addDialog.IsOpen = false;
            Rebuild();
            ShowPlugin(package.Id); // its consent request is the next step
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            ShowAddError($"安装失败：{e.Message}");
        }
    }

    private void ShowAddError(string message)
    {
        _addError = new Alert { Tone = AlertTone.Danger, Title = message };
        FillAdd();
    }

    private static (string, BadgeTone) StateBadge(PluginState? state) => state switch
    {
        PluginState.Running => ("运行中", BadgeTone.Success),
        PluginState.AwaitingConsent => ("等待授权", BadgeTone.Warning),
        PluginState.Restarting => ("出错，正在重启", BadgeTone.Warning),
        PluginState.Disabled => ("已停用", BadgeTone.Danger),
        PluginState.Stopped => ("已停止", BadgeTone.Neutral),
        _ => ("未启动", BadgeTone.Neutral),
    };

    // ---- permissions -------------------------------------------------------------------------------------------

    private Control ConsentRequest(PluginPackage package, PermissionStatus status)
    {
        var list = new StringBuilder();
        foreach (var p in status.Pending)
        {
            list.Append("• ").Append(Describe(p)).Append('\n');
        }
        var allow = new ArcButton { Content = "全部允许", Variant = ButtonVariant.Primary, Size = ControlSize.Small };
        allow.Click += (_, _) => _host.SetGranted(package.Id, status.Declared);
        var deny = new ArcButton { Content = "不授予，直接运行", Variant = ButtonVariant.Secondary, Size = ControlSize.Small };
        deny.Click += (_, _) => _host.SetGranted(package.Id, []);
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new Alert
                {
                    Tone = AlertTone.Warning,
                    Title = "这个插件请求以下权限，决定之前它不会运行",
                    Content = list.ToString().TrimEnd(),
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { allow, deny } },
            },
        };
    }

    private Control PermissionSwitches(PluginPackage package, PermissionStatus status)
    {
        var list = new StackPanel { Spacing = 8 };
        foreach (var permission in status.Declared.Order(StringComparer.Ordinal))
        {
            var toggle = new ArcSwitch
            {
                Content = Describe(permission),
                IsChecked = status.Granted.Contains(permission),
                IsEnabled = _host.IsChangeable(package.Id, permission),
            };
            ToolTip.SetTip(toggle, permission);
            toggle.IsCheckedChanged += (_, _) =>
            {
                var granted = new HashSet<string>(_host.GetPermissions(package.Id).Granted, StringComparer.Ordinal);
                if (toggle.IsChecked == true)
                {
                    granted.Add(permission);
                }
                else
                {
                    granted.Remove(permission);
                }
                _host.SetGranted(package.Id, granted); // restarts the plugin with exactly these
            };
            list.Children.Add(toggle);
        }
        if (package.Bundled)
        {
            list.Children.Add(Text("内置插件按清单授予权限；读取本机其他应用的登录始终需要你确认。", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary));
        }
        return list;
    }

    /// <summary>What a permission lets the plugin do, in words (docs/design/03-plugin-system.md §7).</summary>
    public static string Describe(string permission)
    {
        var colon = permission.IndexOf(':');
        var (kind, arg) = colon < 0 ? (permission, "") : (permission[..colon], permission[(colon + 1)..]);
        return kind switch
        {
            "network" when arg == PluginPermissions.Localhost => "访问本机上的服务",
            "network" when arg.StartsWith(PluginPermissions.SettingHostPrefix, StringComparison.Ordinal) => "访问你在设置中填写的站点（只限那一个地址）",
            "network" when arg.StartsWith("*.", StringComparison.Ordinal) => $"访问 {arg[2..]} 的所有子域名",
            "network" => $"访问 {arg}",
            "oauth" => $"通过 {arg} 登录你的账户",
            "localAuth" when arg == "claude-code" => "读取本机 Claude Code 的账户与用量信息（只读，不读取它的登录凭据）",
            "localAuth" => $"读取本机已登录的 {arg switch { "codex-cli" => "Codex", _ => arg }} 账户（只读，不会刷新或修改它的登录）",
            "secrets" => "在系统钥匙串中保存密钥",
            "notifications" => "发送系统通知",
            "media" => "读取和控制正在播放的媒体",
            "clipboard.read" => "读取剪贴板",
            "clipboard.write" => "写入剪贴板",
            "clipboard.watch" => "持续记录剪贴板变化",
            "clipboard.paste" => "粘贴到当前应用",
            "files.shelf" => "使用文件暂存区",
            "system.info" => "读取电池、勿扰等系统状态",
            "mail" => "访问你连接的邮箱",
            "shell.openUrl" => "在浏览器中打开链接",
            "sidecar" => "运行附带的本机程序",
            "storage.sql" => "使用数据库存储",
            "location.coarse" => "获取大致位置",
            _ => permission,
        };
    }

    // ---- settings ------------------------------------------------------------------------------------------------

    private static Control SettingField(PluginSettingsStore store, SettingDefinition d)
    {
        var current = store.GetJson(d.Key) is { } json ? JsonDocument.Parse(json).RootElement.Clone() : (JsonElement?)null;

        switch (d.Type)
        {
            case SettingType.Boolean:
            {
                var toggle = new ArcSwitch { Content = d.Title, IsChecked = current?.ValueKind == JsonValueKind.True };
                var error = Hint(d.Description);
                toggle.IsCheckedChanged += (_, _) => Commit(store, d, toggle.IsChecked == true ? "true" : "false", m => error.Text = m ?? d.Description);
                return new StackPanel { Spacing = 4, Children = { toggle, error } };
            }
            case SettingType.String when d.Enum is { Count: > 0 } options:
            {
                var select = new Select { Label = d.Title, Description = d.Description };
                foreach (var option in options)
                {
                    select.Items.Add(new SelectItem { Content = option, Value = option });
                }
                select.SelectedValue = current?.GetString();
                select.SelectionChanged += (_, _) =>
                {
                    if (select.SelectedValue is string value)
                    {
                        Commit(store, d, Json(value), m => select.Description = m ?? d.Description);
                    }
                };
                return select;
            }
            case SettingType.String when d.IsSecret:
            {
                var field = new PasswordField
                {
                    Label = d.Title,
                    Placeholder = store.HasSecret(d.Key) ? "已保存（输入新值以替换）" : "未设置",
                    Description = d.Description ?? "保存在系统钥匙串中，不会写入配置文件。",
                };
                void Save()
                {
                    if (!string.IsNullOrEmpty(field.Text))
                    {
                        Commit(store, d, Json(field.Text), m => field.ErrorMessage = m);
                        field.Text = "";
                        field.Placeholder = "已保存（输入新值以替换）";
                    }
                }
                field.LostFocus += (_, _) => Save();
                field.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter)
                    {
                        Save();
                    }
                };
                return field;
            }
            case SettingType.String:
            {
                var input = new Input
                {
                    Label = d.Title,
                    Description = d.Description,
                    Text = current?.GetString() ?? "",
                    AcceptsReturn = d.Multiline,
                };
                void Save() => Commit(store, d, Json(input.Text ?? ""), m => input.ErrorMessage = m);
                input.LostFocus += (_, _) => Save();
                input.KeyDown += (_, e) =>
                {
                    if (e.Key == Key.Enter && !d.Multiline)
                    {
                        Save();
                    }
                };
                return input;
            }
            default:
            {
                var integer = d.Type == SettingType.Integer;
                var field = new NumberField
                {
                    Label = d.Title,
                    Description = d.Description,
                    Minimum = (decimal)(d.Minimum ?? -1_000_000_000),
                    Maximum = (decimal)(d.Maximum ?? 1_000_000_000),
                    Step = integer ? 1 : 0.01m,
                    Culture = CultureInfo.InvariantCulture,
                    Value = (decimal)(current?.GetDouble() ?? d.Minimum ?? 0),
                };
                field.PropertyChanged += (_, e) =>
                {
                    if (e.Property == NumberField.ValueProperty)
                    {
                        Commit(store, d, field.Value.ToString(CultureInfo.InvariantCulture), m => field.ErrorMessage = m);
                    }
                };
                return field;
            }
        }
    }

    private static TextBlock Hint(string? text) =>
        Text(text ?? "", ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);

    /// <summary>Validates and saves through the host; <paramref name="report"/> gets the error, or null on success.</summary>
    private static void Commit(PluginSettingsStore store, SettingDefinition d, string json, Action<string?> report)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            store.Set(d.Key, doc.RootElement);
            report(null);
        }
        catch (ArgumentException e)
        {
            report(e.Message);
        }
        catch (Island.Platform.SecretStoreException e)
        {
            report(e.Message);
        }
    }

    private static string Json(string s)
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStringValue(s);
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
