using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace Island.Platform.MacOS;

/// <summary>
/// Notifications and unread counts on macOS (docs/design/08-notifications-and-satellites.md §6).
/// Content: the notification centre's SQLite database, read with /usr/bin/sqlite3 (needs Full Disk Access).
/// Badge: the Dock tile's <c>AXStatusLabel</c> through System Events (needs Accessibility).
/// Icons: the app's .icns converted with /usr/bin/sips. Everything is done by spawning system tools with argument
/// lists; nothing is spliced into a script and nothing uses reflection.
/// </summary>
public sealed partial class MacNotificationListener : INotificationListener
{
    private const string Hint = "需要在 系统设置 → 隐私与安全性 → 完全磁盘访问权限 中允许 O_O，才能读取微信、QQ 的消息内容。";
    private static readonly TimeSpan DbPoll = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan BadgePoll = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BadgeRetry = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan AccessRecheck = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ToolTimeout = TimeSpan.FromSeconds(10);

    private static readonly Dictionary<string, string> KnownNames = new(StringComparer.Ordinal)
    {
        ["com.tencent.xinWeChat"] = "微信",
        ["com.tencent.qq"] = "QQ",
        ["com.tencent.WeWorkMac"] = "企业微信",
        ["com.apple.MobileSMS"] = "信息",
        ["ru.keepcoder.Telegram"] = "Telegram",
        ["com.apple.mail"] = "邮件",
        ["com.apple.iCal"] = "日历",
        ["com.apple.reminders"] = "提醒事项",
        ["com.apple.FaceTime"] = "FaceTime",
        ["com.tinyspeck.slackmacgap"] = "Slack",
        ["com.electron.lark"] = "飞书",
        ["com.alibaba.DingTalkMac"] = "钉钉",
        ["com.hnc.Discord"] = "Discord",
        ["net.whatsapp.WhatsApp"] = "WhatsApp",
    };

    private readonly object _gate = new();
    private readonly ConcurrentDictionary<string, string> _names = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string?> _appPaths = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<(string, int), byte[]?> _icons = new();
    private readonly ConcurrentDictionary<string, string[]> _dockNames = new(StringComparer.Ordinal);
    private readonly Dictionary<string, int?> _badges = new(StringComparer.Ordinal);
    private HashSet<string> _watched = new(StringComparer.Ordinal);
    private Timer? _dbTimer;
    private Timer? _badgeTimer;
    private int _dbBusy, _badgeBusy;
    private long _last = -1; // last rec_id seen; -1 = no baseline yet
    private string? _dbPath;
    private (long Db, long Wal, long DbLen, long WalLen) _stamp;
    private bool _disposed;
    private NotificationAccess _access;
    private DateTime _accessChecked = DateTime.MinValue;

    public NotificationListenerCapabilities Capabilities =>
        NotificationListenerCapabilities.Badge | NotificationListenerCapabilities.Icon
        | (Access == NotificationAccess.Granted ? NotificationListenerCapabilities.Content : 0);

    public NotificationAccess Access
    {
        get
        {
            lock (_gate)
            {
                if (DateTime.UtcNow - _accessChecked > AccessRecheck)
                {
                    _access = ProbeAccess();
                    _accessChecked = DateTime.UtcNow;
                }
                return _access;
            }
        }
    }

    public string? AccessHint => Access == NotificationAccess.Granted ? null : Hint;

    public event Action<PlatformNotification>? Received;
    public event Action<string, int?>? BadgeChanged;

