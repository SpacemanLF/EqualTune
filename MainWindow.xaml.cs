using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace EqualTune.AudioLab;

public partial class MainWindow : Window
{
    private readonly DispatcherTimer _meterTimer = new() { Interval = TimeSpan.FromMilliseconds(80) };
    private readonly DispatcherTimer _spectrumTimer = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private List<CoreAudioDevice> _devices = [];
    private List<CoreAudioDevice> _captureDevices = [];
    private WasapiTonePlayer? _player;
    private WasapiLoopbackRouter? _router;
    private AudioEqBand[] _prototypeEqBands = [new(1000, 0, 1)];
    private bool _prototypeEqReceived;
    private bool _loadingDevices;
    private bool _isSweep;
    private bool _autoPreamp = false, _bypass, _matchLevel = true, _limiter = true;
    private double _manualPreampDb;
    private double _attenuationDb, _loudness, _balance;
    private readonly OutputGuard _outputGuard = new(IsVirtualCableId);
    private static List<CoreAudioDevice> _knownDevices = [];
    private string? _pendingNotice;
    private bool _spectrumEnabled = true, _spectrumSent, _hotkeysEnabled = true, _hotkeysRegistered;
    private IntPtr _windowHandle;
    private const int WmHotkey = 0x0312;
    private const uint ModAlt = 0x1, ModControl = 0x2, ModNoRepeat = 0x4000;
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    private readonly DispatcherTimer _appTimer = new() { Interval = TimeSpan.FromMilliseconds(600) };
    private string _appCandidate = "", _appReported = "";
    private int _appCandidateCount;
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    private long _lastReportedClips = -1;
    private double _lastReportedPreamp = double.NaN;
    private string _lastReportedSound = "";
    private EndpointVolume? _sysVolume;
    private readonly AppSettings _settings = AppSettings.Load();
    private TrayIcon? _tray;
    private readonly DispatcherTimer _deviceTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private string _deviceSignature = "";
    private bool _devicePollBusy;
    private readonly List<string> _outputHistory = new();   // listening outputs used so far, newest last
    private bool _recoveringOutput;
    private bool _watchdogStarted;
    private readonly DispatcherTimer _updateTimer = new() { Interval = TimeSpan.FromSeconds(20) };
    private UpdateInfo? _update;
    private bool _updateBusy;
    private bool _exiting;
    private readonly DispatcherTimer _sysVolTimer = new() { Interval = TimeSpan.FromMilliseconds(100) };

    public MainWindow()
    {
        InitializeComponent();
        var startHidden = Environment.GetCommandLineArgs().Skip(1).Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        if (startHidden) { WindowState = WindowState.Minimized; ShowInTaskbar = false; }
        _tray = new TrayIcon();
        _tray.OpenRequested += ShowFromTray;
        _tray.ToggleEqRequested += () => RunHotkey(1);
        _tray.ExitRequested += () => { _exiting = true; Close(); };
        if (Application.Current is not null) Application.Current.SessionEnding += (_, _) => _exiting = true;
        Loaded += async (_, _) =>
        {
            LoadDevices();
            var recovered = _outputGuard.RecoverAfterCrash();
            _windowHandle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(_windowHandle)?.AddHook(HotkeyHook);
            ApplyHotkeyRegistration();
            await InitializePrototypeUiAsync();
            if (recovered) AppLog.Info("Restored the Windows sound output after an unexpected close.");
            if (startHidden) { Hide(); ShowInTaskbar = true; WindowState = WindowState.Normal; }
            if (recovered) _pendingNotice = "EqualTune restored your Windows sound output after it last closed unexpectedly.";
        };
        AppDomain.CurrentDomain.ProcessExit += (_, _) => OutputGuard.EmergencyRestore();
        AppDomain.CurrentDomain.UnhandledException += (_, _) => OutputGuard.EmergencyRestore();
        if (Application.Current is not null) Application.Current.DispatcherUnhandledException += (_, _) => OutputGuard.EmergencyRestore();
        Closing += (_, e) =>
        {
            if (!_exiting && _settings.CloseToTray)
            {
                e.Cancel = true;
                HideToTray();
                return;
            }
            StopPlayback();
        };
        _meterTimer.Tick += (_, _) => UpdateMeter();
        _meterTimer.Start();
        _spectrumTimer.Tick += (_, _) => UpdateSpectrum();
        _spectrumTimer.Start();
        _appTimer.Tick += (_, _) => UpdateForegroundApp();
        _sysVolTimer.Tick += (_, _) => SyncSystemVolume();
        _deviceTimer.Tick += async (_, _) => await PollDevicesAsync();
        _updateTimer.Tick += async (_, _) =>
        {
            _updateTimer.Interval = TimeSpan.FromHours(12);
            if (_settings.CheckUpdates) await CheckForUpdatesAsync(false);
        };
        _updateTimer.Start();
        _deviceTimer.Start();
        DrawResponse();
    }

