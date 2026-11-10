using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Island.Plugins;
using TKCode.Arc;

namespace Island.UI.Plugins;

/// <summary>
/// The Arc controls a plugin may create with <c>ui.arc(type, props, children)</c>. Plugins are untrusted: they name a
/// control from this list and set the properties listed for it, with plain values (string, number, bool; enums by name,
/// dates as YYYY-MM-DD, colours as #RRGGBB). Nothing outside the list can be created or set.
/// </summary>
/// <param name="Name">What the plugin writes: <c>ui.arc('Slider', …)</c>.</param>
/// <param name="Type">The Arc (or Avalonia) control.</param>
/// <param name="Props">Properties a plugin may set (case-sensitive CLR names).</param>
/// <param name="ValueProp">The property that reports the user's change as <c>onChange(value)</c>; null for display-only controls.</param>
internal sealed record ArcControlSpec(string Name, Type Type, string[] Props, string? ValueProp = null)
{
    public Control Create() => (Control)Activator.CreateInstance(Type)!;
}

internal static class ArcRegistry
{
    private static readonly ArcControlSpec[] Specs =
    [
        new("Alert", typeof(Alert), ["Title", "Tone", "IsOpen", "IsDismissible"]),
        new("Avatar", typeof(Avatar), ["DisplayName", "Size", "Status", "Initials"]),
        new("Calendar", typeof(TKCode.Arc.Calendar), ["SelectedDate", "MinDate", "MaxDate", "ShowToday", "FirstDayOfWeek", "TodayLabel"], "SelectedDate"),
        new("Checkbox", typeof(Checkbox), ["Description", "IsInvalid", "IsRequired", "IsChecked", "IsEnabled"], "IsChecked"),
        new("Countdown", typeof(Countdown), ["Label", "IsPaused"]),
        new("EmptyState", typeof(EmptyState), ["Title", "Description", "Label"]),
        new("MetricCard", typeof(MetricCard), ["Label", "Value", "Prefix", "Suffix", "Decimals", "Context", "Change", "IsChangeGlyphVisible"]),
        new("NumberField", typeof(NumberField), ["Value", "Minimum", "Maximum", "Step", "IsEnabled"], "Value"),
        new("Pagination", typeof(Pagination), ["CurrentPage", "PageCount", "ShowBoundaryPages", "Label"], "CurrentPage"),
        new("Skeleton", typeof(Skeleton), ["Lines", "IsAvatarVisible", "IsLoading", "Label"]),
        new("Slider", typeof(TKCode.Arc.Slider), ["Label", "Minimum", "Maximum", "Value", "IsValueVisible", "IsEnabled"], "Value"),
        new("Switch", typeof(TKCode.Arc.Switch), ["IsChecked", "IsEnabled"], "IsChecked"),
    ];

    private static readonly Dictionary<string, ArcControlSpec> ByName = Specs.ToDictionary(s => s.Name, StringComparer.Ordinal);

    public static IEnumerable<ArcControlSpec> All => Specs;

    public static ArcControlSpec? Find(string? name) => name is not null && ByName.TryGetValue(name, out var s) ? s : null;

    /// <summary>The property to watch for user changes, and how to report it, once the control exists.</summary>
    public static void WireChange(Control control, ArcControlSpec spec, Action<string> report, Func<bool> applying)
    {
        if (spec.ValueProp is null || FindProperty(spec.Type, spec.ValueProp) is not { } info)
        {
            return;
        }
        var avalonia = AvaloniaPropertyRegistry.Instance.FindRegistered(control, info.Name);
        control.PropertyChanged += (_, e) =>
        {
            if (avalonia is not null && e.Property == avalonia && !applying())
            {
                report(ToJson(e.NewValue));
            }
        };
    }

    /// <summary>Sets the listed properties from the node; others are ignored. Returns what was set, for tests.</summary>
    public static void Apply(Control control, ArcControlSpec spec, VNode node)
    {
        foreach (var name in spec.Props)
        {
            if (!node.Props.TryGetValue(CamelCase(name), out var raw) || FindProperty(spec.Type, name) is not { CanWrite: true } info)
            {
                continue;
            }
            if (TryConvert(raw, info.PropertyType, out var value))
            {
                info.SetValue(control, value);
            }
        }
    }

    internal static PropertyInfo? FindProperty(Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>JS-style prop name for a CLR property: <c>IsOpen</c> → <c>isOpen</c>.</summary>
    internal static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    private static string ToJson(object? value) => value switch
    {
        null => "null",
        DateOnly d => JsonSerializer.Serialize(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        bool b => b ? "true" : "false",
        Enum e => JsonSerializer.Serialize(e.ToString()),
        string s => JsonSerializer.Serialize(s),
        IConvertible c => JsonSerializer.Serialize(Convert.ToDouble(c, CultureInfo.InvariantCulture)),
        _ => "null",
    };

    internal static bool TryConvert(object raw, Type target, out object? value)
    {
        var type = Nullable.GetUnderlyingType(target) ?? target;
        value = null;
        try
        {
            switch (raw)
            {
                case string s when type == typeof(string):
                    value = s;
                    return true;
                case string s when type == typeof(DateOnly):
                    if (DateOnly.TryParseExact(s, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
                    {
                        value = d;
                        return true;
                    }
                    return s.Length == 0 && Nullable.GetUnderlyingType(target) is not null;
                case string s when type == typeof(Color):
                    if (Color.TryParse(s, out var c)) { value = c; return true; }
                    return false;
                case string s when type == typeof(IBrush):
                    if (Color.TryParse(s, out var bc)) { value = new SolidColorBrush(bc); return true; }
                    return false;
                case string s when type.IsEnum:
                    return Enum.TryParse(type, s, ignoreCase: true, out value);
                case double n when type.IsEnum:
                    value = Enum.ToObject(type, (int)n);
                    return true;
                case double n when type == typeof(bool):
                    return false;
                case double n when type.IsPrimitive || type == typeof(decimal):
                    value = Convert.ChangeType(n, type, CultureInfo.InvariantCulture);
                    return true;
                case bool b when type == typeof(bool):
                    value = b;
                    return true;
                default:
                    return false;
            }
        }
        catch (Exception e) when (e is InvalidCastException or OverflowException or FormatException or ArgumentException)
        {
            return false;
        }
    }
}
