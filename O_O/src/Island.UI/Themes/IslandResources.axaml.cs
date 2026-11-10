using Avalonia.Controls;
using Avalonia.Markup.Xaml;

namespace Island.UI.Themes;

/// <summary>The island's tokens and control themes. Add to Application.Resources.MergedDictionaries.</summary>
public partial class IslandResources : ResourceDictionary
{
    public IslandResources() => AvaloniaXamlLoader.Load(this);
}
