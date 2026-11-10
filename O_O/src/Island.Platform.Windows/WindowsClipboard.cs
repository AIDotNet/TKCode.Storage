using System.Runtime.InteropServices;

namespace Island.Platform.Windows;

/// <summary>
/// Windows clipboard support: <c>GetClipboardSequenceNumber</c>, the formats apps use to keep content out of
/// clipboard history (password managers), and direct paste by restoring the last foreground window and sending
/// Ctrl+V with <c>SendInput</c>. UIPI blocks input into elevated windows; that is reported, not hidden.
/// </summary>
public sealed partial class WindowsClipboard : IClipboardPlatform
{
    private const ushort VkControl = 0x11;
    private const ushort VkV = 0x56;
    private const uint KeyUp = 0x0002;

    private readonly uint _excludeMonitoring = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _canIncludeInHistory = RegisterClipboardFormatW("CanIncludeInClipboardHistory");
    private readonly int _ownPid = Environment.ProcessId;
    private nint _target;

    public long? ChangeCount => GetClipboardSequenceNumber();

    // CanIncludeInClipboardHistory holds a DWORD; apps set 0 to opt out. Its presence alone is treated as opting out,
    // which errs on the side of not recording.
    public bool IsConcealed() => IsClipboardFormatAvailable(_excludeMonitoring) || IsClipboardFormatAvailable(_canIncludeInHistory);

    public void TrackForegroundApp()
    {
        var window = GetForegroundWindow();
        if (window == 0)
        {
            return;
        }
        GetWindowThreadProcessId(window, out var pid);
        if (pid != _ownPid)
        {
            _target = window;
        }
    }

    public string? TargetAppName
    {
        get
        {
            if (_target == 0)
            {
                return null;
            }
            unsafe
            {
                var buffer = stackalloc char[256];
                var n = GetWindowTextW(_target, buffer, 256);
                return n > 0 ? new string(buffer, 0, n) : null;
            }
        }
    }

    public void RestoreFocus()
    {
        if (_target != 0)
        {
            SetForegroundWindow(_target);
        }
    }

    public PasteResult Paste()
    {
        if (_target != 0)
        {
            SetForegroundWindow(_target);
            Thread.Sleep(60);
        }
        Span<Input> inputs =
        [
            Key(VkControl, 0), Key(VkV, 0), Key(VkV, KeyUp), Key(VkControl, KeyUp),
        ];
        var sent = SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<Input>());
        return sent == inputs.Length
            ? PasteResult.Done
            : new(false, "无法向目标窗口发送按键（它可能以管理员身份运行）。已复制，请手动粘贴。");
    }

    private static Input Key(ushort vk, uint flags) => new() { Type = 1, Ki = new KeyboardInput { Vk = vk, Flags = flags } };

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    // INPUT is a union; the keyboard member padded to the size of the largest (MOUSEINPUT).
    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Ki;
        [FieldOffset(8)] private MouseInputPadding _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPadding
    {
        public int Dx, Dy;
        public uint Data, Flags, Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetClipboardSequenceNumber();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsClipboardFormatAvailable(uint format);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out int pid);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetWindowTextW(nint window, char* text, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, ref Input inputs, int size);
}
