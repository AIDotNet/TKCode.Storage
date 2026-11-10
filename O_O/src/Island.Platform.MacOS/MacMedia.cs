using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Island.Platform.MacOS;

/// <summary>
/// macOS media support (docs/design/05-builtin-plugins.md §1, risk R2). Since macOS 15.4 MediaRemote no longer tells
/// non-Apple processes what is playing, so:
/// <list type="bullet">
/// <item>Changes: the distributed notifications Music (<c>com.apple.Music.playerInfo</c>) and Spotify
/// (<c>com.spotify.client.PlaybackStateChanged</c>) post on every change — no permission needed.</item>
/// <item>The state before the first change: one AppleScript query, only to a player that is already running (never
/// launches it; macOS asks the user for the Automation permission the first time).</item>
/// <item>Commands: <c>MRMediaRemoteSendCommand</c>, which still reaches whatever owns the system's now-playing
/// controls (any player, browsers included).</item>
/// </list>
/// Must be created on the main thread (the notifications arrive on its run loop).
/// </summary>
public sealed unsafe partial class MacMedia : IMediaPlatform
{
    private const string Objc = "/usr/lib/libobjc.A.dylib";
    private const string MusicId = "com.apple.Music";
    private const string SpotifyId = "com.spotify.client";

    private static MacMedia? _instance;
    private readonly Lock _gate = new();
    private NowPlaying? _current;
    private bool _queried;
    private readonly delegate* unmanaged<uint, nint, byte> _sendCommand;

    public MacMedia()
    {
        _instance = this;
        var mediaRemote = NativeLibrary.TryLoad("/System/Library/PrivateFrameworks/MediaRemote.framework/MediaRemote", out var h) ? h : 0;
        _sendCommand = mediaRemote != 0 && NativeLibrary.TryGetExport(mediaRemote, "MRMediaRemoteSendCommand", out var f)
            ? (delegate* unmanaged<uint, nint, byte>)f
            : null;
        Observe();
    }

    public bool PushesChanges => true;

    public event Action? Changed;

    public NowPlaying? Query()
    {
        lock (_gate)
        {
            if (_current is not null || _queried)
            {
                return Extrapolated(_current);
            }
            _queried = true;
        }
        // Nothing heard yet: ask a running player once.
        var state = IsRunning(SpotifyId) ? AskSpotify() : null;
        state ??= IsRunning(MusicId) ? AskMusic() : null;
        lock (_gate)
        {
            _current ??= state;
            return Extrapolated(_current);
        }
    }

    public bool Send(MediaCommand command)
    {
        if (_sendCommand == null)
        {
            return false;
        }
        // MRMediaCommand: play 0, pause 1, togglePlayPause 2, nextTrack 4, previousTrack 5.
        uint code = command switch
        {
            MediaCommand.Play => 0,
            MediaCommand.Pause => 1,
            MediaCommand.Toggle => 2,
            MediaCommand.Next => 4,
            _ => 5,
        };
        // Its Boolean result is not reliable (players accept commands without confirming), so sending is success.
        _sendCommand(code, 0);
        return true;
    }

    private static NowPlaying? Extrapolated(NowPlaying? s) =>
        s is { Playing: true, Position: { } p, PositionAt: { } at }
            ? s with { Position = Math.Min(p + (DateTimeOffset.Now - at).TotalSeconds, s.Duration ?? double.MaxValue), PositionAt = DateTimeOffset.Now }
            : s;

    // ---- distributed notifications ------------------------------------------------------------------------------

