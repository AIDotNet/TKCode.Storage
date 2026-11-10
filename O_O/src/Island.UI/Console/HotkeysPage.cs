using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Island.Core.Input;
using Island.UI.Services;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Console;

/// <summary>
/// docs/design/04-console.md, 快捷键: the global hotkeys, each recorded with Arc's shortcut recorder. Changes apply at
/// once; while one is being recorded every global hotkey is released, so pressing an existing one is recorded rather
/// than run.
/// </summary>
public sealed class HotkeysPage : StackPanel
{
    private readonly HotkeyService? _service;
    private readonly Dictionary<string, (ShortcutRecorder Recorder, TextBlock Problem)> _rows = new(StringComparer.Ordinal);
    private bool _applying;

    public HotkeysPage(HotkeyService? service)
    {
        _service = service;
        Children.Add(PageTitle("快捷键", "在任何应用中都能使用的全局快捷键。点击后按下新的组合；Esc 取消，Backspace 清除。"));
        if (service is null)
        {
            Children.Add(Group(Note("快捷键在 O_O 运行时才能设置。")));
            return;
        }

        var list = new StackPanel { Spacing = 20 };
        foreach (var (id, title, description) in HotkeyActions.All)
        {
            var recorder = new ShortcutRecorder
            {
                Label = title,
                Description = description,
                Placeholder = "未设置",
                RecordingPlaceholder = "请按下组合键",
                RecordingHint = "Esc 取消，Backspace 清除",
                UseAnywayText = "仍然使用",
                CancelText = "取消",
                ClearText = "清除快捷键",
                IsModifierRequired = true,
                ResetValue = ToGesture(HotkeyActions.Defaults(OperatingSystem.IsMacOS()).GetValueOrDefault(id)),
            };
            var problem = Note("");
            problem.IsVisible = false;
            ArcTokens.Bind(problem, TextBlock.ForegroundProperty, ArcDesignTokenKind.Danger);
            var actionId = id;
            recorder.ValueChanged += (_, e) => OnRecorded(actionId, e);
            recorder.PropertyChanged += (_, e) =>
            {
                if (e.Property == ShortcutRecorder.IsRecordingProperty)
                {
                    service.Suspend(_rows.Values.Any(r => r.Recorder.IsRecording));
                }
            };
            _rows[id] = (recorder, problem);
            list.Children.Add(new StackPanel { Spacing = 6, Children = { recorder, problem } });
        }

        var reset = new ArcButton { Content = "全部恢复默认", Variant = ButtonVariant.Ghost, Size = ControlSize.Small, HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Left };
        reset.Click += (_, _) => service.Store.ResetAll();
        list.Children.Add(reset);
        Children.Add(Group(list));
        Children.Add(Note(OperatingSystem.IsLinux()
            ? "在 Wayland 原生应用处于前台时，全局快捷键可能收不到。"
            : "如果某个组合已被其他应用占用，这里会说明；换一个组合即可。"));

        Refresh();
        Action<string> statusChanged = _ => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
        WhileAttached(this, () => service.StatusChanged += statusChanged, () => service.StatusChanged -= statusChanged);
        DetachedFromVisualTree += (_, _) => service.Suspend(false);
    }

    private void OnRecorded(string action, ShortcutRecorderValueChangedEventArgs e)
    {
        if (_applying || _service is null)
        {
            return;
        }
        var store = _service.Store;
        if (e.NewValue is null)
        {
            store.Set(action, null);
            return;
        }
        if (FromGesture(e.NewValue) is not { IsValid: true } hotkey)
        {
            Refresh(); // a key O_O cannot register globally: back to what it was, with the reason
            _rows[action].Problem.Text = "这个按键不能用作全局快捷键，请换一个（字母、数字、F1–F12、空格等）。";
            _rows[action].Problem.IsVisible = true;
            return;
        }
        // "仍然使用" on another action's shortcut moves it here; that action is left without one.
        if (store.UsedBy(hotkey, action) is { } other)
        {
            store.Set(other, null);
        }
        store.Set(action, hotkey);
    }

    private void Refresh()
    {
        if (_service is null)
        {
            return;
        }
        _applying = true;
        try
        {
            foreach (var (id, (recorder, problem)) in _rows)
            {
                var gesture = ToGesture(_service.Store.Get(id));
                if (!Equals(recorder.Value, gesture))
                {
                    recorder.Value = gesture;
                }
                // The other actions' shortcuts, so recording one of them asks first.
                recorder.Bindings = [.. HotkeyActions.All
                    .Where(a => a.Id != id && _service.Store.Get(a.Id) is not null)
                    .Select(a => new ShortcutBinding(ToGesture(_service.Store.Get(a.Id))!, a.Title))];
                var reason = _service.Problem(id);
                problem.Text = reason ?? "";
                problem.IsVisible = reason is not null;
            }
        }
        finally
        {
            _applying = false;
        }
    }

    private static TextBlock Note(string text) => Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true);

    // ---- KeyGesture (Arc's recorder) ⇄ Hotkey (what O_O stores and registers) -------------------------------------

    private static readonly (string Name, Key Key)[] KeyMap =
    [
        .. Enumerable.Range(0, 26).Select(i => (((char)('A' + i)).ToString(), Key.A + i)),
        .. Enumerable.Range(0, 10).Select(i => (((char)('0' + i)).ToString(), Key.D0 + i)),
        .. Enumerable.Range(0, 12).Select(i => ($"F{i + 1}", Key.F1 + i)),
        ("Space", Key.Space), ("Enter", Key.Enter), ("Tab", Key.Tab), ("Comma", Key.OemComma),
        ("Period", Key.OemPeriod), ("Slash", Key.OemQuestion), ("Semicolon", Key.OemSemicolon),
    ];

    public static KeyGesture? ToGesture(Hotkey? hotkey)
    {
        if (hotkey is null || KeyMap.FirstOrDefault(k => k.Name == hotkey.Key) is not { Name: not null } entry)
        {
            return null;
        }
        var m = KeyModifiers.None;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Control)) m |= KeyModifiers.Control;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Alt)) m |= KeyModifiers.Alt;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Shift)) m |= KeyModifiers.Shift;
        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Meta)) m |= KeyModifiers.Meta;
        return new KeyGesture(entry.Key, m);
    }

    public static Hotkey? FromGesture(KeyGesture gesture)
    {
        var key = gesture.Key switch
        {
            Key.NumPad0 or Key.NumPad1 or Key.NumPad2 or Key.NumPad3 or Key.NumPad4 or Key.NumPad5 or Key.NumPad6 or
                Key.NumPad7 or Key.NumPad8 or Key.NumPad9 => Key.D0 + (gesture.Key - Key.NumPad0),
            Key.Return => Key.Enter,
            var k => k,
        };
        if (KeyMap.FirstOrDefault(k => k.Key == key) is not { Name: not null } entry)
        {
            return null;
        }
        var m = HotkeyModifiers.None;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Control)) m |= HotkeyModifiers.Control;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Alt)) m |= HotkeyModifiers.Alt;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Shift)) m |= HotkeyModifiers.Shift;
        if (gesture.KeyModifiers.HasFlag(KeyModifiers.Meta)) m |= HotkeyModifiers.Meta;
        return new Hotkey(m, entry.Name);
    }
}
