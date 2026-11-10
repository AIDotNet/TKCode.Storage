using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;

namespace Island.App.Tools;

/// <summary>
/// <c>O_O --render-icon &lt;dir&gt;</c>: draws the app icon from the <see cref="AppLogo"/> and writes every size, an
/// <c>.ico</c> (PNG entries) and, on macOS, an <c>.icns</c> via <c>iconutil</c>.
/// </summary>
internal static class IconRenderer
{
    // Apple's icon grid: the artwork is an 824 px squircle centred on a 1024 px canvas.
    private const double Canvas = 256;            // layout size in DIPs, rendered at up to 4x
    private const double Plate = Canvas * 824 / 1024;
    private const double LogoSize = 150;

    private static readonly int[] Sizes = [16, 32, 64, 128, 256, 512, 1024];

    public static int Run(string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var plate = new Border
        {
            Width = Plate,
            Height = Plate,
            // ≈ Apple's squircle radius (22.37 % of the side); a plain rounded rectangle is close at icon sizes.
            CornerRadius = new CornerRadius(Plate * 0.2237),
            Background = new SolidColorBrush(Color.Parse("#0B0B12")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Child = AppLogo.Create(LogoSize),
        };
        var root = new Panel { Width = Canvas, Height = Canvas, Background = Brushes.Transparent, Children = { plate } };

        root.Measure(new Size(Canvas, Canvas));
        root.Arrange(new Rect(0, 0, Canvas, Canvas));

        var pngs = new Dictionary<int, byte[]>();
        foreach (var px in Sizes)
        {
            var scale = px / Canvas;
            using var bitmap = new RenderTargetBitmap(new PixelSize(px, px), new Vector(96 * scale, 96 * scale));
            bitmap.Render(root);
            using var stream = new MemoryStream();
            bitmap.Save(stream, PngBitmapEncoderOptions.Default);
            pngs[px] = stream.ToArray();
            File.WriteAllBytes(Path.Combine(outputDirectory, $"icon_{px}.png"), pngs[px]);
        }

        WriteIco(Path.Combine(outputDirectory, "O_O.ico"), [.. new[] { 16, 32, 64, 128, 256 }.Select(s => (s, pngs[s]))]);
        if (OperatingSystem.IsMacOS())
        {
            WriteIcns(outputDirectory, pngs);
        }
        Console.WriteLine($"Icon written to {Path.GetFullPath(outputDirectory)}");
        return 0;
    }

    /// <summary>An ICO with PNG-compressed entries (supported since Windows Vista).</summary>
    private static void WriteIco(string path, IReadOnlyList<(int Size, byte[] Png)> images)
    {
        using var file = File.Create(path);
        using var w = new BinaryWriter(file);
        w.Write((ushort)0);
        w.Write((ushort)1);
        w.Write((ushort)images.Count);
        var offset = 6 + 16 * images.Count;
        foreach (var (size, png) in images)
        {
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)(size >= 256 ? 0 : size));
            w.Write((byte)0);
            w.Write((byte)0);
            w.Write((ushort)1);
            w.Write((ushort)32);
            w.Write(png.Length);
            w.Write(offset);
            offset += png.Length;
        }
        foreach (var (_, png) in images)
        {
            w.Write(png);
        }
    }

    private static void WriteIcns(string outputDirectory, IReadOnlyDictionary<int, byte[]> pngs)
    {
        var iconset = Path.Combine(outputDirectory, "O_O.iconset");
        Directory.CreateDirectory(iconset);
        foreach (var size in new[] { 16, 32, 128, 256, 512 })
        {
            File.WriteAllBytes(Path.Combine(iconset, $"icon_{size}x{size}.png"), pngs[size]);
            File.WriteAllBytes(Path.Combine(iconset, $"icon_{size}x{size}@2x.png"), pngs[size * 2]);
        }
        using var iconutil = System.Diagnostics.Process.Start("iconutil",
            ["-c", "icns", iconset, "-o", Path.Combine(outputDirectory, "O_O.icns")]);
        iconutil.WaitForExit();
        Directory.Delete(iconset, recursive: true);
    }
}
