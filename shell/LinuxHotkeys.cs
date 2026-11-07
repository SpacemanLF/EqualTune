using System.Diagnostics;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace EqualTune.Shell;

/// <summary>
/// Global hotkeys on Linux. A desktop app cannot grab keys itself (Wayland forbids it), so EqualTune asks the
/// desktop to run "EqualTune --command toggleEq" when a shortcut is pressed. That tiny second process sends the
/// command to the running EqualTune through a local socket and exits.
/// Shortcuts are registered with GNOME (Ubuntu's desktop); on other desktops they can be set by hand.
/// </summary>
internal static class LinuxHotkeys
{
    private const string Schema = "org.gnome.settings-daemon.plugins.media-keys";
    private const string BasePath = "/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/";

    // Ctrl+Alt+E / B, and comma / period for previous / next preset (Ubuntu uses Ctrl+Alt+arrows for workspaces).
    private static readonly (string Id, string Label, string Command, string Binding)[] Items =
    [
        ("equaltune-eq", "EqualTune: EQ on/off", "toggleEq", "<Primary><Alt>e"),
        ("equaltune-ab", "EqualTune: A/B compare", "ab", "<Primary><Alt>b"),
        ("equaltune-next", "EqualTune: next preset", "nextPreset", "<Primary><Alt>period"),
        ("equaltune-prev", "EqualTune: previous preset", "prevPreset", "<Primary><Alt>comma"),
    ];

    private static string SocketPath
    {
        get
        {
            var runtime = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
            if (string.IsNullOrEmpty(runtime) || !Directory.Exists(runtime)) runtime = "/tmp";
            return Path.Combine(runtime, "equaltune.sock");
        }
    }

    // ------------------------------------------------------------------ the tiny client ("--command name")

    public static int SendCommand(string name)
    {
        try
        {
            using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
            socket.Connect(new UnixDomainSocketEndPoint(SocketPath));
            socket.Send(Encoding.UTF8.GetBytes(name));
            return 0;
        }
        catch { return 1; } // EqualTune is not running; nothing to do
    }

    // ------------------------------------------------------------------ the listener inside the running app

    public static void StartListener(Action<string> onCommand)
    {
        var thread = new Thread(() =>
        {
            try
            {
                var path = SocketPath;
                try { File.Delete(path); } catch { }
                using var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(path));
                listener.Listen(8);
                var allowed = Items.Select(i => i.Command).ToHashSet();
                while (true)
                {
                    using var client = listener.Accept();
                    var buffer = new byte[64];
                    var read = client.Receive(buffer);
                    var name = Encoding.UTF8.GetString(buffer, 0, read).Trim();
                    if (allowed.Contains(name)) onCommand(name);
                }
            }
            catch (Exception ex) { Console.Error.WriteLine("Hotkey listener stopped: " + ex.Message); }
        }) { IsBackground = true, Name = "EqualTune hotkeys" };
        thread.Start();
    }

    // ------------------------------------------------------------------ registering with GNOME

    private static string? Gsettings(params string[] args)
    {
        try
        {
            var info = new ProcessStartInfo("gsettings") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in args) info.ArgumentList.Add(arg);
            using var process = Process.Start(info);
            if (process is null) return null;
            var output = process.StandardOutput.ReadToEnd();
            if (!process.WaitForExit(4000)) { try { process.Kill(); } catch { } return null; }
            return process.ExitCode == 0 ? output : null;
        }
        catch { return null; }
    }

    private static List<string> ReadList()
    {
        var text = Gsettings("get", Schema, "custom-keybindings") ?? "";
        return Regex.Matches(text, "'([^']+)'").Select(m => m.Groups[1].Value).ToList();
    }

    private static void WriteList(List<string> paths)
        => Gsettings("set", Schema, "custom-keybindings", paths.Count == 0 ? "@as []" : "[" + string.Join(", ", paths.Select(p => "'" + p + "'")) + "]");

    private static string CommandLine(string command)
    {
        var exe = Environment.ProcessPath ?? "equaltune";
        var dll = Assembly.GetEntryAssembly()?.Location;
        // Shortcuts start with a clean environment; tell the launcher where .NET lives if it was installed per-user.
        var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
        var prefix = string.IsNullOrEmpty(dotnetRoot) ? "" : $"env DOTNET_ROOT=\"{dotnetRoot}\" ";
        var text = prefix + (Path.GetFileName(exe) == "dotnet" && !string.IsNullOrEmpty(dll)
            ? $"\"{exe}\" \"{dll}\" --command {command}"
            : $"\"{exe}\" --command {command}");
        return "'" + text.Replace("'", "\\'") + "'";
    }

    /// <summary>Adds the EqualTune shortcuts to the desktop. Returns false when gsettings is not available.</summary>
    public static bool Register()
    {
        if (Gsettings("list-schemas") is null) return false;
        var list = ReadList();
        foreach (var item in Items)
        {
            var path = BasePath + item.Id + "/";
            var key = $"{Schema}.custom-keybinding:{path}";
            Gsettings("set", key, "name", "'" + item.Label + "'");
            Gsettings("set", key, "command", CommandLine(item.Command));
            Gsettings("set", key, "binding", "'" + item.Binding + "'");
            if (!list.Contains(path)) list.Add(path);
        }
        WriteList(list);
        return true;
    }

    public static void Unregister()
    {
        if (Gsettings("list-schemas") is null) return;
        var ours = Items.Select(i => BasePath + i.Id + "/").ToHashSet();
        var list = ReadList();
        if (list.RemoveAll(ours.Contains) > 0) WriteList(list);
    }
}
