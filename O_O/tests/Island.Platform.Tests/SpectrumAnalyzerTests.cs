using Island.Platform;

namespace Island.Platform.Tests;

public sealed class SpectrumAnalyzerTests
{
    private static float[] Tone(double hz, int rate, int count) =>
        [.. Enumerable.Range(0, count).Select(i => (float)(0.5 * Math.Sin(2 * Math.PI * hz * i / rate)))];

    [Fact]
    public void A_tone_lights_the_band_that_holds_its_frequency()
    {
        var analyzer = new SpectrumAnalyzer(8, 48000);
        float[]? bands = null;
        foreach (var chunk in Tone(1000, 48000, 8192).Chunk(480))
        {
            bands = analyzer.Push(chunk) ?? bands;
        }
        Assert.NotNull(bands);
        Assert.Equal(8, bands!.Length);
        var loudest = Array.IndexOf(bands, bands.Max());
        // 60 Hz × 200^(b/8): band 4 spans ~ 0.87–1.6 kHz.
        Assert.Equal(4, loudest);
        Assert.True(bands[4] > 0.5f);
        Assert.True(bands[0] < 0.2f && bands[7] < 0.2f);
    }

    [Fact]
    public void Silence_gives_zero_levels_and_all_values_stay_between_0_and_1()
    {
        var analyzer = new SpectrumAnalyzer(5, 44100);
        var bands = analyzer.Push(new float[4096]);
        Assert.NotNull(bands);
        Assert.All(bands!, v => Assert.InRange(v, 0f, 0.001f));
        var noisy = new Random(1).GetItems(new[] { -1f, 1f }, 4096);
        bands = analyzer.Push(noisy);
        Assert.All(bands!, v => Assert.InRange(v, 0f, 1f));
    }
}
