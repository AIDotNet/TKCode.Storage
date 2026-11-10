using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Island.Core.Input;

namespace Island.Platform.Windows;

/// <summary>
/// Global hotkeys through <c>RegisterHotKey</c> on a thread of our own: hotkeys registered without a window post
/// <c>WM_HOTKEY</c> to the registering thread's queue, so that thread runs a message loop and does every
/// registration itself (requests reach it as posted thread messages).
/// </summary>
public sealed partial class WindowsGlobalHotkeys : IGlobalHotkeys
{
    private const uint WmHotkey = 0x0312;
    private const uint WmApp = 0x8000;
    private const uint WmQuit = 0x0012;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModShift = 0x4, ModWin = 0x8, ModNoRepeat = 0x4000;
    private const int ErrorHotkeyAlreadyRegistered = 1409;

    private readonly Thread _thread;
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ManualResetEventSlim _ready = new();
    private readonly Dictionary<string, int> _registered = new(StringComparer.Ordinal);
    private readonly Dictionary<int, string> _ids = [];
    private uint _threadId;
    private int _next = 1;

    public WindowsGlobalHotkeys()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "O_O hotkeys" };
        _thread.Start();
        _ready.Wait();
    }

    public event Action<string>? Pressed;

    public string? Register(string id, Hotkey hotkey) => OnThread(() =>
    {
        UnregisterCore(id);
        if (VirtualKey(hotkey.Key) is not { } vk)
        {
            return $"不支持按键 {hotkey.Key}。";
        }
        var number = _next++;
        if (!RegisterHotKey(0, number, Modifiers(hotkey.Modifiers) | ModNoRepeat, vk))
        {
            return Marshal.GetLastPInvokeError() == ErrorHotkeyAlreadyRegistered
                ? "这个快捷键已被其他应用或系统占用。"
                : $"无法注册快捷键（{Marshal.GetLastPInvokeError()}）。";
        }
        _registered[id] = number;
        _ids[number] = id;
        return null;
    });

    public void Unregister(string id) => OnThread<string?>(() =>
    {
        UnregisterCore(id);
        return null;
    });

    private void UnregisterCore(string id)
    {
        if (_registered.Remove(id, out var number))
        {
            _ids.Remove(number);
            UnregisterHotKey(0, number);
        }
    }

    private T OnThread<T>(Func<T> work)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() =>
        {
            try
            {
                done.SetResult(work());
            }
            catch (Exception e)
            {
                done.SetException(e);
            }
        });
        PostThreadMessageW(_threadId, WmApp, 0, 0);
        return done.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        PeekMessageW(out _, 0, 0, 0, 0); // creates this thread's message queue before anyone posts to it
        _ready.Set();
        while (GetMessageW(out var msg, 0, 0, 0) > 0)
        {
            if (msg.Message == WmHotkey && _ids.TryGetValue((int)msg.WParam, out var id))
            {
                Pressed?.Invoke(id);
            }
            while (_work.TryDequeue(out var work))
            {
                work();
            }
        }
    }

    public void Dispose()
    {
        OnThread<string?>(() =>
        {
            foreach (var id in _registered.Keys.ToList())
            {
                UnregisterCore(id);
            }
            return null;
        });
        PostThreadMessageW(_threadId, WmQuit, 0, 0);
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Alt) ? ModAlt : 0) | (m.HasFlag(HotkeyModifiers.Control) ? ModControl : 0) |
        (m.HasFlag(HotkeyModifiers.Shift) ? ModShift : 0) | (m.HasFlag(HotkeyModifiers.Meta) ? ModWin : 0);

    private static uint? VirtualKey(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetterOrDigit(key[0]) => key[0],
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => (uint)(0x70 + f - 1),
        "Space" => 0x20, "Enter" => 0x0D, "Tab" => 0x09,
        "Comma" => 0xBC, "Period" => 0xBE, "Slash" => 0xBF, "Semicolon" => 0xBA,
        _ => null,
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public nint Hwnd;
        public uint Message;
        public nuint WParam;
        public nint LParam;
        public uint Time;
        public int X, Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint vk);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    [LibraryImport("user32.dll")]
    private static partial int GetMessageW(out Msg msg, nint hwnd, uint min, uint max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PeekMessageW(out Msg msg, nint hwnd, uint min, uint max, uint remove);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostThreadMessageW(uint thread, uint msg, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();
}
