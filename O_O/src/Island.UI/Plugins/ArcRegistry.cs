using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
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
/// <param name="Extra">Props that are not plain values: charts take their data as one JSON string (<c>data</c>).</param>
internal sealed record ArcControlSpec(
    string Name,
    // On the parameter too: that is what makes the trimmer keep each typeof(...) control's constructor and properties.
    [param: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    [property: DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)]
    Type Type,
    string[] Props,
    string? ValueProp = null,
    Action<Control, VNode>? Extra = null)
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
        new("LineChart", typeof(LineChart), ChartProps("ShowLegend", "ShowGrid", "ShowXAxis", "ShowYAxis", "YMinimum", "YMaximum"), Extra: (c, n) => ApplyChartData((ChartBase)c, n, slices: false)),
        new("BarChart", typeof(BarChart), ChartProps("Period", "ValueLabel", "ShowAverage", "ShowGrid", "ShowXAxis", "ShowYAxis", "YMaximum"), Extra: (c, n) => ApplyChartData((ChartBase)c, n, slices: false)),
        new("DonutChart", typeof(DonutChart), ChartProps("TotalLabel", "Diameter", "RingThickness", "ShowLegend"), Extra: (c, n) => ApplyChartData((ChartBase)c, n, slices: true)),
        new("Sparkline", typeof(Sparkline), ChartProps("Value", "Change", "Tone", "ShowArea", "DrawingWidth", "DrawingHeight"), Extra: (c, n) => ApplyChartData((ChartBase)c, n, slices: false)),
        new("Slider", typeof(TKCode.Arc.Slider), ["Label", "Minimum", "Maximum", "Value", "IsValueVisible", "IsEnabled"], "Value"),
        new("Switch", typeof(TKCode.Arc.Switch), ["IsChecked", "IsEnabled"], "IsChecked"),
    ];

    private static string[] ChartProps(params string[] own) => ["Label", "Unit", "PlotHeight", .. own];

    private static readonly ConditionalWeakTable<Control, string> ChartData = new();

    /// <summary>
    /// A chart's data is one JSON string, <c>data</c>: <c>{ "labels": ["Mon", …], "series": [{ "name": "CPU", "values": [1, 2, …],
    /// "color": "#RRGGBB" }] }</c>; a donut takes <c>{ "labels": […], "values": […] }</c>. It is applied again only when it changed.
    /// </summary>
    private static void ApplyChartData(ChartBase chart, VNode node, bool slices)
    {
        if (node.GetString("data") is not { } json || (ChartData.TryGetValue(chart, out var last) && last == json))
        {
            return;
        }
        ChartData.AddOrUpdate(chart, json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var labels = root.TryGetProperty("labels", out var l) && l.ValueKind == JsonValueKind.Array
                ? l.EnumerateArray().Select(x => x.ToString()).ToArray()
                : [];
            chart.Series.Clear();
            if (slices)
            {
                var values = Doubles(root, "values");
                chart.ItemsSource = labels.Select((label, i) => new Slice(label, i < values.Length ? values[i] : 0)).ToArray();
                chart.XSelector = o => ((Slice)o).Label;
                chart.YSelector = o => ((Slice)o).Value;
                return;
            }
            chart.ItemsSource = labels;
            chart.XSelector = o => o;
            if (root.TryGetProperty("series", out var list) && list.ValueKind == JsonValueKind.Array)
            {
                foreach (var one in list.EnumerateArray().Take(8))
                {
                    var series = new ChartSeries { Name = one.TryGetProperty("name", out var nm) ? nm.ToString() : null, Values = Doubles(one, "values") };
                    if (one.TryGetProperty("color", out var cc) && cc.ValueKind == JsonValueKind.String && Color.TryParse(cc.GetString(), out var color))
                    {
                        series.Color = color;
                    }
                    chart.Series.Add(series);
                }
            }
            else
            {
                chart.Series.Add(new ChartSeries { Values = Doubles(root, "values") });
            }
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or FormatException)
        {
            // Bad data draws an empty chart rather than failing the page.
            chart.Series.Clear();
            chart.ItemsSource = Array.Empty<string>();
        }
    }

    private sealed record Slice(string Label, double Value);

    private static double[] Doubles(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Take(1000).Select(x => x.ValueKind == JsonValueKind.Number ? x.GetDouble() : 0).ToArray()
            : [];

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
        spec.Extra?.Invoke(control, node);
    }

    internal static PropertyInfo? FindProperty(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] Type type, string name) =>
        type.GetProperty(name, BindingFlags.Public | BindingFlags.Instance);

    /// <summary>JS-style prop name for a CLR property: <c>IsOpen</c> → <c>isOpen</c>.</summary>
    internal static string CamelCase(string name) => char.ToLowerInvariant(name[0]) + name[1..];

    // Built by hand or through the source-generated context: reflection-based serialisation is not AOT-safe.
    private static string Quote(string s) => JsonSerializer.Serialize(s, UiJson.Default.String);

    private static string ToJson(object? value) => value switch
    {
        null => "null",
        DateOnly d => Quote(d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
        bool b => b ? "true" : "false",
        Enum e => Quote(e.ToString()),
        string s => Quote(s),
        IConvertible c => Convert.ToDouble(c, CultureInfo.InvariantCulture).ToString("R", CultureInfo.InvariantCulture),
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
