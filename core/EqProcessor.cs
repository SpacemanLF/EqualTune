namespace EqualTune.AudioLab;

/// <summary>
/// Platform-independent audio processor: takes blocks of interleaved float samples and applies the EQ bands,
/// automatic headroom, A/B bypass level matching, equal loudness, attenuation, balance and the soft limiter.
/// It is the same signal chain the Windows router runs, without any Windows audio calls, so Linux (and later macOS)
/// engines only have to move samples in and out of it.
/// </summary>
public sealed class EqProcessor
{
    private readonly object _lock = new();
    private AudioEqBand[] _bands = [];
    private bool _autoPreamp = false, _bypass, _matchLevel = true, _limiter = true;
    private double _manualPreampDb, _cachedManualPreamp;
    private double _attenuationDb, _loudness, _balance;

    // Audio-thread cache; recomputed only when something changed.
    private BiquadCoefficients[] _coefficients = [];
    private AudioEqBand[]? _cachedBands;
    private int _cachedRate;
    private bool _cachedAuto, _cachedBypass, _cachedMatch;
    private double _cachedAttenuation = double.NaN, _cachedLoudness = double.NaN;
    private double _outputGain = 1;
    private double[,] _z1 = new double[14, 2], _z2 = new double[14, 2];
    private int _stateChannels = 2;

    private readonly float[] _scope = new float[4096];
    private int _scopeWrite, _scopeRate;
    private double _peak, _inputPeak, _preampDb;
    private long _clipCount;

    public double Peak => Volatile.Read(ref _peak);
    public double InputPeak => Volatile.Read(ref _inputPeak);
    /// <summary>Automatic headroom currently applied (0 or negative dB).</summary>
    public double PreampDb => Volatile.Read(ref _preampDb);
    /// <summary>Number of blocks that needed the limiter since processing started.</summary>
    public long ClipCount => Interlocked.Read(ref _clipCount);

    public void SetBands(IEnumerable<AudioEqBand> bands)
    {
        var copy = bands.Take(12).ToArray();
        lock (_lock) _bands = copy;
    }

    public void SetEngineOptions(bool autoPreamp, bool bypass, bool matchLevel, double manualPreampDb = 0)
    {
        lock (_lock) { _autoPreamp = autoPreamp; _bypass = bypass; _matchLevel = matchLevel; _manualPreampDb = Math.Clamp(manualPreampDb, -24, 24); }
    }

    public void SetSoundControls(double attenuationDb, double loudnessPercent, bool limiter, double balance)
    {
        lock (_lock)
        {
            _attenuationDb = Math.Clamp(attenuationDb, -24, 0);
            _loudness = Math.Clamp(loudnessPercent, 0, 100);
            _limiter = limiter;
            _balance = Math.Clamp(balance, -100, 100);
        }
    }

    /// <summary>What the audio thread is using right now (for the on-screen readout).</summary>
    public string SoundSummary
    {
        get
        {
            lock (_lock)
                return $"attenuation {_attenuationDb:0.#} dB · loudness {_loudness:0}% · limiter {(_limiter ? "on" : "off")} · balance {(_balance == 0 ? "center" : $"{Math.Abs(_balance):0}% {(_balance < 0 ? "left" : "right")}")}";
        }
    }

    /// <summary>Recent output as log-spaced dBFS levels (20 Hz-20 kHz), or null if nothing has been processed yet.</summary>
    public double[]? GetSpectrum(int bands)
    {
        var rate = Volatile.Read(ref _scopeRate);
        if (rate <= 0) return null;
        var write = Volatile.Read(ref _scopeWrite);
        var window = new double[SpectrumAnalyzer.WindowSize];
        for (var i = 0; i < window.Length; i++)
            window[i] = _scope[(write - window.Length + i) & 4095];
        return SpectrumAnalyzer.Analyze(window, rate, bands);
    }

