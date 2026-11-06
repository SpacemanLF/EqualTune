namespace EqualTune.AudioLab;

public static class FilterNames
{
    public const string Bell = "Bell / peak";
    public const string LowShelf = "Low shelf";
    public const string HighShelf = "High shelf";
    public const string HighPass = "High-pass";
    public const string LowPass = "Low-pass";
    public const string Notch = "Notch";

    public static string Normalize(string? name) => name switch
    {
        LowShelf or HighShelf or HighPass or LowPass or Notch => name,
        _ => Bell
    };
}

/// <summary>Shared RBJ biquad coefficients for every filter shape the EQ supports.</summary>
public readonly record struct BiquadCoefficients(double B0, double B1, double B2, double A1, double A2)
{
    public static BiquadCoefficients Identity => new(1, 0, 0, 0, 0);

    public static BiquadCoefficients Create(int sampleRate, AudioEqBand band)
        => Create(sampleRate, band.Filter, band.Frequency, band.GainDb, band.Q);

    public static BiquadCoefficients Create(int sampleRate, string? filter, double frequency, double gainDb, double q)
    {
        var kind = FilterNames.Normalize(filter);
        var usesGain = kind == FilterNames.Bell || kind == FilterNames.LowShelf || kind == FilterNames.HighShelf;
        if (usesGain && Math.Abs(gainDb) < .0001) return Identity;

        var a = Math.Pow(10, gainDb / 40.0);
        var w0 = 2 * Math.PI * Math.Clamp(frequency, 10, sampleRate * .49) / sampleRate;
        var cos = Math.Cos(w0);
        var sin = Math.Sin(w0);
        var alpha = sin / (2 * Math.Clamp(q, .1, 20));
        double b0, b1, b2, a0, a1, a2;
        switch (kind)
        {
            case FilterNames.LowShelf:
            {
                var s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) - (a - 1) * cos + s);
                b1 = 2 * a * ((a - 1) - (a + 1) * cos);
                b2 = a * ((a + 1) - (a - 1) * cos - s);
                a0 = (a + 1) + (a - 1) * cos + s;
                a1 = -2 * ((a - 1) + (a + 1) * cos);
                a2 = (a + 1) + (a - 1) * cos - s;
                break;
            }
            case FilterNames.HighShelf:
            {
                var s = 2 * Math.Sqrt(a) * alpha;
                b0 = a * ((a + 1) + (a - 1) * cos + s);
                b1 = -2 * a * ((a - 1) + (a + 1) * cos);
                b2 = a * ((a + 1) + (a - 1) * cos - s);
                a0 = (a + 1) - (a - 1) * cos + s;
                a1 = 2 * ((a - 1) - (a + 1) * cos);
                a2 = (a + 1) - (a - 1) * cos - s;
                break;
            }
            case FilterNames.HighPass:
                b0 = (1 + cos) / 2; b1 = -(1 + cos); b2 = (1 + cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterNames.LowPass:
                b0 = (1 - cos) / 2; b1 = 1 - cos; b2 = (1 - cos) / 2;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            case FilterNames.Notch:
                b0 = 1; b1 = -2 * cos; b2 = 1;
                a0 = 1 + alpha; a1 = -2 * cos; a2 = 1 - alpha;
                break;
            default:
                b0 = 1 + alpha * a; b1 = -2 * cos; b2 = 1 - alpha * a;
                a0 = 1 + alpha / a; a1 = -2 * cos; a2 = 1 - alpha / a;
                break;
        }
        return new(b0 / a0, b1 / a0, b2 / a0, a1 / a0, a2 / a0);
    }

    public double ResponseDb(double frequency, int sampleRate)
    {
        var w = 2 * Math.PI * frequency / sampleRate;
        var c1 = Math.Cos(w); var s1 = Math.Sin(w);
        var c2 = Math.Cos(2 * w); var s2 = Math.Sin(2 * w);
        var numReal = B0 + B1 * c1 + B2 * c2;
        var numImag = -B1 * s1 - B2 * s2;
        var denReal = 1 + A1 * c1 + A2 * c2;
        var denImag = -A1 * s1 - A2 * s2;
        var numPower = numReal * numReal + numImag * numImag;
        var denPower = denReal * denReal + denImag * denImag;
        return 10 * Math.Log10(Math.Max(numPower / Math.Max(denPower, 1e-18), 1e-18));
    }
}

