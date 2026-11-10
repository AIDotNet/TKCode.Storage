using System.IO.Compression;
using System.Text.Json;

namespace Island.Plugins.Host;

/// <summary>
/// Plugin packages (<c>.zip</c>): checking, packing and unpacking. A package holds <c>plugin.json</c> at its root (or
/// inside one top-level folder) and the files it needs. Nothing in a package may point outside it, and limits keep a
/// malformed or hostile archive from filling the disk.
/// </summary>
public static class PluginPackages
{
    public const int MaxEntries = 500;
    public const long MaxBytes = 20L * 1024 * 1024;

    // Never packed: version control, editor and OS clutter, dependency folders.
    private static readonly string[] Skipped = [".git", ".svn", ".hg", ".DS_Store", "node_modules", "Thumbs.db", ".vscode", ".idea"];

    /// <summary>Checks a plugin folder the way installing would; returns the problems (empty when it is fine).</summary>
    public static List<string> Validate(string directory, out PluginManifest? manifest)
    {
        var problems = new List<string>();
        manifest = null;
        var manifestPath = Path.Combine(directory, "plugin.json");
        if (!File.Exists(manifestPath))
        {
            problems.Add("没有 plugin.json。");
            return problems;
        }
        try
        {
            manifest = PluginManifest.Parse(File.ReadAllText(manifestPath));
        }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            problems.Add($"plugin.json 无法解析：{e.Message}");
            return problems;
        }
        if (!IsValidId(manifest.Id))
        {
            problems.Add($"id '{manifest.Id}' 不合法：用小写反向域名，如 com.example.weather。");
        }
        var manifestProblems = new List<string>();
        _ = PluginPermissions.Parse(manifest.Permissions, manifestProblems);
        _ = SettingsSchema.Parse(manifest.Contributes.Settings, manifestProblems);
        ThemeContribution.Validate(manifest.Contributes.Themes, manifestProblems);
        LayoutContribution.Validate(manifest.Contributes.Layouts, manifestProblems);
        manifest.ValidateTargets(manifestProblems);
        problems.AddRange(manifestProblems);
        var main = Path.GetFullPath(Path.Combine(directory, manifest.Main));
        if (!main.StartsWith(Path.GetFullPath(directory), StringComparison.Ordinal) || !File.Exists(main))
        {
            problems.Add($"入口文件 '{manifest.Main}' 不存在。");
        }
        return problems;
    }

    public static bool IsValidId(string id) =>
        id.Length is >= 3 and <= 100 && id.Contains('.') &&
        id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-' or '_') &&
        !id.StartsWith('.') && !id.EndsWith('.') && !id.Contains("..");

    /// <summary>Packs a checked plugin folder into <paramref name="zipPath"/>; returns the problems, or none.</summary>
    public static List<string> Pack(string directory, string zipPath)
    {
        var problems = Validate(directory, out _);
        if (problems.Count > 0)
        {
            return problems;
        }
        var root = Path.GetFullPath(directory);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Where(f => !Path.GetRelativePath(root, f).Split(Path.DirectorySeparatorChar).Any(part => Skipped.Contains(part)))
            .Where(f => Path.GetFullPath(f) != Path.GetFullPath(zipPath))
            .ToList();
        if (files.Count > MaxEntries || files.Sum(f => new FileInfo(f).Length) > MaxBytes)
        {
            return [$"插件太大：最多 {MaxEntries} 个文件、{MaxBytes / 1024 / 1024} MB。"];
        }
        if (File.Exists(zipPath))
        {
            File.Delete(zipPath);
        }
        using var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create);
        foreach (var file in files)
        {
            zip.CreateEntryFromFile(file, Path.GetRelativePath(root, file).Replace('\\', '/'), CompressionLevel.Optimal);
        }
        return [];
    }

    /// <summary>
    /// Unpacks a package into a new temporary folder after checking every entry; returns that folder (the caller moves
    /// it into place) and the manifest.
    /// </summary>
    /// <exception cref="InvalidDataException">The package is not a valid plugin; the message says why, for the user.</exception>
    public static (string Directory, PluginManifest Manifest) Unpack(string zipPath)
    {
        using var zip = OpenZip(zipPath);
        var entries = zip.Entries.Where(e => !string.IsNullOrEmpty(e.Name)).ToList();
        if (entries.Count == 0 || entries.Count > MaxEntries)
        {
            throw new InvalidDataException(entries.Count == 0 ? "压缩包是空的。" : $"压缩包里的文件太多（最多 {MaxEntries} 个）。");
        }
        if (entries.Sum(e => e.Length) > MaxBytes)
        {
            throw new InvalidDataException($"解压后超过 {MaxBytes / 1024 / 1024} MB。");
        }
        // plugin.json at the root, or inside one top-level folder (what zipping a folder usually gives).
        var prefix = entries.Any(e => e.FullName == "plugin.json")
            ? ""
            : entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList() is [var top] && entries.Any(e => e.FullName == top + "/plugin.json")
                ? top + "/"
                : throw new InvalidDataException("压缩包里没有 plugin.json。");

        var target = Directory.CreateTempSubdirectory("o_o-install-").FullName;
        try
        {
            foreach (var entry in entries.Where(e => e.FullName.StartsWith(prefix, StringComparison.Ordinal)))
            {
                var relative = entry.FullName[prefix.Length..];
                var path = Path.GetFullPath(Path.Combine(target, relative));
                if (relative.Contains('\\') || Path.IsPathRooted(relative) || !path.StartsWith(target + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    throw new InvalidDataException($"压缩包里有不安全的路径：{entry.FullName}");
                }
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path, overwrite: false);
            }
            var problems = Validate(target, out var manifest);
            if (problems.Count > 0)
            {
                throw new InvalidDataException(string.Join("；", problems));
            }
            return (target, manifest!);
        }
        catch
        {
            Directory.Delete(target, recursive: true);
            throw;
        }
    }

    private static ZipArchive OpenZip(string zipPath)
    {
        try
        {
            return ZipFile.OpenRead(zipPath);
        }
        catch (Exception e) when (e is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException($"无法打开压缩包：{e.Message}");
        }
    }
}
