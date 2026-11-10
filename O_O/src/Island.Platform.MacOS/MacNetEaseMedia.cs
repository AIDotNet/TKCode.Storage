using System.Diagnostics;

namespace Island.Platform.MacOS;

/// <summary>
/// NetEase Cloud Music (网易云音乐) for the media capability on macOS, where the system no longer tells other apps what
/// is playing (<see cref="MacMedia"/>): while Music and Spotify are silent, a running NetEase client counts as playing
/// when its processes are producing sound (CoreAudio, no permission), and its window title — "歌名 - 歌手" while a song
/// plays — gives the track when the user has allowed O_O to use System Events (Automation + Accessibility). The title
/// is optional: without it the app is reported with no track. Transport commands go to MacMedia (the system's own).
/// Polled (about every two seconds, only while a plugin listens), because the client does not announce changes.
/// </summary>
public sealed class MacNetEaseMedia(IMediaPlatform inner) : IMediaPlatform
{
    private const string App = "NeteaseMusic";
    private static readonly TimeSpan TitleEvery = TimeSpan.FromSeconds(4);

    private readonly Lock _gate = new();
    private string? _title;
    private DateTime _titleAt;

    public bool PushesChanges => false;

    public event Action? Changed
    {
        add => inner.Changed += value;
        remove => inner.Changed -= value;
    }

    public NowPlaying? Query()
    {
        var other = inner.Query();
        if (other is { Playing: true })
        {
            return other;
        }
        if (MacAudioLevels.IsOutputting(App) is not true)
        {
            return other; // NetEase is silent or not running: whatever Music or Spotify says (or nothing)
        }
        var (title, artist) = Track();
        return new NowPlaying("网易云音乐", title, artist, null, Playing: true);
    }

    public bool Send(MediaCommand command) => inner.Send(command);

    private (string? Title, string? Artist) Track()
    {
        string? raw;
        lock (_gate)
        {
            if (DateTime.UtcNow - _titleAt > TitleEvery)
            {
                _title = WindowTitle();
                _titleAt = DateTime.UtcNow;
            }
            raw = _title;
        }
        return Parse(raw);
    }

    /// <summary>"歌名 - 歌手" → (歌名, 歌手); the app's own name or an empty title mean no track.</summary>
    public static (string? Title, string? Artist) Parse(string? raw)
    {
        raw = raw?.Trim();
        if (string.IsNullOrEmpty(raw) || raw is "网易云音乐" or "NeteaseMusic" or "NetEase Cloud Music")
        {
            return (null, null);
        }
        var i = raw.LastIndexOf(" - ", StringComparison.Ordinal);
        return i > 0 ? (raw[..i].Trim(), raw[(i + 3)..].Trim()) : (raw, null);
    }

    private static string? WindowTitle()
    {
        const string script = """
            tell application "System Events"
              if not (exists process "NeteaseMusic") then return ""
              try
                return name of window 1 of process "NeteaseMusic"
              on error
                return ""
              end try
            end tell
            """;
        try
        {
            var info = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add(script);
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd();
            return p.WaitForExit(3000) && p.ExitCode == 0 ? output.Trim() : null; // declined: no title, still "playing"
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }
}