    public void Watch(IReadOnlyCollection<string> appIds)
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }
            _watched = new HashSet<string>(appIds, StringComparer.Ordinal);
            _accessChecked = DateTime.MinValue;
            if (_watched.Count == 0)
            {
                StopTimers();
                _badges.Clear();
                _last = -1;
                return;
            }
            foreach (var k in _badges.Keys.Where(k => !_watched.Contains(k)).ToList())
            {
                _badges.Remove(k);
            }
            _dbTimer ??= new Timer(_ => PollDb(), null, TimeSpan.Zero, DbPoll);
            // A new watch list polls right away, also when an earlier failure had slowed the timer down.
            if (_badgeTimer is null)
            {
                _badgeTimer = new Timer(_ => PollBadges(), null, TimeSpan.Zero, BadgePoll);
            }
            else
            {
                _badgeTimer.Change(TimeSpan.Zero, BadgePoll);
            }
        }
    }

    // ---- content ----

    private static IEnumerable<string> CandidateDbPaths()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Path.Combine(home, "Library/Group Containers/group.com.apple.usernoted/db2/db");
        if (DarwinUserDir.Value is { } dir)
        {
            yield return Path.Combine(dir, "com.apple.notificationcenter/db2/db");
        }
    }

    // Fixed for the session; without it every access probe (while Full Disk Access is missing) spawned getconf.
    private static readonly Lazy<string?> DarwinUserDir = new(() =>
    {
        var dir = Run("/usr/bin/getconf", ["DARWIN_USER_DIR"], null, TimeSpan.FromSeconds(3))?.Stdout.Trim();
        return string.IsNullOrEmpty(dir) ? null : dir;
    });

    private NotificationAccess ProbeAccess()
    {
        foreach (var p in CandidateDbPaths())
        {
            try
            {
                using var f = new FileStream(p, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                _dbPath = p;
                return NotificationAccess.Granted;
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
        return NotificationAccess.NeedsPermission;
    }

    private void PollDb()
    {
        if (Interlocked.Exchange(ref _dbBusy, 1) == 1)
        {
            return;
        }
        try
        {
            HashSet<string> watched;
            lock (_gate)
            {
                if (_disposed || _watched.Count == 0)
                {
                    return;
                }
                watched = _watched;
            }
            if (Access != NotificationAccess.Granted || _dbPath is not { } db)
            {
                _last = -1; // re-baseline once access appears, so what arrived meanwhile is not replayed
                return;
            }
            if (_last < 0)
            {
                var r = Run("/usr/bin/sqlite3", ["-readonly", db, "select coalesce(max(rec_id),0) from record"], null, ToolTimeout);
                if (r is { ExitCode: 0 } && long.TryParse(r.Stdout.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var max))
                {
                    _last = max;
                    _stamp = Stamp(db);
                }
                return;
            }
            var stamp = Stamp(db);
            if (stamp == _stamp)
            {
                return;
            }
            _stamp = stamp;
            var sql = "select r.rec_id, a.identifier, hex(r.data), r.delivered_date from record r join app a on a.app_id=r.app_id where r.rec_id > "
                + _last.ToString(CultureInfo.InvariantCulture) + " order by r.rec_id";
            var q = Run("/usr/bin/sqlite3", ["-readonly", "-separator", "\t", db, sql], null, ToolTimeout);
            if (q is not { ExitCode: 0 })
            {
                return;
            }
            foreach (var line in q.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var cols = line.TrimEnd('\r').Split('\t');
                if (cols.Length < 3 || !long.TryParse(cols[0], NumberStyles.None, CultureInfo.InvariantCulture, out var id))
                {
                    continue;
                }
                _last = Math.Max(_last, id);
                if (!watched.Contains(cols[1]))
                {
                    continue;
                }
                var time = cols.Length > 3 && double.TryParse(cols[3], NumberStyles.Float, CultureInfo.InvariantCulture, out var cf)
                    ? NotificationRecordParser.FromCfAbsoluteTime(cf)
                    : DateTimeOffset.UtcNow;
                var n = ConvertRecord(cols[2], cols[1], time);
                if (n is not null)
                {
                    try { Received?.Invoke(n); } catch (Exception) { }
                }
            }
        }
        catch (Exception)
        {
            // never let the polling loop throw
        }
        finally
        {
            Volatile.Write(ref _dbBusy, 0);
        }
    }

    private PlatformNotification? ConvertRecord(string hex, string appId, DateTimeOffset time)
    {
        byte[] blob;
        try { blob = Convert.FromHexString(hex); }
        catch (FormatException) { return null; }
        var name = AppName(appId);
        var json = Run("/usr/bin/plutil", ["-convert", "json", "-o", "-", "-"], blob, ToolTimeout);
        if (json is { ExitCode: 0 })
        {
            return NotificationRecordParser.Parse(json.Stdout, appId, name, time);
        }
        var xml = Run("/usr/bin/plutil", ["-convert", "xml1", "-o", "-", "-"], blob, ToolTimeout);
        return xml is { ExitCode: 0 } ? NotificationRecordParser.ParseXml(xml.Stdout, appId, name, time) : null;
    }

    private static (long, long, long, long) Stamp(string db)
    {
        static (long, long) Of(string p)
        {
            try { var i = new FileInfo(p); return i.Exists ? (i.LastWriteTimeUtc.Ticks, i.Length) : (0, 0); }
            catch (IOException) { return (0, 0); }
        }
        var (a, b) = Of(db);
        var (c, d) = Of(db + "-wal");
        return (a, c, b, d);
    }

    // ---- badge ----

    // argv[0]: a JSON array with one array of candidate Dock item names per app. Reads the Dock items' titles once and
    // prints one line per app: the AXStatusLabel of its first matching Dock item, "" when it has none.
    private const string BadgeScript = """
        function run(argv) {
          var groups = JSON.parse(argv[0]);
          var items = Application('System Events').processes.byName('Dock').lists[0].uiElements();
          var titles = [];
          for (var i = 0; i < items.length; i++) {
            var title = '';
            try { title = items[i].attributes.byName('AXTitle').value() || ''; } catch (e) {}
            titles.push(title);
          }
          var out = [];
          for (var g = 0; g < groups.length; g++) {
            var label = '';
            for (var j = 0; j < titles.length; j++) {
              if (titles[j] && groups[g].indexOf(titles[j]) >= 0) {
                try { label = String(items[j].attributes.byName('AXStatusLabel').value() || ''); } catch (e) {}
                break;
              }
            }
            out.push(label.replace(/[\r\n]+/g, ' '));
          }
          return out.join('\n');
        }
        """;

    public int? Unread(string appId) => ReadBadges([appId]) is [var one] ? one : null;

    /// <summary>The badges of <paramref name="appIds"/>, in order, from one osascript run; null when it failed.</summary>
    private int?[]? ReadBadges(IReadOnlyList<string> appIds)
    {
        if (appIds.Count == 0)
        {
            return [];
        }
        var groups = new System.Text.StringBuilder("[");
        for (var i = 0; i < appIds.Count; i++)
        {
            groups.Append(i == 0 ? "[" : ",[");
            var names = DockNames(appIds[i]);
            for (var k = 0; k < names.Length; k++)
            {
                if (k > 0)
                {
                    groups.Append(',');
                }
                groups.Append('"').Append(System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping.Encode(names[k])).Append('"');
            }
            groups.Append(']');
        }
        groups.Append(']');
        var r = Run("/usr/bin/osascript", ["-l", "JavaScript", "-e", BadgeScript, groups.ToString()], null, ToolTimeout);
        if (r is not { ExitCode: 0 })
        {
            return null;
        }
        var lines = r.Stdout.TrimEnd('\n', '\r').Split('\n');
        var result = new int?[appIds.Count];
        for (var i = 0; i < result.Length && i < lines.Length; i++)
        {
            result[i] = NotificationRecordParser.ParseBadgeLabel(lines[i]);
        }
        return result;
    }

    // Dock names never change while the app is installed; resolving them costs two plutil runs, so remember them.
    private string[] DockNames(string appId) => _dockNames.GetOrAdd(appId, id =>
    {
        var names = new List<string>();
        if (KnownNames.TryGetValue(id, out var known))
        {
            names.Add(known);
        }
        if (AppInfoName(id, "CFBundleDisplayName") is { } d) names.Add(d);
        if (AppInfoName(id, "CFBundleName") is { } n) names.Add(n);
        names.Add(LastSegment(id));
        return names.Distinct(StringComparer.Ordinal).ToArray();
    });

    private void PollBadges()
    {
        if (Interlocked.Exchange(ref _badgeBusy, 1) == 1)
        {
            return;
        }
        try
        {
            string[] ids;
            lock (_gate)
            {
                if (_disposed)
                {
                    return;
                }
                ids = _watched.ToArray();
            }
            var now = ReadBadges(ids);
            // Without Accessibility every run fails; retrying every few seconds only burns a process launch each time.
            var next = now is null ? BadgeRetry : BadgePoll;
            List<(string Id, int? Count)> changed = [];
            lock (_gate)
            {
                _badgeTimer?.Change(next, next);
                if (now is null)
                {
                    return;
                }
                for (var i = 0; i < ids.Length; i++)
                {
                    var id = ids[i];
                    if (!_watched.Contains(id))
                    {
                        continue;
                    }
                    if (_badges.TryGetValue(id, out var old) ? old != now[i] : now[i] is not null)
                    {
                        changed.Add((id, now[i]));
                    }
                    _badges[id] = now[i];
                }
            }
            foreach (var (id, count) in changed)
            {
                try { BadgeChanged?.Invoke(id, count); } catch (Exception) { }
            }
        }
        catch (Exception) { }
        finally
        {
            Volatile.Write(ref _badgeBusy, 0);
        }
    }

    // ---- app lookup, names, icons ----

    [GeneratedRegex("^[A-Za-z0-9._-]+$")]
    private static partial Regex BundleIdPattern();

    internal static string? KnownName(string appId) => KnownNames.TryGetValue(appId, out var n) ? n : null;

    internal static string LastSegment(string appId)
    {
        var i = appId.LastIndexOf('.');
        return i >= 0 && i < appId.Length - 1 ? appId[(i + 1)..] : appId;
    }

    private string AppName(string appId) => _names.GetOrAdd(appId, id =>
        KnownName(id) ?? AppInfoName(id, "CFBundleDisplayName") ?? AppInfoName(id, "CFBundleName") ?? LastSegment(id));

    private string? AppPath(string appId) => _appPaths.GetOrAdd(appId, id =>
    {
        if (!BundleIdPattern().IsMatch(id))
        {
            return null;
        }
        var r = Run("/usr/bin/mdfind", ["kMDItemCFBundleIdentifier == '" + id + "'"], null, ToolTimeout);
        var path = r?.Stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault(p => p.EndsWith(".app", StringComparison.Ordinal));
        if (path is null)
        {
            var o = Run("/usr/bin/osascript", ["-e", "on run argv", "-e", "POSIX path of (path to application id (item 1 of argv))", "-e", "end run", id], null, ToolTimeout);
            path = o is { ExitCode: 0 } ? o.Stdout.Trim().TrimEnd('/') : null;
        }
        return string.IsNullOrEmpty(path) ? null : path;
    });

    private static string? PlistValue(string plist, string key)
    {
        var r = Run("/usr/bin/plutil", ["-extract", key, "raw", "-o", "-", plist], null, ToolTimeout);
        var v = r is { ExitCode: 0 } ? r.Stdout.Trim() : null;
        return string.IsNullOrEmpty(v) ? null : v;
    }

    private string? AppInfoName(string appId, string key)
    {
        var app = AppPath(appId);
        return app is null ? null : PlistValue(Path.Combine(app, "Contents/Info.plist"), key);
    }

    public byte[]? GetAppIcon(string appId, int size)
    {
        size = Math.Clamp(size, 8, 512);
        return _icons.GetOrAdd((appId, size), k => RenderIcon(k.Item1, k.Item2));
    }

    private byte[]? RenderIcon(string appId, int size)
    {
        try
        {
            var cache = Path.Combine(Path.GetTempPath(), "O_O-appicons");
            var png = Path.Combine(cache, $"{appId}-{size}.png");
            if (File.Exists(png) && new FileInfo(png).Length > 0)
            {
                return File.ReadAllBytes(png);
            }
            var icns = FindIcns(appId);
            if (icns is null)
            {
                return null;
            }
            Directory.CreateDirectory(cache);
            var px = (size * 2).ToString(CultureInfo.InvariantCulture);
            var tmp = png + "." + Environment.ProcessId + ".tmp";
            var r = Run("/usr/bin/sips", ["-s", "format", "png", "-z", px, px, icns, "--out", tmp], null, ToolTimeout);
            if (r is not { ExitCode: 0 } || !File.Exists(tmp))
            {
                return null;
            }
            File.Move(tmp, png, true);
            return File.ReadAllBytes(png);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private string? FindIcns(string appId)
    {
        var app = AppPath(appId);
        if (app is null)
        {
            return null;
        }
        var res = Path.Combine(app, "Contents/Resources");
        var info = Path.Combine(app, "Contents/Info.plist");
        foreach (var key in new[] { "CFBundleIconFile", "CFBundleIconName" })
        {
            if (PlistValue(info, key) is { } name)
            {
                var file = name.EndsWith(".icns", StringComparison.OrdinalIgnoreCase) ? name : name + ".icns";
                var p = Path.Combine(res, file);
                if (File.Exists(p))
                {
                    return p;
                }
            }
        }
        try
        {
            return Directory.Exists(res) ? Directory.EnumerateFiles(res, "*.icns").OrderBy(f => f, StringComparer.Ordinal).FirstOrDefault() : null;
        }
        catch (IOException) { return null; }
    }

    // ---- actions ----

    public bool Activate(string appId)
    {
        if (!BundleIdPattern().IsMatch(appId))
        {
            return false;
        }
        return Run("/usr/bin/open", ["-b", appId], null, ToolTimeout) is { ExitCode: 0 };
    }

    public void OpenAccessSettings() =>
        Run("/usr/bin/open", ["x-apple.systempreferences:com.apple.preference.security?Privacy_AllFiles"], null, ToolTimeout);

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _watched = new HashSet<string>(StringComparer.Ordinal);
            StopTimers();
        }
    }

    private void StopTimers()
    {
        _dbTimer?.Dispose();
        _badgeTimer?.Dispose();
        _dbTimer = _badgeTimer = null;
    }

    // ---- process helper ----

    private sealed record Result(int ExitCode, string Stdout);

    private static Result? Run(string exe, IEnumerable<string> args, byte[]? stdin, TimeSpan timeout)
    {
        try
        {
            var info = new ProcessStartInfo(exe)
            {
                RedirectStandardInput = stdin is not null,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args)
            {
                info.ArgumentList.Add(a);
            }
            using var p = Process.Start(info);
            if (p is null)
            {
                return null;
            }
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (stdin is not null)
            {
                try
                {
                    p.StandardInput.BaseStream.Write(stdin, 0, stdin.Length);
                    p.StandardInput.Close();
                }
                catch (IOException) { }
            }
            if (!p.WaitForExit(timeout))
            {
                try { p.Kill(true); } catch (InvalidOperationException) { }
                return null;
            }
            p.WaitForExit(); // flush the async readers
            return new Result(p.ExitCode, stdout.GetAwaiter().GetResult());
        }
        catch (Exception)
        {
            return null;
        }
    }
}
