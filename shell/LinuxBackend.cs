using System.Diagnostics;
using System.Text.Json;
using EqualTune.AudioLab;
using Photino.NET;

namespace EqualTune.Shell;

/// <summary>
/// The real Linux audio engine behind the interface. EqualTune creates a virtual output ("EqualTune"),
/// moves the apps' sound onto it, captures it, applies the EQ and plays the result on the chosen device.
/// </summary>
internal static class LinuxBackend
{
    private const string Version = "0.0.0-linux-dev";

    private static readonly object Gate = new();
    private static readonly EqProcessor Processor = new();
    private static readonly LinuxSettings Settings = LinuxSettings.Load();

    private static PhotinoWindow? _window;
    private static int _uiThread;
    private static Timer? _timer;
    private static LinuxRouter? _router;
    private static bool _systemRoute;
    private static string? _restoreDefault;
    private static string _outputId = "";
    private static string _routeMode = "system", _routeSource = "";
    private static bool _spectrumEnabled = true, _spectrumSent, _statusSent;
    private static bool _eqEnabled = true, _autoPreamp = true, _bypass, _matchLevel = true;
    private static double _attenuation, _loudness, _balance;
    private static bool _limiter = true;
    private static long _lastClips = -1;
    private static double _lastPreamp = double.NaN;
    private static string _lastSound = "";
    private static int _tick;
    private static bool _pactlChecked, _pactlOk;
    private static string? _pactlProblem;

    /// <summary>Call once on the window's own thread before the window opens.</summary>
    public static void Init(PhotinoWindow window)
    {
        _window = window;
        _uiThread = Environment.CurrentManagedThreadId;
        // A crash can leave the virtual output behind; remove it so the sound menu stays clean.
        try { if (EnsurePactl()) PulseCtl.RemoveVirtualSink(); } catch { }
        _timer = new Timer(_ => Tick(), null, 200, 50);
        LinuxHotkeys.StartListener(name => Call("equalTuneHotkey", name));
    }

    public static void Shutdown()
    {
        _timer?.Dispose();
        _timer = null;
        StopRoute(notify: false);
    }

    // ---------------------------------------------------------------- messages from the interface

    public static void Handle(PhotinoWindow window, string raw)
    {
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            switch (type)
            {
                case "shellReady":
                case "refresh":
                    PushStartupState();
                    break;
                case "output":
                    _outputId = Str(root, "id") ?? _outputId;
                    if (_router is not null) RestartCurrent();
                    break;
                case "eq":
                    HandleEq(root);
                    break;
                case "tone":
                    StartTone(Num(root, "frequency", 1000), Bool(root, "sweep"));
                    break;
                case "route":
                    StartRoute(Str(root, "mode") ?? "system", Str(root, "sourceId") ?? "", Str(root, "outputId") ?? _outputId);
                    break;
                case "stop":
                    StopRoute(notify: true);
                    break;
                case "engine":
                    if (root.TryGetProperty("autoPreamp", out var ap)) _autoPreamp = ap.GetBoolean();
                    if (root.TryGetProperty("bypass", out var bp)) _bypass = bp.GetBoolean();
                    if (root.TryGetProperty("matchLevel", out var ml)) _matchLevel = ml.GetBoolean();
                    Processor.SetEngineOptions(_autoPreamp, _bypass, _matchLevel);
                    break;
                case "sound":
                    if (root.TryGetProperty("attenuation", out var att)) _attenuation = att.GetDouble();
                    if (root.TryGetProperty("loudness", out var loud)) _loudness = loud.GetDouble();
                    if (root.TryGetProperty("limiter", out var lim)) _limiter = lim.GetBoolean();
                    if (root.TryGetProperty("balance", out var bal)) _balance = bal.GetDouble();
                    Processor.SetSoundControls(_attenuation, _loudness, _limiter, _balance);
                    break;
                case "spectrum":
                    if (root.TryGetProperty("enabled", out var sp)) _spectrumEnabled = sp.GetBoolean();
                    break;
                case "hotkeys":
                    if (root.TryGetProperty("enabled", out var hk))
                    {
                        var on = hk.GetBoolean();
                        Task.Run(() =>
                        {
                            try
                            {
                                if (on) { if (!LinuxHotkeys.Register()) Console.Error.WriteLine("Hotkeys: gsettings not found; set shortcuts by hand."); }
                                else LinuxHotkeys.Unregister();
                            }
                            catch (Exception ex) { Console.Error.WriteLine("Hotkeys: " + ex.Message); }
                        });
                    }
                    break;
                case "listapps":
                    PushRunningApps();
                    break;
                case "app":
                    HandleApp(root);
                    break;
                case "update":
                    Call("equalTuneUpdate", new { state = "error", message = "Update checks are not available in this Linux preview yet." });
                    break;
                case "openurl":
                    OpenLink(Str(root, "which"));
                    break;
                default:
                    // appwatch, hotkeys, safety: not available on Linux yet.
                    break;
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not handle a message from the interface: " + ex.Message);
            Call("equalTuneShowError", "EqualTune could not apply that control: " + ex.Message);
        }
    }

