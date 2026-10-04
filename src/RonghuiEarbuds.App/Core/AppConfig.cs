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
