using System.Text.Json;
using Avalonia.Threading;
using Island.Core.Updates;

namespace Island.UI.Services;

/// <summary>
/// Asks the O_O service whether a newer O_O exists (docs/design/08-updates-and-catalog.md): a little after start and
/// then once a day, when the user lets it (关于 → 自动检查更新), and whenever they press 检查更新. It never downloads or
/// installs anything: it tells (once per version, on the island) and offers the download link on the 关于 page.
/// Network problems stay quiet — they show only on the 关于 page.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    private readonly HttpClient _http;
    private readonly Action<VersionInfo>? _announce;
    private readonly DispatcherTimer _timer;
    private bool _checking;

    /// <param name="announce">Shows a newer version on the island (called on the UI thread, once per version).</param>
    public UpdateService(string dataDirectory, Action<VersionInfo>? announce, HttpClient? http = null)
    {
        Preferences = new UpdatePreferences(Path.Combine(dataDirectory, "updates.json"));
        _announce = announce;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"O_O/{Current}");
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
            if (UpdateAvailable && Latest.Version != Preferences.AnnouncedVersion)
            {
                Preferences.SetAnnounced(Latest.Version);
                _announce?.Invoke(Latest);
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