    private async Task InitializePrototypeUiAsync()
    {
        try
        {
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, AppInfo.PrepareWebViewData());
            await PrototypeUi.EnsureCoreWebView2Async(environment);
            PrototypeUi.CoreWebView2.WebMessageReceived += PrototypeUi_WebMessageReceived;
            PrototypeUi.CoreWebView2.NavigationCompleted += (_, _) => { PushAppSettings(); PushPrototypeDevices(); PushAbout(); };
            var page = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "EqualizerPrototype.html");
            PrototypeUi.Source = new Uri(page);
        }
        catch (Exception ex)
        {
            AppLog.Error("The interface could not be started", ex);
            PrototypeUi.Visibility = Visibility.Collapsed;
            LegacyUi.Visibility = Visibility.Visible;
            ShowError("The EqualTune interface could not be started: " + ex.Message);
        }
    }

    public void RequestExit()
    {
        _exiting = true;
        Close();
    }

    private void PushUpdate(object state)
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneUpdate && window.equalTuneUpdate({JsonSerializer.Serialize(state)});");
    }

    private async Task CheckForUpdatesAsync(bool manual)
    {
        if (_updateBusy) return;
        _updateBusy = true;
        try
        {
            if (manual) PushUpdate(new { state = "checking" });
            var info = await UpdateChecker.CheckAsync();
            if (info is not null)
            {
                _update = info;
                AppLog.Info("Update available: " + info.Version);
                PushUpdate(new { state = "available", version = info.Version, notes = info.Notes, canInstall = info.DownloadUrl is not null });
            }
            else if (manual) PushUpdate(new { state = "current" });
        }
        catch (Exception ex)
        {
            AppLog.Error("Update check failed", ex);
            if (manual) PushUpdate(new { state = "error", message = "Could not check for updates. Check your internet connection." });
        }
        finally { _updateBusy = false; }
    }

    private async Task InstallUpdateAsync()
    {
        if (_update is null || _updateBusy) return;
        if (_update.DownloadUrl is null) { AppInfo.OpenUrl(_update.PageUrl); return; }
        _updateBusy = true;
        try
        {
            PushUpdate(new { state = "downloading", version = _update.Version });
            await UpdateChecker.RunInstallerAsync(_update);
            PushUpdate(new { state = "installing", version = _update.Version });
        }
        catch (Exception ex)
        {
            AppLog.Error("Update install failed", ex);
            PushUpdate(new { state = "error", message = "The update could not be downloaded. You can get it from the releases page." });
        }
        finally { _updateBusy = false; }
    }

    private void HideToTray()
    {
        Hide();
        if (!_settings.TrayTipShown)
        {
            _settings.TrayTipShown = true;
            _settings.Save();
            _tray?.Balloon("EqualTune is still running", "Right-click the tray icon to open or quit it.");
        }
    }

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        Topmost = true; Topmost = false;
        Focus();
    }

    private void PushAppSettings()
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { closeToTray = _settings.CloseToTray, startWithWindows = _settings.StartWithWindows, startMinimized = _settings.StartMinimized, autoRoute = _settings.AutoRoute, checkUpdates = _settings.CheckUpdates, firstRunDone = _settings.FirstRunDone });
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetAppSettings && window.equalTuneSetAppSettings({json});");
    }

    private void PushAbout()
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        var json = JsonSerializer.Serialize(new { version = AppInfo.Version, donateReady = AppInfo.DonateUrl.Length > 0, logs = AppLog.Folder });
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetAbout && window.equalTuneSetAbout({json});");
    }

    private void PushPrototypeDevices()
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        var outputs = _devices.Where(d => !IsVirtualRoutingPlayback(d)).Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToArray();
        var playbacks = _devices.Select(d => new { id = d.Id, name = d.Name, isDefault = d.IsDefault }).ToArray();
        var captures = _captureDevices.Select(d => new { id = d.Id, name = d.Name }).ToArray();
        var json = JsonSerializer.Serialize(new { outputs, playbacks, captures, virtualCable = FindVirtualCableCapture() is not null && _devices.Any(IsVirtualCablePlayback), virtualCableCaptureId = FindVirtualCableCapture()?.Id });
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetDevices && window.equalTuneSetDevices({json});");
    }

    private void PrototypeUi_WebMessageReceived(object? sender, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var message = JsonDocument.Parse(e.TryGetWebMessageAsString());
            var root = message.RootElement;
            var type = root.GetProperty("type").GetString();
            switch (type)
            {
                case "output":
                    SelectDevice(OutputDeviceCombo, _devices, root.GetProperty("id").GetString());
                    break;
                case "eq":
                    if (root.TryGetProperty("enabled", out var enabled)) EqToggle.IsChecked = enabled.GetBoolean();
                    if (root.TryGetProperty("bands", out var bands) && bands.GetArrayLength() > 0)
                    {
                        var selectedId = root.TryGetProperty("selected", out var selected) ? selected.GetInt32() : -1;
                        var mapped = bands.EnumerateArray().Select(b => new AudioEqBand(
                            b.GetProperty("frequency").GetDouble(),
                            b.GetProperty("gain").GetDouble(),
                            b.GetProperty("q").GetDouble(),
                            FilterNames.Normalize(b.TryGetProperty("filter", out var filterName) ? filterName.GetString() : null))).ToArray();
                        if (root.TryGetProperty("enabled", out var isEnabled) && !isEnabled.GetBoolean()) mapped = [];
                        _prototypeEqBands = mapped;
                        _prototypeEqReceived = true;
                        var target = bands.EnumerateArray().FirstOrDefault(b => b.TryGetProperty("id", out var id) && id.GetInt32() == selectedId);
                        if (target.ValueKind == JsonValueKind.Object)
                        {
                            var hz = target.GetProperty("frequency").GetDouble();
                            FrequencySlider.Value = Math.Clamp(Math.Log10(Math.Clamp(hz, 20, 20000) / 20) / 3 * 1000, 0, 1000);
                            GainSlider.Value = Math.Clamp(target.GetProperty("gain").GetDouble(), -12, 12);
                            QSlider.Value = Math.Clamp(target.GetProperty("q").GetDouble(), .2, 10);
                        }
                    }
                    ApplyParameters();
                    break;
                case "tone":
                    ToneSlider.Value = Math.Clamp(root.GetProperty("frequency").GetDouble(), 40, 12000);
                    if (_router is not null) StopPlayback();
                    if (_player is null) StartPlayback(root.TryGetProperty("sweep", out var sweep) && sweep.GetBoolean());
                    else StopPlayback();
                    break;
                case "route":
                    var mode = root.GetProperty("mode").GetString();
                    CaptureModeCombo.SelectedIndex = mode == "cable" ? 0 : mode == "microphone" ? 2 : 1;
                    SelectDevice(CaptureDeviceCombo, CaptureModeCombo.SelectedIndex == 1 ? _devices : _captureDevices, root.GetProperty("sourceId").GetString());
                    SelectDevice(OutputDeviceCombo, _devices, root.GetProperty("outputId").GetString());
                    if (_player is not null) StopPlayback();
                    if (_router is null) RouteButton_Click(this, new RoutedEventArgs()); else StopPlayback();
                    break;
                case "engine":
                    if (root.TryGetProperty("autoPreamp", out var ap)) _autoPreamp = ap.GetBoolean();
                    if (root.TryGetProperty("bypass", out var bp)) _bypass = bp.GetBoolean();
                    if (root.TryGetProperty("matchLevel", out var ml)) _matchLevel = ml.GetBoolean();
                    if (root.TryGetProperty("preampDb", out var manualDb) && manualDb.ValueKind == JsonValueKind.Number) _manualPreampDb = Math.Clamp(manualDb.GetDouble(), -24, 24);
                    _router?.SetEngineOptions(_autoPreamp, _bypass, _matchLevel, _manualPreampDb);
                    break;
                case "sound":
                    if (root.TryGetProperty("attenuation", out var att)) _attenuationDb = att.GetDouble();
                    if (root.TryGetProperty("loudness", out var loud)) _loudness = loud.GetDouble();
                    if (root.TryGetProperty("limiter", out var lim)) _limiter = lim.GetBoolean();
                    if (root.TryGetProperty("balance", out var bal)) _balance = bal.GetDouble();
                    _router?.SetSoundControls(_attenuationDb, _loudness, _limiter, _balance);
                    break;
                case "appwatch":
                    if (root.TryGetProperty("enabled", out var aw))
                    {
                        if (aw.GetBoolean()) { _appReported = ""; _appCandidate = ""; _appCandidateCount = 0; _appTimer.Start(); }
                        else _appTimer.Stop();
                    }
                    break;
                case "listapps":
                    PushRunningApps();
                    break;
                case "hotkeys":
                    if (root.TryGetProperty("enabled", out var hk)) { _hotkeysEnabled = hk.GetBoolean(); ApplyHotkeyRegistration(); }
                    break;
                case "spectrum":
                    if (root.TryGetProperty("enabled", out var sp)) _spectrumEnabled = sp.GetBoolean();
                    break;
                case "safety":
                    if (root.TryGetProperty("restoreOutput", out var restore)) _outputGuard.Enabled = restore.GetBoolean();
                    break;
                case "stop": StopPlayback(); break;
                case "update":
                    var updateAction = root.GetProperty("action").GetString();
                    if (updateAction == "check") _ = CheckForUpdatesAsync(true);
                    else if (updateAction == "install") _ = InstallUpdateAsync();
                    else if (updateAction == "page") AppInfo.OpenUrl(_update?.PageUrl ?? $"https://github.com/{UpdateChecker.Repo}/releases");
                    break;
                case "app":
                    if (root.TryGetProperty("closeToTray", out var ct)) _settings.CloseToTray = ct.GetBoolean();
                    if (root.TryGetProperty("startWithWindows", out var sw)) _settings.StartWithWindows = sw.GetBoolean();
                    if (root.TryGetProperty("startMinimized", out var sm)) _settings.StartMinimized = sm.GetBoolean();
                    if (root.TryGetProperty("autoRoute", out var ar)) _settings.AutoRoute = ar.GetBoolean();
                    if (root.TryGetProperty("checkUpdates", out var cu)) _settings.CheckUpdates = cu.GetBoolean();
                    if (root.TryGetProperty("firstRunDone", out var fr)) _settings.FirstRunDone = fr.GetBoolean();
                    _settings.Save();
                    _settings.ApplyStartup();
                    break;
                case "openurl":
                    var which = root.GetProperty("which").GetString();
                    if (which == "donate")
                    {
                        if (AppInfo.DonateUrl.Length > 0) AppInfo.OpenUrl(AppInfo.DonateUrl);
                        else { _pendingNotice = "The EqualTune support link has not been set up yet."; FlushNotice(); }
                    }
                    else if (which == "vbaudio") AppInfo.OpenUrl(AppInfo.VbAudioUrl);
                    else if (which == "community") AppInfo.OpenUrl(AppInfo.CommunityUrl);
                    else if (which == "logs") AppInfo.OpenFolder(AppLog.Folder);
                    break;
                case "refresh": LoadDevices(); PushPrototypeDevices(); break;
            }
        }
        catch (Exception ex) { AppLog.Error("Control message failed", ex); ShowError("EqualTune could not apply that control: " + ex.Message); }
    }

    private static void SelectDevice(ComboBox combo, IEnumerable<CoreAudioDevice> devices, string? id)
    {
        if (id is null) return;
        var selected = devices.FirstOrDefault(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (selected is not null) combo.SelectedItem = selected;
    }

    private void LoadDevices()
    {
        _loadingDevices = true;
        try
        {
            _devices = CoreAudioDevices.Enumerate();
            _knownDevices = _devices;
            _captureDevices = CoreAudioDevices.EnumerateCapture();
            var listeningOutputs = _devices.Where(d => !IsVirtualRoutingPlayback(d)).ToList();
            OutputDeviceCombo.ItemsSource = null;
            OutputDeviceCombo.ItemsSource = listeningOutputs.Count > 0 ? listeningOutputs : _devices;
            OutputDeviceCombo.SelectedItem = listeningOutputs.FirstOrDefault(d => d.IsDefault)
                ?? listeningOutputs.FirstOrDefault()
                ?? _devices.FirstOrDefault();
            var equalTuneOutput = FindEqualTuneVirtualPlayback();
            var cableInput = FindVirtualCableCapture();
            CaptureModeCombo.SelectedIndex = equalTuneOutput is not null ? 1 : cableInput is null ? 1 : 0;
            UpdateCaptureSources();
            if (_devices.Count == 0)
            {
                StatusText.Text = "No active audio outputs found";
                ShowError("Windows did not report any active output devices.");
            }
            else
            {
                StatusText.Text = "Ready · select a tone or sweep";
                ErrorText.Visibility = Visibility.Collapsed;
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = "Audio device unavailable";
            ShowError("Could not list Windows audio outputs: " + ex.Message);
        }
        finally { _loadingDevices = false; }
        _deviceSignature = DeviceSignature(_devices, _captureDevices);
    }

    private static string DeviceSignature(IEnumerable<CoreAudioDevice> playback, IEnumerable<CoreAudioDevice> capture)
        => string.Join("|", playback.Select(d => d.Id + "=" + d.Name)) + "##" + string.Join("|", capture.Select(d => d.Id + "=" + d.Name));

    // Watches for devices that connect or disconnect (Bluetooth earbuds, USB headsets) and keeps the CABLE default while routing.
    private async Task PollDevicesAsync()
    {
        if (_devicePollBusy || _loadingDevices) return;
        _devicePollBusy = true;
        try
        {
            if (_router is not null && _sysVolume is not null) _outputGuard.TrySetDefault(_sysVolume.DeviceId);   // Windows moves the default to newly connected earbuds; put it back on the cable
            var (playback, capture) = await Task.Run(() => (CoreAudioDevices.Enumerate(), CoreAudioDevices.EnumerateCapture()));
            if (DeviceSignature(playback, capture) == _deviceSignature) return;
            var keepOutput = (OutputDeviceCombo.SelectedItem as CoreAudioDevice)?.Id;
            var keepSource = (CaptureDeviceCombo.SelectedItem as CoreAudioDevice)?.Id;
            if (_router is not null && keepOutput is not null && !playback.Any(d => string.Equals(d.Id, keepOutput, StringComparison.OrdinalIgnoreCase)))
            {
                RecoverFromLostOutput("the device was removed");   // earbuds turned off or unplugged while routing
                return;
            }
            var status = StatusText.Text;
            LoadDevices();
            _loadingDevices = true;
            try
            {
                SelectDevice(OutputDeviceCombo, _devices, keepOutput);
                SelectDevice(CaptureDeviceCombo, CaptureModeCombo.SelectedIndex == 1 ? _devices : _captureDevices, keepSource);
            }
            finally { _loadingDevices = false; }
            if (_router is not null) StatusText.Text = status;
            PushPrototypeDevices();
            AppLog.Info("Audio devices changed; the device list was refreshed");
        }
        catch (Exception ex) { AppLog.Error("Device check failed", ex); }
        finally { _devicePollBusy = false; }
    }

    private void ToneButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null) { StopPlayback(); return; }
        StartPlayback(false);
    }

    private void SweepButton_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null) { StopPlayback(); return; }
        StartPlayback(true);
    }

    private void RouteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_router is not null) { StopPlayback(); return; }
        var loopback = CaptureModeCombo.SelectedIndex == 1;
        if (CaptureDeviceCombo.SelectedItem is not CoreAudioDevice source || OutputDeviceCombo.SelectedItem is not CoreAudioDevice target)
        {
            ShowError(loopback ? "Choose a playback source and an output device before starting the route." : "Choose a recording input and an output device before starting the route.");
            return;
        }
        if (CaptureModeCombo.SelectedIndex == 0 && (FindVirtualCableCapture() is null || !_devices.Any(IsVirtualCablePlayback)))
        {
            ShowError("No supported virtual cable pair was detected. Install and enable the cable's playback and recording endpoints, then refresh devices.");
            return;
        }
        if (loopback && string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase))
        {
            ShowError("Choose different capture and output devices to prevent audio feedback.");
            return;
        }
        try
        {
            _router = new WasapiLoopbackRouter(source.Id, target.Id, loopback);
            ApplyParameters();
            _router.Start();
            ArmCableDefault(source, target);
            RememberOutput(target.Id);
            AppLog.Info($"Route started: {source.Name} -> {target.Name} ({(loopback ? "loopback" : "cable/input")})");
            _tray?.SetTip("EqualTune · routing system audio");
            RouteButton.Content = "■  Stop system audio route";
            ToneButton.IsEnabled = false;
            SweepButton.IsEnabled = false;
            StatusText.Text = $"Routing {source.Name} → {target.Name}";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StopPlayback();
            ShowError("Could not start system audio route: " + ex.Message);
        }
    }

    // Remembers the real listening output and makes the virtual cable the Windows default so apps play into the route.
    private void ArmCableDefault(CoreAudioDevice source, CoreAudioDevice target)
    {
        var loopback = CaptureModeCombo.SelectedIndex == 1;
        CoreAudioDevice? cable = null;
        if (!loopback) cable = _devices.Where(IsVirtualCablePlayback).OrderBy(d => d.Name.Contains("16ch", StringComparison.OrdinalIgnoreCase) ? 1 : 0).FirstOrDefault();
        else if (IsVirtualCablePlayback(source)) cable = source;
        if (cable is null) { CloseSystemVolume(); return; }
        _outputGuard.Arm(target.Id);
        StartWatchdog();
        _outputGuard.TrySetDefault(cable.Id);
        OpenSystemVolume(cable.Id);
    }

    // A tiny helper process that restores Windows sound if EqualTune is force-quit or crashes while routing.
    private void StartWatchdog()
    {
        if (_watchdogStarted || Environment.ProcessPath is not { } exe) return;
        if (Environment.GetCommandLineArgs().Skip(1).Contains("--watchdog")) return;   // the helper never starts another helper
        try
        {
            // Started through cmd so it is not a child of this process (Task Manager's "end process tree" would otherwise kill it too).
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c start \"\" \"{exe}\" --watchdog {Environment.ProcessId}") { UseShellExecute = false, CreateNoWindow = true });
            _watchdogStarted = true;
            AppLog.Info("Started the output watchdog");
        }
        catch (Exception ex) { AppLog.Error("Could not start the output watchdog", ex); }
    }

    // The virtual cable ignores the Windows volume slider, so EqualTune reads it and applies it to the routed sound itself.
    private void OpenSystemVolume(string deviceId)
    {
        if (_sysVolume is not null && string.Equals(_sysVolume.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase)) { _sysVolTimer.Start(); SyncSystemVolume(); return; }
        CloseSystemVolume();
        _sysVolume = EndpointVolume.Open(deviceId);
        if (_sysVolume is not null) { _sysVolTimer.Start(); SyncSystemVolume(); }
    }

    private void CloseSystemVolume()
    {
        _sysVolTimer.Stop();
        _sysVolume?.Dispose();
        _sysVolume = null;
        _router?.SetSystemGain(1.0);
    }

    private void SyncSystemVolume()
    {
        if (_router is null || _sysVolume is null) return;
        if (_sysVolume.TryRead(out var level, out var muted)) _router.SetSystemGain(muted ? 0 : level * level);
        else CloseSystemVolume();
    }

    // Changing the listening output while a route runs restarts the route on the new device. Windows stays on the cable.
    private void RestartRouteForNewOutput()
    {
        var loopback = CaptureModeCombo.SelectedIndex == 1;
        if (CaptureDeviceCombo.SelectedItem is not CoreAudioDevice source || OutputDeviceCombo.SelectedItem is not CoreAudioDevice target)
        {
            StopPlayback();
            return;
        }
        if (IsVirtualRoutingPlayback(target) || (loopback && string.Equals(source.Id, target.Id, StringComparison.OrdinalIgnoreCase)))
        {
            StopPlayback();
            ShowError("Choose headphones or speakers as the listening output, not a virtual cable.");
            return;
        }
        try
        {
            _router?.Dispose();
            _router = null;
            _router = new WasapiLoopbackRouter(source.Id, target.Id, loopback);
            ApplyParameters();
            _router.Start();
            ArmCableDefault(source, target);
            RememberOutput(target.Id);
            StatusText.Text = $"Routing {source.Name} → {target.Name}";
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StopPlayback();
            ShowError("Could not switch the output: " + ex.Message);
        }
    }

    private void StartPlayback(bool sweep)
    {
        if (OutputDeviceCombo.SelectedItem is not CoreAudioDevice device)
        {
            ShowError("Select an output device before starting audio.");
            return;
        }
        try
        {
            _isSweep = sweep;
            _player = new WasapiTonePlayer(device.Id);
            ApplyParameters();
            _player.Start();
            ToneButton.Content = sweep ? "■  Stop sweep" : "■  Stop tone";
            SweepButton.Content = sweep ? "■  Stop sweep" : "↗  EQ sweep";
            StatusText.Text = (sweep ? "Sweeping 100 Hz–10 kHz through " : "Playing to ") + device.Name;
            ErrorText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            StopPlayback();
            ShowError("Could not start audio: " + ex.Message);
        }
    }

    private void RememberOutput(string id)
    {
        _outputHistory.RemoveAll(x => string.Equals(x, id, StringComparison.OrdinalIgnoreCase));
        _outputHistory.Add(id);
        while (_outputHistory.Count > 6) _outputHistory.RemoveAt(0);
    }

    // Windows reports these when the playback device disappears (Bluetooth turned off, USB unplugged, driver reset).
    private static bool IsDeviceGone(string error)
    {
        var text = error.ToUpperInvariant();
        return text.Contains("0X88890004") || text.Contains("0X88890026") || text.Contains("0X80070490") || text.Contains("0X8007001F");
    }

    // The listening output vanished mid-route: move to the previously used output (or the Windows default) and keep playing, with no error box.
    private void RecoverFromLostOutput(string reason)
    {
        if (_recoveringOutput) return;
        _recoveringOutput = true;
        try
        {
            var lost = OutputDeviceCombo.SelectedItem as CoreAudioDevice;
            var keepSource = (CaptureDeviceCombo.SelectedItem as CoreAudioDevice)?.Id;
            var wasRouting = _router is not null;
            AppLog.Info($"Listening output lost ({reason}); switching to another output");
            StopPlayback(restoreDefault: false);   // the old output is gone, so do not try to hand Windows sound back to it
            LoadDevices();
            _loadingDevices = true;
            try
            {
                SelectDevice(CaptureDeviceCombo, CaptureModeCombo.SelectedIndex == 1 ? _devices : _captureDevices, keepSource);
                if (CaptureDeviceCombo.SelectedItem is null && CaptureModeCombo.SelectedIndex == 0 && FindVirtualCableCapture() is { } cableIn) CaptureDeviceCombo.SelectedItem = cableIn;
            }
            finally { _loadingDevices = false; }
            var outputs = OutputDeviceCombo.Items.OfType<CoreAudioDevice>().Where(d => lost is null || !string.Equals(d.Id, lost.Id, StringComparison.OrdinalIgnoreCase)).ToList();
            CoreAudioDevice? pick = null;
            for (var i = _outputHistory.Count - 1; i >= 0 && pick is null; i--)
                pick = outputs.FirstOrDefault(d => string.Equals(d.Id, _outputHistory[i], StringComparison.OrdinalIgnoreCase));
            pick ??= outputs.FirstOrDefault(d => d.IsDefault) ?? outputs.FirstOrDefault();
            PushPrototypeDevices();
            var lostName = lost?.Name ?? "Your output device";
            if (pick is null)
            {
                _pendingNotice = $"{lostName} was disconnected and no other output is available.";
                return;
            }
            _loadingDevices = true;
            try { OutputDeviceCombo.SelectedItem = pick; }
            finally { _loadingDevices = false; }
            PushPrototypeDevices();
            if (wasRouting) { RetryRouteAfterLoss(lostName, pick.Name, 0); return; }
            _pendingNotice = $"{lostName} disconnected. Audio moved to {pick.Name}.";
        }
        catch (Exception ex) { AppLog.Error("Could not recover from a lost output", ex); ShowError("Your output device was disconnected. Choose another output and start the route again."); }
        finally { _recoveringOutput = false; }
    }

    // Starts the route again on the chosen output, trying a few times because Windows needs a moment to settle after a device vanishes.
    private void RetryRouteAfterLoss(string lostName, string pickName, int attempt)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(attempt == 0 ? 350 : 900) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var ok = false;
            try { ok = TryStartRouteQuietly(); }
            catch (Exception ex) { AppLog.Error($"Route restart attempt {attempt + 1} failed", ex); }
            if (ok) { _pendingNotice = $"{lostName} disconnected. Audio moved to {pickName}."; FlushNotice(); return; }
            if (attempt < 4) { RetryRouteAfterLoss(lostName, pickName, attempt + 1); return; }
            _pendingNotice = $"{lostName} disconnected. Choose an output and press Start to continue.";
            FlushNotice();
        };
        timer.Start();
    }

    private bool TryStartRouteQuietly()
    {
        if (_router is not null) return true;
        var loopback = CaptureModeCombo.SelectedIndex == 1;
        if (CaptureDeviceCombo.SelectedItem is not CoreAudioDevice source || OutputDeviceCombo.SelectedItem is not CoreAudioDevice target) return false;
        if (IsVirtualRoutingPlayback(target)) return false;
        try
        {
            _router = new WasapiLoopbackRouter(source.Id, target.Id, loopback);
            ApplyParameters();
            _router.Start();
            ArmCableDefault(source, target);
            RememberOutput(target.Id);
            AppLog.Info($"Route restarted after output loss: {source.Name} -> {target.Name}");
            _tray?.SetTip("EqualTune · routing system audio");
            RouteButton.Content = "■  Stop system audio route";
            ToneButton.IsEnabled = false;
            SweepButton.IsEnabled = false;
            StatusText.Text = $"Routing {source.Name} → {target.Name}";
            ErrorText.Visibility = Visibility.Collapsed;
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Error("Could not restart the route", ex);
            try { _router?.Dispose(); } catch { }
            _router = null;
            return false;
        }
    }

    private void StopPlayback(bool restoreDefault = true)
    {
        var wasRouting = _router is not null;
        if (wasRouting) AppLog.Info("Route stopped");
        _tray?.SetTip("EqualTune");
        _sysVolTimer.Stop();
        _sysVolume?.Dispose();
        _sysVolume = null;
        _player?.Dispose();
        _player = null;
        _router?.Dispose();
        _router = null;
        _isSweep = false;
        ToneButton.Content = "▶  Start tone";
        SweepButton.Content = "↗  EQ sweep";
        RouteButton.Content = "▶  Start system audio route";
        ToneButton.IsEnabled = true;
        SweepButton.IsEnabled = true;
        PeakMeter.Value = 0;
        if (StatusText is not null) StatusText.Text = "Playback stopped";
        if (wasRouting && restoreDefault && _outputGuard.RestoreAndDisarm()) _pendingNotice = "Route stopped. Windows sound was switched back to your listening output.";
    }

    private void RefreshDevices_Click(object sender, RoutedEventArgs e)
    {
        StopPlayback();
        LoadDevices();
    }

    private void OutputDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices) return;
        if (_router is not null && _player is null) { RestartRouteForNewOutput(); return; }
        if (_player is not null || _router is not null) StopPlayback();
        if (OutputDeviceCombo.SelectedItem is CoreAudioDevice device)
            StatusText.Text = "Output selected · " + device.Name;
    }

    private void CaptureDeviceCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loadingDevices) return;
        if (_router is not null) StopPlayback();
        UpdateRouteHelpText();
        if (CaptureModeCombo.SelectedIndex == 1 && CaptureDeviceCombo.SelectedItem is CoreAudioDevice source)
        {
            if (IsVirtualCablePlayback(source))
            {
                VirtualCableStatusText.Text = "VB-Cable Input selected. Set it as the Windows default playback device once to route all apps through EqualTune.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(113, 220, 177));
            }
            else if (IsEqualTuneVirtualPlayback(source))
            {
                VirtualCableStatusText.Text = "EqualTune Virtual Speakers selected. Set it as the Windows default playback device once to include all apps.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(113, 220, 177));
            }
            else
            {
                VirtualCableStatusText.Text = "This captures only audio playing through the selected device; the original may also remain audible there.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(146, 160, 187));
            }
        }
        UpdateRouteBanner();
    }

    private void CaptureModeCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingDevices && _router is not null) StopPlayback();
        UpdateCaptureSources();
    }

    private void UpdateCaptureSources()
    {
        if (CaptureDeviceCombo is null || CaptureModeCombo is null) return;
        var mode = CaptureModeCombo.SelectedIndex;
        var loopback = mode == 1;
        var virtualCable = mode == 0;
        var sources = loopback ? _devices : _captureDevices;
        CaptureSourceLabel.Text = loopback ? "Playback device to capture" : virtualCable ? "Virtual cable recording endpoint" : "Microphone / recording input";
        CaptureDeviceCombo.ItemsSource = null;
        CaptureDeviceCombo.ItemsSource = sources;
        if (loopback)
            CaptureDeviceCombo.SelectedItem = FindEqualTuneVirtualPlayback() ?? sources.FirstOrDefault(d => d.IsDefault) ?? sources.FirstOrDefault();
        else if (virtualCable)
            CaptureDeviceCombo.SelectedItem = FindVirtualCableCapture();
        else
            CaptureDeviceCombo.SelectedItem = sources.FirstOrDefault(d => d.IsDefault) ?? sources.FirstOrDefault();
        UpdateRouteHelpText();

        if (virtualCable)
        {
            var cableCapture = FindVirtualCableCapture();
            var cablePlayback = _devices.FirstOrDefault(IsVirtualCablePlayback);
            if (cableCapture is not null && cablePlayback is not null)
            {
                VirtualCableStatusText.Text = $"Virtual cable detected: {cablePlayback.Name} → {cableCapture.Name}";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(113, 220, 177));
            }
            else
            {
                VirtualCableStatusText.Text = "No virtual cable detected. Install a virtual audio cable once; EqualTune will then select its matching recording endpoint here.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(243, 206, 121));
            }
            if (sources.Count == 0) CaptureSourceLabel.Text = "No active recording inputs found";
        }
        else if (mode == 2 && sources.Count == 0)
        {
            CaptureSourceLabel.Text = "No active recording inputs found";
            VirtualCableStatusText.Text = "No active microphone or recording input was found.";
        }
        else
        {
            var equalTunePlayback = FindEqualTuneVirtualPlayback();
            if (CaptureDeviceCombo.SelectedItem is CoreAudioDevice selectedPlayback && IsVirtualCablePlayback(selectedPlayback))
            {
                VirtualCableStatusText.Text = "VB-Cable Input selected. Set it as the Windows default playback device once to route all apps through EqualTune.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(113, 220, 177));
            }
            else if (equalTunePlayback is not null)
            {
                VirtualCableStatusText.Text = "EqualTune virtual device detected. Set it as the Windows default output once to route system audio here.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(113, 220, 177));
                if (CaptureDeviceCombo.SelectedItem is not CoreAudioDevice selected || !IsEqualTuneVirtualPlayback(selected))
                    CaptureDeviceCombo.SelectedItem = equalTunePlayback;
            }
            else
            {
                VirtualCableStatusText.Text = "EqualTune Virtual Speakers is not installed yet. Playback loopback is available for diagnostic testing.";
                VirtualCableStatusText.Foreground = new SolidColorBrush(Color.FromRgb(146, 160, 187));
            }
        }
        UpdateRouteBanner();
    }

    private void UpdateRouteBanner()
    {
        if (RouteBannerText is null || CaptureModeCombo is null) return;
        if (CaptureModeCombo.SelectedIndex == 0)
        {
            RouteBannerText.Text = "VB-Cable route: send Windows or app audio to CABLE Input. EqualTune captures the paired recording endpoint, applies EQ, and forwards it to your selected headphones or speakers.";
        }
        else if (CaptureModeCombo.SelectedIndex == 1 && CaptureDeviceCombo.SelectedItem is CoreAudioDevice source && IsEqualTuneVirtualPlayback(source))
        {
            RouteBannerText.Text = "EqualTune virtual route: set EqualTune Virtual Speakers as the Windows playback device once. EqualTune captures that audio, applies EQ, and forwards it to your selected headphones or speakers.";
        }
        else if (CaptureModeCombo.SelectedIndex == 1 && CaptureDeviceCombo.SelectedItem is CoreAudioDevice cable && IsVirtualCablePlayback(cable))
        {
            RouteBannerText.Text = "VB-Cable route: set CABLE Input as the Windows default playback device once, or choose it in individual apps. EqualTune captures that audio, applies EQ, and forwards it to your selected headphones or speakers.";
        }
        else if (CaptureModeCombo.SelectedIndex == 1)
        {
            RouteBannerText.Text = "Playback preview: EqualTune captures the selected playback device and sends an EQ copy to your chosen output. The original may also remain audible.";
        }
        else
        {
            RouteBannerText.Text = "Microphone route: EqualTune captures the selected recording input, applies EQ, and forwards it to your selected output.";
        }
    }

    private void UpdateRouteHelpText()
    {
        var loopback = CaptureModeCombo.SelectedIndex == 1;
        if (loopback && CaptureDeviceCombo.SelectedItem is CoreAudioDevice source && IsEqualTuneVirtualPlayback(source))
            RouteHelpText.Text = "Set EqualTune Virtual Speakers as the Windows default playback device once. Choose your headphones or speakers above, then start the route to hear system audio with EQ.";
        else if (loopback && CaptureDeviceCombo.SelectedItem is CoreAudioDevice cable && IsVirtualCablePlayback(cable))
            RouteHelpText.Text = "Set VB-Cable Input as the Windows default playback device once (or choose it in an app). EqualTune captures it and sends the EQ audio to your selected headphones or speakers.";
        else if (loopback)
            RouteHelpText.Text = "Diagnostic mode: the original audio keeps playing on its source device. Choose another output and listen there to hear the EQ copy.";
        else if (CaptureModeCombo.SelectedIndex == 0)
            RouteHelpText.Text = "Set the virtual cable playback endpoint as the Windows output (or Brave's output). EqualTune captures its paired recording endpoint and sends the EQ audio to your selected listening device.";
        else
            RouteHelpText.Text = "Captures microphone or line-in audio and sends the EQ result to your selected output.";
    }

    private CoreAudioDevice? FindVirtualCableCapture()
        => _captureDevices.FirstOrDefault(d => d.Name.Contains("CABLE Output", StringComparison.OrdinalIgnoreCase))
            ?? _captureDevices.FirstOrDefault(d => d.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && d.Name.Contains("Output", StringComparison.OrdinalIgnoreCase));

    private CoreAudioDevice? FindEqualTuneVirtualPlayback()
        => _devices.FirstOrDefault(IsEqualTuneVirtualPlayback);

    private static bool IsEqualTuneVirtualPlayback(CoreAudioDevice device)
        => device.Name.Contains("EqualTune Virtual Speakers", StringComparison.OrdinalIgnoreCase)
            || device.Name.Contains("EqualTune Virtual Audio Device", StringComparison.OrdinalIgnoreCase);

    private static bool IsVirtualCableId(string id)
        => _knownDevices.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase) && IsVirtualCablePlayback(d));

    private static bool IsVirtualCablePlayback(CoreAudioDevice device)
        => device.Name.Contains("CABLE In", StringComparison.OrdinalIgnoreCase)
            || (device.Name.Contains("VB-Audio", StringComparison.OrdinalIgnoreCase) && device.Name.Contains("Input", StringComparison.OrdinalIgnoreCase));

    private static bool IsVirtualRoutingPlayback(CoreAudioDevice device)
        => IsVirtualCablePlayback(device) || IsEqualTuneVirtualPlayback(device);

    private void ToneSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (ToneValue is null) return;
        ToneValue.Text = $"{ToneSlider.Value:0} Hz";
        ApplyParameters();
    }

    private void FrequencySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (FrequencyValue is null) return;
        var hz = 20.0 * Math.Pow(1000.0, FrequencySlider.Value / 1000.0);
        FrequencyValue.Text = hz >= 1000 ? $"{hz / 1000:0.##} kHz" : $"{hz:0} Hz";
        ApplyParameters(); DrawResponse();
    }

    private void GainSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (GainValue is null) return;
        GainValue.Text = $"{GainSlider.Value:+0.0;-0.0;0.0} dB";
        ApplyParameters(); DrawResponse();
    }

    private void QSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (QValue is null) return;
        QValue.Text = QSlider.Value.ToString("0.0");
        ApplyParameters(); DrawResponse();
    }

    private void LevelSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (LevelValue is null) return;
        LevelValue.Text = $"{LevelSlider.Value:0}%";
        ApplyParameters();
    }

    private void EqToggle_Click(object sender, RoutedEventArgs e)
    {
        EqToggle.Content = EqToggle.IsChecked == true ? "EQ On" : "EQ Off";
        ApplyParameters(); DrawResponse();
    }

    private void ApplyParameters()
    {
        if ((_player is null && _router is null) || ToneSlider is null) return;
        var center = 20.0 * Math.Pow(1000.0, FrequencySlider.Value / 1000.0);
        var gain = EqToggle.IsChecked == true ? GainSlider.Value : 0;
        _player?.SetParameters(ToneSlider.Value, _isSweep, center, gain, QSlider.Value, LevelSlider.Value / 100.0);
        _router?.SetParameters(center, gain, QSlider.Value, 1.0); // routed audio follows the Windows volume, not the test-tone level
        if (_prototypeEqReceived)
        {
            var bands = EqToggle.IsChecked == true ? _prototypeEqBands : [];
            _player?.SetBands(bands);
            _router?.SetBands(bands);
        }
        _router?.SetEngineOptions(_autoPreamp, _bypass, _matchLevel, _manualPreampDb);
        _router?.SetSoundControls(_attenuationDb, _loudness, _limiter, _balance);
    }

    private void UpdateMeter()
    {
        FlushNotice();
        var error = _player?.LastError ?? _router?.LastError;
        if (_player is null && _router is null) {
            if (_spectrumSent && PrototypeUi.CoreWebView2 is not null) { _spectrumSent = false; _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync("window.equalTuneSetSpectrum && window.equalTuneSetSpectrum([]);"); } PeakMeter.Value = 0; InputPeakMeter.Value = 0; return; }
        if (error is not null)
        {
            if (_router is not null && IsDeviceGone(error)) { RecoverFromLostOutput(error); return; }
            StopPlayback();
            ShowError("Audio stopped: " + error);
            return;
        }
        PeakMeter.Value = Math.Clamp((_player?.Peak ?? _router?.Peak ?? 0) * 100, 0, 100);
        InputPeakMeter.Value = Math.Clamp((_router?.InputPeak ?? 0) * 100, 0, 100);
        if (PrototypeUi.CoreWebView2 is not null)
        {
            var state = _router is not null ? "Routing" : _player is not null ? (_isSweep ? "EQ sweep" : "Test tone") : "Ready";
            var input = (int)InputPeakMeter.Value;
            var output = (int)PeakMeter.Value;
            var latency = (int)Math.Round(_router?.LatencyMs ?? 0);
            _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetStatus && window.equalTuneSetStatus({JsonSerializer.Serialize(state)},{input},{output},{latency});");
            var clips = _router?.ClipCount ?? 0;
            var preamp = _router?.PreampDb ?? 0;
            var soundSummary = _router?.SoundSummary ?? "";
            if (clips != _lastReportedClips || Math.Abs(preamp - _lastReportedPreamp) > .05 || double.IsNaN(_lastReportedPreamp) || soundSummary != _lastReportedSound)
            {
                _lastReportedClips = clips; _lastReportedPreamp = preamp; _lastReportedSound = soundSummary;
                var engine = JsonSerializer.Serialize(new { preampDb = Math.Round(preamp, 1), clips, sound = soundSummary });
                _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetEngine && window.equalTuneSetEngine({engine});");
            }
        }
    }

    private void UpdateSpectrum()
    {
        if (!_spectrumEnabled || PrototypeUi.CoreWebView2 is null) return;
        if (_router?.GetSpectrum(64) is not { } levels) return;
        _spectrumSent = true;
        var text = string.Join(",", levels.Select(v => Math.Round(v, 1).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)));
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetSpectrum && window.equalTuneSetSpectrum([{text}]);");
    }

    // Reports the program you are using (not EqualTune itself) once it has stayed in front for ~1 second.
    private void UpdateForegroundApp()
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        string name;
        try
        {
            var handle = GetForegroundWindow();
            if (handle == IntPtr.Zero) return;
            GetWindowThreadProcessId(handle, out var pid);
            if (pid == 0 || pid == (uint)Environment.ProcessId) return;
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            name = process.ProcessName;
        }
        catch { return; }
        if (!string.Equals(name, _appCandidate, StringComparison.OrdinalIgnoreCase)) { _appCandidate = name; _appCandidateCount = 1; return; }
        if (++_appCandidateCount < 2 || string.Equals(name, _appReported, StringComparison.OrdinalIgnoreCase)) return;
        _appReported = name;
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneActiveApp && window.equalTuneActiveApp({JsonSerializer.Serialize(name)});");
    }

    private void PushRunningApps()
    {
        if (PrototypeUi.CoreWebView2 is null) return;
        var apps = new List<object>();
        try
        {
            foreach (var group in System.Diagnostics.Process.GetProcesses()
                .Where(p => { try { return p.Id != Environment.ProcessId && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrWhiteSpace(p.MainWindowTitle); } catch { return false; } })
                .GroupBy(p => p.ProcessName, StringComparer.OrdinalIgnoreCase)
                .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
                apps.Add(new { name = group.Key, title = group.First().MainWindowTitle });
        }
        catch { }
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneSetApps && window.equalTuneSetApps({JsonSerializer.Serialize(apps)});");
    }

    private void FlushNotice()
    {
        if (_pendingNotice is null || PrototypeUi.CoreWebView2 is null) return;
        var notice = JsonSerializer.Serialize(_pendingNotice);
        _pendingNotice = null;
        _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneNotice && window.equalTuneNotice({notice});");
    }

    // Global hotkeys: Ctrl+Alt+E EQ on/off, +B A/B compare, +Right/Left next/previous preset, +Up/Down volume.
    private void ApplyHotkeyRegistration()
    {
        if (_windowHandle == IntPtr.Zero) return;
        if (_hotkeysRegistered)
        {
            for (var id = 1; id <= 6; id++) UnregisterHotKey(_windowHandle, id);
            _hotkeysRegistered = false;
        }
        if (!_hotkeysEnabled) return;
        var mods = ModControl | ModAlt | ModNoRepeat;
        uint[] keys = [0x45, 0x42, 0x27, 0x25, 0x26, 0x28];
        var failed = 0;
        for (var i = 0; i < keys.Length; i++)
            if (!RegisterHotKey(_windowHandle, i + 1, mods, keys[i])) failed++;
        _hotkeysRegistered = failed < keys.Length;
        if (failed > 0) _pendingNotice = $"{failed} global hotkey(s) could not be registered because another app already uses them.";
    }

    private IntPtr HotkeyHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmHotkey)
        {
            handled = true;
            RunHotkey(wParam.ToInt32());
        }
        return IntPtr.Zero;
    }

    private void RunHotkey(int id)
    {
        if (id >= 5)
        {
            var delta = id == 5 ? .05 : -.05;
            var device = _sysVolume ?? EndpointVolume.OpenDefault();   // while routing: the virtual cable; otherwise the current Windows output
            if (device is not null && device.TryRead(out var level, out var muted))
            {
                var steps = level * 20;
                var next = Math.Clamp(delta > 0 ? Math.Floor(steps + 1e-6) + 1 : Math.Ceiling(steps - 1e-6) - 1, 0, 20) / 20;
                if (delta > 0 && muted) device.SetMute(false);
                device.SetScalar(next);
                _pendingNotice = $"Volume {next * 100:0}%";
                if (!ReferenceEquals(device, _sysVolume)) device.Dispose(); else SyncSystemVolume();
                FlushNotice();
            }
            return;
        }
        var action = id switch { 1 => "toggleEq", 2 => "ab", 3 => "nextPreset", _ => "prevPreset" };
        if (PrototypeUi.CoreWebView2 is not null)
            _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneHotkey && window.equalTuneHotkey('{action}');");
    }

    private void ShowError(string message)
    {
        AppLog.Error(message);
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        if (PrototypeUi.CoreWebView2 is not null)
            _ = PrototypeUi.CoreWebView2.ExecuteScriptAsync($"window.equalTuneShowError && window.equalTuneShowError({JsonSerializer.Serialize(message)});");
        MessageBox.Show(this, message, "EqualTune audio", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void ResponseCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawResponse();

    private void DrawResponse()
    {
        if (ResponseCanvas is null || ResponseCanvas.ActualWidth < 20 || ResponseCanvas.ActualHeight < 20 || FrequencySlider is null) return;
        ResponseCanvas.Children.Clear();
        var width = ResponseCanvas.ActualWidth;
        var height = ResponseCanvas.ActualHeight;
        const double left = 35, right = 10, top = 9, bottom = 21;
        var graphWidth = width - left - right;
        var graphHeight = height - top - bottom;
        foreach (var db in new[] { -12, -6, 0, 6, 12 })
        {
            var y = top + (12 - db) / 24.0 * graphHeight;
            ResponseCanvas.Children.Add(new Line { X1 = left, X2 = width - right, Y1 = y, Y2 = y, Stroke = db == 0 ? new SolidColorBrush(Color.FromRgb(91, 101, 121)) : new SolidColorBrush(Color.FromRgb(43, 49, 64)), StrokeThickness = db == 0 ? 1.3 : 1 });
            var label = new TextBlock { Text = db.ToString(), FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(133, 144, 165)) };
            Canvas.SetLeft(label, 0); Canvas.SetTop(label, y - 7); ResponseCanvas.Children.Add(label);
        }
        foreach (var (hz, text) in new[] { (20.0, "20"), (100.0, "100"), (1000.0, "1k"), (10000.0, "10k"), (20000.0, "20k") })
        {
            var x = left + Math.Log10(hz / 20.0) / 3.0 * graphWidth;
            ResponseCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = height - bottom, Stroke = new SolidColorBrush(Color.FromRgb(36, 42, 55)), StrokeThickness = 1 });
            var label = new TextBlock { Text = text, FontSize = 9, Foreground = new SolidColorBrush(Color.FromRgb(133, 144, 165)) };
            Canvas.SetLeft(label, x - 10); Canvas.SetTop(label, height - bottom + 4); ResponseCanvas.Children.Add(label);
        }

        var centerFrequency = 20.0 * Math.Pow(1000.0, FrequencySlider.Value / 1000.0);
        var gain = EqToggle?.IsChecked == true ? GainSlider.Value : 0;
        var q = QSlider?.Value ?? 1.0;
        var curve = new Polyline { Stroke = new SolidColorBrush(Color.FromRgb(184, 142, 255)), StrokeThickness = 2.4 };
        for (var i = 0; i <= 240; i++)
        {
            var hz = 20.0 * Math.Pow(1000.0, i / 240.0);
            var response = WasapiTonePlayer.CalculateResponseDb(hz, centerFrequency, gain, q, 48000);
            var y = top + (12 - Math.Clamp(response, -12, 12)) / 24.0 * graphHeight;
            curve.Points.Add(new Point(left + i / 240.0 * graphWidth, y));
        }
        ResponseCanvas.Children.Add(curve);
    }

    protected override void OnClosed(EventArgs e)
    {
        _meterTimer.Stop();
        _spectrumTimer.Stop();
        _appTimer.Stop();
        _sysVolTimer.Stop();
        _deviceTimer.Stop();
        _updateTimer.Stop();
        _sysVolume?.Dispose();
        AppLog.Info("Window closed");
        _tray?.Dispose();
        if (_hotkeysRegistered) for (var id = 1; id <= 6; id++) UnregisterHotKey(_windowHandle, id);
        StopPlayback();
        OutputGuard.EmergencyRestore();
        base.OnClosed(e);
    }
}

