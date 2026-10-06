// COM 注册样板：改编自微软官方 Widgets 样板
// （github.com/microsoft/WindowsAppSDK-Samples，Samples/Widgets/cs-console-packaged）

using Microsoft.Windows.Widgets.Providers;
using System.Runtime.InteropServices;
using WinRT;

namespace RonghuiEarbuds.Widgets;

internal static class ComGuids
{
    public const string IClassFactory = "00000001-0000-0000-C000-000000000046";
    public const string IUnknown = "00000000-0000-0000-C000-000000000046";
}

[ComImport]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
[Guid(ComGuids.IClassFactory)]
internal interface IClassFactory
{
    [PreserveSig]
    int CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject);

    [PreserveSig]
    int LockServer(bool fLock);
}

internal static class ClassObject
{
    public static void Register(Guid clsid, object punk, out uint cookie)
    {
        var hr = CoRegisterClassObject(clsid, punk, 0x4 /*CLSCTX_LOCAL_SERVER*/, 0x1 /*REGCLS_MULTIPLEUSE*/, out cookie);
        if (hr != 0) Marshal.ThrowExceptionForHR(hr);
    }

    public static void Revoke(uint cookie) => CoRevokeClassObject(cookie);

    [DllImport("ole32.dll")]
    private static extern int CoRegisterClassObject(
        [MarshalAs(UnmanagedType.LPStruct)] Guid rclsid,
        [MarshalAs(UnmanagedType.IUnknown)] object punk,
        uint dwClsContext, uint flags, out uint lpdwRegister);

    [DllImport("ole32.dll")]
    private static extern int CoRevokeClassObject(uint dwRegister);
}

/// <summary>Widgets Board 激活时按需创建 IWidgetProvider 实例的类工厂。</summary>
internal sealed class WidgetProviderFactory<T> : IClassFactory where T : IWidgetProvider, new()
{
    private const int ClassENoAggregation = -2147221232;
    private const int ENoInterface = -2147467262;

    int IClassFactory.CreateInstance(IntPtr pUnkOuter, ref Guid riid, out IntPtr ppvObject)
    {
        ppvObject = IntPtr.Zero;
        if (pUnkOuter != IntPtr.Zero) Marshal.ThrowExceptionForHR(ClassENoAggregation);
        if (riid == typeof(T).GUID || riid == Guid.Parse(ComGuids.IUnknown))
            ppvObject = MarshalInspectable<IWidgetProvider>.FromManaged(new T());
        else
            Marshal.ThrowExceptionForHR(ENoInterface);
        return 0;
    }

    int IClassFactory.LockServer(bool fLock) => 0;
}

/// <summary>持有 COM 类对象注册；Dispose 时撤销并触发退出事件。</summary>
internal sealed class RegistrationManager<TWidgetProvider> : IDisposable
    where TWidgetProvider : IWidgetProvider, new()
{
    private readonly uint _cookie;
    private readonly ManualResetEvent _disposedEvent = new(false);
    private bool _disposed;

    private RegistrationManager(uint cookie) => _cookie = cookie;

    public static RegistrationManager<TWidgetProvider> RegisterProvider()
    {
        ClassObject.Register(typeof(TWidgetProvider).GUID, new WidgetProviderFactory<TWidgetProvider>(), out var cookie);
        return new RegistrationManager<TWidgetProvider>(cookie);
    }

    public ManualResetEvent GetDisposedEvent() => _disposedEvent;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { ClassObject.Revoke(_cookie); } catch { /* 退出路径尽力而为 */ }
        _disposedEvent.Set();
        GC.SuppressFinalize(this);
    }

    ~RegistrationManager() => Dispose();
}
