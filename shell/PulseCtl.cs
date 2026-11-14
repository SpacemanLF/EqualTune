using System.Diagnostics;
using System.Runtime.InteropServices;

namespace EqualTune.Shell;

internal sealed record PulseDevice(int Index, string Name, string Description, bool IsMonitor);
internal sealed record PulseStream(int Index, int SinkIndex, string AppName, string MediaName)
{
    /// <summary>True for the streams EqualTune itself opens (its own output must never be moved onto the virtual sink).</summary>
    public bool IsOurs => AppName == PulseCtl.StreamAppName || MediaName.StartsWith("EqualTune ", StringComparison.Ordinal);
}

/// <summary>
/// Talks to PulseAudio / PipeWire (through its PulseAudio-compatible server) using the "pactl" tool:
/// lists devices, creates and removes the EqualTune virtual output, and moves app streams onto it.
/// </summary>
internal static class PulseCtl
{
    public const string VirtualSinkName = "equaltune";
    public const string VirtualMonitorName = "equaltune.monitor";
    public const string StreamAppName = "EqualTune";

    private static string Run(params string[] args)
    {
        var info = new ProcessStartInfo("pactl")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        info.Environment["LC_ALL"] = "C";
        info.Environment["LANG"] = "C";
        foreach (var arg in args) info.ArgumentList.Add(arg);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start pactl.");
        var error = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000)) { try { process.Kill(); } catch { } throw new TimeoutException("pactl did not answer in time."); }
        if (process.ExitCode != 0) throw new InvalidOperationException("pactl " + string.Join(' ', args) + " failed: " + error.GetAwaiter().GetResult().Trim());
        return output;
    }

    /// <summary>True when pactl exists and a PulseAudio-compatible server answers.</summary>
    public static bool IsAvailable(out string? problem)
    {
        try { Run("info"); problem = null; return true; }
        catch (System.ComponentModel.Win32Exception) { problem = "The audio tool \"pactl\" was not found. Install it with: sudo apt install pulseaudio-utils"; return false; }
        catch (Exception ex) { problem = "The sound server did not answer: " + ex.Message; return false; }
    }

    public static string? GetDefaultSink()
    {
        try { var name = Run("get-default-sink").Trim(); return name.Length > 0 ? name : null; }
        catch { return null; }
    }

    public static List<PulseDevice> ListSinks() => ParseDevices(Run("list", "sinks"), "Sink #");
    public static List<PulseDevice> ListSources() => ParseDevices(Run("list", "sources"), "Source #");

    private static List<PulseDevice> ParseDevices(string text, string header)
    {
        var result = new List<PulseDevice>();
        int index = -1; string? name = null, description = null; var monitor = false;
        void Flush()
        {
            if (index >= 0 && name is not null) result.Add(new PulseDevice(index, name, description ?? name, monitor));
            index = -1; name = null; description = null; monitor = false;
        }
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith(header, StringComparison.Ordinal))
            {
                Flush();
                int.TryParse(line[header.Length..].Trim(), out index);
                continue;
            }
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Name: ", StringComparison.Ordinal)) name = trimmed[6..];
            else if (trimmed.StartsWith("Description: ", StringComparison.Ordinal)) description = trimmed[13..];
            else if (trimmed.StartsWith("Monitor of Sink: ", StringComparison.Ordinal)) monitor = !trimmed.EndsWith("n/a", StringComparison.Ordinal);
        }
        Flush();
        return result;
    }

    /// <summary>Playback streams (one per app that is playing sound).</summary>
    public static List<PulseStream> ListSinkInputs()
    {
        var result = new List<PulseStream>();
        int index = -1, sink = -1; var app = ""; var media = "";
        void Flush()
        {
            if (index >= 0) result.Add(new PulseStream(index, sink, app, media));
            index = -1; sink = -1; app = ""; media = "";
        }
        foreach (var raw in Run("list", "sink-inputs").Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            if (line.StartsWith("Sink Input #", StringComparison.Ordinal)) { Flush(); int.TryParse(line[12..].Trim(), out index); continue; }
            var trimmed = line.Trim();
            if (trimmed.StartsWith("Sink: ", StringComparison.Ordinal)) int.TryParse(trimmed[6..].Trim(), out sink);
            else if (trimmed.StartsWith("application.name = ", StringComparison.Ordinal)) app = trimmed[19..].Trim().Trim('"');
            else if (trimmed.StartsWith("media.name = ", StringComparison.Ordinal)) media = trimmed[13..].Trim().Trim('"');
        }
        Flush();
        return result;
    }

    /// <summary>The module id of an existing EqualTune virtual output, or null.</summary>
    public static int? FindVirtualSinkModule()
    {
        try
        {
            foreach (var raw in Run("list", "short", "modules").Split('\n'))
            {
                var parts = raw.Split('\t');
                if (parts.Length >= 3 && parts[1] == "module-null-sink" && parts[2].Contains("sink_name=" + VirtualSinkName, StringComparison.Ordinal) && int.TryParse(parts[0], out var id))
                    return id;
            }
        }
        catch { }
        return null;
    }

    /// <summary>Creates the EqualTune virtual output (apps play into it, EqualTune captures its monitor).</summary>
    public static int CreateVirtualSink()
    {
        if (FindVirtualSinkModule() is { } existing) return existing;
        var output = Run("load-module", "module-null-sink", "sink_name=" + VirtualSinkName,
            "sink_properties=device.description=EqualTune", "rate=48000", "channels=2");
        return int.TryParse(output.Trim(), out var id) ? id : -1;
    }

    public static void RemoveVirtualSink()
    {
        if (FindVirtualSinkModule() is { } id)
        {
            try { Run("unload-module", id.ToString()); } catch { }
        }
    }

    public static void SetDefaultSink(string name) => Run("set-default-sink", name);

    public static void MoveSinkInput(int stream, string sinkName)
    {
        try { Run("move-sink-input", stream.ToString(), sinkName); } catch { }
    }

    /// <summary>Moves every app's playback (except EqualTune's own) from one sink to another.</summary>
    public static void MoveAppStreams(string toSink, int? onlyFromSinkIndex = null, int? exceptFromSinkIndex = null)
    {
        try
        {
            foreach (var stream in ListSinkInputs())
            {
                if (stream.IsOurs) continue;
                if (onlyFromSinkIndex is { } only && stream.SinkIndex != only) continue;
                if (exceptFromSinkIndex is { } except && stream.SinkIndex == except) continue;
                MoveSinkInput(stream.Index, toSink);
            }
        }
        catch { }
    }
}

