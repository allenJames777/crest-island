using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace Crest.Platform;

public enum SpeakerLevel { Muted, Off, Low, High }

/// <summary>Pure volume helpers, kept apart from Core Audio so they can be unit-tested.</summary>
public static class VolumeMath
{
    /// <summary>Volume change per mouse-wheel notch (120 units).</summary>
    public const double StepPerNotch = 0.02;

    public static double Step(double level, int wheelDelta) =>
        Math.Clamp(Math.Round(level + wheelDelta / 120.0 * StepPerNotch, 6), 0, 1);

    public static SpeakerLevel Icon(double level, bool muted) =>
        muted ? SpeakerLevel.Muted : level <= 0.001 ? SpeakerLevel.Off : level < 0.5 ? SpeakerLevel.Low : SpeakerLevel.High;
}

/// <summary>
/// The Windows master volume of the default output device (the taskbar speaker), through Core Audio.
/// Raises <see cref="Changed"/> on the UI thread when the volume or mute changes anywhere, and follows a change of
/// default output device. If Core Audio is unavailable, <see cref="Available"/> is false and calls do nothing.
/// </summary>
internal sealed class SystemVolume : IDisposable, IAudioEndpointVolumeCallback, IMMNotificationClient
{
    private const int ERender = 0, EMultimedia = 1, ClsctxAll = 23;
    private static Guid _ownChange = new("6B9C3E4A-2F7D-4C1B-9E0A-5D8F1C2B3A47");

    private readonly Dispatcher _ui;
    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;

    public SystemVolume(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            _enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            _enumerator.RegisterEndpointNotificationCallback(this);
            Acquire();
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"Crest: volume control unavailable: {ex.Message}");
        }
    }

    public bool Available => _endpoint is not null;
    public double Level { get; private set; }
    public bool Muted { get; private set; }

    /// <summary>Volume (0 to 1) and mute, raised on the UI thread.</summary>
    public event Action<double, bool>? Changed;

    public void SetLevel(double level)
    {
        if (_endpoint is null) return;
        level = Math.Clamp(level, 0, 1);
        try
        {
            _endpoint.SetMasterVolumeLevelScalar((float)level, ref _ownChange);
            if (Muted && level > 0)
            {
                _endpoint.SetMute(false, ref _ownChange); // like the taskbar: moving the volume unmutes
                Muted = false;
            }
            Level = level;
            Changed?.Invoke(Level, Muted);
        }
        catch (COMException ex) { Debug.WriteLine($"Crest: could not set the volume: {ex.Message}"); }
    }

    public void Step(int wheelDelta) => SetLevel(VolumeMath.Step(Level, wheelDelta));

    public void ToggleMute()
    {
        if (_endpoint is null) return;
        try
        {
            _endpoint.SetMute(!Muted, ref _ownChange);
            Muted = !Muted;
            Changed?.Invoke(Level, Muted);
        }
        catch (COMException ex) { Debug.WriteLine($"Crest: could not change mute: {ex.Message}"); }
    }

    public void Dispose()
    {
        Release();
        if (_enumerator is not null)
        {
            try { _enumerator.UnregisterEndpointNotificationCallback(this); } catch (COMException) { }
            Marshal.ReleaseComObject(_enumerator);
            _enumerator = null;
        }
    }

    private void Acquire()
    {
        Release();
        if (_enumerator is null) return;
        try
        {
            Marshal.ThrowExceptionForHR(_enumerator.GetDefaultAudioEndpoint(ERender, EMultimedia, out var device));
            object activated;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                Marshal.ThrowExceptionForHR(device.Activate(ref iid, ClsctxAll, IntPtr.Zero, out activated));
            }
            finally
            {
                Marshal.ReleaseComObject(device);
            }
            _endpoint = (IAudioEndpointVolume)activated;
            try { _endpoint.RegisterControlChangeNotify(this); }
            catch (COMException)
            {
                Marshal.ReleaseComObject(_endpoint);
                _endpoint = null;
                throw;
            }
            _endpoint.GetMasterVolumeLevelScalar(out float level);
            _endpoint.GetMute(out bool muted);
            Level = level;
            Muted = muted;
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException)
        {
            Debug.WriteLine($"Crest: no default output device: {ex.Message}");
            _endpoint = null;
        }
        Changed?.Invoke(Level, Muted);
    }

    private void Release()
    {
        if (_endpoint is null) return;
        try { _endpoint.UnregisterControlChangeNotify(this); } catch (COMException) { }
        Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    // ----- Core Audio callbacks (on a Windows audio thread) -----

    int IAudioEndpointVolumeCallback.OnNotify(IntPtr data)
    {
        // AUDIO_VOLUME_NOTIFICATION_DATA: GUID context (16 bytes), BOOL muted (4), float master volume (4), ...
        // Crest's own changes are already applied locally; their echoes can arrive late and out of order.
        if (Marshal.PtrToStructure<Guid>(data) == _ownChange) return 0;
        bool muted = Marshal.ReadInt32(data, 16) != 0;
        float level = BitConverter.Int32BitsToSingle(Marshal.ReadInt32(data, 20));
        _ui.InvokeAsync(() =>
        {
            Level = level;
            Muted = muted;
            Changed?.Invoke(level, muted);
        });
        return 0;
    }

    int IMMNotificationClient.OnDefaultDeviceChanged(int flow, int role, string? defaultDeviceId)
    {
        if (flow == ERender && role == EMultimedia) _ui.InvokeAsync(Acquire);
        return 0;
    }

    int IMMNotificationClient.OnDeviceStateChanged(string? deviceId, int newState) => 0;
    int IMMNotificationClient.OnDeviceAdded(string? deviceId) => 0;
    int IMMNotificationClient.OnDeviceRemoved(string? deviceId) => 0;
    int IMMNotificationClient.OnPropertyValueChanged(string? deviceId, PropertyKey key) => 0;
}