    /// <summary>Processes one block of interleaved samples in place.</summary>
    public void Process(float[] buffer, int frames, int channels, int sampleRate)
    {
        if (frames <= 0 || channels <= 0) return;
        if (channels != _stateChannels)
        {
            _z1 = new double[14, channels];
            _z2 = new double[14, channels];
            _stateChannels = channels;
            _cachedBands = null;
        }

        AudioEqBand[] bands;
        bool autoPreamp, bypass, matchLevel, limiter;
        double manualPreamp;
        double attenuation, loudness, balance;
        lock (_lock)
        {
            bands = _bands; autoPreamp = _autoPreamp; manualPreamp = _manualPreampDb; bypass = _bypass; matchLevel = _matchLevel;
            attenuation = _attenuationDb; loudness = _loudness; limiter = _limiter; balance = _balance;
        }

        if (!ReferenceEquals(bands, _cachedBands) || sampleRate != _cachedRate || autoPreamp != _cachedAuto || manualPreamp != _cachedManualPreamp || bypass != _cachedBypass
            || matchLevel != _cachedMatch || attenuation != _cachedAttenuation || loudness != _cachedLoudness)
        {
            // Equal loudness adds a gentle bass and treble lift after the user's bands.
            var effective = bands;
            if (loudness > 0 && !bypass)
            {
                var amount = loudness / 100.0;
                effective = [.. bands, new AudioEqBand(100, 9 * amount, .7, FilterNames.LowShelf), new AudioEqBand(9000, 4.5 * amount, .7, FilterNames.HighShelf)];
            }
            var analysis = EqAnalysis.Analyze(effective, sampleRate);
            var preamp = autoPreamp ? -Math.Max(0, analysis.PeakDb) : manualPreamp;
            _coefficients = bypass ? [] : effective.Select(b => BiquadCoefficients.Create(sampleRate, b)).ToArray();
            // Bypass is level-matched to the EQ'd sound so A/B compares tone, not loudness.
            var gainDb = bypass ? (matchLevel ? Math.Clamp(analysis.MeanDb + preamp, -12, 12) : 0) : preamp;
            _outputGain = Math.Pow(10, (gainDb + attenuation) / 20);
            Volatile.Write(ref _preampDb, preamp);
            _cachedBands = bands; _cachedRate = sampleRate; _cachedAuto = autoPreamp; _cachedManualPreamp = manualPreamp; _cachedBypass = bypass; _cachedMatch = matchLevel;
            _cachedAttenuation = attenuation; _cachedLoudness = loudness;
        }

        var leftGain = balance > 0 ? 1 - balance / 100 : 1.0;
        var rightGain = balance < 0 ? 1 + balance / 100 : 1.0;
        var coefficients = _coefficients;
        var outputGain = _outputGain;
        var clipped = false;
        var peak = 0.0;
        var inputPeak = 0.0;

        for (var frame = 0; frame < frames; frame++)
        for (var channel = 0; channel < channels; channel++)
        {
            var index = frame * channels + channel;
            var source = (double)buffer[index];
            inputPeak = Math.Max(inputPeak, Math.Abs(source));
            var filtered = source;
            for (var band = 0; band < coefficients.Length; band++)
            {
                var c = coefficients[band];
                var next = c.B0 * filtered + _z1[band, channel];
                _z1[band, channel] = c.B1 * filtered - c.A1 * next + _z2[band, channel];
                _z2[band, channel] = c.B2 * filtered - c.A2 * next;
                filtered = next;
            }
            var scaled = filtered * outputGain;
            scaled *= (channel & 1) == 0 ? leftGain : rightGain; // even channels = left side, odd = right side
            if (Math.Abs(scaled) > (limiter ? 2.0 : 1.0)) clipped = true; // with the limiter on, only flag peaks well past full scale
            var output = Math.Clamp(limiter ? EqAnalysis.SoftLimit(scaled) : scaled, -.98, .98);
            peak = Math.Max(peak, Math.Abs(output));
            if (channel == 0)
            {
                _scope[_scopeWrite & 4095] = (float)output;
                Volatile.Write(ref _scopeWrite, (_scopeWrite + 1) & 0x3FFFFFFF);
            }
            buffer[index] = (float)output;
        }

        Volatile.Write(ref _scopeRate, sampleRate);
        if (clipped) Interlocked.Increment(ref _clipCount);
        Volatile.Write(ref _peak, peak);
        Volatile.Write(ref _inputPeak, inputPeak);
    }
}
