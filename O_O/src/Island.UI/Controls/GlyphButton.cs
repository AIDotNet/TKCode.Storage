using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc.Icons.Lucide;

namespace Island.UI.Controls;

/// <summary>
/// A bare icon that acts as a button, for the island (docs/design/02-island-shell.md §7): no box ever, not even on
/// hover; the glyph is the island's secondary grey and turns white under the pointer. Keyboard focus works like any
/// button (Enter / Space).
/// </summary>
public sealed class GlyphButton : Border
{
    private readonly LucideIcon _icon = new() { Size = 20, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    private bool _enabled = true;

    public GlyphButton()
    {
        Background = Brushes.Transparent; // hit-testable, draws nothing
        Width = 32;
        Height = 44;
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Child = _icon;
        Tint(false);
        PointerEntered += (_, _) => Tint(_enabled);
        PointerExited += (_, _) => Tint(false);
        PointerReleased += (_, e) =>
        {
            if (_enabled && e.InitialPressMouseButton == MouseButton.Left)
            {
                Click?.Invoke();
            }
        };
        KeyDown += (_, e) =>
        {
            if (_enabled && e.Key is Key.Enter or Key.Space)
            {
                Click?.Invoke();
                e.Handled = true;
            }
        };
    }

    public event Action? Click;

    public LucideIconKind Kind
    {
        get => _icon.Kind;
        set => _icon.Kind = value;
    }

    public string? Label
    {
        get => AutomationProperties.GetName(this);
        set => AutomationProperties.SetName(this, value);
    }

    /// <summary>A smaller glyph and target, for the corner of a tile (<c>size: 'small'</c>).</summary>
    public bool Small
    {
        get => _icon.Size < 20;
        set
        {
            _icon.Size = value ? 14 : 20;
            Width = value ? 24 : 32;
            Height = value ? 24 : 44;
        }
    }

    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            Opacity = value ? 1 : 0.3;
            Cursor = value ? new Cursor(StandardCursorType.Hand) : Cursor.Default;
        }
    }

    private void Tint(bool hot) =>
        _icon.Bind(LucideIcon.ForegroundProperty, _icon.GetResourceObservable(hot ? "IslandForegroundBrush" : "IslandSecondaryBrush"));
}
