using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// CPU, memory, battery and network figures for the <c>system.info</c> permission. Read-only and coarse; fields the
/// platform cannot tell are left out (null). CPU and network are rates between two samples, so the first call after
/// start reports no rate yet.
/// </summary>
internal sealed partial class SystemStats
{
    /// <summary>One sampler for every plugin: rates need a single baseline, and a second reader within
    /// <see cref="Fresh"/> gets the same figures instead of another sample.</summary>
    public static SystemStats Shared { get; } = new();

    private static readonly TimeSpan Fresh = TimeSpan.FromSeconds(1);
    private readonly object _gate = new();
    private string? _last;
    private DateTime _lastAt = DateTime.MinValue;
    private (long Busy, long Total)? _cpu;
    private (long In, long Out, DateTime At)? _net;

    /// <summary>The figures as the JSON the plugin API hands out.</summary>
    public string Sample()
    {
        lock (_gate)
        {
            if (_last is not null && DateTime.UtcNow - _lastAt < Fresh)
            {
                return _last;
            }
            double? cpu = null;
            long? memTotal = null, memUsed = null;
            (int Percent, bool Charging)? battery = null;
            (long In, long Out)? net = null;
            try
            {
                if (OperatingSystem.IsLinux())
                {
                    cpu = LinuxCpu();
                    (memTotal, memUsed) = LinuxMemory();
                    battery = LinuxBattery();
                    net = LinuxNet();
                }
                else if (OperatingSystem.IsMacOS())
                {
                    cpu = MacCpu();
                    (memTotal, memUsed) = MacMemory();
                    battery = MacBattery();
                    net = MacNet();
                }
                else
                {
                    memTotal = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes;
                }
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or FormatException or UnauthorizedAccessException)
            {
                // A figure the platform will not give is simply missing.
            }

            double? netIn = null, netOut = null;
            if (net is { } n)
            {
                var now = DateTime.UtcNow;
                if (_net is { } prev && now > prev.At)
                {
                    var secs = (now - prev.At).TotalSeconds;
                    netIn = Math.Max(0, (n.In - prev.In) / secs);
                    netOut = Math.Max(0, (n.Out - prev.Out) / secs);
                }
                _net = (n.In, n.Out, now);
            }

            using var ms = new MemoryStream();
            using (var w = new Utf8JsonWriter(ms))
            {
                w.WriteStartObject();
                Num(w, "cpu", cpu);
                w.WriteNumber("cores", Environment.ProcessorCount);
                if (memTotal is { } t) { w.WriteNumber("memTotal", t); }
                if (memUsed is { } u) { w.WriteNumber("memUsed", u); }
                if (battery is { } b) { w.WriteNumber("battery", b.Percent); w.WriteBoolean("charging", b.Charging); }
                Num(w, "netIn", netIn);
                Num(w, "netOut", netOut);
                w.WriteEndObject();
            }
            _lastAt = DateTime.UtcNow;
            return _last = Encoding.UTF8.GetString(ms.GetBuffer(), 0, (int)ms.Length);
        }
    }

    private static void Num(Utf8JsonWriter w, string name, double? v)
    {
        if (v is { } d)
        {
            w.WriteNumber(name, Math.Round(d, 1));
        }
    }

    private double? Rate(long busy, long total)
    {
        double? result = null;
        if (_cpu is { } p && total > p.Total)
        {
            result = 100.0 * (busy - p.Busy) / (total - p.Total);
        }
        _cpu = (busy, total);
        return result;
    }

    // ---- Linux ------------------------------------------------------------------------------------------------------

