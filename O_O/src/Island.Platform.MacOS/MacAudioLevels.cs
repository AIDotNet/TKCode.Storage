using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace Island.Platform.MacOS;

/// <summary>
/// Loudness of one application's audio, per band (<see cref="IAudioLevelSource"/>), through a CoreAudio process tap
/// (macOS 14.4+): a private tap on the app's audio-producing processes, read through a private aggregate device. The
/// first use makes macOS ask for "System Audio Recording". Anything that fails — older macOS, the app not running, the
/// permission refused — just means <c>Listen</c> returns null or no levels arrive, and the visualiser moves by itself.
/// Only levels leave this class; the samples are analysed in place and dropped.
/// </summary>
public sealed unsafe partial class MacAudioLevels : IAudioLevelSource
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string CoreAudio = "/System/Library/Frameworks/CoreAudio.framework/CoreAudio";
    private const int Bands = 16;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, Shared> _shared = new(StringComparer.OrdinalIgnoreCase);

    public bool IsSupported => OperatingSystem.IsMacOSVersionAtLeast(14, 4);

    public IDisposable? Listen(string processName, int bands, Action<float[]> levels)
    {
        if (!IsSupported || string.IsNullOrWhiteSpace(processName))
        {
            return null;
        }
        lock (_gate)
        {
            if (!_shared.TryGetValue(processName, out var shared))
            {
                shared = Shared.TryStart(processName, () => { lock (_gate) { _shared.Remove(processName); } });
                if (shared is null)
                {
                    return null;
                }
                _shared[processName] = shared;
            }
            return shared.Add(bands, levels);
        }
    }

    /// <summary>
    /// True while a process of the application is producing sound (CoreAudio's "running output" flag): no permission needed,
    /// no samples. False when the app is not running or silent; null where this cannot be known.
    /// </summary>
    public static bool? IsOutputting(string processName)
    {
        if (!OperatingSystem.IsMacOSVersionAtLeast(14, 4))
        {
            return null;
        }
        try
        {
            return FindProcessObjects(processName).Any(p => ReadUInt(p, Fcc("piro")) is > 0);
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    // ---- one tap per application, shared by every listener ---------------------------------------------------------

    private sealed class Shared
    {
        private readonly Lock _lock = new();
        private readonly List<(int Bands, Action<float[]> Levels)> _listeners = [];
        private readonly Action _onEmpty;
        private uint _tap, _device;
        private nint _procId;
        private GCHandle _self;
        private SpectrumAnalyzer? _analyzer;
        private int _channels = 2;
        private bool _interleaved = true;
        private long _lastSoundTicks;
        private float[] _mono = new float[4096];

        private Shared(Action onEmpty) => _onEmpty = onEmpty;

        public static Shared? TryStart(string processName, Action onEmpty)
        {
            var shared = new Shared(onEmpty);
            return shared.Start(processName) ? shared : null;
        }

        public IDisposable Add(int bands, Action<float[]> levels)
        {
            var entry = (bands, levels);
            lock (_lock)
            {
                _listeners.Add(entry);
            }
            return new Handle(() =>
            {
                bool empty;
                lock (_lock)
                {
                    _listeners.Remove(entry);
                    empty = _listeners.Count == 0;
                }
                if (empty)
                {
                    _onEmpty();
                    Stop();
                }
            });
        }

        private bool Start(string processName)
        {
            try
            {
                var processes = FindProcessObjects(processName);
                if (processes.Count == 0)
                {
                    return false;
                }
                var numbers = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Sel("array"));
                foreach (var p in processes)
                {
                    ObjC.Send(numbers, ObjC.Sel("addObject:"), NumberFromUInt(p));
                }
                var desc = ObjC.Send(ObjC.Send(ObjC.Class("CATapDescription"), ObjC.Sel("alloc")), ObjC.Sel("initStereoMixdownOfProcesses:"), numbers);
                if (desc == 0)
                {
                    return false;
                }
                ObjC.SendVoid(desc, ObjC.Sel("setPrivate:"), (byte)1);
                if (AudioHardwareCreateProcessTap(desc, out _tap) != 0)
                {
                    return false;
                }
                var tapUid = FromNSString(ObjC.Send(ObjC.Send(desc, ObjC.Sel("UUID")), ObjC.Sel("UUIDString")));
                if (tapUid is null || !ReadTapFormat())
                {
                    Stop();
                    return false;
                }

                // A private aggregate device that holds only the tap, anchored on the system output so it has a clock.
                var outputUid = DefaultOutputUid();
                var aggregate = ObjC.Send(ObjC.Class("NSMutableDictionary"), ObjC.Sel("dictionary"));
                Set(aggregate, "name", NSString("O_O audio levels"));
                Set(aggregate, "uid", NSString(Guid.NewGuid().ToString()));
                Set(aggregate, "private", NumberFromUInt(1));
                Set(aggregate, "stacked", NumberFromUInt(0));
                Set(aggregate, "tapautostart", NumberFromUInt(1));
                if (outputUid is not null)
                {
                    Set(aggregate, "master", NSString(outputUid));
                    var sub = ObjC.Send(ObjC.Class("NSMutableDictionary"), ObjC.Sel("dictionary"));
                    Set(sub, "uid", NSString(outputUid));
                    var subs = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Sel("array"));
                    ObjC.Send(subs, ObjC.Sel("addObject:"), sub);
                    Set(aggregate, "subdevices", subs);
                }
                var tapEntry = ObjC.Send(ObjC.Class("NSMutableDictionary"), ObjC.Sel("dictionary"));
                Set(tapEntry, "uid", NSString(tapUid));
                Set(tapEntry, "drift", NumberFromUInt(1));
                var taps = ObjC.Send(ObjC.Class("NSMutableArray"), ObjC.Sel("array"));
                ObjC.Send(taps, ObjC.Sel("addObject:"), tapEntry);
                Set(aggregate, "taps", taps);
                if (AudioHardwareCreateAggregateDevice(aggregate, out _device) != 0)
                {
                    Stop();
                    return false;
                }

                _self = GCHandle.Alloc(this);
                _lastSoundTicks = Environment.TickCount64;
                if (AudioDeviceCreateIOProcID(_device, &OnAudio, GCHandle.ToIntPtr(_self), out _procId) != 0 || AudioDeviceStart(_device, _procId) != 0)
                {
                    Stop();
                    return false;
                }
                return true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or InvalidOperationException)
            {
                Stop();
                return false;
            }
        }

        private void Stop()
        {
            if (_device != 0)
            {
                if (_procId != 0)
                {
                    AudioDeviceStop(_device, _procId);
                    AudioDeviceDestroyIOProcID(_device, _procId);
                    _procId = 0;
                }
                AudioHardwareDestroyAggregateDevice(_device);
                _device = 0;
            }
            if (_tap != 0)
            {
                AudioHardwareDestroyProcessTap(_tap);
                _tap = 0;
            }
            if (_self.IsAllocated)
            {
                _self.Free();
            }
        }

        /// <summary>The tap's sample format: float32, how many channels, interleaved or not, the sample rate.</summary>
        private bool ReadTapFormat()
        {
            var address = new Address(Fcc("tfmt"), Fcc("glob"), 0);
            uint size = (uint)sizeof(StreamDescription);
            StreamDescription format;
            if (AudioObjectGetPropertyData(_tap, ref address, 0, 0, ref size, (nint)(&format)) != 0)
            {
                return false;
            }
            const uint IsFloat = 0x1, NonInterleaved = 0x20;
            if ((format.Flags & IsFloat) == 0 || format.BitsPerChannel != 32)
            {
                return false;
            }
            _channels = (int)Math.Max(1, format.ChannelsPerFrame);
            _interleaved = (format.Flags & NonInterleaved) == 0;
            _analyzer = new SpectrumAnalyzer(Bands, (int)format.SampleRate);
            return true;
        }

        [UnmanagedCallersOnly]
        private static int OnAudio(uint device, nint now, nint input, nint inputTime, nint output, nint outputTime, nint client)
        {
            try
            {
                ((Shared)GCHandle.FromIntPtr(client).Target!).Process((BufferList*)input);
            }
            catch (Exception)
            {
                // Never let an exception cross into CoreAudio's thread.
            }
            return 0;
        }

        private void Process(BufferList* list)
        {
            if (list is null || list->Count == 0 || _analyzer is null)
            {
                return;
            }
            Buffer* buffers = &list->First;
            int frames;
            if (_interleaved)
            {
                frames = (int)(buffers[0].ByteSize / sizeof(float)) / _channels;
                EnsureMono(frames);
                var data = (float*)buffers[0].Data;
                for (var i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < _channels; c++)
                    {
                        sum += data[i * _channels + c];
                    }
                    _mono[i] = sum / _channels;
                }
            }
            else
            {
                frames = (int)(buffers[0].ByteSize / sizeof(float));
                EnsureMono(frames);
                var n = (int)Math.Min(list->Count, (uint)_channels);
                for (var i = 0; i < frames; i++)
                {
                    float sum = 0;
                    for (var c = 0; c < n; c++)
                    {
                        sum += ((float*)buffers[c].Data)[i];
                    }
                    _mono[i] = sum / n;
                }
            }

            var any = false;
            for (var i = 0; i < frames; i++)
            {
                if (_mono[i] != 0)
                {
                    any = true;
                    break;
                }
            }
            var now = Environment.TickCount64;
            if (any)
            {
                _lastSoundTicks = now;
            }
            else if (now - _lastSoundTicks > 1500)
            {
                return; // exact silence for a while: paused, or the permission was refused — send nothing, the bars fall back
            }

            if (_analyzer.Push(_mono.AsSpan(0, frames)) is { } bands)
            {
                Publish(bands);
            }
        }

        private void EnsureMono(int frames)
        {
            if (_mono.Length < frames)
            {
                _mono = new float[frames];
            }
        }

        private void Publish(float[] bands)
        {
            (int Bands, Action<float[]> Levels)[] copy;
            lock (_lock)
            {
                copy = [.. _listeners];
            }
            foreach (var (count, levels) in copy)
            {
                if (count == Bands)
                {
                    levels(bands);
                    continue;
                }
                var fitted = new float[count];
                for (var i = 0; i < count; i++)
                {
                    int from = i * Bands / count, to = Math.Max(from + 1, (i + 1) * Bands / count);
                    float max = 0;
                    for (var k = from; k < to && k < Bands; k++)
                    {
                        max = Math.Max(max, bands[k]);
                    }
                    fitted[i] = max;
                }
                levels(fitted);
            }
        }
    }

    private sealed class Handle(Action onDispose) : IDisposable
    {
        private int _done;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _done, 1) == 0)
            {
                onDispose();
            }
        }
    }

    // ---- finding the application's audio processes ------------------------------------------------------------------

    /// <summary>The CoreAudio process objects of every running process whose name or bundle id contains <paramref name="name"/>.</summary>
    private static List<uint> FindProcessObjects(string name)
    {
        var result = new List<uint>();
        var system = new Address(Fcc("prs#"), Fcc("glob"), 0);
        uint size;
        if (AudioObjectGetPropertyDataSize(1, ref system, 0, 0, out size) != 0 || size == 0)
        {
            return result;
        }
        var ids = new uint[size / sizeof(uint)];
        fixed (uint* p = ids)
        {
            if (AudioObjectGetPropertyData(1, ref system, 0, 0, ref size, (nint)p) != 0)
            {
                return result;
            }
        }
        foreach (var id in ids)
        {
            var pid = ReadUInt(id, Fcc("ppid"));
            var bundle = ReadString(id, Fcc("pbid"));
            string? processName = null;
            if (pid is { } value)
            {
                try
                {
                    processName = Process.GetProcessById((int)value).ProcessName;
                }
                catch (Exception e) when (e is ArgumentException or InvalidOperationException)
                {
                    // gone already
                }
            }
            if ((processName?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false) || (bundle?.Contains(name, StringComparison.OrdinalIgnoreCase) ?? false))
            {
                result.Add(id);
            }
        }
        return result;
    }

    private static uint? ReadUInt(uint obj, uint selector)
    {
        var address = new Address(selector, Fcc("glob"), 0);
        uint size = sizeof(uint), value = 0;
        return AudioObjectGetPropertyData(obj, ref address, 0, 0, ref size, (nint)(&value)) == 0 ? value : null;
    }

    private static string? ReadString(uint obj, uint selector)
    {
        var address = new Address(selector, Fcc("glob"), 0);
        uint size = (uint)sizeof(nint);
        nint cf = 0;
        return AudioObjectGetPropertyData(obj, ref address, 0, 0, ref size, (nint)(&cf)) == 0 && cf != 0 ? FromNSString(cf) : null;
    }

    private static string? DefaultOutputUid()
    {
        var address = new Address(Fcc("sOut"), Fcc("glob"), 0);
        uint size = sizeof(uint), device = 0;
        if (AudioObjectGetPropertyData(1, ref address, 0, 0, ref size, (nint)(&device)) != 0 || device == 0)
        {
            return null;
        }
        return ReadString(device, Fcc("uid "));
    }

    // ---- Objective-C and CoreAudio plumbing -------------------------------------------------------------------------

    /// <summary>A CoreAudio four-character code.</summary>
    private static uint Fcc(string code) => (uint)(code[0] << 24 | code[1] << 16 | code[2] << 8 | code[3]);

    private static void Set(nint dictionary, string key, nint value) =>
        ObjC.SendVoid2(dictionary, ObjC.Sel("setObject:forKey:"), value, NSString(key));

    private static nint NSString(string s)
    {
        var bytes = Encoding.UTF8.GetBytes(s + "\0");
        fixed (byte* p = bytes)
        {
            return ObjC.Send(ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("alloc")), ObjC.Sel("initWithUTF8String:"), (nint)p);
        }
    }

    private static string? FromNSString(nint s)
    {
        if (s == 0)
        {
            return null;
        }
        var utf8 = ObjC.Send(s, ObjC.Sel("UTF8String"));
        return utf8 == 0 ? null : Marshal.PtrToStringUTF8(utf8);
    }

    private static nint NumberFromUInt(uint value) =>
        SendUInt(ObjC.Class("NSNumber"), ObjC.Sel("numberWithUnsignedInt:"), value);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial nint SendUInt(nint receiver, nint selector, uint arg);

    [StructLayout(LayoutKind.Sequential)]
    private struct Address(uint selector, uint scope, uint element)
    {
        public uint Selector = selector, Scope = scope, Element = element;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StreamDescription
    {
        public double SampleRate;
        public uint FormatId, Flags, BytesPerPacket, FramesPerPacket, BytesPerFrame, ChannelsPerFrame, BitsPerChannel, Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Buffer
    {
        public uint Channels, ByteSize;
        public nint Data;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BufferList
    {
        public uint Count;
        public Buffer First;
    }

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyDataSize(uint obj, ref Address address, uint qualifierSize, nint qualifier, out uint size);

    [LibraryImport(CoreAudio)]
    private static partial int AudioObjectGetPropertyData(uint obj, ref Address address, uint qualifierSize, nint qualifier, ref uint size, nint data);

    [LibraryImport(CoreAudio)]
    private static partial int AudioHardwareCreateProcessTap(nint description, out uint tap);

    [LibraryImport(CoreAudio)]
    private static partial int AudioHardwareDestroyProcessTap(uint tap);

    [LibraryImport(CoreAudio)]
    private static partial int AudioHardwareCreateAggregateDevice(nint description, out uint device);

    [LibraryImport(CoreAudio)]
    private static partial int AudioHardwareDestroyAggregateDevice(uint device);

    [LibraryImport(CoreAudio)]
    private static partial int AudioDeviceCreateIOProcID(uint device, delegate* unmanaged<uint, nint, nint, nint, nint, nint, nint, int> proc, nint client, out nint procId);

    [LibraryImport(CoreAudio)]
    private static partial int AudioDeviceStart(uint device, nint procId);

    [LibraryImport(CoreAudio)]
    private static partial int AudioDeviceStop(uint device, nint procId);

    [LibraryImport(CoreAudio)]
    private static partial int AudioDeviceDestroyIOProcID(uint device, nint procId);
}
