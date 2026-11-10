using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Island.Plugins.Host.Services;

/// <summary>One plan-usage window from Claude Code's status line.</summary>
public sealed record StatusLineWindow(double UsedPercentage, DateTimeOffset? ResetsAt);

/// <summary>What the last status-line refresh of a Claude Code configuration reported.</summary>
/// <param name="UpdatedAt">When Claude Code last refreshed its status line (data is only as fresh as that).</param>
public sealed record StatusLineUsage(StatusLineWindow? FiveHour, StatusLineWindow? SevenDay, DateTimeOffset UpdatedAt);

/// <summary>
/// Claude Code's documented status line (https://code.claude.com/docs/en/statusline) receives the plan's usage
/// limits as <c>rate_limits.five_hour</c> / <c>rate_limits.seven_day</c> (<c>used_percentage</c>, <c>resets_at</c>) on
/// stdin. O_O reads them through a small bridge script set as the status line command: it saves that JSON for O_O and
/// then runs the status line command that was configured before, so the user's status line looks the same.
/// No token is read and no undocumented endpoint is called.
/// <para>
/// Installing edits <c>&lt;config dir&gt;/settings.json</c>: the previous <c>statusLine</c> is kept in a backup and
/// restored exactly by <see cref="Restore"/>; a settings file that cannot be parsed is never touched.
/// </para>
/// </summary>
public sealed class ClaudeStatusLineBridge(string dataDirectory)
{
    /// <summary>Appears in the installed command, so an installation is recognised (and never stacked twice).</summary>
    public const string Marker = "o_o-statusline";

    public string Directory => Path.Combine(dataDirectory, "claude-statusline");

    public string ScriptPath => Path.Combine(Directory, OperatingSystem.IsWindows() ? $"{Marker}.ps1" : $"{Marker}.sh");

