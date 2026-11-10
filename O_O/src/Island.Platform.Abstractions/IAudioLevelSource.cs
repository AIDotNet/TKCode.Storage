namespace Island.Platform;

/// <summary>
/// Coarse loudness per frequency band of one application's audio output, for the island's music visualiser. It carries
/// no audio: callers only ever see a handful of 0–1 levels. Needs the system's audio-capture permission where there is one.
/// </summary>
public interface IAudioLevelSource
{
    /// <summary>False where the platform cannot do this at all (the visualiser then moves by itself).</summary>
    bool IsSupported { get; }

    /// <summary>
    /// Listens to the application whose process name contains <paramref name="processName"/> (case-insensitive) and calls
    /// <paramref name="levels"/> (any thread, about 30 times a second) with <paramref name="bands"/> values between 0 and 1.
    /// Disposing stops it. Returns null when it cannot listen now (not running, not permitted).
    /// </summary>
    IDisposable? Listen(string processName, int bands, Action<float[]> levels);
}

/// <summary>For tests and platforms without audio levels.</summary>
public sealed class NullAudioLevelSource : IAudioLevelSource
{
    public bool IsSupported => false;
    public IDisposable? Listen(string processName, int bands, Action<float[]> levels) => null;
}

/// <summary>Turns samples into band levels: a small FFT, log-spaced bands, smoothed. Shared by the platform sources.</summary>
public sealed class SpectrumAnalyzer
{
    private const int FftSize = 1024;
    private readonly int _bands;
    private readonly int _sampleRate;
    private readonly float[] _window = new float[FftSize];
    private readonly float[] _ring = new float[FftSize];
    private readonly float[] _smooth;
    private readonly float[] _re = new float[FftSize];
    private readonly float[] _im = new float[FftSize];
    private int _filled;

    public SpectrumAnalyzer(int bands, int sampleRate)
    {
        _bands = Math.Clamp(bands, 1, 32);
        _sampleRate = sampleRate > 0 ? sampleRate : 48000;
        _smooth = new float[_bands];
        for (var i = 0; i < FftSize; i++)
        {
            _window[i] = 0.5f - 0.5f * MathF.Cos(2 * MathF.PI * i / (FftSize - 1)); // Hann
        }
    }

    /// <summary>Adds mono samples; returns fresh band levels whenever a full block has been collected, else null.</summary>
    public float[]? Push(ReadOnlySpan<float> mono)
    {
        float[]? result = null;
        foreach (var s in mono)
        {
            _ring[_filled++] = s;
            if (_filled == FftSize)
            {
                result = Analyze();
                // 50% overlap keeps the motion smooth at 48 kHz (a block every ~10 ms of new audio).
                Array.Copy(_ring, FftSize / 2, _ring, 0, FftSize / 2);
                _filled = FftSize / 2;
            }
        }
        return result;
    }

    private float[] Analyze()
    {
        for (var i = 0; i < FftSize; i++)
        {
            _re[i] = _ring[i] * _window[i];
            _im[i] = 0;
        }
        Fft(_re, _im);
        // Log-spaced bands from 60 Hz to 12 kHz.
        var binHz = (float)_sampleRate / FftSize;
        for (var b = 0; b < _bands; b++)
        {
            var lo = 60f * MathF.Pow(200f, (float)b / _bands);
            var hi = 60f * MathF.Pow(200f, (float)(b + 1) / _bands);
            int from = Math.Max(1, (int)(lo / binHz)), to = Math.Min(FftSize / 2 - 1, Math.Max(from, (int)(hi / binHz)));
            float sum = 0;
            for (var k = from; k <= to; k++)
            {
                sum += MathF.Sqrt(_re[k] * _re[k] + _im[k] * _im[k]);
            }
            var magnitude = sum / (to - from + 1) / (FftSize / 4f);
            // Decibels mapped from -60..-6 dB onto 0..1; the highs are boosted a little (music falls off with frequency).
            var db = 20 * MathF.Log10(magnitude + 1e-6f) + b * 1.2f;
            var level = Math.Clamp((db + 60f) / 54f, 0f, 1f);
            _smooth[b] = level > _smooth[b] ? level : _smooth[b] * 0.82f + level * 0.18f; // fast attack, slow release
        }
        return (float[])_smooth.Clone();
    }

    private static void Fft(float[] re, float[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1)
            {
                j ^= bit;
            }
            j ^= bit;
            if (i < j)
            {
                (re[i], re[j]) = (re[j], re[i]);
                (im[i], im[j]) = (im[j], im[i]);
            }
        }
        for (var len = 2; len <= n; len <<= 1)
        {
            var ang = -2 * MathF.PI / len;
            float wr = MathF.Cos(ang), wi = MathF.Sin(ang);
            for (var i = 0; i < n; i += len)
            {
                float cr = 1, ci = 0;
                for (var k = 0; k < len / 2; k++)
                {
                    int a = i + k, b = i + k + len / 2;
                    var tr = re[b] * cr - im[b] * ci;
                    var ti = re[b] * ci + im[b] * cr;
                    re[b] = re[a] - tr;
                    im[b] = im[a] - ti;
                    re[a] += tr;
                    im[a] += ti;
                    var ncr = cr * wr - ci * wi;
                    ci = cr * wi + ci * wr;
                    cr = ncr;
                }
            }
        }
    }
}