    private static void HandleEq(JsonElement root)
    {
        if (root.TryGetProperty("enabled", out var enabled)) _eqEnabled = enabled.GetBoolean();
        if (root.TryGetProperty("bands", out var bands) && bands.ValueKind == JsonValueKind.Array)
        {
            var mapped = bands.EnumerateArray().Select(b => new AudioEqBand(
                b.GetProperty("frequency").GetDouble(),
                b.GetProperty("gain").GetDouble(),
                b.GetProperty("q").GetDouble(),
                FilterNames.Normalize(b.TryGetProperty("filter", out var f) ? f.GetString() : null))).ToArray();
            _bandsFromUi = mapped;
        }
        Processor.SetBands(_eqEnabled ? _bandsFromUi : []);
    }

    private static AudioEqBand[] _bandsFromUi = [];

    private static void HandleApp(JsonElement root)
    {
        if (root.TryGetProperty("closeToTray", out var ct)) Settings.CloseToTray = ct.GetBoolean();
        if (root.TryGetProperty("startAtLogin", out var sl)) Settings.StartAtLogin = sl.GetBoolean();
        if (root.TryGetProperty("startMinimized", out var sm)) Settings.StartMinimized = sm.GetBoolean();
        if (root.TryGetProperty("autoRoute", out var ar)) Settings.AutoRoute = ar.GetBoolean();
        if (root.TryGetProperty("checkUpdates", out var cu)) Settings.CheckUpdates = cu.GetBoolean();
        if (root.TryGetProperty("firstRunDone", out var fr)) Settings.FirstRunDone = fr.GetBoolean();
        Settings.Save();
        Settings.ApplyAutostart();
    }

    // ---------------------------------------------------------------- routing

    private static void StartRoute(string mode, string sourceId, string outputId)
    {
        StopRoute(notify: false);
        _routeMode = mode;
        _routeSource = sourceId;
        if (!EnsurePactl()) { Call("equalTuneShowError", _pactlProblem ?? "The sound server is not available."); return; }
        if (!string.IsNullOrEmpty(outputId)) _outputId = outputId;

        var sinks = PulseCtl.ListSinks().Where(s => s.Name != PulseCtl.VirtualSinkName).ToList();
        var target = sinks.FirstOrDefault(s => s.Name == _outputId)?.Name ?? DefaultRealSink(sinks);
        if (target is null) { Call("equalTuneShowError", "No audio output was found. Plug in or enable an output device, then press Refresh."); return; }
        _outputId = target;

        string source;
        if (mode == "system") source = PulseCtl.VirtualMonitorName;
        else if (mode == "loopback")
        {
            if (string.IsNullOrEmpty(sourceId) || sourceId == target) { Call("equalTuneShowError", "Choose different capture and output devices to prevent audio feedback."); return; }
            source = sourceId + ".monitor";
        }
        else source = sourceId;
        if (string.IsNullOrEmpty(source)) { Call("equalTuneShowError", "Choose a recording input and an output device before starting the route."); return; }

        try
        {
            _restoreDefault = null;
            if (mode == "system")
            {
                var previous = PulseCtl.GetDefaultSink();
                _restoreDefault = previous is null || previous == PulseCtl.VirtualSinkName ? target : previous;
                PulseCtl.CreateVirtualSink();
            }
            ApplyProcessorSettings();
            _router = new LinuxRouter(Processor, source, target);
            if (mode == "system")
            {
                _systemRoute = true;
                PulseCtl.SetDefaultSink(PulseCtl.VirtualSinkName);
                KeepOwnOutputOnTarget(target);
                MoveAppsToVirtual();
            }
            Console.WriteLine($"route started: {source} -> {target}");
        }
        catch (Exception ex)
        {
            StopRoute(notify: false);
            Call("equalTuneShowError", "Could not start system audio route: " + ex.Message);
        }
    }

