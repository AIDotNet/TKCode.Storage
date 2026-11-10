using Avalonia;
using Avalonia.Controls;

namespace Island.UI.Controls;

/// <summary>
/// The visible island shape. Its size, position (Margin) and corners are set by <see cref="IslandController"/>;
/// the springs that animate them are declared in its ControlTheme (Themes/IslandResources.axaml), so they follow
/// Arc's motion level like every other control. The outline itself is a <see cref="ContinuousBorder"/>.
/// </summary>
public sealed class IslandSurface : ContentControl
{
    /// <summary>Radius of the concave shoulders when the island hangs from the top edge (attached or around a notch).</summary>
    public static readonly StyledProperty<double> ShoulderProperty =
        ContinuousBorder.ShoulderProperty.AddOwner<IslandSurface>();

    public double Shoulder
    {
        get => GetValue(ShoulderProperty);
        set => SetValue(ShoulderProperty, value);
    }
}
