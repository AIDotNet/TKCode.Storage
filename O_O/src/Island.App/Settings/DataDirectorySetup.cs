using System.Diagnostics;
using Island.Core.Storage;

namespace Island.App.Settings;

/// <summary>
/// The data directory of this run (控制台 → 关于 → 数据目录, see <see cref="DataLocation"/>): settled before the app is
/// built — finishing a move the About page asked for — and changed by moving the data and starting O_O again.
/// </summary>
internal static class DataDirectorySetup
{
    /// <summary>
    /// Sets <see cref="AppSettings.DataRoot"/>: moves the data first when a move is pending; falls back to the default
    /// folder for this run when the chosen one cannot be used (a removed drive). Runs before anything opens the data.
    /// </summary>
    public static void Settle(AppSettings settings)
    {
        var defaultRoot = AppSettings.DefaultRoot;
        var current = settings.DataDirectory is { Length: > 0 } chosen ? chosen : defaultRoot;
        if (settings.PendingDataDirectory is { Length: > 0 } target)
        {
            settings.PendingDataDirectory = null;
            var problem = DataLocation.Problem(current, target, defaultRoot) ?? DataLocation.Copy(current, target, defaultRoot);
            if (problem is null)
            {
                settings.DataDirectory = DataLocation.SamePath(target, defaultRoot) ? null : target;
                settings.Write();
                DataLocation.DeleteOld(current, defaultRoot);
                current = target;
                Notice = $"数据已移动到 {target}。";
            }
            else
            {
                settings.Write();
                Notice = $"未能把数据移动到 {target}：{problem} 数据仍在原来的位置。";
            }
        }
        try
        {
            Directory.CreateDirectory(current);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            Notice = $"数据目录 {current} 无法使用（{e.Message}），本次运行使用默认目录。";
            current = defaultRoot;
        }
        AppSettings.DataRoot = current;
    }

    /// <summary>What happened to the data directory at this start (a move done or failed), or null.</summary>
    public static string? Notice { get; private set; }

    /// <summary>
    /// Moves the data to <paramref name="chosen"/> (see <see cref="DataLocation.Target"/>): checks it can, records the
    /// move, starts O_O again and quits; the new O_O does the move. Returns why it cannot, or null when O_O is restarting.
    /// </summary>
    public static string? MoveAndRestart(string chosen)
    {
        var defaultRoot = AppSettings.DefaultRoot;
        var target = DataLocation.Target(chosen, defaultRoot);
        if (DataLocation.Problem(AppSettings.DataRoot, target, defaultRoot) is { } problem)
        {
            return problem;
        }
        var settings = AppSettings.Current;
        settings.PendingDataDirectory = target;
        settings.Save();
        if (!Relaunch())
        {
            settings.PendingDataDirectory = null;
            settings.Save();
            return "无法重新启动 O_O，数据没有移动。";
        }
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return null;
    }

    /// <summary>Starts another O_O that waits for this one to quit, then opens its console.</summary>
    private static bool Relaunch()
    {
        if (Environment.ProcessPath is not { } exe)
        {
            return false;
        }
        string[] args = ["--after", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture), "--console"];
        ProcessStartInfo info;
        if (OperatingSystem.IsMacOS() && Island.Core.Updates.UpdatePlan.MacBundle(exe) is { } bundle)
        {
            info = new ProcessStartInfo("/usr/bin/open") { ArgumentList = { "-n", bundle, "--args" } };
        }
        else
        {
            info = new ProcessStartInfo(exe);
            if (Path.GetFileNameWithoutExtension(exe).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                info.ArgumentList.Add(typeof(DataDirectorySetup).Assembly.Location); // started as `dotnet O_O.dll`
            }
        }
        foreach (var a in args)
        {
            info.ArgumentList.Add(a);
        }
        info.UseShellExecute = false;
        try
        {
            Process.Start(info)?.Dispose();
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
