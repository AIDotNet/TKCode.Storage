using System.Diagnostics;
using System.Text.Json.Nodes;
using Island.Plugins.Host.Services;

namespace Island.Plugins.Host.Tests;

/// <summary>The status line bridge, against real files and the real shell (macOS / Linux).</summary>
public sealed class ClaudeStatusLineTests : IDisposable
{
    private readonly string _root = Directory.CreateTempSubdirectory("o_o-statusline-").FullName;
    private string ConfigDir => Path.Combine(_root, "config with space's");
    private string Settings => Path.Combine(ConfigDir, "settings.json");

    private static (int Code, string Output) RunShell(string command, string stdin)
    {
        var info = new ProcessStartInfo("sh") { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false };
        info.ArgumentList.Add("-c");
        info.ArgumentList.Add(command);
        using var p = Process.Start(info)!;
        p.StandardInput.Write(stdin);
        p.StandardInput.Close();
        var output = p.StandardOutput.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, output);
    }

    private const string Sample = """
        { "session_id": "s", "model": { "display_name": "Opus" },
          "rate_limits": { "five_hour": { "used_percentage": 23.5, "resets_at": 1767225600 },
                           "seven_day": { "used_percentage": 41.2, "resets_at": 1767744000 } } }
        """;

    [Fact]
    public void Install_keeps_the_previous_status_line_running_and_restore_puts_it_back_exactly()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Settings, """
            { "theme": "dark", // a comment Claude Code tolerates
              "statusLine": { "type": "command", "command": "printf 'mine:%s' \"$(cat | head -c 1)\"", "padding": 2 },
              "env": { "A": "1" } }
            """);
        var bridge = new ClaudeStatusLineBridge(Path.Combine(_root, "data"));
        Assert.False(bridge.IsInstalled(ConfigDir));

        bridge.Install(ConfigDir);
        bridge.Install(ConfigDir); // idempotent: never stacks the bridge on itself
        var installed = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.True(bridge.IsInstalled(ConfigDir));
        Assert.Equal("dark", installed["theme"]!.GetValue<string>());
        Assert.Equal(2, installed["statusLine"]!["padding"]!.GetValue<int>());   // other options kept
        Assert.Equal("1", installed["env"]!["A"]!.GetValue<string>());

        // Run exactly what Claude Code would run: the user's own status line still prints, and O_O gets the data.
        var (code, output) = RunShell(installed["statusLine"]!["command"]!.GetValue<string>(), Sample);
        Assert.Equal(0, code);
        Assert.Equal("mine:{", output);
        var usage = bridge.Read(ConfigDir)!;
        Assert.Equal(23.5, usage.FiveHour!.UsedPercentage);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1767225600), usage.FiveHour.ResetsAt);
        Assert.Equal(41.2, usage.SevenDay!.UsedPercentage);

        bridge.Restore(ConfigDir);
        var restored = JsonNode.Parse(File.ReadAllText(Settings))!;
        Assert.False(bridge.IsInstalled(ConfigDir));
        Assert.Equal("printf 'mine:%s' \"$(cat | head -c 1)\"", restored["statusLine"]!["command"]!.GetValue<string>());
        Assert.Equal(2, restored["statusLine"]!["padding"]!.GetValue<int>());
        Assert.Null(bridge.Read(ConfigDir));
    }

    [Fact]
    public void Without_a_previous_status_line_install_prints_nothing_and_restore_removes_the_key()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var bridge = new ClaudeStatusLineBridge(Path.Combine(_root, "data"));
        bridge.Install(ConfigDir); // no settings.json yet: created
        var command = JsonNode.Parse(File.ReadAllText(Settings))!["statusLine"]!["command"]!.GetValue<string>();
        Assert.Equal((0, ""), RunShell(command, """{ "session_id": "s" }"""));
        var usage = bridge.Read(ConfigDir)!;                  // ran, but no rate_limits (not a subscriber yet)
        Assert.Null(usage.FiveHour);

        bridge.Restore(ConfigDir);
        Assert.Null(JsonNode.Parse(File.ReadAllText(Settings))!["statusLine"]);
    }

    [Fact]
    public void A_settings_file_that_cannot_be_parsed_is_never_touched()
    {
        Directory.CreateDirectory(ConfigDir);
        File.WriteAllText(Settings, "{ this is not json");
        var bridge = new ClaudeStatusLineBridge(Path.Combine(_root, "data"));
        Assert.Throws<InvalidOperationException>(() => bridge.Install(ConfigDir));
        Assert.Equal("{ this is not json", File.ReadAllText(Settings));
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);
}
