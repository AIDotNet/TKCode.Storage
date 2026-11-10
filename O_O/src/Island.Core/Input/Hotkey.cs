using System.Text;
using System.Text.Json;

namespace Island.Core.Input;

/// <summary>Modifier keys of a global hotkey. <see cref="Meta"/> is ⌘ on macOS and the Windows / Super key elsewhere.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Control = 1,
    Alt = 2,
    Shift = 4,
    Meta = 8,
}

/// <summary>
/// A global keyboard shortcut: modifiers plus one key, by name (<see cref="Keys"/>). Written as text such as
/// <c>Ctrl+Shift+V</c> (the stored form) and shown as <c>⌃⇧V</c> on macOS or <c>Ctrl+Shift+V</c> elsewhere.
/// </summary>
public sealed record Hotkey(HotkeyModifiers Modifiers, string Key)
{
    /// <summary>The keys a hotkey may use; each platform maps them to its own key codes.</summary>
    public static readonly IReadOnlyList<string> Keys =
    [
        .. Enumerable.Range('A', 26).Select(c => ((char)c).ToString()),
        .. Enumerable.Range('0', 10).Select(c => ((char)c).ToString()),
        .. Enumerable.Range(1, 12).Select(n => $"F{n}"),
        "Space", "Enter", "Tab", "Comma", "Period", "Slash", "Semicolon",
    ];

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        ["Space"] = "Space", ["Enter"] = "↩", ["Tab"] = "⇥", ["Comma"] = ",", ["Period"] = ".", ["Slash"] = "/", ["Semicolon"] = ";",
    };

    /// <summary>
    /// Whether it can be a global hotkey: a known key with at least one modifier other than Shift (a lone letter or
    /// Shift+letter would swallow typing), except function keys, which may stand alone.
    /// </summary>
    public bool IsValid =>
        Keys.Contains(Key) &&
        ((Modifiers & ~HotkeyModifiers.Shift) != HotkeyModifiers.None || (Key.StartsWith('F') && Key.Length > 1));

    /// <summary>The stored form, modifiers in a fixed order: <c>Ctrl+Alt+Shift+Meta+Key</c>.</summary>
    public override string ToString()
    {
        var b = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) b.Append("Ctrl+");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) b.Append("Alt+");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) b.Append("Shift+");
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) b.Append("Meta+");
        return b.Append(Key).ToString();
    }

    /// <summary>
    /// For people: macOS symbols in Apple's order (⌃⌥⇧⌘, then the key), or <c>Ctrl+Alt+Shift+Win+Key</c> elsewhere.
    /// </summary>
    public string Display(bool mac)
    {
        if (!mac)
        {
            return ToString().Replace("Meta+", OperatingSystem.IsWindows() ? "Win+" : "Super+", StringComparison.Ordinal);
        }
        var b = new StringBuilder();
        if (Modifiers.HasFlag(HotkeyModifiers.Control)) b.Append('⌃');
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) b.Append('⌥');
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) b.Append('⇧');
        if (Modifiers.HasFlag(HotkeyModifiers.Meta)) b.Append('⌘');
        return b.Append(Symbols.GetValueOrDefault(Key, Key)).ToString();
    }

    /// <summary>Reads the stored form (case-insensitive; Cmd / Command / Win / Super mean Meta, Option means Alt).</summary>
    public static Hotkey? Parse(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0)
        {
            return null;
        }
        var modifiers = HotkeyModifiers.None;
        foreach (var part in parts[..^1])
        {
            modifiers |= part.ToLowerInvariant() switch
            {
                "ctrl" or "control" => HotkeyModifiers.Control,
                "alt" or "option" or "opt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                "meta" or "cmd" or "command" or "win" or "super" => HotkeyModifiers.Meta,
                _ => (HotkeyModifiers)(-1),
            };
            if ((int)modifiers < 0)
            {
                return null;
            }
        }
        var key = Keys.FirstOrDefault(k => k.Equals(parts[^1], StringComparison.OrdinalIgnoreCase));
        return key is null ? null : new Hotkey(modifiers, key);
    }
}

/// <summary>What a global hotkey can do (docs/design/04-console.md, 快捷键).</summary>
public static class HotkeyActions
{
    public const string ToggleIsland = "toggle-island";
    public const string OpenClipboard = "open-clipboard";
    public const string OpenConsole = "open-console";