internal sealed record CoreAudioDevice(string Id, string Name, bool IsDefault);

internal static class CoreAudioDevices
{
    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var comType = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)(Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("Windows did not create the audio device enumerator."));
    }

    public static List<CoreAudioDevice> Enumerate() => Enumerate(0);
    public static List<CoreAudioDevice> EnumerateCapture() => Enumerate(1);

    private static List<CoreAudioDevice> Enumerate(int dataFlow)
    {
        var result = new List<CoreAudioDevice>();
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        IMMDevice? defaultDevice = null;
        try
        {
            enumerator = CreateEnumerator();
            string? defaultId = null;
            if (enumerator.GetDefaultAudioEndpoint(dataFlow, 1, out defaultDevice) >= 0)
                defaultDevice.GetId(out defaultId);
            Marshal.ThrowExceptionForHR(enumerator.EnumAudioEndpoints(dataFlow, 1, out collection));
            collection.GetCount(out var count);
            for (uint i = 0; i < count; i++)
            {
                IMMDevice? device = null;
                IPropertyStore? properties = null;
                try
                {
                    Marshal.ThrowExceptionForHR(collection.Item(i, out device));
                    device.GetId(out var id);
                    var name = id;
                    if (device.OpenPropertyStore(0, out properties) >= 0)
                    {
                        var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 14);
                        if (properties.GetValue(ref key, out var value) >= 0)
                        {
                            try { if (value.Pointer != IntPtr.Zero) name = Marshal.PtrToStringUni(value.Pointer) ?? id; }
                            finally { PropVariantClear(ref value); }
                        }
                    }
                    result.Add(new CoreAudioDevice(id, name, string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)));
                }
                finally
                {
                    Release(properties); Release(device);
                }
            }
        }
        finally { Release(defaultDevice); Release(collection); Release(enumerator); }
        return result;
    }

    internal static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
        {
            try { Marshal.ReleaseComObject(value); } catch { }
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint accessMode, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99")]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [StructLayout(LayoutKind.Sequential)] private struct PropertyKey(Guid formatId, uint propertyId) { public Guid FormatId = formatId; public uint PropertyId = propertyId; }
    [StructLayout(LayoutKind.Explicit)] private struct PropVariant { [FieldOffset(0)] public ushort Type; [FieldOffset(8)] public IntPtr Pointer; }
    [DllImport("ole32.dll")] private static extern int PropVariantClear(ref PropVariant value);
}

