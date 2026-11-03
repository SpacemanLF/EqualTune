using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace EqualTune.AudioLab;

/// <summary>
/// Keeps Windows sound from getting stuck on a silent virtual cable. While a cable route runs, the
/// listening output is remembered; when the route stops, the app exits, or the app crashes and is
/// reopened, the Windows default playback device is moved back to that output.
/// </summary>
internal sealed class OutputGuard
{
    private static readonly string StatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "EqualTune", "output-guard.json");

    private readonly Func<string, bool> _isVirtualCableId;
    private string? _restoreTargetId;

    public static OutputGuard? Current { get; private set; }
    public bool Enabled { get; set; } = true;

    public OutputGuard(Func<string, bool> isVirtualCableId)
    {
        _isVirtualCableId = isVirtualCableId;
        Current = this;
    }

    /// <summary>Called when a cable route starts. Saves the real listening output to disk.</summary>
    public void Arm(string listeningOutputId)
    {
        _restoreTargetId = listeningOutputId;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(StatePath)!);
            File.WriteAllText(StatePath, JsonSerializer.Serialize(new { pending = true, outputId = listeningOutputId, pid = Environment.ProcessId, restore = Enabled }));
        }
        catch { }
    }

    /// <summary>Makes the given virtual cable the Windows default playback device (all roles). Returns true when it is the default afterwards.</summary>
    public bool TrySetDefault(string deviceId)
    {
        try
        {
            var currentId = GetDefaultPlaybackId();
            if (string.Equals(currentId, deviceId, StringComparison.OrdinalIgnoreCase)) return true;
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), true)!)!;
            try
            {
                for (var role = 0; role < 3; role++)
                    policy.SetDefaultEndpoint(deviceId, role);
            }
            finally { Marshal.ReleaseComObject(policy); }
            return true;
        }
        catch { return false; }
    }

    /// <summary>Called when the route stops cleanly or the app is closing.</summary>
    public bool RestoreAndDisarm()
    {
        var restored = false;
        if (Enabled && _restoreTargetId is not null) restored = TryRestore(_restoreTargetId);
        _restoreTargetId = null;
        try { if (File.Exists(StatePath)) File.Delete(StatePath); } catch { }
        return restored;
    }

    /// <summary>Called at startup: if the previous run never disarmed, it crashed or was killed.</summary>
    public bool RecoverAfterCrash()
    {
        try
        {
            if (!File.Exists(StatePath)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = document.RootElement;
            var pending = root.TryGetProperty("pending", out var p) && p.GetBoolean();
            var outputId = root.TryGetProperty("outputId", out var o) ? o.GetString() : null;
            File.Delete(StatePath);
            return pending && Enabled && outputId is not null && TryRestore(outputId);
        }
        catch { return false; }
    }

    /// <summary>
    /// Runs inside the small background helper EqualTune starts while routing. When the main app dies without
    /// cleaning up (killed in Task Manager, crash), this puts Windows sound back on the real output.
    /// </summary>
    public static void RunWatchdog(int watchedPid)
    {
        try { using var process = Process.GetProcessById(watchedPid); process.WaitForExit(); }
        catch { /* the process is already gone */ }
        Thread.Sleep(700);
        try
        {
            if (!File.Exists(StatePath)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(StatePath));
            var root = document.RootElement;
            if (!(root.TryGetProperty("pid", out var pid) && pid.GetInt32() == watchedPid)) return;   // a newer EqualTune owns the file
            if (root.TryGetProperty("restore", out var restore) && !restore.GetBoolean()) return;
            new OutputGuard(_ => true).RecoverAfterCrash();
        }
        catch { }
    }

    /// <summary>Last-resort restore for crash and process-exit handlers.</summary>
    public static void EmergencyRestore() => Current?.RestoreAndDisarm();

    private bool TryRestore(string targetId)
    {
        try
        {
            var currentId = GetDefaultPlaybackId();
            if (currentId is null) return false;
            if (string.Equals(currentId, targetId, StringComparison.OrdinalIgnoreCase)) return false;
            if (!_isVirtualCableId(currentId)) return false; // Only undo our own virtual-cable default.
            var policy = (IPolicyConfig)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9"), true)!)!;
            try
            {
                for (var role = 0; role < 3; role++)
                    policy.SetDefaultEndpoint(targetId, role);
            }
            finally { Marshal.ReleaseComObject(policy); }
            return true;
        }
        catch { return false; }
    }

    private static string? GetDefaultPlaybackId()
    {
        IMMDeviceEnumeratorGuard? enumerator = null;
        IMMDeviceGuard? device = null;
        try
        {
            enumerator = (IMMDeviceEnumeratorGuard)Activator.CreateInstance(Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!)!;
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out device) != 0 || device is null) return null;
            return device.GetId(out var id) == 0 ? id : null;
        }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumeratorGuard
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDeviceGuard device);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDeviceGuard
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    // Undocumented but long-stable Windows interface; only SetDefaultEndpoint is called, the others only keep the vtable order.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("F8679F50-850A-41CF-9C72-430F290290C8")]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPropertyValue(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, int role);
        [PreserveSig] int SetEndpointVisibility(IntPtr a, int b);
    }
}
