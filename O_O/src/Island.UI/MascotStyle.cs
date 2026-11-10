using Avalonia.Controls;
using Avalonia.Media;
using TKCode.Arc;

namespace Island.UI;

/// <summary>Which of Arc's mascots O_O draws. Each one is a different control; <see cref="MascotStyle"/> makes them.</summary>
public enum MascotKind
{
    /// <summary>The AI mascot (<see cref="AiMascot"/>): the dot that follows the assistant's status.</summary>
    Classic,

    /// <summary>The pixel-art robot (<see cref="PixelMascot"/>).</summary>
    Pixel,

    /// <summary>The floating ghost (<see cref="GhostMascot"/>).</summary>
    Ghost,

    /// <summary>The isometric cube (<see cref="CubeMascot"/>).</summary>
    Cube,
}

/// <summary>
/// The mascot every O_O surface draws (the console's sidebar and the About page): which <see cref="MascotKind"/> it is
/// and an optional colour (<see cref="Tint"/>; null is the theme's own). Chosen on the About page and kept between
/// runs (<c>AppSettings.Mascot</c>, <c>AppSettings.MascotColor</c>). <see cref="MascotHost"/> shows the current one.
/// </summary>
public static class MascotStyle
{
    /// <summary>The colours offered on the About page, besides the theme's own.</summary>
    public static readonly (string Label, Color Value)[] Swatches =
    [
        ("蓝", Color.Parse("#4F6BFF")),
        ("紫", Color.Parse("#7747FF")),
        ("绿", Color.Parse("#0DB879")),
        ("橙", Color.Parse("#E8774A")),
        ("玫红", Color.Parse("#ED4E9D")),
        ("琥珀", Color.Parse("#F3AD20")),
        ("墨", Color.Parse("#353535")),
    ];

    /// <summary>The kind of mascot every surface shows now.</summary>
    public static MascotKind Kind { get; private set; } = MascotKind.Classic;

    /// <summary>The colour every mascot shows now, or null for the theme's.</summary>
    public static Color? Tint { get; private set; }

    /// <summary>Raised after <see cref="Kind"/> or <see cref="Tint"/> changes.</summary>
    public static event EventHandler? Changed;

    /// <summary>Changes every mascot that shows this style. Setting the current style does nothing.</summary>
    public static void Set(MascotKind kind, Color? tint)
    {
        if (kind == Kind && tint == Tint)
        {
            return;
        }
        Kind = kind;
        Tint = tint;
        Changed?.Invoke(null, EventArgs.Empty);
    }

    /// <summary>A new mascot of the current kind, sized, coloured and posed as the style and status say.</summary>
    public static Control Create(double size, AiMascotStatus status = AiMascotStatus.Idle)
    {
        Control control = Kind switch
        {
            MascotKind.Pixel => new PixelMascot(),
            MascotKind.Ghost => new GhostMascot(),
            MascotKind.Cube => new CubeMascot(),
            _ => new AiMascot(),
        };
        Apply(control, size, status);
        return control;
    }

    /// <summary>
    /// The status of the same name in the mascot's own enum, or Idle when that kind has no such pose (the pixel robot
    /// has no listening pose, for example).
    /// </summary>
    private static T StatusOf<T>(AiMascotStatus status) where T : struct, Enum =>
        Enum.TryParse<T>(status.ToString(), out var result) ? result : Enum.Parse<T>("Idle");

    /// <summary>
    /// Sets <paramref name="control"/> (made by <see cref="Create"/>) to the current colour at <paramref name="size"/>.
    /// The mascots stand still in these places: the status is idle and the ground is not drawn.
    /// </summary>
    public static void Apply(Control control, double size, AiMascotStatus status = AiMascotStatus.Idle)
    {
        var brush = Tint is { } tint ? new SolidColorBrush(tint) : null;
        switch (control)
        {
            case AiMascot ai:
                ai.Size = size;
                ai.Status = status;
                ai.ShowGround = false;
                ai.BodyColor = Tint;
                break;
            case PixelMascot pixel:
                // The sprite is drawn in whole pixels: scale 1 is 24px, 2 is 48px, and so on.
                pixel.Scale = Math.Clamp((int)Math.Round(size / 24), 1, 8);
                pixel.Status = StatusOf<PixelMascotStatus>(status);
                pixel.Announce = false;
                pixel.AccentBrush = brush;
                break;
            case GhostMascot ghost:
                ghost.Size = size;
                ghost.Status = StatusOf<GhostMascotStatus>(status);
                ghost.ShowGround = false;
                ghost.SheetBrush = brush;
                break;
            case CubeMascot cube:
                cube.Size = size;
                cube.Status = StatusOf<CubeMascotStatus>(status);
                cube.ShowsGround = false;
                cube.BodyBrush = brush;
                break;
        }
    }
}
