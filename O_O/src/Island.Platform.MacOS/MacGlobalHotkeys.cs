using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Island.Core.Input;

namespace Island.Platform.MacOS;

/// <summary>
/// Global hotkeys through Carbon's <c>RegisterEventHotKey</c>: still the supported way on macOS, needs no
/// Accessibility permission (unlike event taps), and delivers only our own key combinations. Presses arrive as Carbon
/// events on the main thread's run loop, which Avalonia's NSApplication runs. Register on the UI thread.
/// </summary>
public sealed unsafe partial class MacGlobalHotkeys : IGlobalHotkeys
{
    private const string Carbon = "/System/Library/Frameworks/Carbon.framework/Carbon";
    private const uint KEventClassKeyboard = 0x6B657962;     // 'keyb'
    private const uint KEventHotKeyPressed = 5;
    private const uint KEventParamDirectObject = 0x2D2D2D2D; // '----'
    private const uint TypeEventHotKeyID = 0x686B6964;       // 'hkid'
    private const uint Signature = 0x4F5F4F21;               // 'O_O!'
    private const int EventHotKeyExistsErr = -9878;

    private static readonly Dictionary<string, uint> KeyCodes = new(StringComparer.Ordinal)
    {
        ["A"] = 0, ["S"] = 1, ["D"] = 2, ["F"] = 3, ["H"] = 4, ["G"] = 5, ["Z"] = 6, ["X"] = 7, ["C"] = 8, ["V"] = 9,
        ["B"] = 11, ["Q"] = 12, ["W"] = 13, ["E"] = 14, ["R"] = 15, ["Y"] = 16, ["T"] = 17, ["O"] = 31, ["U"] = 32,
        ["I"] = 34, ["P"] = 35, ["L"] = 37, ["J"] = 38, ["K"] = 40, ["N"] = 45, ["M"] = 46,
        ["1"] = 18, ["2"] = 19, ["3"] = 20, ["4"] = 21, ["6"] = 22, ["5"] = 23, ["9"] = 25, ["7"] = 26, ["8"] = 28, ["0"] = 29,
        ["Enter"] = 36, ["Tab"] = 48, ["Space"] = 49, ["Comma"] = 43, ["Period"] = 47, ["Slash"] = 44, ["Semicolon"] = 41,
        ["F1"] = 122, ["F2"] = 120, ["F3"] = 99, ["F4"] = 118, ["F5"] = 96, ["F6"] = 97, ["F7"] = 98, ["F8"] = 100,
        ["F9"] = 101, ["F10"] = 109, ["F11"] = 103, ["F12"] = 111,
    };

    // The Carbon callback is static; it finds the instance through this handle passed as user data.
    private readonly GCHandle _self;
    private readonly nint _handler;
    private readonly Dictionary<string, (uint Number, nint Ref)> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<uint, string> _ids = [];
    private uint _next = 1;

    public MacGlobalHotkeys()
    {
        _self = GCHandle.Alloc(this);
        var spec = new EventTypeSpec { EventClass = KEventClassKeyboard, EventKind = KEventHotKeyPressed };
        var status = InstallEventHandler(GetApplicationEventTarget(), &OnHotKey, 1, &spec, GCHandle.ToIntPtr(_self), out _handler);
        if (status != 0)
        {
            _self.Free();
            throw new InvalidOperationException($"InstallEventHandler failed ({status}).");
        }
    }

    public event Action<string>? Pressed;

    public string? Register(string id, Hotkey hotkey)
    {
        Unregister(id);
        if (!KeyCodes.TryGetValue(hotkey.Key, out var keyCode))
        {
            return $"不支持按键 {hotkey.Key}。";
        }
        var number = _next++;
        var hotKeyId = new EventHotKeyID { Signature = Signature, Id = number };
        var status = RegisterEventHotKey(keyCode, Modifiers(hotkey.Modifiers), hotKeyId, GetApplicationEventTarget(), 0, out var hotKeyRef);
        if (status != 0)
        {
            return status == EventHotKeyExistsErr ? "这个快捷键已被其他应用或系统占用。" : $"无法注册快捷键（{status}）。";
        }
        _registered[id] = (number, hotKeyRef);
        _ids[number] = id;
        return null;
    }

    public void Unregister(string id)
    {
        if (_registered.Remove(id, out var r))
        {
            _ids.Remove(r.Number);
            UnregisterEventHotKey(r.Ref);
        }
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys.ToList())
        {
            Unregister(id);
        }
        RemoveEventHandler(_handler);
        if (_self.IsAllocated)
        {
            _self.Free();
        }
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Meta) ? 0x100u : 0) |     // cmdKey
        (m.HasFlag(HotkeyModifiers.Shift) ? 0x200u : 0) |    // shiftKey
        (m.HasFlag(HotkeyModifiers.Alt) ? 0x800u : 0) |      // optionKey
        (m.HasFlag(HotkeyModifiers.Control) ? 0x1000u : 0);  // controlKey

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnHotKey(nint callRef, nint @event, nint userData)
    {
        try
        {
            EventHotKeyID hotKeyId;
            if (GetEventParameter(@event, KEventParamDirectObject, TypeEventHotKeyID, 0, (nuint)sizeof(EventHotKeyID), 0, &hotKeyId) == 0 &&
                hotKeyId.Signature == Signature &&
                GCHandle.FromIntPtr(userData).Target is MacGlobalHotkeys self &&
                self._ids.TryGetValue(hotKeyId.Id, out var id))
            {
                self.Pressed?.Invoke(id);
                return 0; // noErr: handled
            }
        }
        catch (Exception)
        {
            // Never let an exception cross into Carbon.
        }
        return -9874; // eventNotHandledErr
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventTypeSpec
    {
        public uint EventClass;
        public uint EventKind;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct EventHotKeyID
    {
        public uint Signature;
        public uint Id;
    }

    [LibraryImport(Carbon)]
    private static partial nint GetApplicationEventTarget();

    [LibraryImport(Carbon)]
    private static partial int InstallEventHandler(nint target, delegate* unmanaged[Cdecl]<nint, nint, nint, int> handler,
        uint numTypes, EventTypeSpec* list, nint userData, out nint handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RemoveEventHandler(nint handlerRef);

    [LibraryImport(Carbon)]
    private static partial int RegisterEventHotKey(uint keyCode, uint modifiers, EventHotKeyID id, nint target, uint options, out nint hotKeyRef);

    [LibraryImport(Carbon)]
    private static partial int UnregisterEventHotKey(nint hotKeyRef);

    [LibraryImport(Carbon)]
    private static partial int GetEventParameter(nint @event, uint name, uint desiredType, nint actualType, nuint bufferSize, nint actualSize, void* data);
}
