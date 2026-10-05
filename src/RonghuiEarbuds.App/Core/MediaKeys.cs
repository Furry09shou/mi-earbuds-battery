using System.Runtime.InteropServices;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 系统媒体键：模拟键盘媒体键（上一曲/播放暂停/下一曲），
/// Windows 经 SMTC 转发给当前会话的播放器——效果与耳机线控、键盘媒体键一致。
/// 零依赖（仅 user32），悬浮条媒体控制栏使用。
/// </summary>
public static class MediaKeys
{
    private const byte VK_MEDIA_NEXT_TRACK = 0xB0;
    private const byte VK_MEDIA_PREV_TRACK = 0xB1;
    private const byte VK_MEDIA_PLAY_PAUSE = 0xB3;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    private static void Tap(byte vk)
    {
        keybd_event(vk, 0, 0, UIntPtr.Zero);
        keybd_event(vk, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static void Prev() => Tap(VK_MEDIA_PREV_TRACK);
    public static void PlayPause() => Tap(VK_MEDIA_PLAY_PAUSE);
    public static void Next() => Tap(VK_MEDIA_NEXT_TRACK);
}