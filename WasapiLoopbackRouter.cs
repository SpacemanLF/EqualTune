using System.ComponentModel;
using System.Runtime.InteropServices;

namespace EqualTune.AudioLab;

/// <summary>
/// Prototype bridge: captures a render endpoint with WASAPI loopback, applies one
/// peaking EQ, then renders the samples to a different endpoint.
/// </summary>
internal sealed class WasapiLoopbackRouter : IDisposable
{
    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var comType = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)(Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("Windows did not create the audio device enumerator."));
    }

    private const uint SharedMode = 0;
    private const uint Loopback = 0x00020000;
    private const uint EventCallback = 0x00040000;
    private const uint SilentBuffer = 0x2;
    private const int WaitObject0 = 0;
    private const int ChannelsExpectedMinimum = 1;
    private readonly string _sourceId;
    private readonly string _targetId;
    private readonly bool _loopback;
    private readonly object _parameterLock = new();
    private readonly ManualResetEvent _ready = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _thread;
    private AudioEqBand[] _bands = [new(1000, 0, 1)];
    private double _volume = .4, _peak, _inputPeak;
    private bool _autoPreamp = false, _bypass, _matchLevel = true;
    private double _manualPreampDb, _cachedManualPreamp;
    // Sound controls from the UI: attenuation (dB, <= 0), equal loudness (0-100 %), limiter on/off, balance (-100 left .. +100 right).
    private double _attenuationDb, _loudness, _balance;
    private bool _limiter = true;
    private double _cachedAttenuation = double.NaN, _cachedLoudness = double.NaN;
    private long _clipCount;
    private double _preampDb;
    // Render-thread cache: recomputed only when the bands, rate or options change.
    private BiquadCoefficients[] _coefficients = [];
    private AudioEqBand[]? _cachedBands;
    private int _cachedRate;
    private bool _cachedAuto, _cachedBypass, _cachedMatch;
    private double _outputGain = 1;
    private double _systemGainTarget = 1, _systemGainSmoothed = -1;
    private readonly float[] _scope = new float[4096];
    private int _scopeWrite, _scopeRate;
    private volatile bool _disposed;
    private volatile string? _lastError;
    private Exception? _startupError;

    public double Peak => Volatile.Read(ref _peak);
    public double InputPeak => Volatile.Read(ref _inputPeak);
    public string? LastError => _lastError;
    /// <summary>Automatic headroom currently applied (0 or negative dB).</summary>
    public double PreampDb => Volatile.Read(ref _preampDb);
    /// <summary>Number of audio buffers that needed the limiter since routing started.</summary>
    public long ClipCount => Interlocked.Read(ref _clipCount);

    public WasapiLoopbackRouter(string sourceId, string targetId, bool loopback)
    {
        _sourceId = sourceId;
        _targetId = targetId;
        _loopback = loopback;
        _thread = new Thread(RouteThread) { IsBackground = true, Name = "EqualTune WASAPI loopback route" };
        _thread.SetApartmentState(ApartmentState.MTA);
        _thread.Start();
        if (!_ready.WaitOne(TimeSpan.FromSeconds(8)))
        {
            Dispose();
            throw new TimeoutException("Windows audio routing did not initialize in time.");
        }
        if (_startupError is not null)
        {
            var error = _startupError;
            Dispose();
            throw new InvalidOperationException(error.Message, error);
        }
    }

    /// <summary>Linear gain from the Windows volume slider (and mute) of the virtual cable the apps play into.</summary>
    public void SetSystemGain(double gain) => Volatile.Write(ref _systemGainTarget, Math.Clamp(gain, 0, 1));

    public void SetParameters(double frequency, double gainDb, double q, double volume)
    {
        lock (_parameterLock)
        {
            _bands = [new(Math.Clamp(frequency, 20, 20000), Math.Clamp(gainDb, -12, 12), Math.Clamp(q, .2, 10))];
            _volume = Math.Clamp(volume, 0, 1);
        }
    }

    public void SetBands(AudioEqBand[] bands)
    {
        lock (_parameterLock) _bands = bands.Take(12).ToArray();
    }

    /// <summary>Recent output as log-spaced dBFS levels (20 Hz-20 kHz), or null if nothing has played yet.</summary>
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

    public void SetEngineOptions(bool autoPreamp, bool bypass, bool matchLevel, double manualPreampDb = 0)
    {
        lock (_parameterLock) { _autoPreamp = autoPreamp; _bypass = bypass; _matchLevel = matchLevel; _manualPreampDb = Math.Clamp(manualPreampDb, -24, 24); }
    }

    public void SetSoundControls(double attenuationDb, double loudnessPercent, bool limiter, double balance)
    {
        lock (_parameterLock)
        {
            _attenuationDb = Math.Clamp(attenuationDb, -24, 0);
            _loudness = Math.Clamp(loudnessPercent, 0, 100);
            _limiter = limiter;
            _balance = Math.Clamp(balance, -100, 100);
        }
    }

    /// <summary>What the render thread is using right now (for the on-screen readout).</summary>
    public string SoundSummary
    {
        get
        {
            lock (_parameterLock)
                return $"attenuation {_attenuationDb:0.#} dB · loudness {_loudness:0}% · limiter {(_limiter ? "on" : "off")} · balance {(_balance == 0 ? "center" : $"{Math.Abs(_balance):0}% {(_balance < 0 ? "left" : "right")}")}";
        }
    }

    public void Start() { }

    private void RouteThread()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? sourceDevice = null, targetDevice = null;
        IAudioClient? captureClient = null, renderClient = null;
        IAudioCaptureClient? capture = null;
        IAudioRenderClient? render = null;
        IntPtr sourceFormatPointer = IntPtr.Zero, targetFormatPointer = IntPtr.Zero;
        IntPtr captureEvent = IntPtr.Zero, renderEvent = IntPtr.Zero;
        var comInitialized = false;
        try
        {
            var coResult = CoInitializeEx(IntPtr.Zero, 0);
            Marshal.ThrowExceptionForHR(coResult);
            comInitialized = true;
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(_sourceId, out sourceDevice));
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(_targetId, out targetDevice));

            var audioClientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(sourceDevice.Activate(ref audioClientId, 23, IntPtr.Zero, out var captureObject));
            captureClient = (IAudioClient)captureObject;
            Marshal.ThrowExceptionForHR(targetDevice.Activate(ref audioClientId, 23, IntPtr.Zero, out var renderObject));
            renderClient = (IAudioClient)renderObject;

            Marshal.ThrowExceptionForHR(captureClient.GetMixFormat(out sourceFormatPointer));
            Marshal.ThrowExceptionForHR(renderClient.GetMixFormat(out targetFormatPointer));
            var sourceFormat = WasapiAudioFormat.Read(sourceFormatPointer);
            var targetFormat = WasapiAudioFormat.Read(targetFormatPointer);
            if (sourceFormat.Channels < ChannelsExpectedMinimum)
                throw new NotSupportedException("The selected capture device reports no audio channels.");

            const long bufferDuration = 2_000_000; // 200 ms, in 100 ns units.
            var captureFlags = EventCallback | (_loopback ? Loopback : 0);
            Marshal.ThrowExceptionForHR(captureClient.Initialize(SharedMode, captureFlags, bufferDuration, 0, sourceFormatPointer, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(renderClient.Initialize(SharedMode, EventCallback, bufferDuration, 0, targetFormatPointer, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(captureClient.GetBufferSize(out _));
            Marshal.ThrowExceptionForHR(renderClient.GetBufferSize(out var renderBufferFrames));

            captureEvent = CreateEvent(IntPtr.Zero, false, false, null);
            renderEvent = CreateEvent(IntPtr.Zero, false, false, null);
            if (captureEvent == IntPtr.Zero || renderEvent == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(captureClient.SetEventHandle(captureEvent));
            Marshal.ThrowExceptionForHR(renderClient.SetEventHandle(renderEvent));

            var captureServiceId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");
            Marshal.ThrowExceptionForHR(captureClient.GetService(ref captureServiceId, out var captureService));
            capture = (IAudioCaptureClient)captureService;
            var renderServiceId = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            Marshal.ThrowExceptionForHR(renderClient.GetService(ref renderServiceId, out var renderService));
            render = (IAudioRenderClient)renderService;

            var sampleQueue = new List<float>();
            var z1 = new double[14, targetFormat.Channels];
            var z2 = new double[14, targetFormat.Channels];
            var sourcePosition = 0.0;
            Marshal.ThrowExceptionForHR(renderClient.Start());
            Marshal.ThrowExceptionForHR(captureClient.Start());
            _ready.Set();

            var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle(), captureEvent, renderEvent };
            var maximumQueuedSamples = sourceFormat.SampleRate * targetFormat.Channels / 2;
            while (!_disposed)
            {
                var waitResult = WaitForMultipleObjects((uint)handles.Length, handles, false, 500);
                if (waitResult == WaitObject0) break;
                if (waitResult < WaitObject0 + 1 || waitResult > WaitObject0 + 2) continue;

                DrainCapture(capture, sourceFormat, targetFormat.Channels, sampleQueue);
                RenderAvailable(renderClient, render, sourceFormat, targetFormat, sampleQueue, ref sourcePosition, z1, z2, renderBufferFrames);
                if (sampleQueue.Count > maximumQueuedSamples)
                {
                    var extraFrames = (sampleQueue.Count - maximumQueuedSamples) / targetFormat.Channels;
                    if (extraFrames > 0)
                    {
                        sampleQueue.RemoveRange(0, extraFrames * targetFormat.Channels);
                        sourcePosition = Math.Max(0, sourcePosition - extraFrames);
                    }
                }
            }
            captureClient.Stop();
            renderClient.Stop();
        }
        catch (Exception ex)
        {
            _startupError ??= ex;
            _lastError = ex.Message;
            _ready.Set();
        }
        finally
        {
            if (captureEvent != IntPtr.Zero) CloseHandle(captureEvent);
            if (renderEvent != IntPtr.Zero) CloseHandle(renderEvent);
            if (sourceFormatPointer != IntPtr.Zero) CoTaskMemFree(sourceFormatPointer);
            if (targetFormatPointer != IntPtr.Zero) CoTaskMemFree(targetFormatPointer);
            CoreAudioDevices.Release(capture);
            CoreAudioDevices.Release(render);
            CoreAudioDevices.Release(captureClient);
            CoreAudioDevices.Release(renderClient);
            CoreAudioDevices.Release(sourceDevice);
            CoreAudioDevices.Release(targetDevice);
            CoreAudioDevices.Release(enumerator);
            if (comInitialized) CoUninitialize();
            _ready.Set();
        }
    }

    private void DrainCapture(IAudioCaptureClient capture, WasapiAudioFormat format, int outputChannels, List<float> queue)
    {
        while (true)
        {
            Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out var packetFrames));
            if (packetFrames == 0) return;
            Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out var frames, out var flags, out _, out _));
            try
            {
                var packetPeak = 0.0;
                for (var frame = 0; frame < frames; frame++)
                for (var channel = 0; channel < format.Channels; channel++)
                {
                    var sample = (flags & SilentBuffer) != 0 ? 0 : format.ReadSample(data, checked((int)(frame * format.BlockAlign + channel * format.BytesPerSample)));
                    packetPeak = Math.Max(packetPeak, Math.Abs(sample));
                }
                for (var frame = 0; frame < frames; frame++)
                {
                    if (outputChannels == 1)
                    {
                        var mixed = 0.0f;
                        if (format.Channels == 1)
                            mixed = ReadCapturedSample(data, format, frame, 0, flags);
                        else
                        {
                            for (var channel = 0; channel < format.Channels; channel++)
                                mixed += ReadCapturedSample(data, format, frame, channel, flags) / format.Channels;
                        }
                        queue.Add(mixed);
                    }
                    else
                    {
                        for (var channel = 0; channel < outputChannels; channel++)
                        {
                            var sourceChannel = format.Channels == 1 ? 0 : Math.Min(channel, format.Channels - 1);
                            queue.Add(ReadCapturedSample(data, format, frame, sourceChannel, flags));
                        }
                    }
                }
                Volatile.Write(ref _inputPeak, packetPeak);
            }
            finally { Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames)); }
        }
    }

    private static float ReadCapturedSample(IntPtr data, WasapiAudioFormat format, int frame, int channel, uint flags)
        => (flags & SilentBuffer) != 0 ? 0 : format.ReadSample(data, checked((int)(frame * format.BlockAlign + channel * format.BytesPerSample)));

    private void RenderAvailable(IAudioClient client, IAudioRenderClient render, WasapiAudioFormat sourceFormat, WasapiAudioFormat format, List<float> queue, ref double sourcePosition, double[,] z1, double[,] z2, uint bufferFrames)
    {
        Marshal.ThrowExceptionForHR(client.GetCurrentPadding(out var padding));
        var frames = bufferFrames > padding ? bufferFrames - padding : 0;
        if (frames == 0) return;
        Marshal.ThrowExceptionForHR(render.GetBuffer(frames, out var data));
        AudioEqBand[] bands;
        var volume = .4;
        bool autoPreamp, bypass, matchLevel, limiter;
        double manualPreamp;
        double attenuation, loudness, balance;
        lock (_parameterLock) { bands = _bands; volume = _volume; autoPreamp = _autoPreamp; manualPreamp = _manualPreampDb; bypass = _bypass; matchLevel = _matchLevel; attenuation = _attenuationDb; loudness = _loudness; limiter = _limiter; balance = _balance; }
        if (!ReferenceEquals(bands, _cachedBands) || format.SampleRate != _cachedRate || autoPreamp != _cachedAuto || manualPreamp != _cachedManualPreamp || bypass != _cachedBypass || matchLevel != _cachedMatch || attenuation != _cachedAttenuation || loudness != _cachedLoudness)
        {
            // Equal loudness adds a gentle bass and treble lift (a simplified equal-loudness contour) after the user's bands.
            var effective = bands;
            if (loudness > 0 && !bypass)
            {
                var amount = loudness / 100.0;
                effective = [.. bands, new AudioEqBand(100, 9 * amount, .7, FilterNames.LowShelf), new AudioEqBand(9000, 4.5 * amount, .7, FilterNames.HighShelf)];
            }
            var analysis = EqAnalysis.Analyze(effective, format.SampleRate);
            var preamp = autoPreamp ? -Math.Max(0, analysis.PeakDb) : manualPreamp;
            _coefficients = bypass ? [] : effective.Select(b => BiquadCoefficients.Create(format.SampleRate, b)).ToArray();
            // Bypass is level-matched to the EQ'd sound so A/B compares tone, not loudness.
            var gainDb = bypass ? (matchLevel ? Math.Clamp(analysis.MeanDb + preamp, -12, 12) : 0) : preamp;
            _outputGain = Math.Pow(10, (gainDb + attenuation) / 20);
            Volatile.Write(ref _preampDb, preamp);
            _cachedBands = bands; _cachedRate = format.SampleRate; _cachedAuto = autoPreamp; _cachedManualPreamp = manualPreamp; _cachedBypass = bypass; _cachedMatch = matchLevel; _cachedAttenuation = attenuation; _cachedLoudness = loudness;
        }
        var leftGain = balance > 0 ? 1 - balance / 100 : 1.0;
        var rightGain = balance < 0 ? 1 + balance / 100 : 1.0;
        var coefficients = _coefficients;
        var outputGain = _outputGain;
        var sysTarget = Volatile.Read(ref _systemGainTarget);
        var sysStart = _systemGainSmoothed < 0 ? sysTarget : _systemGainSmoothed;
        _systemGainSmoothed = sysTarget;
        var clipped = false;
        var peak = 0.0;
        var allSilent = true;
        var queuedFrames = queue.Count / format.Channels;
        var resampleStep = (double)sourceFormat.SampleRate / format.SampleRate;
        for (var frame = 0; frame < frames; frame++)
        for (var channel = 0; channel < format.Channels; channel++)
        {
            var source = 0.0;
            var firstFrame = (int)sourcePosition;
            if (firstFrame + 1 < queuedFrames)
            {
                var fraction = sourcePosition - firstFrame;
                var first = queue[firstFrame * format.Channels + channel];
                var second = queue[(firstFrame + 1) * format.Channels + channel];
                source = first + (second - first) * fraction;
                if (channel == format.Channels - 1) sourcePosition += resampleStep;
            }
            else if (channel == format.Channels - 1)
            {
                // Keep the fractional source position while waiting for the next capture packet.
            }
            var filtered = source;
            for (var band = 0; band < coefficients.Length; band++)
            {
                var c = coefficients[band];
                var next = c.B0 * filtered + z1[band, channel];
                z1[band, channel] = c.B1 * filtered - c.A1 * next + z2[band, channel];
                z2[band, channel] = c.B2 * filtered - c.A2 * next;
                filtered = next;
            }
            var sysGain = sysStart + (sysTarget - sysStart) * (frame + 1) / (double)frames; // glide to the new volume across this block
            var scaled = filtered * volume * outputGain * sysGain;
            scaled *= (channel & 1) == 0 ? leftGain : rightGain; // even channels = left side, odd = right side
            if (Math.Abs(scaled) > 1.0) clipped = true;
            var output = Math.Clamp(limiter ? EqAnalysis.SoftLimit(scaled) : scaled, -.98, .98);
            if (Math.Abs(output) > 0.000001) allSilent = false;
            peak = Math.Max(peak, Math.Abs(output));
            if (channel == 0)
            {
                _scope[_scopeWrite & 4095] = (float)output;
                Volatile.Write(ref _scopeWrite, (_scopeWrite + 1) & 0x3FFFFFFF);
            }
            format.WriteSample(data, checked((int)(frame * format.BlockAlign + channel * format.BytesPerSample)), output);
        }
        var consumedFrames = Math.Min((int)sourcePosition, Math.Max(0, queuedFrames - 1));
        if (consumedFrames > 0)
        {
            queue.RemoveRange(0, consumedFrames * format.Channels);
            sourcePosition -= consumedFrames;
        }
        Volatile.Write(ref _scopeRate, format.SampleRate);
        if (clipped) Interlocked.Increment(ref _clipCount);
        Volatile.Write(ref _peak, peak);
        Marshal.ThrowExceptionForHR(render.ReleaseBuffer(frames, allSilent ? SilentBuffer : 0));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop.Set(); } catch { }
        if (_thread.IsAlive && Thread.CurrentThread != _thread) _thread.Join(2000);
        _ready.Dispose();
        _stop.Dispose();
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator { [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices); [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device); [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device); [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client); [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    private interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice { [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance); [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties); [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id); [PreserveSig] int GetState(out uint state); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    private interface IAudioClient { [PreserveSig] int Initialize(uint mode, uint flags, long duration, long periodicity, IntPtr format, IntPtr session); [PreserveSig] int GetBufferSize(out uint frames); [PreserveSig] int GetStreamLatency(out long latency); [PreserveSig] int GetCurrentPadding(out uint padding); [PreserveSig] int IsFormatSupported(uint mode, IntPtr format, out IntPtr closest); [PreserveSig] int GetMixFormat(out IntPtr format); [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod); [PreserveSig] int Start(); [PreserveSig] int Stop(); [PreserveSig] int Reset(); [PreserveSig] int SetEventHandle(IntPtr handle); [PreserveSig] int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object service); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317")]
    private interface IAudioCaptureClient { [PreserveSig] int GetBuffer(out IntPtr data, out uint frames, out uint flags, out ulong devicePosition, out ulong qpcPosition); [PreserveSig] int ReleaseBuffer(uint frames); [PreserveSig] int GetNextPacketSize(out uint frames); }
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    private interface IAudioRenderClient { [PreserveSig] int GetBuffer(uint frames, out IntPtr data); [PreserveSig] int ReleaseBuffer(uint frames, uint flags); }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("ole32.dll")] private static extern void CoTaskMemFree(IntPtr memory);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
