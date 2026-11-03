using System.Runtime.InteropServices;

namespace EqualTune.AudioLab;

/// <summary>Reads and changes the Windows volume (and mute) of one playback device.</summary>
internal sealed class EndpointVolume : IDisposable
{
    private IAudioEndpointVolume? _volume;
    private Guid _context = Guid.Empty;
    public string DeviceId { get; }

    private EndpointVolume(string deviceId, IAudioEndpointVolume volume) { DeviceId = deviceId; _volume = volume; }

    public static EndpointVolume? Open(string deviceId) => Create(e => e.GetDevice(deviceId, out var d) == 0 ? d : null, deviceId);

    public static EndpointVolume? OpenDefault() => Create(e => e.GetDefaultAudioEndpoint(0, 1, out var d) == 0 ? d : null, "");

    private static EndpointVolume? Create(Func<IMMDeviceEnumerator, IMMDevice?> find, string id)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E"), true)!;
            enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;
            device = find(enumerator);
            if (device is null) return null;
            var iid = new Guid("5CDF2C82-841E-4546-9722-0CF74078229A");
            if (device.Activate(ref iid, 23, IntPtr.Zero, out var instance) != 0) return null;
            return new EndpointVolume(id, (IAudioEndpointVolume)instance);
        }
        catch (Exception ex) { AppLog.Error("Could not open the Windows volume control", ex); return null; }
        finally
        {
            if (device is not null) Marshal.ReleaseComObject(device);
            if (enumerator is not null) Marshal.ReleaseComObject(enumerator);
        }
    }

    public bool TryRead(out double scalar, out bool muted)
    {
        scalar = 1; muted = false;
        try
        {
            if (_volume is null) return false;
            if (_volume.GetMasterVolumeLevelScalar(out var s) != 0) return false;
            _volume.GetMute(out var m);
            scalar = Math.Clamp(s, 0, 1); muted = m;
            return true;
        }
        catch { return false; }
    }

    public void SetScalar(double scalar)
    {
        try { _volume?.SetMasterVolumeLevelScalar((float)Math.Clamp(scalar, 0, 1), ref _context); } catch { }
    }

    public void SetMute(bool mute)
    {
        try { _volume?.SetMute(mute, ref _context); } catch { }
    }

    public void Dispose()
    {
        if (_volume is not null) { try { Marshal.ReleaseComObject(_volume); } catch { } _volume = null; }
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("A95664D2-9614-4F35-A746-DE8DB63617E6")]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("D666063F-1587-4E43-81F1-B948E807363F")]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, uint context, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("5CDF2C82-841E-4546-9722-0CF74078229A")]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
