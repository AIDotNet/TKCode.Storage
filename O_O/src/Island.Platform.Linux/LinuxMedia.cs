using System.Diagnostics;
using System.Globalization;

namespace Island.Platform.Linux;

/// <summary>
/// Linux media support through MPRIS, via the <c>playerctl</c> tool when it is installed (no D-Bus library is bundled).
/// Without playerctl there is no media support (<see cref="Query"/> returns null, commands fail).
/// </summary>
public sealed class LinuxMedia : IMediaPlatform
{
    private const string Format = "{{status}}\t{{playerName}}\t{{title}}\t{{artist}}\t{{album}}\t{{position}}\t{{mpris:length}}";

    public bool PushesChanges => false;

    public event Action? Changed { add { } remove { } }

    public NowPlaying? Query()
    {
        var output = Run("metadata", "--format", Format);
        var f = output?.TrimEnd('\n').Split('\t');
        if (f is not { Length: >= 7 } || f[0] is not ("Playing" or "Paused"))
        {
            return null;
        }
        // playerctl reports microseconds.
        static double? Seconds(string s) => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) ? us / 1e6 : null;
        return new NowPlaying(f[1], Empty(f[2]), Empty(f[3]), Empty(f[4]), f[0] == "Playing", Seconds(f[5]), Seconds(f[6]), DateTimeOffset.Now);
    }

    public bool Send(MediaCommand command) => Run(command switch
    {
        MediaCommand.Play => "play",
        MediaCommand.Pause => "pause",
        MediaCommand.Toggle => "play-pause",
        MediaCommand.Next => "next",
        _ => "previous",
    }) is not null;

    private static string? Empty(string s) => s.Length == 0 ? null : s;

    private static string? Run(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("playerctl") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(2000) && p.ExitCode == 0 ? output : null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null; // playerctl is not installed
        }
    }
}