    public static string Hash(string configDir) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(configDir)))[..8];

    public string OutputPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.json");

    private string OriginalCommandPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.original");

    private string BackupPath(string configDir) => Path.Combine(Directory, $"{Hash(configDir)}.statusline-backup.json");

    private static string SettingsPath(string configDir) => Path.Combine(configDir, "settings.json");

    public bool IsInstalled(string configDir) =>
        ReadSettings(configDir) is { } settings &&
        settings["statusLine"]?["command"]?.GetValue<string>() is { } command &&
        command.Contains(Marker, StringComparison.Ordinal);

    /// <summary>Sets the bridge as the status line of <paramref name="configDir"/>, keeping what was there.</summary>
    /// <exception cref="InvalidOperationException">settings.json exists but cannot be parsed; nothing was changed.</exception>
    public void Install(string configDir)
    {
        var path = SettingsPath(configDir);
        var settings = File.Exists(path)
            ? ReadSettings(configDir) ?? throw new InvalidOperationException($"无法解析 {path}，为避免损坏它，没有做任何修改。")
            : new JsonObject();
        if (IsInstalled(configDir))
        {
            return;
        }
        System.IO.Directory.CreateDirectory(Directory);
        WriteScript();

        var previous = settings["statusLine"];
        File.WriteAllText(BackupPath(configDir), previous?.ToJsonString() ?? "null");
        if (File.Exists(path))
        {
            File.Copy(path, Path.Combine(Directory, $"{Hash(configDir)}.settings.json.bak"), overwrite: true);
        }
        var originalCommand = previous?["type"]?.GetValue<string>() == "command" ? previous["command"]?.GetValue<string>() : null;
        if (!string.IsNullOrWhiteSpace(originalCommand))
        {
            File.WriteAllText(OriginalCommandPath(configDir), originalCommand);
        }
        else
        {
            File.Delete(OriginalCommandPath(configDir));
        }

        // Keep the previous status line's other options (padding and so on); only the command changes.
        var statusLine = previous is JsonObject old ? (JsonObject)old.DeepClone() : new JsonObject();
        statusLine["type"] = "command";
        statusLine["command"] = Command(configDir);
        settings["statusLine"] = statusLine;
        WriteSettings(configDir, settings);
    }

    /// <summary>Puts back the status line that was configured before <see cref="Install"/>.</summary>
    public void Restore(string configDir)
    {
        if (ReadSettings(configDir) is not { } settings || !IsInstalled(configDir))
        {
            return;
        }
        var backup = File.Exists(BackupPath(configDir)) ? JsonNode.Parse(File.ReadAllText(BackupPath(configDir))) : null;
        if (backup is null)
        {
            settings.Remove("statusLine");
        }
        else
        {
            settings["statusLine"] = backup;
        }
        WriteSettings(configDir, settings);
        foreach (var file in new[] { BackupPath(configDir), OriginalCommandPath(configDir), OutputPath(configDir) })
        {
            File.Delete(file);
        }
    }

    /// <summary>The usage the status line last reported, or null when it has not run since installation.</summary>
    public StatusLineUsage? Read(string configDir)
    {
        var path = OutputPath(configDir);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
            if (!doc.RootElement.TryGetProperty("rate_limits", out var limits))
            {
                // Claude Code sends rate_limits only to Pro / Max subscribers, after the session's first response.
                return new StatusLineUsage(null, null, File.GetLastWriteTimeUtc(path));
            }
            return new StatusLineUsage(Window(limits, "five_hour"), Window(limits, "seven_day"), File.GetLastWriteTimeUtc(path));
        }
        catch (JsonException)
        {
            return null; // caught mid-write by an older bridge; the next refresh fixes it
        }
    }

    private static StatusLineWindow? Window(JsonElement limits, string name)
    {
        if (!limits.TryGetProperty(name, out var w) || !w.TryGetProperty("used_percentage", out var used) ||
            !used.TryGetDouble(out var percent))
        {
            return null;
        }
        DateTimeOffset? resets = w.TryGetProperty("resets_at", out var r) && r.TryGetInt64(out var epoch)
            ? DateTimeOffset.FromUnixTimeSeconds(epoch)
            : null;
        return new StatusLineWindow(percent, resets);
    }

    /// <summary>The status line command for <paramref name="configDir"/>; paths are quoted for the shell.</summary>
    public string Command(string configDir)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"powershell -NoProfile -ExecutionPolicy Bypass -File \"{ScriptPath}\" \"{OutputPath(configDir)}\" \"{OriginalCommandPath(configDir)}\"";
        }
        static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";
        return $"sh {Quote(ScriptPath)} {Quote(OutputPath(configDir))} {Quote(OriginalCommandPath(configDir))}";
    }

    private void WriteScript()
    {
        var script = OperatingSystem.IsWindows()
            ? """
              # O_O bridge for Claude Code's status line (installed by O_O; restore it in the O_O console).
              # Saves the JSON Claude Code passes on stdin for O_O, then runs the status line configured before.
              param([string]$Out, [string]$Orig)
              $in = [Console]::In.ReadToEnd()
              [IO.File]::WriteAllText("$Out.tmp", $in)
              Move-Item -Force "$Out.tmp" $Out
              if ($Orig -and (Test-Path $Orig)) { $in | & cmd /c (Get-Content -Raw $Orig) }
              """
            : """
              #!/bin/sh
              # O_O bridge for Claude Code's status line (installed by O_O; restore it in the O_O console).
              # Saves the JSON Claude Code passes on stdin for O_O, then runs the status line configured before.
              out="$1"
              orig="$2"
              input=$(cat)
              printf '%s' "$input" > "$out.tmp" && mv -f "$out.tmp" "$out"
              if [ -s "$orig" ]; then
                printf '%s' "$input" | sh -c "$(cat "$orig")"
              fi
              """;
        File.WriteAllText(ScriptPath, script.Replace("\r\n", "\n"));
    }

    private static JsonObject? ReadSettings(string configDir)
    {
        var path = SettingsPath(configDir);
        if (!File.Exists(path))
        {
            return null;
        }
        try
        {
            return JsonNode.Parse(File.ReadAllText(path), documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            }) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static void WriteSettings(string configDir, JsonObject settings)
    {
        var path = SettingsPath(configDir);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            settings.WriteTo(w);
        }
        System.IO.Directory.CreateDirectory(configDir);
        File.WriteAllBytes(path + ".tmp", buffer.ToArray());
        File.Move(path + ".tmp", path, overwrite: true);
    }
}
