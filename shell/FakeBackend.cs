using System.Diagnostics;
using System.Text.Json;
using Photino.NET;

namespace EqualTune.Shell;

/// <summary>Stand-in for the real audio engine. Answers the interface with sample data so the screens can be checked.</summary>
internal static class FakeBackend
{
    public static void Handle(PhotinoWindow window, string raw)
    {
        string? type;
        try
        {
            using var document = JsonDocument.Parse(raw);
            var root = document.RootElement;
            type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "openurl" && root.TryGetProperty("which", out var which)) OpenLink(which.GetString());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("Could not read a message from the interface: " + ex.Message);
            return;
        }

        Console.WriteLine("interface -> shell: " + type);
        if (type is "shellReady" or "refresh") PushStartupState(window);
    }

    private static void PushStartupState(PhotinoWindow window)
    {
        Call(window, "equalTuneSetAppSettings", new { closeToTray = true, startAtLogin = false, startMinimized = false, autoRoute = false, checkUpdates = false, firstRunDone = true });
        Call(window, "equalTuneSetAbout", new { version = "0.0.0-shell-prototype", donateReady = true, logs = Path.GetTempPath() });

        var outputs = new[]
        {
            new { id = "fake-speakers", name = "Fake speakers (prototype)", isDefault = true },
            new { id = "fake-headphones", name = "Fake headphones (prototype)", isDefault = false },
        };
        Call(window, "equalTuneSetDevices", new
        {
            outputs,
            playbacks = outputs,
            captures = new[] { new { id = "fake-sink", name = "EqualTune virtual output (prototype)" }, new { id = "fake-capture", name = "Fake capture (prototype)" } },
            virtualSink = true,
            virtualSinkCaptureId = "fake-sink",
        });
        Call(window, "equalTuneNotice", "Shell prototype connected: the interface loaded and the bridge works.");
    }

    /// <summary>Calls window.&lt;function&gt;(args) inside the interface (the bridge script in the page does the actual call).</summary>
    private static void Call(PhotinoWindow window, string function, params object?[] args)
        => window.SendWebMessage(JsonSerializer.Serialize(new { fn = function, args }));

    private static void OpenLink(string? which)
    {
        var target = which switch
        {
            "donate" => "https://buymeacoffee.com/equaltune",
            "community" => "https://www.reddit.com/r/EqualTune/",
            "logs" => Path.GetTempPath(),
            _ => null,
        };
        if (target is null) return;
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception ex) { Console.Error.WriteLine("Could not open " + target + ": " + ex.Message); }
    }
}
