using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Island.Core.Input;

namespace Island.Platform.Linux;

/// <summary>
/// Global hotkeys on X11 / XWayland through <c>XGrabKey</c> on the root window. One thread owns its own display
/// connection: it does every grab (requests are queued to it) and polls for key presses. Each hotkey is grabbed four
/// times, with and without Caps Lock and Num Lock, so those locks do not break it. Native Wayland apps in front do
/// not pass keys to X11 grabs.
/// </summary>
public sealed unsafe partial class LinuxGlobalHotkeys : IGlobalHotkeys
{
    private const string Xlib = "libX11.so.6";
    private const int KeyPress = 2;
    private const int GrabModeAsync = 1;
    private const uint ShiftMask = 1, LockMask = 2, ControlMask = 4, Mod1Mask = 8, Mod2Mask = 16, Mod4Mask = 64;
    private const int BadAccess = 10;
    private static readonly uint[] LockVariants = [0, LockMask, Mod2Mask, LockMask | Mod2Mask];

    // XGrabKey reports conflicts asynchronously through the error handler, which is process-wide (Avalonia's X11
    // backend has its own). Ours is installed only around a grab and its XSync, records errors of our connection,
    // and passes any other connection's errors on to the handler it replaced.
    private static int s_lastError;
    private static nint s_ownDisplay;
    private static nint s_previous;

    private readonly ConcurrentQueue<Action> _work = new();
    private readonly Dictionary<string, (byte Code, uint Mods)> _registered = new(StringComparer.Ordinal);
    private readonly Thread _thread;
    private readonly nint _display;
    private readonly nint _root;
    private volatile bool _stop;

    public LinuxGlobalHotkeys()
    {
        XInitThreads();
        _display = XOpenDisplay(0);
        if (_display == 0)
        {
            throw new InvalidOperationException("无法连接 X 服务器。");
        }
        _root = XDefaultRootWindow(_display);
        s_ownDisplay = _display;
        _thread = new Thread(Loop) { IsBackground = true, Name = "O_O hotkeys" };
        _thread.Start();
    }

    public event Action<string>? Pressed;

    public string? Register(string id, Hotkey hotkey) => OnThread(() =>
    {
        UnregisterCore(id);
        if (KeySym(hotkey.Key) is not { } sym || XKeysymToKeycode(_display, sym) is var code && code == 0)
        {
            return $"不支持按键 {hotkey.Key}。";
        }
        var mods = Modifiers(hotkey.Modifiers);
        s_lastError = 0;
        s_previous = XSetErrorHandler(&OnError);
        foreach (var variant in LockVariants)
        {
            XGrabKey(_display, code, mods | variant, _root, 0, GrabModeAsync, GrabModeAsync);
        }
        XSync(_display, 0);
        XSetErrorHandler((delegate* unmanaged[Cdecl]<nint, nint, int>)s_previous);
        if (s_lastError != 0)
        {
            foreach (var variant in LockVariants)
            {
                XUngrabKey(_display, code, mods | variant, _root);
            }
            XSync(_display, 0);
            return s_lastError == BadAccess ? "这个快捷键已被其他应用或系统占用。" : $"无法注册快捷键（X 错误 {s_lastError}）。";
        }
        _registered[id] = (code, mods);
        return null;
    });

    public void Unregister(string id) => OnThread<string?>(() =>
    {
        UnregisterCore(id);
        return null;
    });

    private void UnregisterCore(string id)
    {
        if (_registered.Remove(id, out var r))
        {
            foreach (var variant in LockVariants)
            {
                XUngrabKey(_display, r.Code, r.Mods | variant, _root);
            }
            XFlush(_display);
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
        return done.Task.GetAwaiter().GetResult();
    }

    private void Loop()
    {
        var ev = stackalloc byte[192]; // XEvent is a union of 24 longs
        while (!_stop)
        {
            while (_work.TryDequeue(out var work))
            {
                work();
            }
            while (XPending(_display) > 0)
            {
                XNextEvent(_display, ev);
                if (*(int*)ev != KeyPress)
                {
                    continue;
                }
                // XKeyEvent on 64-bit: state at 80, keycode at 84.
                var state = *(uint*)(ev + 80) & ~(LockMask | Mod2Mask);
                var code = (byte)*(uint*)(ev + 84);
                foreach (var (id, r) in _registered)
                {
                    if (r.Code == code && r.Mods == state)
                    {
                        Pressed?.Invoke(id);
                    }
                }
            }
            Thread.Sleep(30);
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
        _stop = true;
        _thread.Join(TimeSpan.FromSeconds(1));
        XCloseDisplay(_display);
    }

    private static uint Modifiers(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Shift) ? ShiftMask : 0) | (m.HasFlag(HotkeyModifiers.Control) ? ControlMask : 0) |
        (m.HasFlag(HotkeyModifiers.Alt) ? Mod1Mask : 0) | (m.HasFlag(HotkeyModifiers.Meta) ? Mod4Mask : 0);

    private static ulong? KeySym(string key) => key switch
    {
        { Length: 1 } when char.IsAsciiLetter(key[0]) => char.ToLowerInvariant(key[0]),
        { Length: 1 } when char.IsAsciiDigit(key[0]) => key[0],
        ['F', .. var n] when int.TryParse(n, out var f) && f is >= 1 and <= 12 => (ulong)(0xffbe + f - 1),
        "Space" => 0x20, "Enter" => 0xff0d, "Tab" => 0xff09,
        "Comma" => 0x2c, "Period" => 0x2e, "Slash" => 0x2f, "Semicolon" => 0x3b,
        _ => null,
    };

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnError(nint display, nint errorEvent)
    {
        if (display != s_ownDisplay)
        {
            return s_previous != 0 ? ((delegate* unmanaged[Cdecl]<nint, nint, int>)s_previous)(display, errorEvent) : 0;
        }
        // XErrorEvent: type(int) display(ptr) resourceid serial(ulong) error_code(byte at 32 on 64-bit)
        s_lastError = *(byte*)(errorEvent + 32);
        return 0;
    }

    [LibraryImport(Xlib)] private static partial int XInitThreads();
    [LibraryImport(Xlib)] private static partial nint XOpenDisplay(nint name);
    [LibraryImport(Xlib)] private static partial int XCloseDisplay(nint display);
    [LibraryImport(Xlib)] private static partial nint XDefaultRootWindow(nint display);
    [LibraryImport(Xlib)] private static partial byte XKeysymToKeycode(nint display, ulong keysym);
    [LibraryImport(Xlib)] private static partial int XGrabKey(nint display, int keycode, uint modifiers, nint window, int ownerEvents, int pointerMode, int keyboardMode);
    [LibraryImport(Xlib)] private static partial int XUngrabKey(nint display, int keycode, uint modifiers, nint window);
    [LibraryImport(Xlib)] private static partial int XSync(nint display, int discard);
    [LibraryImport(Xlib)] private static partial int XFlush(nint display);
    [LibraryImport(Xlib)] private static partial int XPending(nint display);
    [LibraryImport(Xlib)] private static partial int XNextEvent(nint display, byte* ev);
    [LibraryImport(Xlib)] private static partial nint XSetErrorHandler(delegate* unmanaged[Cdecl]<nint, nint, int> handler);
}
