using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Island.Platform;
using Island.Plugins.Host.Services;

namespace Island.UI.Services;

/// <summary>
/// The clipboard for plugins (docs/design/05-builtin-plugins.md §8): Avalonia's clipboard for the content, the
/// platform layer for change detection, secret content and direct paste. Polls on the UI thread — cheaply where the
/// platform has a change counter (macOS, Windows), by comparing content elsewhere.
/// </summary>
public sealed class ClipboardService : IPluginClipboard, IDisposable
{
    private static readonly TimeSpan CountedInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ComparedInterval = TimeSpan.FromSeconds(1);

    private readonly TopLevel _top;
    private readonly IClipboardPlatform _platform;
    private readonly DispatcherTimer _timer;
    private long? _lastCount;
    private ClipboardEntry? _last;
    private bool _polling;

    /// <summary>At most this many recorded images are kept; the oldest go first.</summary>
    public const int MaxImages = 200;
    private const long MaxImageBytes = 10L * 1024 * 1024;

    private readonly string? _imageFolder;

    /// <param name="imageFolder">Where copied images are saved (PNG); null: images are not recorded.</param>
    public ClipboardService(TopLevel top, IClipboardPlatform platform, string? imageFolder = null)
    {
        _top = top;
        _imageFolder = imageFolder;
        _platform = platform;
        _lastCount = platform.ChangeCount; // what is on the clipboard at start is not "new"
        _timer = new DispatcherTimer(platform.ChangeCount is null ? ComparedInterval : CountedInterval, DispatcherPriority.Background, (_, _) => _ = Poll());
        _timer.Start();
    }

    public event Action<ClipboardEntry>? Changed;

    public string? ImageFolder => _imageFolder;

    private IClipboard? Clipboard => _top.Clipboard;

    private async Task Poll()
    {
        if (_polling)
        {
            return;
        }
        _polling = true;
        try
        {
            _platform.TrackForegroundApp();
            var count = _platform.ChangeCount;
            if (count is not null && count == _lastCount)
            {
                return;
            }
            _lastCount = count;
            if (_platform.IsConcealed())
            {
                return; // a password manager's content: never recorded
            }
            var entry = await ReadCoreAsync();
            if (entry is null || entry.SameContent(_last))
            {
                return;
            }
            var first = _last is null && count is null; // content comparison: the first read is only a baseline
            _last = entry;
            if (!first)
            {
                Changed?.Invoke(entry);
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException or TimeoutException)
        {
            // The clipboard is busy (another app holds it open): try again next tick.
            _lastCount = null;
        }
        finally
        {
            _polling = false;
        }
    }

    public Task<ClipboardEntry?> ReadAsync(CancellationToken ct) => OnUi(ReadCoreAsync);

    private async Task<ClipboardEntry?> ReadCoreAsync()
    {
        if (Clipboard is not { } clipboard)
        {
            return null;
        }
        var now = DateTimeOffset.Now;
        var app = _platform.TargetAppName;
        if (await clipboard.TryGetFilesAsync() is { Length: > 0 } items)
        {
            var paths = items.Select(i => i.TryGetLocalPath()).OfType<string>().ToList();
            if (paths.Count > 0)
            {
                return ClipboardEntry.FromFiles(paths, app, now);
            }
        }
        if (await clipboard.TryGetTextAsync() is { Length: > 0 } text && !string.IsNullOrWhiteSpace(text))
        {
            return ClipboardEntry.FromText(text, app, now);
        }
        // Images last: apps that copy text often add a picture of it too. Only where a change counter tells us the
        // clipboard changed — comparing by decoding the image every second would be too costly.
        return _imageFolder is not null && _platform.ChangeCount is not null ? await ReadImageAsync(clipboard, app, now) : null;
    }

    private async Task<ClipboardEntry?> ReadImageAsync(IClipboard clipboard, string? app, DateTimeOffset now)
    {
        using var bitmap = await clipboard.TryGetBitmapAsync();
        if (bitmap is null)
        {
            return null;
        }
        var (width, height) = (bitmap.PixelSize.Width, bitmap.PixelSize.Height);
        using var png = new MemoryStream();
        bitmap.Save(png, Avalonia.Media.Imaging.PngBitmapEncoderOptions.Default);
        if (png.Length > MaxImageBytes)
        {
            return null; // too large to keep around
        }
        var bytes = png.ToArray();
        var path = await Task.Run(() =>
        {
            // Named by content: copying the same image again finds the same file (and the same history entry).
            var name = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..32].ToLowerInvariant() + ".png";
            Directory.CreateDirectory(_imageFolder!);
            var file = Path.Combine(_imageFolder!, name);
            if (!File.Exists(file))
            {
                File.WriteAllBytes(file, bytes);
                Prune();
            }
            else
            {
                File.SetLastWriteTimeUtc(file, DateTime.UtcNow);
            }
            return file;
        });
        return ClipboardEntry.FromImage(path, width, height, app, now);
    }

    /// <summary>Keeps the newest <see cref="MaxImages"/> recorded images.</summary>
    private void Prune()
    {
        foreach (var old in new DirectoryInfo(_imageFolder!).GetFiles("*.png").OrderByDescending(f => f.LastWriteTimeUtc).Skip(MaxImages))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    public Task WriteAsync(ClipboardEntry entry, CancellationToken ct) => OnUi(async () =>
    {
        await WriteCoreAsync(entry);
        return true;
    });

    private async Task WriteCoreAsync(ClipboardEntry entry)
    {
        if (Clipboard is not { } clipboard)
        {
            throw new PluginCapabilityException("剪贴板不可用。");
        }
        if (entry.Files is { Count: > 0 } files)
        {
            var items = new List<IStorageItem>();
            foreach (var path in files)
            {
                IStorageItem? item = Directory.Exists(path)
                    ? await _top.StorageProvider.TryGetFolderFromPathAsync(path)
                    : await _top.StorageProvider.TryGetFileFromPathAsync(path);
                if (item is not null)
                {
                    items.Add(item);
                }
            }
            if (items.Count == 0)
            {
                throw new PluginCapabilityException("这些文件已不存在。");
            }
            await clipboard.SetFilesAsync(items);
        }
        else if (entry.Image is { } image)
        {
            using var bitmap = new Avalonia.Media.Imaging.Bitmap(image);
            await clipboard.SetBitmapAsync(bitmap);
        }
        else
        {
            await clipboard.SetTextAsync(entry.Text ?? "");
        }
        // What we just wrote is not news to anyone: plugins that wrote it already know.
        _lastCount = _platform.ChangeCount;
        _last = entry with { App = null };
    }

    public Task<(bool Pasted, string? Reason)> PasteAsync(ClipboardEntry entry, CancellationToken ct) => OnUi(async () =>
    {
        await WriteCoreAsync(entry);
        // The keystroke goes out off the UI thread: the platform waits for the target app to come forward.
        var result = await Task.Run(_platform.Paste, ct);
        return (result.Pasted, result.Reason);
    });

    private static Task<T> OnUi<T>(Func<Task<T>> work) =>
        Dispatcher.UIThread.CheckAccess() ? work() : Dispatcher.UIThread.InvokeAsync(work);

    public void Dispose() => _timer.Stop();
}
