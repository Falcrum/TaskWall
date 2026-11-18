using System;
using System.Runtime.InteropServices;

namespace TaskWall;

/// <summary>
/// The default speakers' volume and mute through Windows' Core Audio (the same the volume slider of Windows moves): read, set,
/// and told about – a change from anywhere (the keyboard's keys, another app) and a new default device raise <see cref="Changed"/>
/// on a Core Audio thread. No polling.
/// </summary>
static class Volume
{
    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")] class MMDeviceEnumeratorCom { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int flow, int mask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int flow, int role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr parameters, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IPropertyStore store);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [StructLayout(LayoutKind.Sequential)] struct PropertyKey { public Guid Fmtid; public uint Pid; }
    [StructLayout(LayoutKind.Sequential)] struct PropVariant { public ushort Vt; public ushort R1, R2, R3; public IntPtr P; public IntPtr P2; }
    [DllImport("ole32.dll")] static extern int PropVariantClear(ref PropVariant pv);

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int UnregisterControlChangeNotify(IAudioEndpointVolumeCallback cb);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float db, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float db);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float db, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float db);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }

    [ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IAudioEndpointVolumeCallback { [PreserveSig] int OnNotify(IntPtr data); }

    [ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMNotificationClient
    {
        [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string id, int state);
        [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string id);
        [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string id, PropertyKey key);
    }

    sealed class Callbacks : IAudioEndpointVolumeCallback, IMMNotificationClient
    {
        public int OnNotify(IntPtr data) { Changed?.Invoke(); return 0; }
        public int OnDeviceStateChanged(string id, int state) => 0;
        public int OnDeviceAdded(string id) => 0;
        public int OnDeviceRemoved(string id) => 0;
        public int OnDefaultDeviceChanged(int flow, int role, string id) { if (flow == 0 && role == 1) DeviceChanged?.Invoke(); return 0; }
        public int OnPropertyValueChanged(string id, PropertyKey key) => 0;
    }

    static IMMDeviceEnumerator? _devices;
    static IAudioEndpointVolume? _endpoint;
    static readonly Callbacks Listener = new();
    static Guid _context = Guid.NewGuid();
    static bool _listening;

    /// <summary>The volume or the mute changed (from anywhere) – raised on a Core Audio thread.</summary>
    public static event Action? Changed;
    /// <summary>Another device became the default speakers – raised on a Core Audio thread; call <see cref="Reopen"/> on the UI thread.</summary>
    public static event Action? DeviceChanged;

    /// <summary>The default speakers' name ("Głośniki (Realtek)").</summary>
    public static string DeviceName { get; private set; } = "";

    static IAudioEndpointVolume? Endpoint()
    {
        if (_endpoint != null) return _endpoint;
        try
        {
            _devices ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
            if (!_listening) { _devices.RegisterEndpointNotificationCallback(Listener); _listening = true; }
            if (_devices.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device) != 0 || device == null) return null;
            var iid = typeof(IAudioEndpointVolume).GUID;
            if (device.Activate(ref iid, 23 /* CLSCTX_ALL */, IntPtr.Zero, out var o) != 0) return null;
            _endpoint = (IAudioEndpointVolume)o;
            _endpoint.RegisterControlChangeNotify(Listener);
            DeviceName = Name(device);
            return _endpoint;
        }
        catch (Exception ex) { Log.Error("volume", ex); return null; }
    }

    static string Name(IMMDevice device)
    {
        try
        {
            if (device.OpenPropertyStore(0 /* STGM_READ */, out var store) != 0) return "";
            var key = new PropertyKey { Fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), Pid = 14 }; // PKEY_Device_FriendlyName
            if (store.GetValue(ref key, out var pv) != 0) return "";
            try { return pv.Vt == 31 && pv.P != IntPtr.Zero ? Marshal.PtrToStringUni(pv.P) ?? "" : ""; }
            finally { PropVariantClear(ref pv); }
        }
        catch { return ""; }
    }

    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    // Windows' own way to change the default device (what its sound settings and the volume flyout use): not documented, but the
    // same interface since Windows 7 – SoundSwitch, EarTrumpet and others use it
    [ComImport, Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9")] class PolicyConfigCom { }

    [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(IntPtr a, IntPtr b);
        [PreserveSig] int GetDeviceFormat(IntPtr a, int b, IntPtr c);
        [PreserveSig] int ResetDeviceFormat(IntPtr a);
        [PreserveSig] int SetDeviceFormat(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int GetProcessingPeriod(IntPtr a, int b, IntPtr c, IntPtr d);
        [PreserveSig] int SetProcessingPeriod(IntPtr a, IntPtr b);
        [PreserveSig] int GetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int SetShareMode(IntPtr a, IntPtr b);
        [PreserveSig] int GetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetPropertyValue(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string id, int role);
        [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string id, int visible);
    }

    /// <summary>An output device: its id and its name ("Głośniki (Realtek)", "Słuchawki (G PRO X 2)").</summary>
    public sealed record Device(string Id, string Name);

    /// <summary>The output devices that are plugged in and on, and the id of the default one.</summary>
    public static (System.Collections.Generic.List<Device> Devices, string? Default) Outputs()
    {
        var list = new System.Collections.Generic.List<Device>();
        string? current = null;
        try
        {
            Endpoint();
            if (_devices == null) return (list, null);
            if (_devices.GetDefaultAudioEndpoint(0, 1, out var def) == 0 && def != null && def.GetId(out var defId) == 0) current = defId;
            if (_devices.EnumAudioEndpoints(0 /* eRender */, 1 /* DEVICE_STATE_ACTIVE */, out var p) != 0 || p == IntPtr.Zero) return (list, current);
            var all = (IMMDeviceCollection)Marshal.GetObjectForIUnknown(p);
            Marshal.Release(p);
            all.GetCount(out var n);
            for (uint i = 0; i < n; i++)
                if (all.Item(i, out var d) == 0 && d != null && d.GetId(out var id) == 0)
                    list.Add(new Device(id, Name(d) is { Length: > 0 } nm ? nm : id));
        }
        catch (Exception ex) { Log.Error("audio devices", ex); }
        return (list, current);
    }

    /// <summary>Whether Windows gives out the interface that changes the default device (checked without changing anything).</summary>
    public static bool CanSwitch()
    {
        try { var p = (IPolicyConfig)new PolicyConfigCom(); Marshal.ReleaseComObject(p); return true; }
        catch { return false; }
    }

    /// <summary>Makes <paramref name="id"/> the default output for everything (the console, multimedia and communications roles).</summary>
    public static void SetDefault(string id)
    {
        try
        {
            var policy = (IPolicyConfig)new PolicyConfigCom();
            for (int role = 0; role < 3; role++) policy.SetDefaultEndpoint(id, role);
        }
        catch (Exception ex) { Log.Error("audio default device", ex); }
    }

    /// <summary>The default speakers changed: let go of the old ones, take the new ones.</summary>
    public static void Reopen()
    {
        try { _endpoint?.UnregisterControlChangeNotify(Listener); } catch { }
        _endpoint = null;
        Endpoint();
    }

    /// <summary>0..1, or null without speakers.</summary>
    public static double? Level
    {
        get => Endpoint() is { } e && e.GetMasterVolumeLevelScalar(out var l) == 0 ? l : null;
        set { if (value is { } v && Endpoint() is { } e) e.SetMasterVolumeLevelScalar((float)Math.Clamp(v, 0, 1), ref _context); }
    }

    public static bool Muted
    {
        get => Endpoint() is { } e && e.GetMute(out var m) == 0 && m;
        set { if (Endpoint() is { } e) e.SetMute(value, ref _context); }
    }
}