    private double? LinuxCpu()
    {
        var f = File.ReadLines("/proc/stat").First().Split(' ', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(long.Parse).ToArray();
        var idle = f[3] + (f.Length > 4 ? f[4] : 0);
        var total = f.Sum();
        return Rate(total - idle, total);
    }

    private static (long?, long?) LinuxMemory()
    {
        long total = 0, avail = 0;
        foreach (var line in File.ReadLines("/proc/meminfo"))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts[0] == "MemTotal:") { total = long.Parse(parts[1]) * 1024; }
            else if (parts[0] == "MemAvailable:") { avail = long.Parse(parts[1]) * 1024; }
        }
        return (total, total - avail);
    }

    private static (int, bool)? LinuxBattery()
    {
        foreach (var dir in Directory.EnumerateDirectories("/sys/class/power_supply", "BAT*"))
        {
            if (int.TryParse(File.ReadAllText(Path.Combine(dir, "capacity")).Trim(), out var pct))
            {
                var status = File.Exists(Path.Combine(dir, "status")) ? File.ReadAllText(Path.Combine(dir, "status")).Trim() : "";
                return (pct, status is "Charging" or "Full");
            }
        }
        return null;
    }

    private static (long, long)? LinuxNet()
    {
        long rx = 0, tx = 0;
        foreach (var line in File.ReadLines("/proc/net/dev").Skip(2))
        {
            var (name, rest) = (line[..line.IndexOf(':')].Trim(), line[(line.IndexOf(':') + 1)..].Split(' ', StringSplitOptions.RemoveEmptyEntries));
            if (name == "lo") { continue; }
            rx += long.Parse(rest[0]);
            tx += long.Parse(rest[8]);
        }
        return (rx, tx);
    }

    // ---- macOS (Mach and sysctl in-process; only the battery still asks pmset, at most once a minute) ----------------

    private (int, bool)? _battery;
    private DateTime _batteryAt = DateTime.MinValue;
    private static readonly TimeSpan BatteryEvery = TimeSpan.FromMinutes(1);

    private static string Run(string file, string args)
    {
        using var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true })
                      ?? throw new InvalidOperationException(file);
        var output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(2000))
        {
            p.Kill();
            throw new InvalidOperationException($"{file} timed out");
        }
        return output;
    }

    private unsafe double? MacCpu()
    {
        // HOST_CPU_LOAD_INFO: ticks spent in user, system, idle and nice, summed over every core.
        var ticks = stackalloc uint[4];
        var count = 4;
        if (host_statistics(mach_host_self(), 3, ticks, ref count) != 0)
        {
            return null;
        }
        long user = ticks[0], system = ticks[1], idle = ticks[2], nice = ticks[3];
        var total = user + system + idle + nice;
        return Rate(total - idle, total);
    }

    private static unsafe (long?, long?) MacMemory()
    {
        long total = 0;
        var size = (nint)sizeof(long);
        if (sysctlbyname("hw.memsize", &total, ref size, null, 0) != 0)
        {
            return (null, null);
        }
        // HOST_VM_INFO64 (vm_statistics64): free_count at 0, inactive_count at 8, speculative_count at 92.
        var vm = stackalloc uint[38];
        var count = 38;
        if (host_statistics64(mach_host_self(), 4, vm, ref count) != 0)
        {
            return (total, null);
        }
        long free = vm[0], inactive = vm[2], speculative = vm[23];
        return (total, total - (free + inactive + speculative) * Environment.SystemPageSize);
    }

    private (int, bool)? MacBattery()
    {
        if (DateTime.UtcNow - _batteryAt < BatteryEvery)
        {
            return _battery;
        }
        _batteryAt = DateTime.UtcNow;
        var text = Run("/usr/bin/pmset", "-g batt");
        var i = text.IndexOf('%', StringComparison.Ordinal);
        if (i < 0) { return _battery = null; }
        var start = i;
        while (start > 0 && char.IsDigit(text[start - 1])) { start--; }
        return _battery = int.TryParse(text.AsSpan(start, i - start), out var pct)
            ? (pct, text.Contains("AC Power", StringComparison.Ordinal) && !text.Contains("discharging", StringComparison.Ordinal))
            : null;
    }

    private readonly Dictionary<ushort, (uint In, uint Out)> _ifCounters = [];
    private long _netInTotal, _netOutTotal;

    internal unsafe (long, long)? MacNet()
    {
        // sysctl { CTL_NET, PF_ROUTE, 0, 0, NET_RT_IFLIST2, 0 }: one RTM_IFINFO2 message (if_msghdr2) per interface.
        // Unprivileged callers get the byte counters cut to 32 bits, so they wrap every 4 GB: keep running totals from
        // each interface's wrap-safe change instead of summing the raw values. Loopback is left out.
        var mib = stackalloc int[] { 4, 17, 0, 0, 6, 0 };
        nint len = 0;
        if (sysctl(mib, 6, null, ref len, null, 0) != 0 || len <= 0)
        {
            return null;
        }
        var buffer = new byte[len + 1024]; // interfaces can appear between the two calls
        len = buffer.Length;
        fixed (byte* p = buffer)
        {
            if (sysctl(mib, 6, p, ref len, null, 0) != 0)
            {
                return null;
            }
            for (nint at = 0; at + 4 <= len;)
            {
                var msg = p + at;
                var msgLen = *(ushort*)msg;
                if (msgLen == 0 || at + msgLen > len)
                {
                    break;
                }
                // Offsets from offsetof(struct if_msghdr2, …) on macOS 26: flags 8, index 12, ibytes 96, obytes 104.
                const int RtmIfInfo2 = 0x12, IffLoopback = 0x8;
                if (msg[3] == RtmIfInfo2 && msgLen >= 112 && (*(int*)(msg + 8) & IffLoopback) == 0)
                {
                    var index = *(ushort*)(msg + 12);
                    var rx = (uint)*(ulong*)(msg + 96);
                    var tx = (uint)*(ulong*)(msg + 104);
                    if (_ifCounters.TryGetValue(index, out var prev))
                    {
                        _netInTotal += unchecked(rx - prev.In);
                        _netOutTotal += unchecked(tx - prev.Out);
                    }
                    _ifCounters[index] = (rx, tx);
                }
                at += msgLen;
            }
            return (_netInTotal, _netOutTotal);
        }
    }

    private const string LibSystem = "/usr/lib/libSystem.dylib";

    [LibraryImport(LibSystem)]
    private static partial uint mach_host_self();

    [LibraryImport(LibSystem)]
    private static unsafe partial int host_statistics(uint host, int flavor, uint* info, ref int count);

    [LibraryImport(LibSystem)]
    private static unsafe partial int host_statistics64(uint host, int flavor, uint* info, ref int count);

    [LibraryImport(LibSystem)]
    private static unsafe partial int sysctl(int* name, uint length, void* oldp, ref nint oldlen, void* newp, nint newlen);

    [LibraryImport(LibSystem, StringMarshalling = StringMarshalling.Utf8)]
    private static unsafe partial int sysctlbyname(string name, void* oldp, ref nint oldlen, void* newp, nint newlen);
}
