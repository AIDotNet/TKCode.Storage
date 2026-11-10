using System.Diagnostics;
using Island.Core.Updates;

namespace Island.UI.Services;

/// <summary>
/// Downloads a newer O_O and installs it when O_O quits or restarts (docs/design/08-updates-and-catalog.md):
/// <list type="bullet">
/// <item>macOS: the .dmg is opened, the app in it must be signed by the same team as this one, and a small script swaps
/// it in for this app once O_O has quit.</item>
/// <item>Windows: the per-user installer runs silently once O_O has quit.</item>
/// <item>Linux (a .deb needs root): not installed here — the 关于 page keeps offering the download.</item>
/// </list>
/// Only from GitHub's release hosts, at most <see cref="UpdatePlan.MaxDownloadBytes"/>.
/// </summary>
public sealed class UpdateInstaller(string dataDirectory, HttpClient http)
{
    private readonly string _root = Path.Combine(dataDirectory, "updates");

    /// <summary>The version downloaded and ready to install, or null.</summary>
    public string? ReadyVersion { get; private set; }

    /// <summary>What went wrong last, for the 关于 page; null when nothing did.</summary>
    public string? Problem { get; private set; }

    private string? _staged;   // macOS: the new app, verified and copied out of the .dmg; Windows: the installer

    /// <summary>Whether this build can install an update by itself (macOS from an .app, Windows; not Linux, not a dev build).</summary>
    public static bool Supported =>
        (OperatingSystem.IsMacOS() && Environment.ProcessPath is { } exe && UpdatePlan.MacBundle(exe) is not null) ||
        OperatingSystem.IsWindows();

    /// <summary>Downloads <paramref name="latest"/>'s installer for this platform and gets it ready to install.</summary>
    /// <returns>Whether it is ready.</returns>
    public async Task<bool> DownloadAsync(VersionInfo latest, CancellationToken ct)
    {
        if (ReadyVersion == latest.Version)
        {
            return true;
        }
        Problem = null;
        if (latest.Download is not { Length: > 0 } link || !Uri.TryCreate(link, UriKind.Absolute, out var url) || !UpdatePlan.IsTrustedDownload(url))
        {
            Problem = "这个版本没有可自动安装的安装包";
            return false;
        }
        var directory = Path.Combine(_root, latest.Version);
        try
        {
            if (Directory.Exists(_root))
            {
                Directory.Delete(_root, recursive: true); // older downloads
            }
            Directory.CreateDirectory(directory);
            var file = Path.Combine(directory, UpdatePlan.FileName(url));
            await DownloadFileAsync(url, file, ct).ConfigureAwait(false);
            _staged = OperatingSystem.IsMacOS() ? await PrepareMacAsync(file, directory, ct).ConfigureAwait(false) : file;
            if (_staged is null)
            {
                return false;
            }
            ReadyVersion = latest.Version;
            return true;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidDataException)
        {
            Problem = $"下载新版本失败：{e.Message}";
            return false;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            Problem = "下载新版本超时";
            return false;
        }
    }

