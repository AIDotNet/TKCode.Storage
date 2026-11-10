using System.Runtime.InteropServices;

namespace Island.Platform.Windows;

/// <summary>
/// Windows media support, minimal for now: transport commands as media keys (which the system routes to the current
/// player). What is playing needs the WinRT media session API (GSMTC), which is not used yet because CsWinRT is heavy
/// under NativeAOT (risk R5); <see cref="Query"/> returns null.
/// </summary>
public sealed partial class WindowsMedia : IMediaPlatform
{
    private const ushort VkMediaNext = 0xB0;
    private const ushort VkMediaPrevious = 0xB1;
    private const ushort VkMediaPlayPause = 0xB3;
    private const uint KeyUp = 0x0002;

    public bool PushesChanges => false;

    public event Action? Changed { add { } remove { } }

    public NowPlaying? Query() => null;

    public bool Send(MediaCommand command)
    {
        // Play and pause are both the play/pause key: without knowing the state, that is the best a key can do.
        var vk = command switch
        {
            MediaCommand.Next => VkMediaNext,
            MediaCommand.Previous => VkMediaPrevious,
            _ => VkMediaPlayPause,
        };
        Span<Input> inputs = [Key(vk, 0), Key(vk, KeyUp)];
        return SendInput((uint)inputs.Length, ref MemoryMarshal.GetReference(inputs), Marshal.SizeOf<Input>()) == inputs.Length;
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

    [StructLayout(LayoutKind.Explicit)]
    private struct Input
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public KeyboardInput Ki;
        [FieldOffset(8)] private MousePadding _padding;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MousePadding
    {
        public int Dx, Dy;
        public uint Data, Flags, Time;
        public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static partial uint SendInput(uint count, ref Input inputs, int size);
}