    private static void StartTone(double hz, bool sweep)
    {
        var wasTone = _router is { IsTone: true };
        StopRoute(notify: false);
        if (wasTone) { PushStatusIdle(); return; } // pressing the button again stops the tone
        if (!EnsurePactl()) { Call("equalTuneShowError", _pactlProblem ?? "The sound server is not available."); return; }
        var sinks = PulseCtl.ListSinks().Where(s => s.Name != PulseCtl.VirtualSinkName).ToList();
        var target = sinks.FirstOrDefault(s => s.Name == _outputId)?.Name ?? DefaultRealSink(sinks);
        if (target is null) { Call("equalTuneShowError", "Select an output device before starting audio."); return; }
        try
        {
            ApplyProcessorSettings();
            _router = new LinuxRouter(Processor, null, target, Math.Clamp(hz, 40, 12000), sweep);
        }
        catch (Exception ex)
        {
            StopRoute(notify: false);
            Call("equalTuneShowError", "Could not start audio: " + ex.Message);
        }
    }

    private static void RestartCurrent()
    {
        var tone = _router is { IsTone: true } r ? r : null;
        if (tone is not null) { var sweep = tone.IsSweep; StopRoute(notify: false); StartTone(1000, sweep); return; }
        StartRoute(_routeMode, _routeSource, _outputId);
    }

    private static void StopRoute(bool notify)
    {
        LinuxRouter? router;
        lock (Gate) { router = _router; _router = null; }
        router?.Dispose();
        if (_systemRoute)
        {
            _systemRoute = false;
            try
            {
                var restore = _restoreDefault ?? _outputId;
                var virtualIndex = PulseCtl.ListSinks().FirstOrDefault(s => s.Name == PulseCtl.VirtualSinkName)?.Index;
                if (!string.IsNullOrEmpty(restore))
                {
                    try { PulseCtl.SetDefaultSink(restore); } catch { }
                    if (virtualIndex is not null) PulseCtl.MoveAppStreams(restore, onlyFromSinkIndex: virtualIndex);
                }
                PulseCtl.RemoveVirtualSink();
            }
            catch (Exception ex) { Console.Error.WriteLine("Could not restore the sound output: " + ex.Message); }
        }
        _restoreDefault = null;
        if (notify || router is not null) PushStatusIdle();
    }

    /// <summary>EqualTune's own playback must stay on the real output, or it would feed back into itself.</summary>
    private static void KeepOwnOutputOnTarget(string target)
    {
        try
        {
            foreach (var stream in PulseCtl.ListSinkInputs().Where(s => s.IsOurs))
                PulseCtl.MoveSinkInput(stream.Index, target);
        }
        catch { }
    }

    private static void MoveAppsToVirtual()
    {
        var virtualIndex = PulseCtl.ListSinks().FirstOrDefault(s => s.Name == PulseCtl.VirtualSinkName)?.Index;
        PulseCtl.MoveAppStreams(PulseCtl.VirtualSinkName, exceptFromSinkIndex: virtualIndex);
    }

    private static string? DefaultRealSink(List<PulseDevice> sinks)
    {
        var def = _restoreDefault ?? PulseCtl.GetDefaultSink();
        return sinks.FirstOrDefault(s => s.Name == def)?.Name ?? sinks.FirstOrDefault()?.Name;
    }

    private static void ApplyProcessorSettings()
    {
        Processor.SetBands(_eqEnabled ? _bandsFromUi : []);
        Processor.SetEngineOptions(_autoPreamp, _bypass, _matchLevel);
        Processor.SetSoundControls(_attenuation, _loudness, _limiter, _balance);
    }