internal sealed class WasapiTonePlayer : IDisposable
{
    private static IMMDeviceEnumerator CreateEnumerator()
    {
        var clsid = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        var comType = Type.GetTypeFromCLSID(clsid, throwOnError: true)!;
        return (IMMDeviceEnumerator)(Activator.CreateInstance(comType)
            ?? throw new InvalidOperationException("Windows did not create the audio device enumerator."));
    }

    private const uint StreamFlagEventCallback = 0x00040000;
    private const int WaitObject0 = 0;
    private readonly string _deviceId;
    private readonly object _sync = new();
    private readonly ManualResetEvent _ready = new(false);
    private readonly ManualResetEvent _stop = new(false);
    private readonly Thread _renderThread;
    private double _toneFrequency = 1000, _eqFrequency = 1000, _gainDb, _q = 1, _volume = .4;
    private AudioEqBand[] _bands = [new(1000, 0, 1)];
    private bool _sweep;
    private volatile bool _disposed;
    private volatile string? _lastError;
    private double _peak;
    private Exception? _startupError;

    public double Peak => Volatile.Read(ref _peak);
    public string? LastError => _lastError;

    public WasapiTonePlayer(string deviceId)
    {
        _deviceId = deviceId;
        _renderThread = new Thread(RenderThread) { IsBackground = true, Name = "EqualTune WASAPI renderer" };
        _renderThread.SetApartmentState(ApartmentState.MTA);
        _renderThread.Start();
        if (!_ready.WaitOne(TimeSpan.FromSeconds(5)))
        {
            Dispose();
            throw new TimeoutException("Windows audio did not initialize in time.");
        }
        if (_startupError is not null)
        {
            var error = _startupError;
            Dispose();
            throw new InvalidOperationException(error.Message, error);
        }
    }

