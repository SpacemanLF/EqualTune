using EqualTune.AudioLab;

namespace EqualTune.Shell;

/// <summary>
/// Moves audio through the shared EQ: records from a PulseAudio/PipeWire source (or generates a test tone),
/// runs the samples through <see cref="EqProcessor"/>, and plays them to the chosen output.
/// </summary>
internal sealed class LinuxRouter : IDisposable
{
    private const int Rate = 48000;
    private const int Channels = 2;
    // ~43 ms by default; bigger blocks survive a busy computer without stuttering.
    // For testing, set EQUALTUNE_BLOCK (frames, 512-16384) before starting, e.g. EQUALTUNE_BLOCK=8192.
    private static readonly int BlockFrames = int.TryParse(Environment.GetEnvironmentVariable("EQUALTUNE_BLOCK"), out var block) ? Math.Clamp(block, 512, 16384) : 2048;

    private readonly string? _source;      // null = generate a tone instead of recording
    private readonly string _target;
    private readonly bool _sweep;
    private readonly double _toneHz;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private volatile bool _stop;
    private volatile string? _lastError;
    private Exception? _startupError;
    private IntPtr _record, _play;

    private long _latencyUs;

    public EqProcessor Processor { get; }
    /// <summary>Rough delay EqualTune adds, in milliseconds (the sound server's buffers plus one block of waiting).</summary>
    public double LatencyMs => Volatile.Read(ref _latencyUs) / 1000.0;
    public string? LastError => _lastError;
    public bool IsTone => _source is null;
    public bool IsSweep => _sweep;

    public LinuxRouter(EqProcessor processor, string? sourceName, string targetSink, double toneHz = 1000, bool sweep = false)
    {
        Processor = processor;
        _source = sourceName;
        _target = targetSink;
        _toneHz = toneHz;
        _sweep = sweep;
        _thread = new Thread(Run) { IsBackground = true, Name = "EqualTune audio", Priority = ThreadPriority.Highest };
        _thread.Start();
        if (!_ready.Wait(TimeSpan.FromSeconds(8)))
        {
            Dispose();
            throw new TimeoutException("The sound server did not respond in time.");
        }
        if (_startupError is not null)
        {
            var error = _startupError;
            Dispose();
            throw new InvalidOperationException(error.Message, error);
        }
    }

    private void Run()
    {
        try
        {
            const uint Unset = uint.MaxValue;
            var blockBytes = (uint)(BlockFrames * Channels * sizeof(float));
            if (_source is not null)
                _record = PulseSimple.Open(PulseSimple.Record, _source, "EqualTune input", Rate, Channels,
                    new PulseSimple.BufferAttr { MaxLength = Unset, TLength = Unset, PreBuf = Unset, MinReq = Unset, FragSize = blockBytes });
            _play = PulseSimple.Open(PulseSimple.Playback, _target, "EqualTune output", Rate, Channels,
                new PulseSimple.BufferAttr { MaxLength = Unset, TLength = blockBytes * 4, PreBuf = Unset, MinReq = Unset, FragSize = Unset });
            _ready.Set();

            var buffer = new float[BlockFrames * Channels];
            var latencyTick = 0;
            var phase = 0.0;
            var sweepPosition = 0.0;
            while (!_stop)
            {
                if (_source is not null)
                {
                    if (PulseSimple.pa_simple_read(_record, buffer, (UIntPtr)blockBytes, out var readError) < 0)
                        throw new InvalidOperationException("Reading the audio input failed: " + PulseSimple.Error(readError));
                }
                else
                {
                    for (var frame = 0; frame < BlockFrames; frame++)
                    {
                        var hz = _toneHz;
                        if (_sweep)
                        {
                            // Exponential sweep 100 Hz -> 10 kHz over 8 seconds, then repeat.
                            hz = 100 * Math.Pow(100, sweepPosition);
                            sweepPosition += 1.0 / (8 * Rate);
                            if (sweepPosition >= 1) sweepPosition = 0;
                        }
                        phase += 2 * Math.PI * hz / Rate;
                        if (phase > 2 * Math.PI) phase -= 2 * Math.PI;
                        var sample = (float)(Math.Sin(phase) * 0.25);
                        buffer[frame * Channels] = sample;
                        buffer[frame * Channels + 1] = sample;
                    }
                }
                if (_stop) break;
                Processor.Process(buffer, BlockFrames, Channels, Rate);
                if (PulseSimple.pa_simple_write(_play, buffer, (UIntPtr)blockBytes, out var writeError) < 0)
                    throw new InvalidOperationException("Playing to the output failed: " + PulseSimple.Error(writeError));
                if (++latencyTick % 8 == 0) UpdateLatency();
            }
        }
        catch (DllNotFoundException)
        {
            Fail(new InvalidOperationException("The PulseAudio library was not found. Install it with: sudo apt install libpulse0"));
        }
        catch (Exception ex)
        {
            Fail(ex);
        }
        finally
        {
            Free();
            _ready.Set();
        }
    }

    private void UpdateLatency()
    {
        try
        {
            ulong us = 0;
            if (_record != IntPtr.Zero) { var a = PulseSimple.pa_simple_get_latency(_record, out _); if (a != ulong.MaxValue) us += a; }
            if (_play != IntPtr.Zero) { var b = PulseSimple.pa_simple_get_latency(_play, out _); if (b != ulong.MaxValue) us += b; }
            // pa_simple hands over audio a whole block at a time, so one block of the delay comes from waiting for it.
            if (_source is not null) us += (ulong)(BlockFrames * 1_000_000L / Rate);
            Volatile.Write(ref _latencyUs, (long)us);
        }
        catch { }
    }

    private void Fail(Exception ex)
    {
        if (_stop) return; // an error caused by shutting down is not interesting
        _startupError ??= ex;
        _lastError = ex.Message;
    }

    private void Free()
    {
        if (_record != IntPtr.Zero) { PulseSimple.pa_simple_free(_record); _record = IntPtr.Zero; }
        if (_play != IntPtr.Zero) { PulseSimple.pa_simple_free(_play); _play = IntPtr.Zero; }
    }

    public void Dispose()
    {
        _stop = true;
        // The thread may be blocked waiting for audio; give it a moment, then let it finish by itself.
        _thread.Join(1500);
    }
}