// ----- Core Audio COM declarations -----

[ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
internal class MMDeviceEnumeratorComObject;

[ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDeviceEnumerator
{
    [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
    [PreserveSig] int RegisterEndpointNotificationCallback(IMMNotificationClient client);
    [PreserveSig] int UnregisterEndpointNotificationCallback(IMMNotificationClient client);
}

[ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMDevice
{
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);
}

[ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolume
{
    void RegisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void UnregisterControlChangeNotify(IAudioEndpointVolumeCallback notify);
    void GetChannelCount(out uint count);
    void SetMasterVolumeLevel(float levelDb, ref Guid eventContext);
    void SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
    void GetMasterVolumeLevel(out float levelDb);
    void GetMasterVolumeLevelScalar(out float level);
    void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid eventContext);
    void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
    void GetChannelVolumeLevel(uint channel, out float levelDb);
    void GetChannelVolumeLevelScalar(uint channel, out float level);
    void SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
    void GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
}

[ComImport, Guid("657804FA-D6AD-4496-8A60-352752AF4F89"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IAudioEndpointVolumeCallback
{
    [PreserveSig] int OnNotify(IntPtr notifyData);
}

[StructLayout(LayoutKind.Sequential)]
internal struct PropertyKey
{
    public Guid FormatId;
    public int PropertyId;
}

[ComImport, Guid("7991EEC9-7E89-4D85-8390-6C703CEC60C0"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMMNotificationClient
{
    [PreserveSig] int OnDeviceStateChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, int newState);
    [PreserveSig] int OnDeviceAdded([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    [PreserveSig] int OnDeviceRemoved([MarshalAs(UnmanagedType.LPWStr)] string? deviceId);
    [PreserveSig] int OnDefaultDeviceChanged(int flow, int role, [MarshalAs(UnmanagedType.LPWStr)] string? defaultDeviceId);
    [PreserveSig] int OnPropertyValueChanged([MarshalAs(UnmanagedType.LPWStr)] string? deviceId, PropertyKey key);
}
