using System.Drawing;
using Photino.NET;

namespace EqualTune.Shell;

/// <summary>
/// Step 1 prototype. Opens the unchanged EqualTune interface in a cross-platform window
/// (WebView2 on Windows, WebKitGTK on Linux) and answers it with a fake engine,
/// to prove that the interface renders and the message bridge works. No audio yet.
/// </summary>
internal static class Program
{
    // .NET's own SetEnvironmentVariable does not reach native libraries on Linux, so call the C library directly.
    [System.Runtime.InteropServices.DllImport("libc")]
    private static extern int setenv(string name, string value, int overwrite);

    [STAThread]
    private static void Main(string[] args)
    {
        // "EqualTune --command toggleEq": sent by a desktop shortcut to the running app, then exits.
        var commandAt = Array.IndexOf(args, "--command");
        if (commandAt >= 0 && commandAt + 1 < args.Length)
        {
            Environment.ExitCode = LinuxHotkeys.SendCommand(args[commandAt + 1]);
            return;
        }

        // Safe drawing mode: the web view paints reliably on computers without a working graphics driver (VMs, live USBs).
        // Set EQUALTUNE_GPU=1 to use the faster GPU path on machines with proper graphics drivers.
        if (OperatingSystem.IsLinux() && Environment.GetEnvironmentVariable("EQUALTUNE_GPU") != "1"
            && Environment.GetEnvironmentVariable("WEBKIT_DISABLE_COMPOSITING_MODE") is null)
            setenv("WEBKIT_DISABLE_COMPOSITING_MODE", "1", 0);

        var page = Path.Combine(AppContext.BaseDirectory, "Assets", "EqualizerPrototype.html");
        if (!File.Exists(page))
        {
            Console.Error.WriteLine("Cannot find the interface file: " + page);
            return;
        }
        Console.WriteLine("Loading " + page);

        // The real audio engine runs on Linux. Pass --fake to use the sample-data test backend instead.
        var useFake = args.Contains("--fake") || !OperatingSystem.IsLinux();

        var window = new PhotinoWindow()
            .SetTitle("EqualTune - shell prototype")
            .SetUseOsDefaultSize(false)
            .SetSize(new Size(1280, 820))
            .Center()
            .RegisterWebMessageReceivedHandler((sender, message) =>
            {
                if (sender is not PhotinoWindow w) return;
                if (useFake) FakeBackend.Handle(w, message); else LinuxBackend.Handle(w, message);
            })
            .Load(new Uri(page));

        if (!useFake)
        {
            LinuxBackend.Init(window);
            Console.CancelKeyPress += (_, e) => { LinuxBackend.Shutdown(); };
            AppDomain.CurrentDomain.ProcessExit += (_, _) => LinuxBackend.Shutdown();
        }
        window.WaitForClose();
        if (!useFake) LinuxBackend.Shutdown();
    }
}
