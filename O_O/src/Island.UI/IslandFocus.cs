using Avalonia.Controls;
using Avalonia.Input;

namespace Island.UI;

/// <summary>
/// Keyboard focus on the island (docs/design/02-island-shell.md §7, decided 2026-11-10): the island never takes it on
/// its own — the app the user works in keeps the keyboard — except while the user is typing into a text box on the
/// island, which starts with a click on that box. Esc, collapsing the island or clicking elsewhere ends it; Esc and
/// collapsing also hand the keyboard back to the app that had it.
/// </summary>
public static class IslandFocus
{
    private static TextBox? s_box;

    /// <summary>Raised when typing starts (true) or ends (false); the island stays open while the user types.</summary>
    public static event Action<bool>? EditingChanged;

    /// <summary>Brings the app the user was in back to the front (set by the app from the platform layer).</summary>
    public static Action? RestoreFocus { get; set; }

    public static bool IsEditing => s_box is not null;

    /// <summary>The user clicked into <paramref name="box"/>: make the island the key window and put the caret there.</summary>
    public static void Begin(TextBox box)
    {
        if (TopLevel.GetTopLevel(box) is Window window && !window.IsActive)
        {
            // Activation completes asynchronously (macOS makes the window key a moment later); focus the box then.
            void Activated(object? sender, EventArgs e)
            {
                window.Activated -= Activated;
                Avalonia.Threading.Dispatcher.UIThread.Post(() => box.Focus(NavigationMethod.Pointer));
            }
            window.Activated += Activated;
            window.Activate();
        }
        box.Focus(NavigationMethod.Pointer);
        if (s_box == box)
        {
            return;
        }
        var was = s_box is not null;
        s_box = box;
        if (!was)
        {
            EditingChanged?.Invoke(true);
        }
    }

    /// <summary>Ends typing. <paramref name="restore"/>: give the keyboard back to the previous app (Esc, collapse).</summary>
    public static void End(bool restore)
    {
        if (s_box is not { } box)
        {
            return;
        }
        s_box = null;
        // Move focus off the box onto the window itself, so keys stop going to it.
        TopLevel.GetTopLevel(box)?.Focus();
        EditingChanged?.Invoke(false);
        if (restore)
        {
            RestoreFocus?.Invoke();
        }
    }
}
