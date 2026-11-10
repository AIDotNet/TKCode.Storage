using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;

namespace Island.UI.Console;

/// <summary>Small layout helpers shared by the console pages; every value comes from Arc tokens.</summary>
internal static class ConsoleParts
{
    public static Control PageTitle(string title, string description) => new StackPanel
    {
        Spacing = 6,
        Margin = new Thickness(0, 0, 0, 24),
        Children =
        {
            Text(title, ArcDesignTokenKind.Type2XL, ArcDesignTokenKind.Foreground, FontWeight.SemiBold, display: true),
            Text(description, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary, wrap: true),
        },
    };

    /// <summary>A grouped surface, like a group in macOS System Settings.</summary>
    public static Border Group(Control content)
    {
        var border = new Border { Child = content, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 0, 16) };
        // Set by ConsoleView: the Surface token, translucent when the window is glass.
        border.Bind(Border.BackgroundProperty, border.GetResourceObservable(ConsoleView.GroupBrushKey));
        ArcTokens.Bind(border, Border.BorderBrushProperty, ArcDesignTokenKind.BorderSubtle);
        // Radii and spacing are numbers; Border wants CornerRadius and Thickness. BindCornerRadius converts the
        // former; the spacing scale is the same in every theme, so the padding is read once when attached.
        ArcTokens.BindCornerRadius(border, ArcDesignTokenKind.RadiusPanel);
        border.AttachedToVisualTree += (_, _) => border.Padding = new Thickness(ArcTokens.Get<double>(border, ArcDesignTokenKind.Space5));
        return border;
    }

    public static TextBlock SectionLabel(string text)
    {
        var t = Text(text, ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, FontWeight.SemiBold);
        t.Margin = new Thickness(0, 16, 0, 8);
        return t;
    }

    public static TextBlock Text(
        string text,
        ArcDesignTokenKind size,
        ArcDesignTokenKind color,
        FontWeight? weight = null,
        bool wrap = false,
        bool display = false)
    {
        var t = new TextBlock
        {
            Text = text,
            FontWeight = weight ?? FontWeight.Normal,
            TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
            TextTrimming = wrap ? TextTrimming.None : TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        ArcTokens.Bind(t, TextBlock.FontSizeProperty, size);
        ArcTokens.Bind(t, TextBlock.ForegroundProperty, color);
        ArcTokens.Bind(t, TextBlock.FontFamilyProperty,
            display ? ArcDesignTokenKind.DisplayFontFamily : ArcDesignTokenKind.BodyFontFamily);
        return t;
    }
}
