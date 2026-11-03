using System.Threading;
using System.Windows;

namespace EqualTune.AudioLab;

public partial class App : Application
{
    private const string MutexName = @"Local\EqualTune.AudioLab.SingleInstance";
    private const string ShowEventName = @"Local\EqualTune.AudioLab.ShowWindow";
    private const string QuitEventName = @"Local\EqualTune.AudioLab.Quit";
    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private EventWaitHandle? _quitEvent;
    private bool _ownsInstance;
    private bool _isWatchdog;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Background helper mode: no window; waits for the main app to exit and restores Windows sound if it died while routing.
        var watchIndex = Array.IndexOf(e.Args, "--watchdog");
        if (watchIndex >= 0 && watchIndex + 1 < e.Args.Length && int.TryParse(e.Args[watchIndex + 1], out var watchedPid))
        {
            _isWatchdog = true;
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Task.Run(() =>
            {
                OutputGuard.RunWatchdog(watchedPid);
                Dispatcher.BeginInvoke(new Action(Shutdown));
            });
            return;
        }

        // Command-line helpers used by the installer/uninstaller.
        if (e.Args.Contains("--quit", StringComparer.OrdinalIgnoreCase))
        {
            _isWatchdog = true;
            if (EventWaitHandle.TryOpenExisting(QuitEventName, out var running)) { running.Set(); running.Dispose(); }
            Shutdown();
            return;
        }
        if (e.Args.Contains("--restore-output", StringComparer.OrdinalIgnoreCase))
        {
            // Used by the installer: if EqualTune was stopped while routing, put Windows sound back on the real output.
            _isWatchdog = true;
            new OutputGuard(_ => true).RecoverAfterCrash();
            Shutdown();
            return;
        }
        if (e.Args.Contains("--remove-startup", StringComparer.OrdinalIgnoreCase))
        {
            _isWatchdog = true;
            new AppSettings { StartWithWindows = false }.ApplyStartup();
            Shutdown();
            return;
        }

        _instanceMutex = new Mutex(true, MutexName, out _ownsInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        _quitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, QuitEventName);
        if (!_ownsInstance)
        {
            // EqualTune is already running: ask it to come to the front, then quit this copy.
            try { _showEvent.Set(); } catch { }
            Shutdown();
            return;
        }

        AppLog.Info($"EqualTune {AppInfo.Version} starting (Windows {Environment.OSVersion.Version})");
        DispatcherUnhandledException += (_, args) => AppLog.Error("Unhandled UI error", args.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, args) => AppLog.Error("Unhandled error", args.ExceptionObject as Exception);
        TaskScheduler.UnobservedTaskException += (_, args) => AppLog.Error("Unobserved task error", args.Exception);

        var listener = new Thread(() =>
        {
            while (true)
            {
                var which = WaitHandle.WaitAny([_showEvent, _quitEvent]);
                if (which == 0) Dispatcher.BeginInvoke(new Action(BringMainWindowToFront));
                else Dispatcher.BeginInvoke(new Action(() => (Current.MainWindow as EqualTune.AudioLab.MainWindow)?.RequestExit()));
            }
        }) { IsBackground = true, Name = "EqualTune show-window listener" };
        listener.Start();

        base.OnStartup(e);
        var window = new EqualTune.AudioLab.MainWindow();
        MainWindow = window;
        window.Show();
    }

    private void BringMainWindowToFront()
    {
        if (Application.Current.MainWindow is not Window window) return;
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = WindowState.Normal;
        window.Activate();
        window.Topmost = true;   // nudges Windows to really raise the window, then releases it
        window.Topmost = false;
        window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (!_isWatchdog) AppLog.Info("EqualTune exited");
        if (_ownsInstance) { try { _instanceMutex?.ReleaseMutex(); } catch { } }
        base.OnExit(e);
    }
}
