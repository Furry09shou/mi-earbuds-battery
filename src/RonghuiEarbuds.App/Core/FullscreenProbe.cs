using System.Runtime.InteropServices;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 检测前台应用是否全屏（开盖弹窗抑制用）。
/// 判据：前台窗口矩形覆盖整个显示器（含任务栏区域，允许 2px 误差）即视为全屏；
/// 最大化窗口只覆盖工作区，不会误判。游戏/视频全屏时弹主窗会打断用户，
/// 此时跳过本次弹窗且不消耗冷却——退出全屏后下次开盖仍会弹。
/// </summary>
public static class FullscreenProbe
{
    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    /// <summary>前台是否全屏应用（游戏/全屏视频）。桌面与最小化窗口不算。</summary>
    public static bool IsForegroundFullscreen()
    {
        try
        {
            var hwnd = GetForegroundWindow();
            if (hwnd == IntPtr.Zero || hwnd == GetShellWindow() || IsIconic(hwnd))
                return false;
            if (!GetWindowRect(hwnd, out var wr))
                return false;
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero)
                return false;
            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(mon, ref info))
                return false;

            // 覆盖整个显示器（含任务栏区域）= 全屏；最大化窗口只盖工作区
            return wr.Left <= info.rcMonitor.Left + 2 &&
                   wr.Top <= info.rcMonitor.Top + 2 &&
                   wr.Right >= info.rcMonitor.Right - 2 &&
                   wr.Bottom >= info.rcMonitor.Bottom - 2;
        }
        catch { return false; }
    }
}
