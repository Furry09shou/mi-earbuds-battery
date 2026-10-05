using System.IO;
using System.Text.Json;

namespace RonghuiEarbuds.App.Core;

public sealed class AppConfig
{
    public string? BoundMac { get; set; }

    // null = 未记录（NaN 无法写入 JSON，必须用可空类型）
    public double? WindowLeft { get; set; }
    public double? WindowTop { get; set; }

    // 每天最多静默检查一次更新
    public DateTime? LastUpdateCheckUtc { get; set; }

    /// <summary>标题栏图钉：窗口置顶显示（微信式，随退出持久化）。</summary>
    public bool TopMost { get; set; }

    // 出现过的设备名单（切换列表用）：不同时段连接的耳机也能切回，跨重启保留
    public List<KnownDeviceEntry>? KnownDevices { get; set; }

    // ================= v1.6.0 设置界面项（不再手改 config.json） =================

    /// <summary>低电量提醒阈值（%）。电量低于该值弹托盘提醒。</summary>
    public int LowBatteryThreshold { get; set; } = 20;

    /// <summary>耳机开盖（收到广播）时自动弹出主窗口。</summary>
    public bool OpenLidPopup { get; set; } = true;

    /// <summary>开盖弹窗冷却时间（分钟），避免反复打扰。</summary>
    public int PopupCooldownMinutes { get; set; } = 5;

    /// <summary>提醒勿扰时段开关（时段内不弹低电量/骤降提醒）。</summary>
    public bool QuietHoursEnabled { get; set; } = true;

    /// <summary>勿扰开始小时（0-23，默认 22 = 22:00）。</summary>
    public int QuietStartHour { get; set; } = 22;

    /// <summary>勿扰结束小时（0-23，默认 8 = 08:00）。</summary>
    public int QuietEndHour { get; set; } = 8;

    /// <summary>电量骤降提醒（短时间下降过快时提醒，可能是耳机异常耗电）。</summary>
    public bool SuddenDropAlert { get; set; } = true;

    /// <summary>悬浮迷你电量条开关。</summary>
    public bool MiniBarEnabled { get; set; }

    /// <summary>悬浮条位置（拖动后持久化）。</summary>
    public double? MiniBarLeft { get; set; }
    public double? MiniBarTop { get; set; }

    /// <summary>外观模式：system=跟随 Windows（默认）/ dark=深色 / light=浅色。</summary>
    public string ThemeMode { get; set; } = "system";

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RonghuiEarbuds");
    private static string FilePath => Path.Combine(Dir, "config.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
        }
        catch { /* 损坏则重建 */ }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch (Exception ex)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.AppendAllText(Path.Combine(Dir, "error.log"),
                    $"[{DateTime.Now:HH:mm:ss}] ConfigSave 失败: {ex}\r\n");
            }
            catch { /* 彻底没辙 */ }
        }
    }
}

/// <summary>设备名单条目。</summary>
public sealed class KnownDeviceEntry
{
    public string Mac { get; set; } = "";
    public string Name { get; set; } = "";
    public DateTime LastSeen { get; set; }

    /// <summary>false = 未适配格式（只有连接状态，无电量数据）。</summary>
    public bool Adapted { get; set; } = true;
}

public static class AutoStartHelper
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "RonghuiEarbuds";

    public static bool IsEnabled()
    {
        using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
        return k?.GetValue(ValueName) is string;
    }

    /// <summary>
    /// 旧版本注册的自启动值不带 --minimized 参数（开机弹出主窗口），
    /// 启动时静默升级为托盘启动。
    /// </summary>
    public static void EnsureMinimizedFlag()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (k?.GetValue(ValueName) is string v && !v.Contains("--minimized"))
            {
                var exe = Environment.ProcessPath;
                if (exe is not null)
                    k.SetValue(ValueName, $"\"{exe}\" --minimized");
            }
        }
        catch { /* 注册表不可写时放弃，下次再试 */ }
    }

    public static void Set(bool enabled)
    {
        if (enabled)
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            var exe = Environment.ProcessPath;
            if (exe is not null)
                k.SetValue(ValueName, $"\"{exe}\" --minimized");
        }
        else
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            k?.DeleteValue(ValueName, throwOnMissingValue: false);
        }
    }
}
