using System.Globalization;
using Island.Core.Geometry;

namespace Island.Core.Layout;

/// <summary>
/// A stable name for a screen across launches (docs 07 §5, 显示器 = 指定屏): the display's name when the system gives
/// one, plus its size in pixels, so the same monitor is found again even if it moves in the arrangement.
/// </summary>
public static class ScreenIdentity
{
    private const string Unnamed = "screen";

    /// <param name="displayName">The system's name for the display (e.g. "LG HDR 4K"), or null.</param>
    /// <param name="pixelBounds">The screen's bounds in pixels.</param>
    public static string Of(string? displayName, RectD pixelBounds) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(string.IsNullOrWhiteSpace(displayName) ? Unnamed : displayName.Trim())}|{pixelBounds.Width:0}x{pixelBounds.Height:0}");

    /// <summary>
    /// The index of the screen <paramref name="id"/> names among <paramref name="screens"/>, or -1 (then the caller
    /// uses the primary screen). An exact match first; then the same name at another resolution; then the same
    /// resolution under another name — each only when unambiguous.
    /// </summary>
    public static int Find(string? id, IReadOnlyList<(string? Name, RectD PixelBounds)> screens)
    {
        if (string.IsNullOrEmpty(id))
        {
            return -1;
        }
        var ids = screens.Select(s => Of(s.Name, s.PixelBounds)).ToList();
        var exact = ids.IndexOf(id);
        if (exact >= 0)
        {
            return exact;
        }
        var (name, size) = Split(id);
        if (name != Unnamed)
        {
            var byName = ids.Select((x, i) => (Split(x).Name, i)).Where(t => t.Name == name).ToList();
            if (byName.Count == 1)
            {
                return byName[0].i;
            }
        }
        var bySize = ids.Select((x, i) => (Split(x).Size, i)).Where(t => t.Size == size).ToList();
        return bySize.Count == 1 ? bySize[0].i : -1;
    }

    private static (string Name, string Size) Split(string id)
    {
        var bar = id.LastIndexOf('|');
        return bar < 0 ? (id, "") : (id[..bar], id[(bar + 1)..]);
    }
}
