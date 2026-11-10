using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// What the clipboard holds, as plugins see it (docs/design/05-builtin-plugins.md §8). Text that is a single web
/// address is a <c>url</c>; files are absolute paths; an <c>image</c> is a PNG the host saved in its clipboard image
/// folder (<see cref="Image"/>, with its size) — plugins get the path to show it, never the pixels.
/// </summary>
public sealed record ClipboardEntry(string Kind, string? Text, IReadOnlyList<string>? Files, string? App, DateTimeOffset At)
{
    public string? Image { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }

    public static ClipboardEntry FromImage(string pngPath, int width, int height, string? app, DateTimeOffset at) =>
        new("image", null, null, app, at) { Image = pngPath, Width = width, Height = height };

    public const int MaxTextLength = 100_000;
    public const int MaxFiles = 200;

    public static ClipboardEntry FromText(string text, string? app, DateTimeOffset at)
    {
        var trimmed = text.Trim();
        var url = trimmed.Length < 2048 && !trimmed.Contains(' ') && !trimmed.Contains('\n') &&
                  Uri.TryCreate(trimmed, UriKind.Absolute, out var u) && u.Scheme is "http" or "https";
        return new(url ? "url" : "text", text.Length > MaxTextLength ? text[..MaxTextLength] : text, null, app, at);
    }

    public static ClipboardEntry FromFiles(IReadOnlyList<string> files, string? app, DateTimeOffset at) =>
        new("files", null, [.. files.Take(MaxFiles)], app, at);

    /// <summary>Same content (kind, text, files); the app and time do not count.</summary>
    public bool SameContent(ClipboardEntry? other) =>
        other is not null && other.Kind == Kind && other.Text == Text && other.Image == Image &&
        (other.Files ?? []).SequenceEqual(Files ?? []);

    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind);
            if (Text is not null)
            {
                w.WriteString("text", Text);
            }
            if (Files is not null)
            {
                w.WriteStartArray("files");
                foreach (var f in Files)
                {
                    w.WriteStringValue(f);
                }
                w.WriteEndArray();
            }
            if (Image is not null)
            {
                w.WriteString("image", Image);
                w.WriteNumber("width", Width);
                w.WriteNumber("height", Height);
            }
            if (App is not null)
            {
                w.WriteString("app", App);
            }
            w.WriteString("at", At.ToString("O"));
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>What a plugin asks to put on the clipboard: <c>{ text }</c>, <c>{ files: [...] }</c> or <c>{ image }</c>.</summary>
    /// <param name="imageFolder">Images may only come from the host's clipboard image folder (what it recorded).</param>
    /// <exception cref="PluginCapabilityException">Neither, or too much.</exception>
    public static ClipboardEntry Parse(string json, string? imageFolder = null)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("image", out var img) && img.ValueKind == JsonValueKind.String)
        {
            var path = Path.GetFullPath(img.GetString()!);
            if (imageFolder is null || !path.StartsWith(Path.GetFullPath(imageFolder) + Path.DirectorySeparatorChar, StringComparison.Ordinal) || !File.Exists(path))
            {
                throw new PluginCapabilityException("只能放回剪贴板记录过的图片。");
            }
            return FromImage(path, 0, 0, null, DateTimeOffset.Now);
        }
        if (r.TryGetProperty("files", out var f) && f.ValueKind == JsonValueKind.Array)
        {
            var files = f.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList();
            if (files.Count is 0 or > MaxFiles || files.Any(p => !Path.IsPathFullyQualified(p)))
            {
                throw new PluginCapabilityException($"files 必须是 1 到 {MaxFiles} 个绝对路径。");
            }
            return FromFiles(files, null, DateTimeOffset.Now);
        }
        if (r.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
        {
            var text = t.GetString()!;
            if (text.Length > MaxTextLength)
            {
                throw new PluginCapabilityException($"文本超过 {MaxTextLength} 个字符。");
            }
            return FromText(text, null, DateTimeOffset.Now);
        }
        throw new PluginCapabilityException("需要 { text } 或 { files: [...] }。");
    }
}

/// <summary>The clipboard as the host offers it to plugins; the app supplies the implementation (UI thread, native code).</summary>
public interface IPluginClipboard
{
    /// <summary>Raised (on any thread) when the clipboard changes to content worth recording; secret content is never raised.</summary>
    event Action<ClipboardEntry>? Changed;

    Task<ClipboardEntry?> ReadAsync(CancellationToken ct);

    Task WriteAsync(ClipboardEntry entry, CancellationToken ct);

    /// <summary>Writes <paramref name="entry"/>, then pastes it into the app the user was working in.</summary>
    /// <returns>Whether it was pasted, and if not, why (it is on the clipboard either way).</returns>
    Task<(bool Pasted, string? Reason)> PasteAsync(ClipboardEntry entry, CancellationToken ct);

    /// <summary>Where recorded images are kept; plugins may put only these back on the clipboard.</summary>
    string? ImageFolder => null;
}
