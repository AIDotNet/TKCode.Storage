using System.Text.Json;
using Island.Platform;
using Island.Plugins.Host.Services;
using Jaunt;

namespace Island.Plugins.Host;

/// <summary>The <c>media</c> capability (docs/design/05-builtin-plugins.md §1): now playing, transport commands, changes.</summary>
internal sealed partial class PluginRunner
{
    /// <summary><c>media.nowPlaying</c> / <c>media.command</c> ({ command: 'play'|'pause'|'toggle'|'next'|'previous' }).</summary>
    private Func<CancellationToken, Task<string>> MediaCall(string op, string payload, ref JsCallContext c)
    {
        if (!_permissions.Has("media"))
        {
            throw c.TypeError("This plugin has no media permission.");
        }
        var media = _services.Media ?? throw c.TypeError("Media is not available here.");
        if (op == "media.nowPlaying")
        {
            return async ct => PlatformMedia.ToJson(await media.NowPlayingAsync(ct));
        }
        using var doc = JsonDocument.Parse(payload);
        var name = doc.RootElement.TryGetProperty("command", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
        MediaCommand command = name switch
        {
            "play" => MediaCommand.Play,
            "pause" => MediaCommand.Pause,
            "toggle" => MediaCommand.Toggle,
            "next" => MediaCommand.Next,
            "previous" => MediaCommand.Previous,
            _ => throw c.TypeError($"Unknown media command '{name}'."),
        };
        return async ct => await media.CommandAsync(command, ct) ? "true" : "false";
    }

    /// <summary>What is playing changed: plugins holding media get it (their media.onChange listeners).</summary>
    private void OnMediaChanged(NowPlaying? now)
    {
        if (!_permissions.Has("media"))
        {
            return;
        }
        var generation = Volatile.Read(ref _generation);
        var payload = PlatformMedia.ToJson(now);
        Post(() =>
        {
            if (generation == _generation)
            {
                Call("media", 0, payload);
            }
        });
    }
}
