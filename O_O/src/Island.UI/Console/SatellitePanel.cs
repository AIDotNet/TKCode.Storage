using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Island.Plugins;
using Island.Plugins.Host;
using Island.UI.Plugins;
using TKCode.Arc;
using TKCode.Arc.DesignTokens;
using static Island.UI.Console.ConsoleParts;

namespace Island.UI.Console;

/// <summary>Whether each of a plugin's satellite islands shows, and on which side of the main island (docs/design/08 §4.3).</summary>
public sealed class SatellitePanel : StackPanel
{
    /// <summary>The store the app's satellite host reads; set once at startup. Without it the panel shows nothing.</summary>
    public static SatellitePreferencesStore? Preferences { get; set; }

    private static readonly (string Value, string Label)[] Shown = [("on", "显示"), ("off", "关闭")];
    private static readonly (string Value, string Label)[] Sides = [("auto", "自动"), ("left", "左侧"), ("right", "右侧")];

    public SatellitePanel(PluginPackage package, SatellitePreferencesStore prefs)
    {
        Spacing = 12;
        Margin = new Thickness(0, 0, 0, 8);
        Children.Add(Text("消息小岛是主岛旁边的独立小窗。“自动”放在右侧，右侧放不下时翻到左侧。",
            ArcDesignTokenKind.TypeXS, ArcDesignTokenKind.TextSecondary, wrap: true));
        foreach (var sat in package.Manifest.Contributes.Satellites)
        {
            var key = $"{package.Id}/{sat.Id}";
            var setting = prefs.Get(key);
            Children.Add(Text(sat.Title ?? sat.Id, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.Foreground));
            Children.Add(Row("显示", Choice(Shown, setting.Enabled ? "on" : "off", v => prefs.SetEnabled(key, v == "on"))));
            Children.Add(Row("位置", Choice(Sides, setting.Side ?? "auto", v => prefs.SetSide(key, v == "auto" ? null : v))));
        }
    }

    private static Control Row(string label, Control control)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("110,*") };
        grid.Children.Add(Text(label, ArcDesignTokenKind.TypeSM, ArcDesignTokenKind.TextSecondary));
        Grid.SetColumn(control, 1);
        control.HorizontalAlignment = HorizontalAlignment.Left;
        grid.Children.Add(control);
        return grid;
    }

    private static SegmentedControl Choice((string Value, string Label)[] options, string selected, Action<string> picked)
    {
        var control = new SegmentedControl();
        foreach (var (value, label) in options)
        {
            control.Items.Add(new SegmentedOption { Value = value, Label = label });
        }
        control.SelectedValue = selected;
        control.SelectionChanged += (_, _) =>
        {
            if (control.SelectedValue is string v)
            {
                picked(v);
            }
        };
        return control;
    }
}
