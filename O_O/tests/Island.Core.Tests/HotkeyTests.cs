using Island.Core.Input;

namespace Island.Core.Tests;

public class HotkeyTests
{
    [Theory]
    [InlineData("Ctrl+Shift+V", "Ctrl+Shift+V")]
    [InlineData("cmd+option+o", "Alt+Meta+O")]
    [InlineData("Shift + Meta + space", "Shift+Meta+Space")]
    [InlineData("F5", "F5")]
    [InlineData("Win+Comma", "Meta+Comma")]
    public void Parses_aliases_into_the_canonical_form(string text, string canonical) =>
        Assert.Equal(canonical, Hotkey.Parse(text)!.ToString());

    [Theory]
    [InlineData("")]
    [InlineData("Ctrl+")]
    [InlineData("Hyper+V")]
    [InlineData("Ctrl+Backspace")]
    public void Rejects_what_it_does_not_know(string text) => Assert.Null(Hotkey.Parse(text));

    [Fact]
    public void A_global_hotkey_needs_a_real_modifier_unless_it_is_a_function_key()
    {
        Assert.False(Hotkey.Parse("V")!.IsValid);
        Assert.False(Hotkey.Parse("Shift+V")!.IsValid);
        Assert.True(Hotkey.Parse("Alt+V")!.IsValid);
        Assert.True(Hotkey.Parse("F9")!.IsValid);
        Assert.True(Hotkey.Parse("Shift+F9")!.IsValid);
    }

    [Fact]
    public void Shows_mac_symbols_in_apples_order()
    {
        Assert.Equal("⌃⌥⇧⌘V", Hotkey.Parse("Meta+Shift+Alt+Ctrl+V")!.Display(mac: true));
        Assert.Equal("⌥⌘Space", Hotkey.Parse("Alt+Meta+Space")!.Display(mac: true));
        Assert.StartsWith("Ctrl+Alt+", Hotkey.Parse("Ctrl+Alt+V")!.Display(mac: false));
    }

    [Fact]
    public void The_store_keeps_overrides_turned_off_actions_and_defaults()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-hotkeys-{Guid.NewGuid():N}.json");
        try
        {
            var store = new HotkeyStore(path, mac: true);
            var changed = new List<string>();
            store.Changed += changed.Add;
            Assert.Equal("Alt+Meta+V", store.Get(HotkeyActions.OpenClipboard)!.ToString());
            Assert.Null(store.Get(HotkeyActions.OpenConsole));

            store.Set(HotkeyActions.OpenConsole, Hotkey.Parse("Ctrl+Alt+Comma"));
            store.Set(HotkeyActions.OpenClipboard, null);
            Assert.Throws<ArgumentException>(() => store.Set(HotkeyActions.ToggleIsland, Hotkey.Parse("Shift+V")));
            Assert.Equal([HotkeyActions.OpenConsole, HotkeyActions.OpenClipboard], changed);
            Assert.Equal(HotkeyActions.OpenConsole, store.UsedBy(Hotkey.Parse("Ctrl+Alt+Comma")!, HotkeyActions.ToggleIsland));

            var again = new HotkeyStore(path, mac: true);
            Assert.Equal("Ctrl+Alt+Comma", again.Get(HotkeyActions.OpenConsole)!.ToString());
            Assert.Null(again.Get(HotkeyActions.OpenClipboard));                 // turned off, not back to default
            Assert.True(again.IsDefault(HotkeyActions.ToggleIsland));

            again.ResetAll();
            Assert.Equal("Alt+Meta+V", new HotkeyStore(path, mac: true).Get(HotkeyActions.OpenClipboard)!.ToString());
        }
        finally
        {
            File.Delete(path);
        }
    }
}
