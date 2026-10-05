using System.Runtime.InteropServices;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 系统默认播放设备的主音量（CoreAudio COM 互操作，零依赖）。
/// 悬浮条媒体栏的音量滑条使用。所有操作静默降级：音频服务不可用时读返回 false、写不生效。
/// </summary>
public static class VolumeControl
{
    private const int DataFlowRender = 0;  // eRender
    private const int RoleConsole = 0;     // eConsole
    private const uint ClsCtxAll = 23;     // CLSCTX_ALL

    /// <summary>缓存 30 秒后重建：切默认播放设备（插拔耳机）后还能读到新设备的音量。</summary>
    private static IAudioEndpointVolume? _cached;
    private static DateTime _cachedAt;

    // ---------------- COM 契约：vtable 顺序必须与原生接口一致 ----------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
        int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr client);
        int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    [Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, uint clsCtx, IntPtr activationParams, out IAudioEndpointVolume volume);
        int OpenPropertyStore(uint stgmAccess, out IntPtr properties);
        int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        int GetState(out uint state);
    }

    [Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr client);
        int UnregisterControlChangeNotify(IntPtr client);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid eventContext);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint support);
        int GetVolumeRange(out float min, out float max, out float inc);
    }

    private static IAudioEndpointVolume? GetVolume()
    {
        if (_cached is not null && (DateTime.Now - _cachedAt).TotalSeconds < 30)
            return _cached;
        DropCache();
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
            enumerator.GetDefaultAudioEndpoint(DataFlowRender, RoleConsole, out var device);
            var iid = typeof(IAudioEndpointVolume).GUID;
            device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var volume);
            Marshal.ReleaseComObject(device);
            Marshal.ReleaseComObject(enumerator);
            _cached = volume;
            _cachedAt = DateTime.Now;
            return volume;
        }
        catch { DropCache(); return null; }
    }

    private static void DropCache()
    {
        if (_cached is null) return;
        Marshal.ReleaseComObject(_cached);
        _cached = null;
    }

    /// <summary>读当前主音量（0..1）。</summary>
    public static bool TryGetScalar(out float scalar)
    {
        scalar = 0;
        if (GetVolume() is not { } v) return false;
        try { return v.GetMasterVolumeLevelScalar(out scalar) == 0; }
        catch { DropCache(); return false; }
    }

    /// <summary>设主音量（0..1），自动取消静音。拖拽滑条时连续调用。</summary>
    public static void SetScalar(float scalar)
    {
        if (GetVolume() is not { } v) return;
        try
        {
            var ctx = Guid.Empty;
            v.SetMute(false, ref ctx);
            v.SetMasterVolumeLevelScalar(Math.Clamp(scalar, 0f, 1f), ref ctx);
        }
        catch { /* 音频服务瞬态不可用：忽略 */ }
    }

    public static bool TryGetMute(out bool muted)
    {
        muted = false;
        if (GetVolume() is not { } v) return false;
        try { return v.GetMute(out muted) == 0; }
        catch { DropCache(); return false; }
    }

    public static void SetMute(bool muted)
    {
        if (GetVolume() is not { } v) return;
        try
        {
            var ctx = Guid.Empty;
            v.SetMute(muted, ref ctx);
        }
        catch { /* 忽略 */ }
    }
}