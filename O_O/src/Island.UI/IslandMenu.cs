using Avalonia.Controls;
using TKCode.Arc;
using TKCode.Arc.Icons.Lucide;

namespace Island.UI;

/// <summary>
/// The island's commands (docs/design/02-island-shell.md §2.2) as Arc menu rows, built fresh for each menu that shows
/// them: the "⋯" dropdown in the expanded header and the right-click menu on the island itself.
/// </summary>
internal sealed class IslandMenu(Action openConsole, Func<bool> isPinned, Action<bool> setPinned, Action quit)
{
    public IEnumerable<MenuEntry> Entries()
    {
        var console = new MenuEntry { Header = "打开控制台", Icon = Icon(LucideIconKind.Settings) };
        console.Click += (_, _) => openConsole();

        var pin = new MenuEntry
        {
            Header = "保持展开",
            ToggleType = MenuEntryToggleType.CheckBox,
            IsChecked = isPinned(),
        };
        pin.Click += (_, _) => setPinned(!isPinned());

        // Appearance and theme at hand (docs 05 §3): a submenu kept current by ThemeMenu.
        var looks = new MenuEntry { Header = "外观", Icon = Icon(LucideIconKind.Palette) };
        Island.UI.Themes.ThemeMenu.Attach(looks);

        var exit = new MenuEntry { Header = "退出 O_O", Icon = Icon(LucideIconKind.Power), HasSeparatorBefore = true };
        exit.Click += (_, _) => quit();

        return [console, pin, looks, exit];
    }

    private static LucideIcon Icon(LucideIconKind kind) => new() { Kind = kind, Size = 16 };
}
