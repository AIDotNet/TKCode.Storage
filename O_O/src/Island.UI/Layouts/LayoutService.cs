using Island.Core.Layout;
using Island.Core.Pages;
using Island.Plugins;
using Island.Plugins.Host;

namespace Island.UI.Layouts;

/// <summary>A layout an enabled plugin offers (<c>contributes.layouts</c>).</summary>
/// <param name="Key"><c>plugin/layout</c>.</param>
public sealed record LayoutOption(string Key, string Title, string? Description, string Plugin, LayoutContribution Layout);

/// <summary>
/// Layout presets from plugins: applying one sets the island's shape, dock, size and 极简模式, orders and switches on the
/// pages and chips it lists (and, if exclusive, switches the others off) and chooses the home page — through the same
/// stores the 岛体 page edits, so the island follows at once and the user can still change everything by hand.
/// </summary>
public sealed class LayoutService(PluginHost plugins, IslandAppearanceStore appearance, PagePreferencesStore pages, ChipPreferencesStore chips)
{
    /// <summary>Raised after a layout was applied, so pages that list pages and chips can rebuild.</summary>
    public event Action? Applied;

    public IReadOnlyList<LayoutOption> Layouts => [.. plugins.Packages
        .Where(p => p.Manifest.Contributes.Layouts.Count > 0 && plugins.IsEnabled(p.Id))
        .SelectMany(p => p.Manifest.Contributes.Layouts.Select(l => new LayoutOption($"{p.Id}/{l.Id}", l.Title ?? l.Id, l.Description, p.Manifest.Name, l)))];

    public bool Apply(string key)
    {
        if (Layouts.FirstOrDefault(l => l.Key == key)?.Layout is not { } layout)
        {
            return false;
        }

        appearance.Set(appearance.Current with
        {
            Shape = layout.Shape is { } shape ? Enum.Parse<IslandShape>(shape, ignoreCase: true) : appearance.Current.Shape,
            Dock = layout.Dock is { } dock ? Enum.Parse<IslandDock>(dock, ignoreCase: true) : appearance.Current.Dock,
            Scale = layout.Scale ?? appearance.Current.Scale,
            Minimal = layout.Minimal ?? appearance.Current.Minimal,
        });

        var pageKeys = plugins.Packages.SelectMany(p => p.Manifest.Contributes.AllPages.Select(g => $"{p.Id}/{g.Id}")).ToList();
        if (layout.Pages.Count > 0 || layout.Exclusive || layout.Home is not null)
        {
            var enabled = new Dictionary<string, bool>(pages.Current.Enabled);
            foreach (var key2 in layout.Pages)
            {
                enabled[key2] = true;
            }
            if (layout.Exclusive)
            {
                foreach (var other in pageKeys.Where(k => !layout.Pages.Contains(k)))
                {
                    enabled[other] = false;
                }
            }
            pages.Set(pages.Current with
            {
                Order = layout.Pages.Count > 0 ? [.. layout.Pages, .. pages.Current.Order.Where(k => !layout.Pages.Contains(k))] : pages.Current.Order,
                Enabled = enabled,
                Home = layout.Home ?? pages.Current.Home,
            });
        }

        if (layout.Chips.Count > 0 || layout.Exclusive)
        {
            var listed = layout.Chips.Select(c => c.Key).ToList();
            var enabled = new Dictionary<string, bool>(chips.Current.Enabled);
            var sides = new Dictionary<string, ChipSide>(chips.Current.Sides);
            foreach (var chip in layout.Chips)
            {
                enabled[chip.Key] = true;
                if (chip.Side is { } side)
                {
                    sides[chip.Key] = side == "left" ? ChipSide.Left : ChipSide.Right;
                }
            }
            if (layout.Exclusive)
            {
                var chipKeys = plugins.Packages.SelectMany(p => p.Manifest.Contributes.CollapsedItems.Select(c => $"{p.Id}/{c.Id}"));
                foreach (var other in chipKeys.Where(k => !listed.Contains(k)))
                {
                    enabled[other] = false;
                }
            }
            chips.Set(chips.Current with
            {
                Order = layout.Chips.Count > 0 ? [.. listed, .. chips.Current.Order.Where(k => !listed.Contains(k))] : chips.Current.Order,
                Enabled = enabled,
                Sides = sides,
            });
        }

        Applied?.Invoke();
        return true;
    }
}
