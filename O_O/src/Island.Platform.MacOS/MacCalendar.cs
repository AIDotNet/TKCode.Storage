using System.Diagnostics;
using System.Globalization;

namespace Island.Platform.MacOS;

/// <summary>
/// The calendars of the macOS Calendar app (iCloud, Exchange, Google and any other account added in System Settings),
/// read through EventKit. EventKit needs a block-based permission request, which is simplest to make from JavaScript
/// for Automation, so a small embedded script (<c>Scripts/calendar.js</c>) runs under <c>osascript</c>. macOS asks the
/// user for Calendar access the first time (the usage text is in Info.plist).
/// </summary>
public sealed class MacCalendar : ICalendarPlatform
{
    // The first run waits for the user to answer the permission dialog.
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(2);

    public async Task<string> ReadAsync(DateTimeOffset from, DateTimeOffset to, CancellationToken ct)
    {
        string script;
        await using (var s = typeof(MacCalendar).Assembly.GetManifestResourceStream("calendar.js")
            ?? throw new InvalidOperationException("calendar.js is not embedded."))
        using (var r = new StreamReader(s))
        {
            script = await r.ReadToEndAsync(ct);
        }
        var path = Path.Combine(Path.GetTempPath(), $"o_o-calendar-{Environment.ProcessId}.js");
        await File.WriteAllTextAsync(path, script, ct);
        try
        {
            var info = new ProcessStartInfo("/usr/bin/osascript")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (var a in new[] { "-l", "JavaScript", path, Epoch(from), Epoch(to) })
            {
                info.ArgumentList.Add(a);
            }
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(Timeout);
            try
            {
                await p.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                ct.ThrowIfCancellationRequested();
                return """{"error":"timeout"}""";
            }
            var output = (await stdout).Trim();
            if (p.ExitCode != 0 || !output.StartsWith('{'))
            {
                var message = (await stderr).Trim();
                return System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, string> { ["error"] = "failed", ["message"] = message });
            }
            return output;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return """{"error":"unavailable"}""";
        }
        finally
        {
            try { File.Delete(path); } catch (IOException) { }
        }
    }

    private static string Epoch(DateTimeOffset t) => t.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
}
