using System.Text;
using System.Text.Json;
using Island.Platform;

namespace Island.Plugins.Host.Services;

/// <summary>The <c>media</c> capability as the host offers it to plugins (docs/design/05-builtin-plugins.md §1).</summary>
public interface IPluginMedia
{
    /// <summary>Raised (any thread) when what is playing changes; null when nothing is.</summary>
    event Action<NowPlaying?>? Changed;

    Task<NowPlaying?> NowPlayingAsync(CancellationToken ct);

    Task<bool> CommandAsync(MediaCommand command, CancellationToken ct);
}

/// <summary>
/// <see cref="IPluginMedia"/> over a platform: pushes changes where the platform does, otherwise polls — but only
/// while some plugin listens.
/// </summary>
public sealed class PlatformMedia : IPluginMedia, IDisposable
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(2);

    private readonly IMediaPlatform _platform;
    private readonly Lock _gate = new();
    private Action<NowPlaying?>? _changed;
    private Timer? _poll;
    private NowPlaying? _last;

    public PlatformMedia(IMediaPlatform platform)
    {
        _platform = platform;
        _platform.Changed += () => ThreadPool.QueueUserWorkItem(_ => Check());
    }

    public event Action<NowPlaying?>? Changed
    {
        add
        {
            lock (_gate)
            {
                _changed += value;
                if (!_platform.PushesChanges)
                {
                    _poll ??= new Timer(_ => Check(), null, TimeSpan.Zero, PollInterval);
                }
            }
        }
        remove
        {
            lock (_gate)
            {
                _changed -= value;
                if (_changed is null)
                {
                    _poll?.Dispose();
                    _poll = null;
                }
            }
        }
    }

    public Task<NowPlaying?> NowPlayingAsync(CancellationToken ct) => Task.Run(_platform.Query, ct);

    public Task<bool> CommandAsync(MediaCommand command, CancellationToken ct) => Task.Run(() => _platform.Send(command), ct);

    private void Check()
    {
        var now = _platform.Query();
        Action<NowPlaying?>? changed;
        lock (_gate)
        {
            if (SameTrackAndState(now, _last))
            {
                return;
            }
            _last = now;
            changed = _changed;
        }
        changed?.Invoke(now);
    }

    /// <summary>Position alone moving is not a change (plugins extrapolate it); a jump of more than 3 s is (a seek).</summary>
    private static bool SameTrackAndState(NowPlaying? a, NowPlaying? b)
    {
        if (a is null || b is null)
        {
            return a is null && b is null;
        }
        if ((a with { Position = null, PositionAt = null }) != (b with { Position = null, PositionAt = null }))
        {
            return false;
        }
        if (a.Position is not { } pa || b.Position is not { } pb || a.PositionAt is not { } ta || b.PositionAt is not { } tb)
        {
            return a.Position == b.Position;
        }
        var expected = pb + (b.Playing ? (ta - tb).TotalSeconds : 0);
        return Math.Abs(pa - expected) < 3;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _poll?.Dispose();
            _poll = null;
        }
    }

    /// <summary>{ app, title?, artist?, album?, playing, position?, duration? } or null.</summary>
    public static string ToJson(NowPlaying? n)
    {
        if (n is null)
        {
            return "null";
        }
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("app", n.App);
            if (n.Title is not null) w.WriteString("title", n.Title);
            if (n.Artist is not null) w.WriteString("artist", n.Artist);
            if (n.Album is not null) w.WriteString("album", n.Album);
            w.WriteBoolean("playing", n.Playing);
            if (n.Position is { } p && double.IsFinite(p)) w.WriteNumber("position", Math.Round(p, 1));
            if (n.Duration is { } d && double.IsFinite(d) && d > 0) w.WriteNumber("duration", Math.Round(d, 1));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }
}
