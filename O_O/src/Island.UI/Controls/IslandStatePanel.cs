using Avalonia;
using Avalonia.Controls;

namespace Island.UI.Controls;

/// <summary>
/// The content of one island state. Inactive panels fade out and stop taking input; the fade is declared in the
/// ControlTheme under the motion pseudo-classes.
/// </summary>
public sealed class IslandStatePanel : ContentControl
{
    public static readonly StyledProperty<bool> IsActiveProperty =
        AvaloniaProperty.Register<IslandStatePanel, bool>(nameof(IsActive));

    static IslandStatePanel()
    {
        IsActiveProperty.Changed.AddClassHandler<IslandStatePanel>((panel, _) => panel.UpdateActive());
    }

    public IslandStatePanel() => UpdateActive();

    public bool IsActive
    {
        get => GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    private void UpdateActive()
    {
        PseudoClasses.Set(":active", IsActive);
        IsHitTestVisible = IsActive;
    }
}