    public void SetParameters(double toneFrequency, bool sweep, double eqFrequency, double gainDb, double q, double volume)
    {
        lock (_sync)
        {
            _toneFrequency = Math.Clamp(toneFrequency, 30, 20000);
            _sweep = sweep;
            _eqFrequency = Math.Clamp(eqFrequency, 20, 20000);
            _gainDb = Math.Clamp(gainDb, -12, 12);
            _q = Math.Clamp(q, .2, 10);
            _bands = [new(_eqFrequency, _gainDb, _q)];
            _volume = Math.Clamp(volume, 0, 1);
        }
    }

    public void SetBands(AudioEqBand[] bands)
    {
        lock (_sync) _bands = bands.Take(12).ToArray();
    }

    public void Start() { }

    public static double CalculateResponseDb(double frequency, double center, double gainDb, double q, int sampleRate)
        => Biquad.Create(sampleRate, center, gainDb, q).ResponseDb(frequency, sampleRate);

    private void RenderThread()
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        IAudioClient? audioClient = null;
        IAudioRenderClient? renderClient = null;
        IntPtr audioEvent = IntPtr.Zero, formatPointer = IntPtr.Zero;
        try
        {
            Marshal.ThrowExceptionForHR(CoInitializeEx(IntPtr.Zero, 0));
            enumerator = CreateEnumerator();
            Marshal.ThrowExceptionForHR(enumerator.GetDevice(_deviceId, out device));
            var audioClientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
            Marshal.ThrowExceptionForHR(device.Activate(ref audioClientId, 23, IntPtr.Zero, out var audioClientObject));
            audioClient = (IAudioClient)audioClientObject;

            Marshal.ThrowExceptionForHR(audioClient.GetMixFormat(out formatPointer));
            var format = WasapiAudioFormat.Read(formatPointer);
            Marshal.ThrowExceptionForHR(audioClient.Initialize(0, StreamFlagEventCallback, 1_000_000, 0, formatPointer, IntPtr.Zero));
            audioClient.GetBufferSize(out var bufferFrames);
            audioEvent = CreateEvent(IntPtr.Zero, false, false, null);
            if (audioEvent == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            Marshal.ThrowExceptionForHR(audioClient.SetEventHandle(audioEvent));
            var renderId = new Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2");
            Marshal.ThrowExceptionForHR(audioClient.GetService(ref renderId, out var renderObject));
            renderClient = (IAudioRenderClient)renderObject;
            Marshal.ThrowExceptionForHR(audioClient.Start());
            _ready.Set();

            var handles = new[] { _stop.SafeWaitHandle.DangerousGetHandle(), audioEvent };
            var z1 = new double[12, format.Channels];
            var z2 = new double[12, format.Channels];
            long samplePosition = 0;
            double phase = 0;
            while (!_disposed)
            {
                var wait = WaitForMultipleObjects(2, handles, false, 500);
                if (wait == WaitObject0) break;
                if (wait != WaitObject0 + 1) continue;
                Marshal.ThrowExceptionForHR(audioClient.GetCurrentPadding(out var padding));
                var available = bufferFrames > padding ? bufferFrames - padding : 0;
                if (available == 0) continue;
                Marshal.ThrowExceptionForHR(renderClient.GetBuffer(available, out var data));
                FillBuffer(data, available, format, z1, z2, ref samplePosition, ref phase);
                Marshal.ThrowExceptionForHR(renderClient.ReleaseBuffer(available, 0));
            }
            audioClient.Stop();
        }
        catch (Exception ex)
        {
            _startupError ??= ex;
            _lastError = ex.Message;
            _ready.Set();
        }
        finally
        {
            if (audioEvent != IntPtr.Zero) CloseHandle(audioEvent);
            if (formatPointer != IntPtr.Zero) Marshal.FreeCoTaskMem(formatPointer);
            CoreAudioDevices.Release(renderClient);
            CoreAudioDevices.Release(audioClient);
            CoreAudioDevices.Release(device);
            CoreAudioDevices.Release(enumerator);
            CoUninitialize();
            _ready.Set();
        }
    }

