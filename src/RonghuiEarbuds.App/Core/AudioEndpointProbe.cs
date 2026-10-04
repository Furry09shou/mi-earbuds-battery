using Microsoft.Win32;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 通过 Windows 音频端点判断蓝牙耳机是否仍在连接：
/// 耳机连接时系统会为其创建活动（Active）渲染端点（A2DP/HFP），
/// 断开后端点立即变为非活动。比 Win32 蓝牙枚举的 fConnected 更可靠
/// （后者对部分 TWS 耳机始终查不到连接）。
/// </summary>
public static class AudioEndpointProbe
{
    private const string RenderKey =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\MMDevices\Audio\Render";
    // DeviceInterface 名称（如 "Mi Air2 SE Hands-Free"），缺失时退回设备通用名
    private const string InterfaceNameValue =
        @"{b3f8fa53-0004-438e-9003-51a46e139bfc},6";
    private const string DeviceNameValue =
        @"{a45c254e-df1c-4efd-8020-67d146a850e0},2";

    /// <summary>是否存在名称包含 deviceName 的活动音频输出端点。</summary>
    public static bool HasActiveEndpoint(string? deviceName)
    {
        if (string.IsNullOrWhiteSpace(deviceName)) return false;
        try
        {
            using var root = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Registry64);
            using var render = root.OpenSubKey(RenderKey);
            if (render is null) return false;

            foreach (var id in render.GetSubKeyNames())
            {
                using var ep = render.OpenSubKey(id);
                if (ep is null) continue;
                if ((ep.GetValue("DeviceState") as int?) != 1) continue;   // 1 = Active

                using var props = ep.OpenSubKey("Properties");
                if (props is null) continue;
                var name = (props.GetValue(InterfaceNameValue) as string)
                        ?? (props.GetValue(DeviceNameValue) as string);
                if (name is not null &&
                    name.Contains(deviceName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
        }
        catch { /* 读取失败按未连接处理 */ }
        return false;
    }
}
