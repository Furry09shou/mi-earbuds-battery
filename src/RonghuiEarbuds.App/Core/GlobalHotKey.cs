using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 全局热键 Ctrl+Alt+B：任意界面（含全屏游戏）呼出主面板。
/// 注册表级 RegisterHotKey 挂在主窗口句柄上，消息经 WndProc 钩子回调触发。
/// 设置页可开关（开关即注册/注销，无需重启）。
/// </summary>
public sealed class GlobalHotKey
{
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_CONTROL = 0x0002;
    private const uint MOD_ALT = 0x0001;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_B = 0x42;
    private const int HotKeyId = 0x5248;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private IntPtr _hwnd;
    private bool _registered;
    private Action? _trigger;

    public static string Describe => "Ctrl+Alt+B";

    /// <summary>注册/注销（enabled=false 时只注销）。窗口句柄取一次缓存。</summary>
    public void SetEnabled(Window window, bool enabled, Action trigger)
    {
        _trigger = trigger;
        if (_hwnd == IntPtr.Zero)
        {
            _hwnd = new WindowInteropHelper(window).EnsureHandle();
            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
        }

        if (enabled && !_registered)
        {
            _registered = RegisterHotKey(_hwnd, HotKeyId, MOD_CONTROL | MOD_ALT | MOD_NOREPEAT, VK_B);
            EarbudsWatcher.DiagLog($"全局热键注册：{(_registered ? "成功" : "失败（可能已被占用）")}");
        }
        else if (!enabled && _registered)
        {
            UnregisterHotKey(_hwnd, HotKeyId);
            _registered = false;
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HotKeyId)
        {
            _trigger?.Invoke();
            handled = true;
        }
        return IntPtr.Zero;
    }
}