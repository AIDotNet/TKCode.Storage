using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Island.Plugins.Host.Services;

/// <summary>
/// The <c>files.shelf</c> capability (docs/design/05-builtin-plugins.md §7): facts about files the user handed the
/// island, and showing / opening them with the system. Paths must be absolute; nothing here reads file content.
/// </summary>
internal static class PluginFiles
{
    public const int MaxPaths = 200;

    /// <summary>[{ path, name, exists, dir, size?, modified? }] for each path in <c>{ paths: [...] }</c>.</summary>
    public static string Info(string payload)
    {
        var paths = Paths(payload);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer))
        {
            w.WriteStartArray();
            foreach (var path in paths)
            {
                w.WriteStartObject();
                w.WriteString("path", path);
                w.WriteString("name", Path.GetFileName(Path.TrimEndingDirectorySeparator(path)));
                var dir = Directory.Exists(path);
                var file = !dir && File.Exists(path);
                w.WriteBoolean("exists", dir || file);
                w.WriteBoolean("dir", dir);
                if (file)
                {
                    var info = new FileInfo(path);
                    w.WriteNumber("size", info.Length);
                    w.WriteString("modified", info.LastWriteTimeUtc.ToString("O"));
                }
                w.WriteEndObject();
            }
            w.WriteEndArray();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    /// <summary>Shows the item selected in Finder / Explorer / the file manager (<c>{ path }</c>).</summary>
    public static string Reveal(string payload)
    {
        var path = OnePath(payload);
        if (OperatingSystem.IsMacOS())
        {
            Run("open", "-R", path);
        }
        else if (OperatingSystem.IsWindows())
        {
            Run("explorer.exe", $"/select,{path}");
        }
        else
        {
            Run("xdg-open", Directory.Exists(path) ? path : Path.GetDirectoryName(path) ?? path);
        }
        return "true";
    }

    /// <summary>Opens the item with its default app (<c>{ path }</c>).</summary>
    public static string Open(string payload)
    {
        var path = OnePath(payload);
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        else
        {
            Run(OperatingSystem.IsMacOS() ? "open" : "xdg-open", path);
        }
        return "true";
    }

    private static void Run(string file, params string[] args)
    {
        var info = new ProcessStartInfo(file) { UseShellExecute = false };
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        try
        {
            Process.Start(info)?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            throw new PluginCapabilityException($"无法打开：{e.Message}");
        }
    }

    private static string OnePath(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var path = doc.RootElement.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString()! : "";
        Check(path);
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            throw new PluginCapabilityException("文件已不存在。");
        }
        return path;
    }

    private static List<string> Paths(string payload)
    {
        using var doc = JsonDocument.Parse(payload);
        var paths = doc.RootElement.TryGetProperty("paths", out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray().Where(e => e.ValueKind == JsonValueKind.String).Select(e => e.GetString()!).ToList()
            : [];
        if (paths.Count is 0 or > MaxPaths)
        {
            throw new PluginCapabilityException($"需要 1 到 {MaxPaths} 个路径。");
        }
        paths.ForEach(Check);
        return paths;
    }

    private static void Check(string path)
    {
        if (!Path.IsPathFullyQualified(path) || path.Contains('\0'))
        {
            throw new PluginCapabilityException($"不是绝对路径：{path}");
        }
    }

    /// <summary>The JSON a plugin's files.onDrop listeners receive: <c>["/a", "/b"]</c>.</summary>
    public static string DropJson(IReadOnlyList<string> paths) =>
        JsonSerializer.Serialize(paths, HostJsonContext.Default.IReadOnlyListString);
}
