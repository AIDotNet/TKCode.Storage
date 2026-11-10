using Avalonia.Input;
using Avalonia.Platform.Storage;
using Island.UI.Views;

namespace Island.UI;

/// <summary>
/// Files dropped on the island go to the plugins that keep them (docs/design/05-builtin-plugins.md §7, the file
/// shelf). The island accepts a drag only when such a plugin is running, so other drags are refused visibly.
/// </summary>
public static class IslandDropTarget
{
    /// <param name="accepts">Whether a plugin would take files now.</param>
    /// <param name="drop">Hands over the dropped paths; returns whether a plugin took them.</param>
    public static void Attach(IslandView view, Func<bool> accepts, Func<IReadOnlyList<string>, bool> drop)
    {
        var target = view.Surface;
        DragDrop.SetAllowDrop(target, true);
        void Over(object? sender, DragEventArgs e)
        {
            e.DragEffects = accepts() && e.DataTransfer.Contains(DataFormat.File) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        }
        target.AddHandler(DragDrop.DragEnterEvent, Over);
        target.AddHandler(DragDrop.DragOverEvent, Over);
        target.AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var paths = e.DataTransfer.TryGetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            e.DragEffects = paths.Count > 0 && drop(paths) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        });
    }
}
