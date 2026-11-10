using Avalonia;
using Avalonia.Controls;
using Island.Plugins;
using Island.UI.Plugins;

namespace Island.UI.Tests;

/// <summary>The registered Arc controls are really created and laid out in a headless Avalonia, with the plugin's props.</summary>
public sealed class ArcRenderTests
{
    private static VNode Arc(string type, Dictionary<string, object> props) =>
        new("arc", null, new Dictionary<string, object>(props) { ["type"] = type }, new Dictionary<string, int>(), []);

    private static Control Show(VNode node, out VNodeRenderer renderer)
    {
        renderer = new VNodeRenderer((_, _) => { });
        var control = renderer.Render(node, null)!;
        var window = new Window { Width = 480, Height = 360, Content = control };
        window.Show();
        window.UpdateLayout();
        return control;
    }

    [Fact]
    public void Every_registered_control_can_be_created_and_laid_out() => Headless.Run(() =>
    {
        foreach (var spec in ArcRegistry.All)
        {
            var control = Show(Arc(spec.Name, []), out _);
            Assert.True(control.GetType() == spec.Type, spec.Name);
            Assert.True(control.IsMeasureValid, spec.Name);
        }
    });

    [Fact]
    public void Props_are_set_from_plain_values_and_unknown_ones_are_ignored() => Headless.Run(() =>
    {
        var slider = (TKCode.Arc.Slider)Show(Arc("Slider", new() { ["minimum"] = 0.0, ["maximum"] = 200.0, ["value"] = 40.0, ["nonsense"] = "x" }), out _);
        Assert.Equal(200, slider.Maximum);
        Assert.Equal(40, slider.Value);
        var cal = (TKCode.Arc.Calendar)Show(Arc("Calendar", new() { ["selectedDate"] = "2026-11-10" }), out _);
        Assert.Equal(new DateOnly(2026, 11, 10), cal.SelectedDate);
        var unknown = Show(Arc("NoSuchControl", []), out _);
        Assert.IsType<TextBlock>(unknown);
    });

    [Fact]
    public void A_user_change_is_reported_but_the_renderers_own_update_is_not() => Headless.Run(() =>
    {
        var reported = new List<string>();
        var renderer = new VNodeRenderer((_, json) => reported.Add(json ?? ""));
        var node = new VNode("arc", null, new Dictionary<string, object> { ["type"] = "Slider", ["value"] = 10.0 },
            new Dictionary<string, int> { ["onChange"] = 1 }, []);
        var slider = (TKCode.Arc.Slider)renderer.Render(node, null)!;
        new Window { Content = slider }.Show();
        Assert.Empty(reported);                       // applying the plugin's own value is not user input
        slider.Value = 55;                            // the user drags
        Assert.Equal(["55"], reported);
    });

    [Fact]
    public void Charts_take_their_data_as_a_json_string() => Headless.Run(() =>
    {
        var line = Show(Arc("LineChart", new() { ["data"] = """{"labels":["a","b","c"],"series":[{"name":"CPU","values":[1,3,2],"color":"#32ADE6"}]}""" }), out _);
        Assert.Single(((TKCode.Arc.ChartBase)line).Series);
        var donut = Show(Arc("DonutChart", new() { ["data"] = """{"labels":["x","y"],"values":[3,7]}""" }), out _);
        Assert.NotNull(((TKCode.Arc.ChartBase)donut).ItemsSource);
        var bad = Show(Arc("BarChart", new() { ["data"] = "not json" }), out _);
        Assert.Empty(((TKCode.Arc.ChartBase)bad).Series);
    });

    [Fact]
    public void The_spectrum_node_sizes_to_its_bars_and_listens_only_while_active()
    {
        var source = new CountingSource();
        Island.UI.Controls.AudioLevels.Source = source;
        try
        {
            Headless.Run(() =>
            {
                var node = new VNode("spectrum", null, new Dictionary<string, object> { ["bars"] = 7.0, ["source"] = "NeteaseMusic", ["active"] = true },
                    new Dictionary<string, int>(), []);
                var control = Show(node, out var renderer);
                Assert.Equal(7 * 3 + 6 * 2, control.DesiredSize.Width);
                Assert.Equal(1, source.Listening);
                Assert.Equal("NeteaseMusic", source.LastName);

                renderer.Render(new VNode("spectrum", null, new Dictionary<string, object> { ["bars"] = 7.0, ["source"] = "NeteaseMusic", ["active"] = false },
                    new Dictionary<string, int>(), []), control);
                Assert.Equal(0, source.Listening);          // paused: the capture is stopped
            });
        }
        finally
        {
            Island.UI.Controls.AudioLevels.Source = new Island.Platform.NullAudioLevelSource();
        }
    }

    private sealed class CountingSource : Island.Platform.IAudioLevelSource
    {
        public int Listening;
        public string? LastName;
        public bool IsSupported => true;

        public IDisposable? Listen(string processName, int bands, Action<float[]> levels)
        {
            Listening++;
            LastName = processName;
            return new Stop(this);
        }

        private sealed class Stop(CountingSource owner) : IDisposable
        {
            public void Dispose() => owner.Listening--;
        }
    }
}