    // ---------------------------------------------------------------- status loop (runs on a timer thread)

    private static void Tick()
    {
        try
        {
            var router = _router;
            _tick++;
            if (router is null)
            {
                if (_spectrumSent && _tick % 4 == 0) { _spectrumSent = false; Call("equalTuneSetSpectrum", Array.Empty<double>()); }
                return;
            }
            if (router.LastError is { } error)
            {
                StopRoute(notify: false);
                Call("equalTuneShowError", "Audio stopped: " + error);
                return;
            }
            if (_tick % 2 == 0) // status every 100 ms
            {
                var state = router.IsTone ? (router.IsSweep ? "EQ sweep" : "Test tone") : "Routing";
                var input = (int)Math.Clamp(router.Processor.InputPeak * 100, 0, 100);
                var output = (int)Math.Clamp(router.Processor.Peak * 100, 0, 100);
                _statusSent = true;
                if (_tick % 20 == 0) Console.WriteLine($"levels: input {router.Processor.InputPeak:0.000}  output {router.Processor.Peak:0.000}  preamp {Processor.PreampDb:0.0} dB  clips {Processor.ClipCount}");
                Call("equalTuneSetStatus", state, input, output);
                var clips = Processor.ClipCount;
                var preamp = Processor.PreampDb;
                var sound = Processor.SoundSummary;
                if (clips != _lastClips || double.IsNaN(_lastPreamp) || Math.Abs(preamp - _lastPreamp) > .05 || sound != _lastSound)
                {
                    _lastClips = clips; _lastPreamp = preamp; _lastSound = sound;
                    Call("equalTuneSetEngine", new { preampDb = Math.Round(preamp, 1), clips, sound });
                }
            }
            if (_spectrumEnabled && _tick % 2 == 1 && Processor.GetSpectrum(64) is { } levels)
            {
                _spectrumSent = true;
                Call("equalTuneSetSpectrum", levels.Select(v => Math.Round(v, 1)).ToArray());
            }
            if (_systemRoute && _tick % 30 == 0) { KeepOwnOutputOnTarget(_outputId); MoveAppsToVirtual(); } // apps that start later follow the virtual output too
        }
        catch (Exception ex) { Console.Error.WriteLine("status loop: " + ex.Message); }
    }

    private static void PushStatusIdle()
    {
        if (_statusSent) { _statusSent = false; Call("equalTuneSetStatus", "Ready", 0, 0); }
        _lastClips = -1; _lastPreamp = double.NaN;
    }

    // ---------------------------------------------------------------- pushing state to the interface

    private static void PushStartupState()
    {
        Call("equalTuneSetAppSettings", new
        {
            closeToTray = Settings.CloseToTray, startAtLogin = Settings.StartAtLogin, startMinimized = Settings.StartMinimized,
            autoRoute = Settings.AutoRoute, checkUpdates = Settings.CheckUpdates, firstRunDone = Settings.FirstRunDone,
        });
        Call("equalTuneSetAbout", new { version = Version, donateReady = true, logs = LinuxSettings.Folder });
        PushDevices();
    }

    private static void PushDevices()
    {
        if (!EnsurePactl())
        {
            Call("equalTuneSetDevices", new { outputs = Array.Empty<object>(), playbacks = Array.Empty<object>(), captures = Array.Empty<object>(), virtualSink = false, virtualSinkCaptureId = (string?)null });
            Call("equalTuneNotice", _pactlProblem ?? "The sound server is not available.");
            return;
        }
        try
        {
            var defaultSink = _restoreDefault ?? PulseCtl.GetDefaultSink();
            var sinks = PulseCtl.ListSinks().Where(s => s.Name != PulseCtl.VirtualSinkName).ToList();
            if (string.IsNullOrEmpty(_outputId) || sinks.All(s => s.Name != _outputId)) _outputId = defaultSink ?? sinks.FirstOrDefault()?.Name ?? "";
            var outputs = sinks.Select(s => new { id = s.Name, name = s.Description, isDefault = s.Name == defaultSink }).ToArray();
            var captures = PulseCtl.ListSources().Where(s => !s.IsMonitor).Select(s => new { id = s.Name, name = s.Description })
                .Prepend(new { id = PulseCtl.VirtualMonitorName, name = "EqualTune virtual output" }).ToArray();
            Call("equalTuneSetDevices", new { outputs, playbacks = outputs, captures, virtualSink = true, virtualSinkCaptureId = PulseCtl.VirtualMonitorName });
        }
        catch (Exception ex) { Call("equalTuneShowError", "Could not read the sound devices: " + ex.Message); }
    }