/// <summary>Minimal binding of the PulseAudio "simple" API, enough to record from one device and play to another.</summary>
internal static class PulseSimple
{
    private const string Lib = "libpulse-simple.so.0";
    private const string Core = "libpulse.so.0";

    public const int Playback = 1;
    public const int Record = 2;
    public const int Float32LE = 5;

    [StructLayout(LayoutKind.Sequential)]
    public struct SampleSpec { public int Format; public uint Rate; public byte Channels; }

    [StructLayout(LayoutKind.Sequential)]
    public struct BufferAttr { public uint MaxLength, TLength, PreBuf, MinReq, FragSize; }

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr pa_simple_new(string? server, string name, int direction, string? device, string streamName,
        ref SampleSpec spec, IntPtr map, ref BufferAttr attr, out int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_simple_read(IntPtr s, [Out] float[] data, UIntPtr bytes, out int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_simple_write(IntPtr s, [In] float[] data, UIntPtr bytes, out int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern int pa_simple_flush(IntPtr s, out int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern ulong pa_simple_get_latency(IntPtr s, out int error);

    [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)]
    public static extern void pa_simple_free(IntPtr s);

    [DllImport(Core, CallingConvention = CallingConvention.Cdecl)]
    private static extern IntPtr pa_strerror(int error);

    public static string Error(int code)
    {
        try { return Marshal.PtrToStringUTF8(pa_strerror(code)) ?? ("error " + code); }
        catch { return "error " + code; }
    }

    public static IntPtr Open(int direction, string? device, string streamName, int rate, int channels, BufferAttr attr)
    {
        var spec = new SampleSpec { Format = Float32LE, Rate = (uint)rate, Channels = (byte)channels };
        var handle = pa_simple_new(null, PulseCtl.StreamAppName, direction, device, streamName, ref spec, IntPtr.Zero, ref attr, out var error);
        if (handle == IntPtr.Zero) throw new InvalidOperationException("Could not open audio stream" + (device is null ? "" : " for " + device) + ": " + Error(error));
        return handle;
    }
}
