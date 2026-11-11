namespace Island.Core.Storage;

/// <summary>
/// Where O_O keeps its data, and moving it somewhere else (控制台 → 关于 → 数据目录). The settings file and the instance lock
/// always stay in the default folder (<c>&lt;application data&gt;/O_O</c>): they say where the rest is. Everything else —
/// plugin data, user plugins, preferences, clipboard images — lives in the data directory, which is the default folder
/// unless the user picked another one (when installing, or later in the app).
/// <para>A move is decided in the app but done at the next start, before anything has the files open: the app records
/// the target, starts again, and the new process copies the data across, switches to it and deletes the old copy. A
/// move that fails leaves the data where it was. Pure file work, so it is tested without the app.</para>
/// </summary>
public static class DataLocation
{
    /// <summary>The name of the folder made inside a chosen folder that already holds other files.</summary>
    public const string FolderName = "O_O";

    /// <summary>The settings file, which stays in the default folder.</summary>
    public const string SettingsFile = "settings.json";

    private static StringComparison PathComparison =>
        OperatingSystem.IsWindows() || OperatingSystem.IsMacOS() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    /// <summary>
    /// Whether <paramref name="name"/> (a file or folder directly in the default folder) stays there when the data moves:
    /// the settings file, instance locks and the downloaded updates.
    /// </summary>
    public static bool StaysInDefault(string name) =>
        name.Equals(SettingsFile, StringComparison.OrdinalIgnoreCase) ||
        name.EndsWith(".lock", StringComparison.OrdinalIgnoreCase) ||
        name.Equals("updates", StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether two paths name the same folder.</summary>
    public static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), PathComparison);

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or somewhere inside it.</summary>
    public static bool IsInside(string path, string folder)
    {
        var p = Normalize(path);
        var f = Normalize(folder);
        return string.Equals(p, f, PathComparison) ||
               p.StartsWith(f.EndsWith(Path.DirectorySeparatorChar) ? f : f + Path.DirectorySeparatorChar, PathComparison);
    }

    private static string Normalize(string path)
    {
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full) ?? "";
        return full.Length > root.Length ? full.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) : full;
    }

    /// <summary>
    /// The folder the data moves to when the user picks <paramref name="chosen"/>: the folder itself when it is empty
    /// or does not exist yet (or is the default folder), otherwise a new <see cref="FolderName"/> folder inside it — so
    /// picking <c>D:\</c> gives <c>D:\O_O</c> and never mixes O_O's files with others.
    /// </summary>
    public static string Target(string chosen, string defaultRoot)
    {
        var full = Path.GetFullPath(chosen);
        if (SamePath(full, defaultRoot) || !Directory.Exists(full) || !Directory.EnumerateFileSystemEntries(full).Any())
        {
            return full;
        }
        return Path.Combine(full, FolderName);
    }

    /// <summary>
    /// Why the data cannot move from <paramref name="current"/> to <paramref name="target"/> (a <see cref="Target"/>),
    /// or null when it can.
    /// </summary>
    public static string? Problem(string current, string target, string defaultRoot)
    {
        if (!Path.IsPathFullyQualified(target))
        {
            return "请选择一个完整的路径。";
        }
        if (SamePath(current, target))
        {
            return "数据已经在这个目录里了。";
        }
        if (IsInside(target, current))
        {
            return "新目录不能在当前数据目录里面。";
        }
        if (IsInside(current, target) && !SamePath(target, defaultRoot))
        {
            return "新目录不能包含当前数据目录。";
        }
        if (Directory.Exists(target) &&
            Directory.EnumerateFileSystemEntries(target).Any(e => !(SamePath(target, defaultRoot) && StaysInDefault(Path.GetFileName(e)))))
        {
            return $"{target} 里已经有文件了，请选择一个空目录。";
        }
        try
        {
            Directory.CreateDirectory(target);
            var probe = Path.Combine(target, $".o_o-probe-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return $"无法写入 {target}：{e.Message}";
        }
        return null;
    }

    /// <summary>
    /// Copies the data from <paramref name="from"/> to <paramref name="to"/> (leaving behind, in the default folder,
    /// what stays there). Returns null when everything was copied, or the problem; on a problem, what was copied so far
    /// is removed again and <paramref name="from"/> is untouched. The caller switches to <paramref name="to"/> and then
    /// calls <see cref="DeleteOld"/>.
    /// </summary>
    public static string? Copy(string from, string to, string defaultRoot)
    {
        var copied = new List<string>();
        try
        {
            Directory.CreateDirectory(to);
            if (!Directory.Exists(from))
            {
                return null; // nothing stored yet
            }
            foreach (var entry in Entries(from, defaultRoot))
            {
                var destination = Path.Combine(to, Path.GetFileName(entry));
                if (File.Exists(destination) || Directory.Exists(destination))
                {
                    throw new IOException($"{destination} 已存在");
                }
                copied.Add(destination);
                if (Directory.Exists(entry))
                {
                    CopyDirectory(entry, destination);
                }
                else
                {
                    File.Copy(entry, destination);
                }
            }
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            foreach (var path in copied)
            {
                TryDelete(path);
            }
            return e.Message;
        }
    }

    /// <summary>Deletes the old copy once the data lives in its new folder. Best effort: leftovers are harmless.</summary>
    public static void DeleteOld(string from, string defaultRoot)
    {
        if (!Directory.Exists(from))
        {
            return;
        }
        foreach (var entry in Entries(from, defaultRoot))
        {
            TryDelete(entry);
        }
        if (!SamePath(from, defaultRoot))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(from).Any())
                {
                    Directory.Delete(from);
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The empty folder stays: harmless.
            }
        }
    }

    /// <summary>What moves out of <paramref name="from"/>: everything, except what stays in the default folder.</summary>
    private static IEnumerable<string> Entries(string from, string defaultRoot) =>
        Directory.EnumerateFileSystemEntries(from)
            .Where(e => !(SamePath(from, defaultRoot) && StaysInDefault(Path.GetFileName(e))))
            .ToList();

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
        }
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Left behind: harmless.
        }
    }
}
