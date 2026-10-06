using System.IO;
using System.Text.Json;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 把耳机电量状态广播给 Windows 11 小组件提供程序：
/// 序列化到 %PROGRAMDATA%\RonghuiEarbuds\state.json（原子替换），
/// widget/RonghuiEarbuds.Widgets 的 MSIX 包在小组件面板可见时轮询该文件。
/// 用 %PROGRAMDATA% 而非 %APPDATA%：打包应用读 %APPDATA% 会被 MSIX 虚拟化重定向。
/// 内容变化才写盘（内部比对上次序列化结果），写盘走后台线程不占 UI。
/// </summary>
public static class WidgetStatePublisher
{
    public sealed class DeviceInfo
    {
        public string Mac = "";
        public string Name = "";
        public bool Primary;
        public int? Left;
        public int? Right;
        public int? Case;
        public bool LeftInCase;
        public bool RightInCase;
        public DateTime? LastBroadcastUtc;
        public DateTime? ConnSeenUtc;
        public int? SystemBattery;
        public bool IsAdapted = true;
    }

    public sealed class State
    {
        public int Ver = 1;
        public DateTime TsUtc;
        public List<DeviceInfo> Devices = new();
    }

    private static readonly object Gate = new();
    private static string? _lastJson;
    private static bool _dirWarned;

    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RonghuiEarbuds");
    private static readonly string FilePath = Path.Combine(Dir, "state.json");

    private static readonly JsonSerializerOptions JsonOpts = new() { IncludeFields = true };

    public static void Publish(State state)
    {
        try
        {
            state.TsUtc = DateTime.UtcNow;
            var json = JsonSerializer.Serialize(state, JsonOpts);
            lock (Gate)
            {
                if (json == _lastJson) return;   // 内容没变不写盘
                _lastJson = json;
            }
            Task.Run(() =>
            {
                try
                {
                    Directory.CreateDirectory(Dir);
                    var tmp = FilePath + ".tmp";
                    File.WriteAllText(tmp, json);
                    if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                    else File.Move(tmp, FilePath);
                }
                catch (Exception ex)
                {
                    EarbudsWatcher.DiagLog($"小组件状态写入失败: {ex.Message}");
                }
            });
        }
        catch (Exception ex)
        {
            EarbudsWatcher.DiagLog($"小组件状态序列化失败: {ex.Message}");
        }
    }
}