    private static void PushRunningApps()
    {
        var apps = new List<object>();
        try
        {
            foreach (var name in PulseCtl.ListSinkInputs().Select(s => s.AppName).Where(n => n.Length > 0 && n != PulseCtl.StreamAppName).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n))
                apps.Add(new { name, title = name });
        }
        catch { }
        Call("equalTuneSetApps", apps);
    }

    private static bool EnsurePactl()
    {
        if (_pactlChecked && _pactlOk) return true;
        _pactlOk = PulseCtl.IsAvailable(out _pactlProblem);
        _pactlChecked = true;
        return _pactlOk;
    }

    /// <summary>Calls window.&lt;function&gt;(args) inside the interface (the bridge script in the page does the actual call).</summary>
    private static void Call(string function, params object?[] args)
    {
        var window = _window;
        if (window is null) return;
        var json = JsonSerializer.Serialize(new { fn = function, args });
        if (Environment.CurrentManagedThreadId == _uiThread) window.SendWebMessage(json);
        else window.Invoke(() => window.SendWebMessage(json));
    }

    private static void OpenLink(string? which)
    {
        var target = which switch
        {
            "donate" => "https://buymeacoffee.com/equaltune",
            "community" => "https://www.reddit.com/r/EqualTune/",
            "logs" => LinuxSettings.Folder,
            _ => null,
        };
        if (target is null) return;
        try { Process.Start(new ProcessStartInfo("xdg-open", target) { UseShellExecute = false }); }
        catch (Exception ex) { Console.Error.WriteLine("Could not open " + target + ": " + ex.Message); }
    }

    private static string? Str(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static double Num(JsonElement root, string name, double fallback) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : fallback;
    private static bool Bool(JsonElement root, string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
}

/// <summary>App options saved in ~/.config/equaltune/settings.json.</summary>
internal sealed class LinuxSettings
{
    public bool CloseToTray { get; set; }
    public bool StartAtLogin { get; set; }
    public bool StartMinimized { get; set; }
    public bool AutoRoute { get; set; }
    public bool CheckUpdates { get; set; }
    public bool FirstRunDone { get; set; }

    public static string Folder
    {
        get
        {
            var root = Environment.GetEnvironmentVariable("XDG_CONFIG_HOME");
            if (string.IsNullOrEmpty(root)) root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config");
            return Path.Combine(root, "equaltune");
        }
    }

    private static string FilePath => Path.Combine(Folder, "settings.json");

    public static LinuxSettings Load()
    {
        try { if (File.Exists(FilePath)) return JsonSerializer.Deserialize<LinuxSettings>(File.ReadAllText(FilePath)) ?? new LinuxSettings(); }
        catch (Exception ex) { Console.Error.WriteLine("Could not read settings.json: " + ex.Message); }
        return new LinuxSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not save settings.json: " + ex.Message); }
    }

    /// <summary>Adds or removes the "start at login" entry (a .desktop file in ~/.config/autostart).</summary>
    public void ApplyAutostart()
    {
        try
        {
            var folder = Path.Combine(Path.GetDirectoryName(Folder)!, "autostart");
            var file = Path.Combine(folder, "equaltune.desktop");
            var exe = Environment.ProcessPath;
            if (StartAtLogin && !string.IsNullOrEmpty(exe) && !Path.GetFileName(exe).Equals("dotnet", StringComparison.Ordinal))
            {
                Directory.CreateDirectory(folder);
                File.WriteAllText(file, $"[Desktop Entry]\nType=Application\nName=EqualTune\nExec=\"{exe}\"{(StartMinimized ? " --minimized" : "")}\nX-GNOME-Autostart-enabled=true\n");
            }
            else if (File.Exists(file)) File.Delete(file);
        }
        catch (Exception ex) { Console.Error.WriteLine("Could not update the autostart entry: " + ex.Message); }
    }
}