    /// <summary>The actions in the order the console lists them, with their names.</summary>
    public static readonly IReadOnlyList<(string Id, string Title, string Description)> All =
    [
        (ToggleIsland, "展开 / 收起灵动岛", "展开后保持打开，直到指针移过岛再离开，或再按一次。"),
        (OpenClipboard, "打开剪贴板", "展开灵动岛并显示剪贴板历史。"),
        (OpenConsole, "打开控制台", "打开这个窗口。"),
    ];

    /// <summary>
    /// The defaults. Chosen not to take shortcuts apps rely on: ⌘⇧V / Ctrl+Shift+V is "paste without formatting" in
    /// many apps, and Win+V is Windows' own clipboard history, so the clipboard gets ⌥⌘V / Ctrl+Alt+V.
    /// </summary>
    public static IReadOnlyDictionary<string, Hotkey?> Defaults(bool mac) => new Dictionary<string, Hotkey?>
    {
        [ToggleIsland] = mac ? new(HotkeyModifiers.Alt | HotkeyModifiers.Meta, "O") : new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "O"),
        [OpenClipboard] = mac ? new(HotkeyModifiers.Alt | HotkeyModifiers.Meta, "V") : new(HotkeyModifiers.Control | HotkeyModifiers.Alt, "V"),
        [OpenConsole] = null,
    };
}

/// <summary>
/// The user's hotkeys in <c>hotkeys.json</c> (<c>{ "toggle-island": "Alt+Meta+O", "open-console": null }</c>). An
/// action missing from the file has its default; <c>null</c> means the user turned it off.
/// </summary>
public sealed class HotkeyStore
{
    private readonly string _path;
    private readonly bool _mac;
    private readonly Dictionary<string, Hotkey?> _overrides;

    public HotkeyStore(string path, bool mac)
    {
        _path = path;
        _mac = mac;
        _overrides = Load(path);
    }

    /// <summary>Raised after a change, with the action that changed.</summary>
    public event Action<string>? Changed;

    /// <summary>The hotkey for <paramref name="action"/>: the user's, else the default; null when off.</summary>
    public Hotkey? Get(string action) =>
        _overrides.TryGetValue(action, out var h) ? h : HotkeyActions.Defaults(_mac).GetValueOrDefault(action);

    public bool IsDefault(string action) => !_overrides.ContainsKey(action);

    /// <summary>Sets (or with null turns off) an action's hotkey.</summary>
    /// <exception cref="ArgumentException">Not a usable hotkey.</exception>
    public void Set(string action, Hotkey? hotkey)
    {
        if (hotkey is { IsValid: false })
        {
            throw new ArgumentException($"'{hotkey}' cannot be a global hotkey.", nameof(hotkey));
        }
        _overrides[action] = hotkey;
        Save();
        Changed?.Invoke(action);
    }

    public void Reset(string action)
    {
        if (_overrides.Remove(action))
        {
            Save();
            Changed?.Invoke(action);
        }
    }

    public void ResetAll()
    {
        var changed = _overrides.Keys.ToList();
        _overrides.Clear();
        Save();
        foreach (var action in changed)
        {
            Changed?.Invoke(action);
        }
    }

    /// <summary>The action already using <paramref name="hotkey"/>, other than <paramref name="except"/>.</summary>
    public string? UsedBy(Hotkey hotkey, string except) =>
        HotkeyActions.All.Select(a => a.Id).FirstOrDefault(id => id != except && Get(id) == hotkey);

    private static Dictionary<string, Hotkey?> Load(string path)
    {
        var result = new Dictionary<string, Hotkey?>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.Null)
                {
                    result[p.Name] = null;
                }
                else if (p.Value.ValueKind == JsonValueKind.String && Hotkey.Parse(p.Value.GetString()) is { IsValid: true } h)
                {
                    result[p.Name] = h;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Missing or unreadable: the defaults.
        }
        return result;
    }

    private void Save()
    {
        try
        {
            using var buffer = new MemoryStream();
            using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
            {
                w.WriteStartObject();
                foreach (var (action, hotkey) in _overrides.OrderBy(o => o.Key, StringComparer.Ordinal))
                {
                    if (hotkey is null)
                    {
                        w.WriteNull(action);
                    }
                    else
                    {
                        w.WriteString(action, hotkey.ToString());
                    }
                }
                w.WriteEndObject();
            }
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            File.WriteAllBytes(_path + ".tmp", buffer.ToArray());
            File.Move(_path + ".tmp", _path, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A preference: failing to save it must not break the app.
        }
    }
}
