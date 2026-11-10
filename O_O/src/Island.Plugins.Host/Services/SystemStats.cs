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
internal sealed class SystemStats
{
    private readonly object _gate = new();
    private (long Busy, long Total)? _cpu;
    private (long In, long Out, DateTime At)? _net;

    /// <summary>The figures as the JSON the plugin API hands out.</summary>
    public string Sample()
    {
        lock (_gate)
        {
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
            return Encoding.UTF8.GetString(ms.ToArray());
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

    // ---- macOS (the system's own tools; no private APIs) -------------------------------------------------------------

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

    private double? MacCpu()
    {
        // Sum of the per-process shares over the cores: cheap, and close to Activity Monitor's figure.
        var sum = Run("/bin/ps", "-A -o %cpu").Split('\n').Skip(1).Select(l => double.TryParse(l.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0).Sum();
        return Math.Min(100, sum / Environment.ProcessorCount);
    }

    private static (long?, long?) MacMemory()
    {
        var total = long.Parse(Run("/usr/sbin/sysctl", "-n hw.memsize").Trim(), CultureInfo.InvariantCulture);
        var vm = Run("/usr/bin/vm_stat", "");
        var page = 16384L;
        long free = 0, inactive = 0, speculative = 0;
        foreach (var line in vm.Split('\n'))
        {
            if (line.Contains("page size of", StringComparison.Ordinal))
            {
                var digits = new string(line.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());
                if (long.TryParse(digits, out var ps)) { page = ps; }
            }
            var kv = line.Split(':');
            if (kv.Length != 2) { continue; }
            var count = long.TryParse(kv[1].Trim().TrimEnd('.'), out var c) ? c : 0;
            switch (kv[0].Trim())
            {
                case "Pages free": free = count; break;
                case "Pages inactive": inactive = count; break;
                case "Pages speculative": speculative = count; break;
            }
        }
        return (total, total - (free + inactive + speculative) * page);
    }

    private static (int, bool)? MacBattery()
    {
        var text = Run("/usr/bin/pmset", "-g batt");
        var i = text.IndexOf('%', StringComparison.Ordinal);
        if (i < 0) { return null; }
        var start = i;
        while (start > 0 && char.IsDigit(text[start - 1])) { start--; }
        return int.TryParse(text.AsSpan(start, i - start), out var pct)
            ? (pct, text.Contains("AC Power", StringComparison.Ordinal) && !text.Contains("discharging", StringComparison.Ordinal))
            : null;
    }

    private static (long, long)? MacNet()
    {
        // netstat -ib: one line per interface address; take each interface's <Link#> row (the byte counters).
        long rx = 0, tx = 0;
        var lines = Run("/usr/sbin/netstat", "-ib").Split('\n');
        if (lines.Length < 2) { return null; }
        var header = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();
        int ib = header.IndexOf("Ibytes"), ob = header.IndexOf("Obytes");
        if (ib < 0 || ob < 0) { return null; }
        foreach (var line in lines.Skip(1))
        {
            var f = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (f.Length <= ob || !f[2].StartsWith("<Link#", StringComparison.Ordinal) || f[0].StartsWith("lo", StringComparison.Ordinal)) { continue; }
            // Rows without an address column have one field fewer.
            var shift = f.Length - header.Count;
            if (long.TryParse(f[ib + shift], out var i) && long.TryParse(f[ob + shift], out var o)) { rx += i; tx += o; }
        }
        return (rx, tx);
    }
}
