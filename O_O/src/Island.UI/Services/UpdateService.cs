using System.Text.Json;
using Avalonia.Threading;
using Island.Core.Updates;

namespace Island.UI.Services;

/// <summary>
/// Asks the O_O service whether a newer O_O exists (docs/design/08-updates-and-catalog.md): a little after start and
/// then once a day, when the user lets it (关于 → 自动检查更新), and whenever they press 检查更新. With 自动下载并安装更新
/// (on by default, where <see cref="UpdateInstaller.Supported"/>) a newer version is downloaded by itself and installed
/// when O_O quits, or at once with 立即重启并更新; the island says so once per version. Otherwise it tells about the
/// version and the 关于 page offers the download. Network problems stay quiet — they show only on the 关于 page.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly Action<VersionInfo, bool>? _announce;
    private bool _installing;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    /// <param name="announce">Shows a newer version on the island (called on the UI thread, once per version); the flag
    /// says whether it is downloaded and will install when O_O quits.</param>
    public UpdateService(string dataDirectory, Action<VersionInfo, bool>? announce, HttpClient? http = null)
    {
        Preferences = new UpdatePreferences(Path.Combine(dataDirectory, "updates.json"));
        _announce = announce;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"O_O/{Current}");
        var downloads = new HttpClient { Timeout = TimeSpan.FromMinutes(15) };
        downloads.DefaultRequestHeaders.UserAgent.ParseAdd($"O_O/{Current}");
        Installer = new UpdateInstaller(dataDirectory, downloads);
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Interval;
            if (Preferences.AutoCheck)
            {
                await CheckAsync();
            }
        };
    }

    /// <summary>The service the console's 关于 page shows; set by the app.</summary>
    public static UpdateService? Shared { get; set; }

    public UpdatePreferences Preferences { get; }

    public AppVersion Current { get; } = AppVersion.Current;

    /// <summary>The last answer, or null before the first one.</summary>
    public VersionInfo? Latest { get; private set; }

    /// <summary>Why the last check failed, for the 关于 page; null when it worked.</summary>
    public string? Problem { get; private set; }

    public DateTimeOffset? CheckedAt { get; private set; }

    public bool IsChecking => _checking;

    public bool UpdateAvailable => Latest is { } latest && latest.IsNewerThan(Current);

    /// <summary>Downloads and installs updates (where it can).</summary>
    public UpdateInstaller Installer { get; }

    /// <summary>A newer version is being downloaded.</summary>
    public bool IsDownloading { get; private set; }

    /// <summary>The newer version is downloaded and installs when O_O quits (or with <see cref="RestartToUpdate"/>).</summary>
    public bool Ready => Latest is { } latest && UpdateAvailable && Installer.ReadyVersion == latest.Version;

    /// <summary>Whether this O_O downloads and installs updates by itself.</summary>
    public bool Automatic => Preferences.AutoInstall && UpdateInstaller.Supported;

    /// <summary>Downloads the newer version now (the 关于 page's button when automatic updates are off).</summary>
    public async Task DownloadAsync()
    {
        if (Latest is not { } latest || !UpdateAvailable || IsDownloading || !UpdateInstaller.Supported)
        {
            return;
        }
        IsDownloading = true;
        Changed?.Invoke();
        try
        {
            if (await Installer.DownloadAsync(latest, CancellationToken.None) && Preferences.AnnouncedVersion != $"{latest.Version}+ready")
            {
                Preferences.SetAnnounced($"{latest.Version}+ready");
                Dispatcher.UIThread.Post(() => _announce?.Invoke(latest, true));
            }
        }
        finally
        {
            IsDownloading = false;
            Dispatcher.UIThread.Post(() => Changed?.Invoke());
        }
    }

    /// <summary>Called as O_O quits: installs a downloaded update (O_O is not started again).</summary>
    public void InstallOnQuit()
    {
        if (!_installing && Ready && Preferences.AutoInstall)
        {
            _installing = Installer.InstallOnExit(restart: false);
        }
    }

    /// <summary>立即重启并更新: installs the downloaded update and starts the new O_O; false when it cannot (see the installer's problem).</summary>
    public bool RestartToUpdate()
    {
        if (_installing || !Ready || !Installer.InstallOnExit(restart: true))
        {
            Changed?.Invoke();
            return false;
        }
        _installing = true;
        (Avalonia.Application.Current?.ApplicationLifetime as Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime)?.Shutdown();
        return true;
    }

    /// <summary>Raised (on the UI thread) when a check starts or ends.</summary>
    public event Action? Changed;

    public void Start() => _timer.Start();

    public async Task CheckAsync()
    {
        if (_checking)
        {
            return;
        }
        _checking = true;
        Changed?.Invoke();
        try
        {
            var url = ServiceEndpoints.Version(ServiceEndpoints.BuildPlatform, Current.ToString());
            if (!ServiceEndpoints.IsAllowed(url))
            {
                throw new HttpRequestException("not https");
            }
            var json = await _http.GetStringAsync(url);
            Latest = ServiceContract.ParseVersion(json);
            Problem = null;
            CheckedAt = DateTimeOffset.Now;
            if (UpdateAvailable && Automatic)
            {
                _ = DownloadAsync(); // the island is told once it is ready
            }
            else if (UpdateAvailable && Latest.Version != Preferences.AnnouncedVersion)
            {
                Preferences.SetAnnounced(Latest.Version);
                _announce?.Invoke(Latest, false);
            }
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or JsonException)
        {
            Problem = "无法连接更新服务";
        }
        finally
        {
            _checking = false;
            Changed?.Invoke();
        }
    }

    public void Dispose() => _timer.Stop();
}