    private void FillBuffer(IntPtr data, uint frames, WasapiAudioFormat format, double[,] z1, double[,] z2, ref long samplePosition, ref double phase)
    {
        double tone, volume;
        AudioEqBand[] bands;
        bool sweep;
        lock (_sync) { tone = _toneFrequency; sweep = _sweep; bands = _bands; volume = _volume; }
        var coefficients = bands.Select(b => BiquadCoefficients.Create(format.SampleRate, b)).ToArray();
        var peak = 0.0;
        const double sweepDuration = 8.0;
        const double sweepStart = 100.0;
        const double sweepEnd = 10000.0;
        for (var frame = 0; frame < frames; frame++)
        {
            double source;
            if (sweep)
            {
                var elapsed = (samplePosition % (long)(format.SampleRate * sweepDuration)) / (double)format.SampleRate;
                var instantaneousFrequency = sweepStart * Math.Pow(sweepEnd / sweepStart, elapsed / sweepDuration);
                instantaneousFrequency = Math.Min(instantaneousFrequency, format.SampleRate * .49);
                phase += 2 * Math.PI * instantaneousFrequency / format.SampleRate;
                if (phase > 2 * Math.PI) phase %= 2 * Math.PI;
                source = Math.Sin(phase) * .22;
            }
            else
            {
                source = Math.Sin(2 * Math.PI * Math.Min(tone, format.SampleRate * .49) * samplePosition / format.SampleRate) * .22;
            }
            samplePosition++;
            for (var channel = 0; channel < format.Channels; channel++)
            {
                var filtered = source;
                for (var band = 0; band < coefficients.Length; band++)
                {
                    var c = coefficients[band];
                    var next = c.B0 * filtered + z1[band, channel];
                    z1[band, channel] = c.B1 * filtered - c.A1 * next + z2[band, channel];
                    z2[band, channel] = c.B2 * filtered - c.A2 * next;
                    filtered = next;
                }
                var output = Math.Clamp(filtered * volume, -.96, .96);
                peak = Math.Max(peak, Math.Abs(output));
                format.WriteSample(data, checked((int)(frame * format.BlockAlign + channel * format.BytesPerSample)), output);
            }
        }
        Volatile.Write(ref _peak, peak);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _stop.Set(); } catch { }
        if (_renderThread.IsAlive && Thread.CurrentThread != _renderThread) _renderThread.Join(1500);
        _ready.Dispose();
        _stop.Dispose();
    }

    private readonly record struct Biquad(double B0, double B1, double B2, double A1, double A2)
    {
        public static Biquad Create(int sampleRate, double frequency, double gainDb, double q)
        {
            if (Math.Abs(gainDb) < .0001) return new(1, 0, 0, 0, 0);
            var a = Math.Pow(10, gainDb / 40.0);
            var w0 = 2 * Math.PI * Math.Clamp(frequency, 10, sampleRate * .49) / sampleRate;
            var alpha = Math.Sin(w0) / (2 * Math.Clamp(q, .1, 20));
            var cos = Math.Cos(w0);
            var a0 = 1 + alpha / a;
            return new((1 + alpha * a) / a0, -2 * cos / a0, (1 - alpha * a) / a0, -2 * cos / a0, (1 - alpha / a) / a0);
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

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E")]
    private interface IMMDeviceCollection { [PreserveSig] int GetCount(out uint count); [PreserveSig] int Item(uint index, out IMMDevice device); }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint accessMode, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2")]
    private interface IAudioClient
    {
        [PreserveSig] int Initialize(uint shareMode, uint streamFlags, long bufferDuration, long periodicity, IntPtr format, IntPtr audioSessionGuid);
        [PreserveSig] int GetBufferSize(out uint bufferFrames);
        [PreserveSig] int GetStreamLatency(out long latency);
        [PreserveSig] int GetCurrentPadding(out uint padding);
        [PreserveSig] int IsFormatSupported(uint shareMode, IntPtr format, out IntPtr closestMatch);
        [PreserveSig] int GetMixFormat(out IntPtr deviceFormat);
        [PreserveSig] int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
        [PreserveSig] int Start();
        [PreserveSig] int Stop();
        [PreserveSig] int Reset();
        [PreserveSig] int SetEventHandle(IntPtr eventHandle);
        [PreserveSig] int GetService(ref Guid interfaceId, [MarshalAs(UnmanagedType.IUnknown)] out object service);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F294ACFC-3146-4483-A7BF-ADDCA7C260E2")]
    private interface IAudioRenderClient { [PreserveSig] int GetBuffer(uint frames, out IntPtr data); [PreserveSig] int ReleaseBuffer(uint frames, uint flags); }

    [DllImport("ole32.dll")] private static extern int CoInitializeEx(IntPtr reserved, uint coInit);
    [DllImport("ole32.dll")] private static extern void CoUninitialize();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateEvent(IntPtr attributes, bool manualReset, bool initialState, string? name);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForMultipleObjects(uint count, IntPtr[] handles, bool waitAll, uint milliseconds);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool CloseHandle(IntPtr handle);
}
