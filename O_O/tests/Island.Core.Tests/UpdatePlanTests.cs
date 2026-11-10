using System.Diagnostics;
using Island.Core.Updates;

namespace Island.Core.Tests;

/// <summary>The automatic update's decisions (docs/design/08-updates-and-catalog.md).</summary>
public sealed class UpdatePlanTests
{
    [Theory]
    [InlineData("https://github.com/AIDotNet/TKCode.Storage/releases/download/o_o-v0.2.1/O_O-0.2.1-macos-arm64.dmg", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1/2", true)]
    [InlineData("http://github.com/x.dmg", false)]
    [InlineData("https://evil.example.com/O_O.dmg", false)]
    [InlineData("https://github.com.evil.example/O_O.dmg", false)]
    [InlineData("http://127.0.0.1:5080/O_O.dmg", true)]
    public void Installers_come_only_from_githubs_release_hosts(string url, bool trusted) =>
        Assert.Equal(trusted, UpdatePlan.IsTrustedDownload(new Uri(url)));

    [Fact]
    public void The_file_bundle_and_team_are_read_from_their_sources()
    {
        Assert.Equal("O_O-0.2.1-macos-arm64.dmg", UpdatePlan.FileName(new Uri("https://github.com/a/b/releases/download/o_o-v0.2.1/O_O-0.2.1-macos-arm64.dmg")));
        Assert.Equal("O_O-update", UpdatePlan.FileName(new Uri("https://github.com/")));
        Assert.Equal("/Applications/O_O.app", UpdatePlan.MacBundle("/Applications/O_O.app/Contents/MacOS/O_O"));
        Assert.Null(UpdatePlan.MacBundle("/Users/me/code/O_O/src/Island.App/bin/Debug/net10.0/O_O"));
        Assert.Equal("42B564FHYL", UpdatePlan.TeamIdentifier("Executable=/x\nAuthority=Developer ID Application: me (42B564FHYL)\nTeamIdentifier=42B564FHYL\n"));
        Assert.Null(UpdatePlan.TeamIdentifier("Signature=adhoc\nTeamIdentifier=not set\n"));
    }

    [Fact]
    public void The_scripts_quote_paths_and_restart_only_when_asked()
    {
        var mac = UpdatePlan.MacSwapScript(42, "/Applications/O'O.app", "/tmp/new/O_O.app", "/tmp/new", restart: false);
        Assert.Contains("kill -0 42", mac);
        Assert.Contains("BUNDLE='/Applications/O'\\''O.app'", mac);
        Assert.DoesNotContain("/usr/bin/open", mac);
        Assert.Contains("/usr/bin/open \"$BUNDLE\"", UpdatePlan.MacSwapScript(42, "/a.app", "/b.app", "/c", restart: true));
        var win = UpdatePlan.WindowsInstallScript(7, @"C:\Users\it's\setup.exe", @"C:\O_O\O_O.exe", restart: true);
        Assert.Contains("Wait-Process -Id 7", win);
        Assert.Contains(@"'C:\Users\it''s\setup.exe'", win);
        Assert.Contains("/VERYSILENT", win);
        Assert.Contains(@"Start-Process -FilePath 'C:\O_O\O_O.exe'", win);
        Assert.DoesNotContain("O_O.exe", UpdatePlan.WindowsInstallScript(7, "s.exe", @"C:\O_O\O_O.exe", restart: false));
    }

    [Fact]
    public void On_macos_the_swap_waits_for_the_app_to_quit_then_replaces_it()
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }
        var root = Directory.CreateTempSubdirectory("o_o-swap-").FullName;
        try
        {
            var bundle = Path.Combine(root, "Applications", "O_O.app");
            var stage = Path.Combine(root, "stage");
            var staged = Path.Combine(stage, "O_O.app");
            Directory.CreateDirectory(Path.Combine(bundle, "Contents"));
            File.WriteAllText(Path.Combine(bundle, "Contents", "version"), "old");
            Directory.CreateDirectory(Path.Combine(staged, "Contents"));
            File.WriteAllText(Path.Combine(staged, "Contents", "version"), "new");

            using var app = Process.Start(new ProcessStartInfo("/bin/sleep", "1") { UseShellExecute = false })!;
            var script = Path.Combine(root, "install.sh");
            File.WriteAllText(script, UpdatePlan.MacSwapScript(app.Id, bundle, staged, stage, restart: false));
            using var swap = Process.Start(new ProcessStartInfo("/bin/sh", script) { UseShellExecute = false })!;
            Thread.Sleep(300);
            Assert.Equal("old", File.ReadAllText(Path.Combine(bundle, "Contents", "version"))); // still running: untouched
            Assert.True(swap.WaitForExit(10_000));
            Assert.Equal("new", File.ReadAllText(Path.Combine(bundle, "Contents", "version")));
            Assert.False(Directory.Exists(bundle + ".update-old"));
            Assert.False(Directory.Exists(stage));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Automatic_install_is_on_by_default_and_saved()
    {
        var path = Path.Combine(Path.GetTempPath(), $"o_o-updates-{Guid.NewGuid():N}.json");
        try
        {
            var prefs = new UpdatePreferences(path);
            Assert.True(prefs.AutoInstall);
            prefs.SetAutoInstall(false);
            Assert.False(new UpdatePreferences(path).AutoInstall);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
