using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using TKCode.Arc;
using ArcButton = TKCode.Arc.Button;

namespace Island.UI.Controls;

/// <summary>
/// Tabs as words, for the island (docs/design/02-island-shell.md §7, "the island is the only container"): Arc ghost
/// buttons with no box until hovered; the current one is white and semibold, the others the island's secondary grey.
/// </summary>
public sealed class TextTabs : StackPanel
{
    private readonly List<(string Value, ArcButton Button)> _tabs = [];
    private string? _selected;

    public TextTabs()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 2;
    }

    /// <summary>Raised with the value the user picked (not when <see cref="Selected"/> is set in code).</summary>
    public event Action<string>? Picked;

    public string? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value)
            {
                return; // re-setting the same tab (every page refresh) must not restyle and flicker
            }
            _selected = value;
            Restyle();
        }
    }

    /// <summary>Replaces the tabs when they differ from the current ones (so hover state survives re-renders).</summary>
    public void SetItems(IReadOnlyList<(string Value, string Label)> items)
    {
        if (_tabs.Select(t => (t.Value, Label: t.Button.Content as string ?? "")).SequenceEqual(items))
        {
            return;
        }
        Children.Clear();
        _tabs.Clear();
        foreach (var (value, label) in items)
        {
            var button = new ArcButton
            {
                Content = label,
                Variant = ButtonVariant.Ghost,
                Size = ControlSize.Small,
                Padding = new Thickness(8, 0),
            };
            button.Click += (_, _) =>
            {
                Selected = value;
                Picked?.Invoke(value);
            };
            _tabs.Add((value, button));
            Children.Add(button);
        }
        Restyle();
    }

    private void Restyle()
    {
        foreach (var (value, button) in _tabs)
        {
            var current = value == _selected;
            button.FontWeight = current ? FontWeight.SemiBold : FontWeight.Normal;
            button.Bind(ForegroundProperty, button.GetResourceObservable(current ? "IslandForegroundBrush" : "IslandSecondaryBrush"));
        }
    }

    private static readonly AvaloniaProperty ForegroundProperty = TemplatedControlForeground();

    private static AvaloniaProperty TemplatedControlForeground() => Avalonia.Controls.Primitives.TemplatedControl.ForegroundProperty;
}
