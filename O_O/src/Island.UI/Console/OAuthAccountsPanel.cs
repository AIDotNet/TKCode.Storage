using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Island.Plugins.Host;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// The accounts a plugin signed in with (Google, Microsoft, Feishu, DingTalk), in its drawer on the 插件 page:
/// who they are and a sign-out button. Signing out deletes the account's tokens from the keychain.
/// </summary>
public sealed class OAuthAccountsPanel : StackPanel
{
    private readonly PluginHost _host;
    private readonly string _pluginId;

    public OAuthAccountsPanel(PluginHost host, string pluginId)
    {
        _host = host;
        _pluginId = pluginId;
        Spacing = 8;
        Fill();
    }

    private void Fill()
    {
        Children.Clear();
        var accounts = _host.GetOAuthAccounts(_pluginId);
        if (accounts.Count == 0)
        {
            Children.Add(Text("还没有登录的账户。登录在灵动岛上对应页面里完成（会打开浏览器）。",
                ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
            return;
        }
        foreach (var account in accounts)
        {
            var signOut = new ArcButton { Content = "退出登录", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, VerticalAlignment = VerticalAlignment.Center };
            signOut.Click += (_, _) =>
            {
                _host.SignOut(_pluginId, account.Handle);
                Fill();
            };
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
            row.Children.Add(new StackPanel
            {
                Spacing = 2,
                VerticalAlignment = VerticalAlignment.Center,
                Children =
                {
                    Text(account.Label ?? "（未命名账户）", ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground, FontWeight.Medium),
                    Text(account.ProviderName, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary),
                },
            });
            Grid.SetColumn(signOut, 1);
            row.Children.Add(signOut);
            Children.Add(row);
        }
    }
}
