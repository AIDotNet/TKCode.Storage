using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using TKCode.Arc.Themes.Corners;

namespace Island.UI.Themes;

/// <summary>
/// The default look of every O_O window: Apple style (docs/design/README.md §4, decision in 06-roadmap.md §3).
/// Built only from Arc's own levers, so every Arc control follows it:
/// <list type="bullet">
/// <item>accent: Apple's system blue (Arc derives the dark-mode family from the seed);</item>
/// <item>corners: Arc's corner preset scaled to macOS proportions (controls ≈ 11, panels ≈ 16, surfaces ≈ 20);</item>
/// <item>type: the system font (SF) on macOS; Inter elsewhere, the closest freely redistributable face
/// (SF's licence does not allow shipping it).</item>
/// </list>
/// Arc advises overriding as few tokens as possible; surfaces, text and status colours stay Arc's.
/// </summary>
public static class AppleTheme
{
    /// <summary>Apple's system blue (light appearance).</summary>
    public static readonly Color SystemBlue = Color.FromRgb(0x00, 0x7A, 0xFF);

    public static readonly ArcCornerStyle Corners = ArcCornerStyle.Scale(0.6);

    /// <summary>The macOS system UI font as CoreText names it.</summary>
    private const string MacSystemFont = ".AppleSystemUIFont";

    public static readonly ArcThemePreset Preset = ArcThemePreset.Create("O_O.Apple", p =>
    {
        if (OperatingSystem.IsMacOS())
        {
            p.Token(nameof(ArcDesignTokenKind.BodyFontFamily), MacSystemFont);
            p.Token(nameof(ArcDesignTokenKind.DisplayFontFamily), MacSystemFont);
        }
    });

    /// <summary>Applies the Apple look on top of <paramref name="theme"/>; an explicit accent may follow it.</summary>
    public static ArcThemeBuilder Apple(this ArcThemeBuilder theme) =>
        theme.Accent(SystemBlue)
             .Corners(Corners)
             .Preset(Preset);
}