    private static void Observe()
    {
        var cls = objc_getClass("OOMediaObserver");
        if (cls == 0)
        {
            cls = objc_allocateClassPair(objc_getClass("NSObject"), "OOMediaObserver", 0);
            class_addMethod(cls, sel_registerName("musicChanged:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnMusic, "v@:@");
            class_addMethod(cls, sel_registerName("spotifyChanged:"), (nint)(delegate* unmanaged<nint, nint, nint, void>)&OnSpotify, "v@:@");
            objc_registerClassPair(cls);
        }
        var observer = ObjC.Send(ObjC.Send(cls, ObjC.Sel("alloc")), ObjC.Sel("init"));
        var center = ObjC.Send(ObjC.Class("NSDistributedNotificationCenter"), ObjC.Sel("defaultCenter"));
        AddObserver(center, sel_registerName("addObserver:selector:name:object:"), observer, sel_registerName("musicChanged:"), NSString("com.apple.Music.playerInfo"), 0);
        AddObserver(center, sel_registerName("addObserver:selector:name:object:"), observer, sel_registerName("spotifyChanged:"), NSString("com.spotify.client.PlaybackStateChanged"), 0);
    }

    [UnmanagedCallersOnly]
    private static void OnMusic(nint self, nint cmd, nint notification) => _instance?.Update("Music", notification, positionKey: null, durationInMs: true);

    [UnmanagedCallersOnly]
    private static void OnSpotify(nint self, nint cmd, nint notification) => _instance?.Update("Spotify", notification, positionKey: "Playback Position", durationInMs: true);

    private void Update(string app, nint notification, string? positionKey, bool durationInMs)
    {
        try
        {
            var info = ObjC.Send(notification, ObjC.Sel("userInfo"));
            var state = Str(info, "Player State");
            NowPlaying? next = state is null or "Stopped" ? null : new NowPlaying(
                app,
                Str(info, "Name"),
                Str(info, "Artist"),
                Str(info, "Album"),
                state == "Playing",
                positionKey is null ? null : Num(info, positionKey),
                Num(info, app == "Spotify" ? "Duration" : "Total Time") is { } d ? (durationInMs ? d / 1000 : d) : null,
                DateTimeOffset.Now);
            lock (_gate)
            {
                // A stop from one player does not clear what another is playing.
                if (next is null && _current is not null && _current.App != app)
                {
                    return;
                }
                _current = next;
            }
            Changed?.Invoke();
        }
        catch (Exception e)
        {
            Trace.TraceError($"O_O media: {e.Message}");
        }
    }

    private static string? Str(nint dict, string key)
    {
        var v = ObjC.Send(dict, ObjC.Sel("objectForKey:"), NSString(key));
        if (v == 0 || !ObjC.RespondsTo(v, ObjC.Sel("UTF8String")))
        {
            return null;
        }
        return Marshal.PtrToStringUTF8(ObjC.Send(v, ObjC.Sel("UTF8String")));
    }

    private static double? Num(nint dict, string key)
    {
        var v = ObjC.Send(dict, ObjC.Sel("objectForKey:"), NSString(key));
        return v != 0 && ObjC.RespondsTo(v, ObjC.Sel("doubleValue")) ? ObjC.SendDouble(v, ObjC.Sel("doubleValue")) : null;
    }

    // ---- AppleScript, only to a player that is already running -------------------------------------------------

    private static bool IsRunning(string bundleId)
    {
        var apps = ObjC.Send(ObjC.Class("NSRunningApplication"), ObjC.Sel("runningApplicationsWithBundleIdentifier:"), NSString(bundleId));
        return apps != 0 && ObjC.Send(apps, ObjC.Sel("count")) != 0;
    }

    private static NowPlaying? AskMusic() => Ask("Music", MusicId, "player position", "duration of t");

    private static NowPlaying? AskSpotify() => Ask("Spotify", SpotifyId, "player position", "(duration of t) / 1000");

    private static NowPlaying? Ask(string app, string bundleId, string position, string duration)
    {
        var script = $$"""
            tell application id "{{bundleId}}"
              if player state is stopped then return ""
              set t to current track
              return (player state as text) & tab & (name of t) & tab & (artist of t) & tab & (album of t) & tab & ({{position}} as text) & tab & ({{duration}} as text)
            end tell
            """;
        try
        {
            var info = new ProcessStartInfo("/usr/bin/osascript") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
            info.ArgumentList.Add("-e");
            info.ArgumentList.Add(script);
            using var p = Process.Start(info)!;
            var output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(3000) || p.ExitCode != 0)
            {
                return null; // e.g. the user declined the Automation permission
            }
            var f = output.TrimEnd('\n').Split('\t');
            if (f.Length < 6)
            {
                return null;
            }
            static double? D(string s) => double.TryParse(s.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;
            return new NowPlaying(app, f[1], f[2], f[3], f[0] == "playing", D(f[4]), D(f[5]), DateTimeOffset.Now);
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    private static nint NSString(string s)
    {
        var utf8 = Marshal.StringToCoTaskMemUTF8(s);
        try
        {
            return ObjC.Send(ObjC.Class("NSString"), ObjC.Sel("stringWithUTF8String:"), utf8);
        }
        finally
        {
            Marshal.FreeCoTaskMem(utf8);
        }
    }

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_getClass(string name);

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint objc_allocateClassPair(nint superclass, string name, nint extraBytes);

    [LibraryImport(Objc)]
    private static partial void objc_registerClassPair(nint cls);

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    [return: MarshalAs(UnmanagedType.U1)]
    private static partial bool class_addMethod(nint cls, nint selector, nint imp, string types);

    [LibraryImport(Objc, StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint sel_registerName(string name);

    [LibraryImport(Objc, EntryPoint = "objc_msgSend")]
    private static partial void AddObserver(nint center, nint selector, nint observer, nint observerSelector, nint name, nint obj);
}