    private async Task DownloadFileAsync(Uri url, string file, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        // Redirects end on GitHub's storage; anywhere else is refused.
        if (response.RequestMessage?.RequestUri is { } final && !UpdatePlan.IsTrustedDownload(final))
        {
            throw new InvalidDataException("下载地址不可信");
        }
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > UpdatePlan.MaxDownloadBytes)
        {
            throw new InvalidDataException("安装包太大");
        }
        var partial = file + ".part";
        await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
        await using (var target = File.Create(partial))
        {
            var buffer = new byte[81920];
            long total = 0;
            int read;
            while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                total += read;
                if (total > UpdatePlan.MaxDownloadBytes)
                {
                    throw new InvalidDataException("安装包太大");
                }
                await target.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            }
        }
        File.Move(partial, file, overwrite: true);
    }

    /// <summary>
    /// Opens the .dmg, checks that its app is signed (deeply, strictly) by the same team as this app, and copies it out.
    /// Null, with <see cref="Problem"/> set, when any of that fails.
    /// </summary>
    private async Task<string?> PrepareMacAsync(string dmg, string directory, CancellationToken ct)
    {
        if (Environment.ProcessPath is not { } exe || UpdatePlan.MacBundle(exe) is not { } current)
        {
            Problem = "不是从安装包运行的 O_O，不能自动更新";
            return null;
        }
        var ourTeam = UpdatePlan.TeamIdentifier((await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", current], ct).ConfigureAwait(false)).Output);
        if (ourTeam is null)
        {
            Problem = "当前的 O_O 没有签名，不能自动更新";
            return null;
        }
        var mount = Path.Combine(directory, "mnt");
        Directory.CreateDirectory(mount);
        var attach = await RunAsync("/usr/bin/hdiutil", ["attach", "-nobrowse", "-readonly", "-noautoopen", "-mountpoint", mount, dmg], ct).ConfigureAwait(false);
        if (attach.Code != 0)
        {
            Problem = "打不开下载的安装包";
            return null;
        }
        try
        {
            if (Directory.GetDirectories(mount, "*.app").FirstOrDefault() is not { } app)
            {
                Problem = "安装包里没有 O_O";
                return null;
            }
            var verify = await RunAsync("/usr/bin/codesign", ["--verify", "--deep", "--strict", app], ct).ConfigureAwait(false);
            var team = UpdatePlan.TeamIdentifier((await RunAsync("/usr/bin/codesign", ["-dv", "--verbose=2", app], ct).ConfigureAwait(false)).Output);
            if (verify.Code != 0 || team != ourTeam)
            {
                Problem = "新版本的签名和当前的不一致，没有安装";
                return null;
            }
            var staged = Path.Combine(directory, Path.GetFileName(app));
            if ((await RunAsync("/usr/bin/ditto", [app, staged], ct).ConfigureAwait(false)).Code != 0)
            {
                Problem = "无法准备新版本";
                return null;
            }
            return staged;
        }
        finally
        {
            await RunAsync("/usr/bin/hdiutil", ["detach", mount, "-quiet", "-force"], CancellationToken.None).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Hands the ready update to a helper that installs it once O_O has quit, and (with <paramref name="restart"/>)
    /// starts O_O again. The caller quits O_O right after. False when nothing is ready or it cannot be installed here.
    /// </summary>
    public bool InstallOnExit(bool restart)
    {
        if (ReadyVersion is null || _staged is null || Environment.ProcessPath is not { } exe)
        {
            return false;
        }
        try
        {
            var pid = Environment.ProcessId;
            if (OperatingSystem.IsMacOS() && UpdatePlan.MacBundle(exe) is { } bundle)
            {
                if (!CanWrite(Path.GetDirectoryName(bundle)!))
                {
                    Problem = $"没有权限替换 {bundle}";
                    return false;
                }
                var script = Path.Combine(_root, "install.sh");
                File.WriteAllText(script, UpdatePlan.MacSwapScript(pid, bundle, _staged, Path.GetDirectoryName(_staged)!, restart));
                Detach("/bin/sh", [script]);
                return true;
            }
            if (OperatingSystem.IsWindows())
            {
                var script = Path.Combine(_root, "install.ps1");
                File.WriteAllText(script, UpdatePlan.WindowsInstallScript(pid, _staged, exe, restart));
                Detach("powershell.exe", ["-NoProfile", "-ExecutionPolicy", "Bypass", "-WindowStyle", "Hidden", "-File", script]);
                return true;
            }
            return false;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Problem = $"无法安装新版本：{e.Message}";
            return false;
        }
    }

    private static bool CanWrite(string directory)
    {
        try
        {
            var probe = Path.Combine(directory, $".o_o-update-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "");
            File.Delete(probe);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Starts a helper that outlives O_O.</summary>
    private static void Detach(string program, IEnumerable<string> arguments)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }
        Process.Start(info)?.Dispose();
    }

    private static async Task<(int Code, string Output)> RunAsync(string program, IEnumerable<string> arguments, CancellationToken ct)
    {
        var info = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in arguments)
        {
            info.ArgumentList.Add(a);
        }
        try
        {
            using var p = Process.Start(info)!;
            var stdout = p.StandardOutput.ReadToEndAsync(ct);
            var stderr = p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct).ConfigureAwait(false);
            return (p.ExitCode, await stdout.ConfigureAwait(false) + await stderr.ConfigureAwait(false)); // codesign -dv writes to stderr
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return (-1, "");
        }
    }
}
