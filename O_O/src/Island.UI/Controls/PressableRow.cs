using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Platform.Storage;

namespace Island.UI.Controls;

/// <summary>
/// A row that acts as a button, for lists on the island (clipboard entries, files): like <see cref="GlyphButton"/>,
/// no box ever (docs 02 §7) — it is slightly dimmed and comes up to full strength under the pointer. Enter / Space
/// activate it from the keyboard.
/// </summary>
public sealed class PressableRow : Border
{
    private const double Rest = 0.82;

    // A press becomes a drag once the pointer has moved this far (DIPs).
    private const double DragThreshold = 6;
    private PointerPressedEventArgs? _press;
    private Avalonia.Point _pressAt;
    private bool _dragging;

    public PressableRow()
    {
        AddHandler(PointerPressedEvent, (_, e) => System.Console.Error.WriteLine("[zz] row pressed"), Avalonia.Interactivity.RoutingStrategies.Tunnel, true); // TEMP
        PointerPressed += (_, e) =>
        {
            if (DragFiles is { Count: > 0 } && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                _press = e;
                _pressAt = e.GetPosition(this);
            }
        };
        PointerMoved += async (_, e) =>
        {
            if (_press is not { } press || _dragging || DragFiles is not { Count: > 0 } files)
            {
                return;
            }
            var p = e.GetPosition(this);
            if (Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < DragThreshold)
            {
                return;
            }
            _dragging = true;
            _press = null;
            try
            {
                await DragOut(press, files);
            }
            finally
            {
                _dragging = false;
            }
        };
        Background = Brushes.Transparent; // hit-testable, draws nothing
        Focusable = true;
        Cursor = new Cursor(StandardCursorType.Hand);
        Opacity = Rest;
        Child = Row;
        PointerEntered += (_, _) => Tint(true);
        PointerExited += (_, _) => Tint(false);
        PointerReleased += (_, e) =>
        {
            System.Console.Error.WriteLine($"[zz] row released btn={e.InitialPressMouseButton} drag={_dragging}"); // TEMP

            _press = null;
            if (e.InitialPressMouseButton == MouseButton.Left && !_dragging)
            {
                Click?.Invoke();
                e.Handled = true;
            }
        };
        KeyDown += (_, e) =>
        {
            if (e.Key is Key.Enter or Key.Space)
            {
                Click?.Invoke();
                e.Handled = true;
            }
        };
    }

    /// <summary>
    /// A tile (<c>card</c> node) rather than a row: a rounded, softly filled shape that lightens under the pointer —
    /// the one filled shape the island allows, for things the user picks up (files), docs 02 §7.
    /// </summary>
    public bool IsCard
    {
        get => _isCard;
        set
        {
            _isCard = value;
            Opacity = value ? 1 : Rest;
            CornerRadius = value ? new Avalonia.CornerRadius(16) : default;
            Padding = value ? new Avalonia.Thickness(10) : default;
            Tint(false);
        }
    }

    private bool _isCard;

    private void Tint(bool hot)
    {
        if (_isCard)
        {
            Bind(BackgroundProperty, this.GetResourceObservable(hot ? "IslandCardHoverBrush" : "IslandCardBrush"));
        }
        else
        {
            Background = Brushes.Transparent;
            Opacity = hot ? 1 : Rest;
        }
    }

    /// <summary>The row's content, laid out like a <c>row</c> node.</summary>
    public Grid Row { get; } = new();

    public event Action? Click;

    /// <summary>Files this row carries when dragged to another app (the file shelf); null: not draggable.</summary>
    public IReadOnlyList<string>? DragFiles { get; set; }

    private async Task DragOut(PointerPressedEventArgs press, IReadOnlyList<string> files)
    {
        if (Avalonia.Controls.TopLevel.GetTopLevel(this)?.StorageProvider is not { } storage)
        {
            return;
        }
        var data = new DataTransfer();
        foreach (var path in files)
        {
            Avalonia.Platform.Storage.IStorageItem? item = Directory.Exists(path)
                ? await storage.TryGetFolderFromPathAsync(path)
                : await storage.TryGetFileFromPathAsync(path);
            if (item is not null)
            {
                data.Add(DataTransferItem.CreateFile(item));
            }
        }
        await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy | DragDropEffects.Move);
    }
}