/// <summary>Estimates how loud a set of bands is, so the engine can add headroom and level-match A/B.</summary>
public static class EqAnalysis
{
    public static (double PeakDb, double MeanDb) Analyze(IReadOnlyList<AudioEqBand> bands, int sampleRate)
    {
        if (bands.Count == 0) return (0, 0);
        var coefficients = bands.Select(b => BiquadCoefficients.Create(sampleRate, b)).ToArray();
        const int points = 120;
        var peak = double.NegativeInfinity;
        double weighted = 0, weights = 0;
        for (var i = 0; i < points; i++)
        {
            var frequency = Math.Min(20 * Math.Pow(1000, i / (points - 1.0)), sampleRate * .45);
            var db = 0.0;
            foreach (var c in coefficients) db += c.ResponseDb(frequency, sampleRate);
            peak = Math.Max(peak, db);
            // Weight the 100 Hz-8 kHz range most, where hearing is most sensitive and most music energy lives.
            var weight = Math.Exp(-0.5 * Math.Pow(Math.Log10(frequency / 1500) / 0.45, 2));
            weighted += db * weight;
            weights += weight;
        }
        return (peak, weights > 0 ? weighted / weights : 0);
    }

    /// <summary>Smoothly rounds peaks above 0.8 toward full scale instead of hard clipping.</summary>
    public static double SoftLimit(double x)
    {
        var magnitude = Math.Abs(x);
        if (magnitude <= .8) return x;
        var limited = .8 + .18 * Math.Tanh((magnitude - .8) / .18);
        return Math.CopySign(limited, x);
    }
}

/// <summary>Turns a block of recent output samples into log-spaced dBFS levels for the live spectrum.</summary>
public static class SpectrumAnalyzer
{
    public const int WindowSize = 2048;

    public static double[] Analyze(double[] samples, int sampleRate, int bands)
    {
        var n = WindowSize;
        var re = new double[n];
        var im = new double[n];
        for (var i = 0; i < n; i++)
            re[i] = samples[i] * (0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1)));
        Fft(re, im);
        var half = n / 2;
        var magnitudeDb = new double[half + 1];
        for (var k = 0; k <= half; k++)
        {
            var magnitude = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * 4 / n; // Hann window gain and one-sided scaling.
            magnitudeDb[k] = 20 * Math.Log10(Math.Max(magnitude, 1e-5));
        }
        var result = new double[bands];
        var binWidth = (double)sampleRate / n;
        var nyquist = sampleRate / 2.0;
        for (var b = 0; b < bands; b++)
        {
            var lowEdge = 20 * Math.Pow(1000, (b - 0.5) / (bands - 1));
            var highEdge = 20 * Math.Pow(1000, (b + 0.5) / (bands - 1));
            var center = 20 * Math.Pow(1000, b / (bands - 1.0));
            if (center >= nyquist) { result[b] = -90; continue; }
            var firstBin = (int)Math.Ceiling(lowEdge / binWidth);
            var lastBin = (int)Math.Floor(highEdge / binWidth);
            double level;
            if (lastBin >= firstBin)
            {
                level = -200;
                for (var k = Math.Max(1, firstBin); k <= Math.Min(half, lastBin); k++) level = Math.Max(level, magnitudeDb[k]);
            }
            else
            {
                var position = center / binWidth;
                var lower = Math.Clamp((int)Math.Floor(position), 1, half - 1);
                var fraction = Math.Clamp(position - lower, 0, 1);
                level = magnitudeDb[lower] * (1 - fraction) + magnitudeDb[lower + 1] * fraction;
            }
            result[b] = Math.Clamp(level, -90, 0);
        }
        return result;
    }

    private static void Fft(double[] re, double[] im)
    {
        var n = re.Length;
        for (int i = 1, j = 0; i < n; i++)
        {
            var bit = n >> 1;
            for (; (j & bit) != 0; bit >>= 1) j ^= bit;
            j ^= bit;
            if (i < j) { (re[i], re[j]) = (re[j], re[i]); (im[i], im[j]) = (im[j], im[i]); }
        }
        for (var length = 2; length <= n; length <<= 1)
        {
            var angle = -2 * Math.PI / length;
            var wLenRe = Math.Cos(angle);
            var wLenIm = Math.Sin(angle);
            for (var i = 0; i < n; i += length)
            {
                double wRe = 1, wIm = 0;
                for (var k = 0; k < length / 2; k++)
                {
                    var uRe = re[i + k];
                    var uIm = im[i + k];
                    var vRe = re[i + k + length / 2] * wRe - im[i + k + length / 2] * wIm;
                    var vIm = re[i + k + length / 2] * wIm + im[i + k + length / 2] * wRe;
                    re[i + k] = uRe + vRe; im[i + k] = uIm + vIm;
                    re[i + k + length / 2] = uRe - vRe; im[i + k + length / 2] = uIm - vIm;
                    var nextRe = wRe * wLenRe - wIm * wLenIm;
                    wIm = wRe * wLenIm + wIm * wLenRe;
                    wRe = nextRe;
                }
            }
        }
    }
}
