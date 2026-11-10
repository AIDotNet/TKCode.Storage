using Avalonia;
using Island.App.Settings;
using TKCode.Arc;
using TKCode.Arc.Fonts;
using TKCode.Arc.Themes.Corners;

namespace Island.App;

/// <summary>
/// How the app sets up Avalonia and Arc, in one place: every host (the desktop Program.cs, a browser or test host)
/// starts from <c>AppBuilder.Configure&lt;App&gt;()</c>, picks its platform and calls <see cref="UseArcApp"/>.
/// </summary>
public static class AppSetup
{
    /// <summary>Fonts, Arc's packages and the theme of the first frame.</summary>
    public static AppBuilder UseArcApp(this AppBuilder builder) =>
        builder.WithInterFont()     // Inter for body text (Avalonia.Fonts.Inter)
               .WithArcFonts()      // Geist for display text, Geist Mono for code (TKCode.Arc.Fonts)
               .UseArc(arc => arc
                   .UseArcControls() // Arc's controls, themes and design tokens
                   .UseArcSpecial()  // AiMascot and the other AI components
                   .UseArcBlocks()   // Sidebar and the other page-level blocks (console)
                   .UseArcCorners()  // corner presets, used by the Apple look (Island.UI/Themes/AppleTheme.cs)
                   // Your own Arc controls (dotnet new tkcode-arc-control): uncomment this line once the first one exists.
                   // .UsePackage("Island.App", static p => TKCode.Arc.Generated.IslandApp.ArcGeneratedAssets.AddTo(p))
                   // The theme of the first frame: the appearance and accent saved last time (Settings/AppSettings.cs).
                   // Change it at run time with ArcTheme.Apply(t => t.Appearance(...).Accent(...)); the change is saved.
                   .WithTheme(AppSettings.Current.ApplyTheme));
}
