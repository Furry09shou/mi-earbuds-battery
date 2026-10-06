using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RonghuiEarbuds.Widgets;

/// <summary>
/// 读取主程序的状态文件并生成 Adaptive Cards 模板 + 数据。
/// 状态文件由主程序维护于 %PROGRAMDATA%\RonghuiEarbuds\state.json。
/// 新鲜度判定与主面板一致：广播 8 秒内=实时；仅连接心跳=灰显待更新；
/// 全部过期=离线回「--」（卡片绝不挂旧值）。
/// </summary>
internal static class CardBuilder
{
    // ---------- 状态文件模型（与主程序序列化格式一一对应） ----------

    public sealed class StateFile
    {
        public int Ver { get; set; }
        public DateTime? TsUtc { get; set; }
        public List<Device>? Devices { get; set; }
    }

    public sealed class Device
    {
        public string Mac { get; set; } = "";
        public string Name { get; set; } = "";
        public bool Primary { get; set; }
        public int? Left { get; set; }
        public int? Right { get; set; }
        public int? Case { get; set; }
        public bool LeftInCase { get; set; }
        public bool RightInCase { get; set; }
        public DateTime? LastBroadcastUtc { get; set; }
        public DateTime? ConnSeenUtc { get; set; }
        public int? SystemBattery { get; set; }
        public bool IsAdapted { get; set; } = true;
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        IncludeFields = true,
    };

    private const double FreshSeconds = 8;        // 广播新鲜窗口（同主面板 Channel/Stale 判定）
    private const double AliveSeconds = 60;       // 连接心跳存活窗口

    // ---------- 卡片模板 ----------

    private const string Template = """
        {
          "type": "AdaptiveCard",
          "$schema": "http://adaptivecards.io/schemas/adaptive-card.json",
          "version": "1.5",
          "body": [
            {
              "type": "TextBlock",
              "text": "${name}",
              "weight": "bolder",
              "size": "medium",
              "wrap": true,
              "maxLines": 1
            },
            {
              "type": "TextBlock",
              "text": "${status}",
              "isSubtle": true,
              "size": "small",
              "spacing": "none",
              "wrap": true,
              "maxLines": 2
            },
            {
              "type": "ColumnSet",
              "spacing": "medium",
              "$when": "${$host.widgetSize == \"medium\"}",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${lv}", "size": "extraLarge", "weight": "bolder", "color": "${lc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${ll}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${lb}", "color": "accent", "size": "small", "horizontalAlignment": "center", "spacing": "none", "$when": "${li}" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${rv}", "size": "extraLarge", "weight": "bolder", "color": "${rc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${rl}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${rb}", "color": "accent", "size": "small", "horizontalAlignment": "center", "spacing": "none", "$when": "${ri}" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${cv}", "size": "extraLarge", "weight": "bolder", "color": "${cc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${cl}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${cb}", "color": "accent", "size": "small", "horizontalAlignment": "center", "spacing": "none", "$when": "${ci}" }
                  ]
                }
              ]
            },
            {
              "type": "ColumnSet",
              "spacing": "medium",
              "$when": "${$host.widgetSize == \"small\"}",
              "columns": [
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${lv}", "size": "large", "weight": "bolder", "color": "${lc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${sl}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${rv}", "size": "large", "weight": "bolder", "color": "${rc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${sr}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" }
                  ]
                },
                {
                  "type": "Column",
                  "width": "stretch",
                  "items": [
                    { "type": "TextBlock", "text": "${cv}", "size": "large", "weight": "bolder", "color": "${cc}", "horizontalAlignment": "center", "spacing": "none" },
                    { "type": "TextBlock", "text": "${sc}", "isSubtle": true, "size": "small", "horizontalAlignment": "center", "spacing": "none" }
                  ]
                }
              ]
            }
          ]
        }
        """;

    // ---------- 构建 ----------

    public static (string Template, string Data) Build()
    {
        var state = TryLoadState();
        var zh = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "zh";
        var dev = PickDevice(state?.Devices);
        var data = zh ? BuildZh(dev) : BuildEn(dev);
        return (Template, JsonSerializer.Serialize(data, JsonOpts));
    }

    private static StateFile? TryLoadState()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "RonghuiEarbuds", "state.json");
            if (!File.Exists(path)) return null;
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<StateFile>(json, JsonOpts);
        }
        catch { return null; }
    }

    private static Device? PickDevice(List<Device>? devices)
    {
        if (devices is null || devices.Count == 0) return null;
        var now = DateTime.UtcNow;
        Device? primary = null, alive = null;
        foreach (var d in devices)
        {
            if (d.Primary) { primary ??= d; }
            if (alive is null && IsAlive(d, now)) alive = d;
        }
        return primary ?? alive ?? devices[0];
    }

    private static bool IsAlive(Device d, DateTime now) =>
        (d.LastBroadcastUtc is { } lb && (now - lb).TotalSeconds <= AliveSeconds) ||
        (d.ConnSeenUtc is { } cs && (now - cs).TotalSeconds <= AliveSeconds);

    private static (bool Alive, bool Fresh) Freshness(Device d)
    {
        var now = DateTime.UtcNow;
        var fresh = d.LastBroadcastUtc is { } lb && (now - lb).TotalSeconds <= FreshSeconds;
        return (IsAlive(d, now), fresh);
    }

    private static string ColorOf(int? pct, bool fresh)
    {
        if (pct is null || !fresh) return "default";
        return pct.Value switch
        {
            >= 50 => "good",
            >= 20 => "warning",
            _ => "attention",
        };
    }

    private static string Badge(bool inCase) => inCase ? "⚡" : "";

    private static Dictionary<string, object?> EmptyData(bool zh) => new()
    {
        ["name"] = zh ? "绒汇耳机助手" : "Ronghui Earbuds",
        ["status"] = zh ? "未发现耳机 · 打开耳机盖后自动显示" : "No earbuds found · open the case lid",
        ["lv"] = "--", ["rv"] = "--", ["cv"] = "--",
        ["lc"] = "default", ["rc"] = "default", ["cc"] = "default",
        ["ll"] = zh ? "左耳" : "Left", ["rl"] = zh ? "右耳" : "Right", ["cl"] = zh ? "充电仓" : "Case",
        ["sl"] = "L", ["sr"] = "R", ["sc"] = zh ? "仓" : "Case",
        ["lb"] = "", ["rb"] = "", ["cb"] = "",
        ["li"] = false, ["ri"] = false, ["ci"] = false,
    };

    private static Dictionary<string, object?> BuildZh(Device? d)
    {
        if (d is null) return EmptyData(true);

        var (alive, fresh) = Freshness(d);
        var data = new Dictionary<string, object?>
        {
            ["name"] = d.Name.Length > 0 ? d.Name : "未知耳机",
            ["lv"] = "--", ["rv"] = "--", ["cv"] = "--",
            ["lc"] = "default", ["rc"] = "default", ["cc"] = "default",
            ["ll"] = "左耳", ["rl"] = "右耳", ["cl"] = "充电仓",
            ["sl"] = "L", ["sr"] = "R", ["sc"] = "仓",
            ["lb"] = "", ["rb"] = "", ["cb"] = "",
            ["li"] = false, ["ri"] = false, ["ci"] = false,
        };

        if (!alive)
        {
            data["status"] = "离线 · 打开耳机盖可刷新";
            return data;
        }

        if (fresh)
        {
            data["status"] = d.LeftInCase && d.RightInCase ? "充电中" : "已连接";
            data["lv"] = Pct(d.Left); data["rv"] = Pct(d.Right); data["cv"] = Pct(d.Case);
            data["lc"] = ColorOf(d.Left, true); data["rc"] = ColorOf(d.Right, true); data["cc"] = ColorOf(d.Case, true);
            data["li"] = d.LeftInCase; data["ri"] = d.RightInCase;
        }
        else
        {
            // 广播停发但设备连接中：分耳数据待更新，灰显兜底整机
            if (!d.IsAdapted && d.SystemBattery is { } sys)
                data["status"] = $"未适配分耳电量 · 整机 {sys}%";
            else if (d.SystemBattery is { } sys2)
                data["status"] = $"使用中 · 整机 {sys2}%";
            else
                data["status"] = "已连接 · 分耳数据待更新";
        }
        return data;
    }

    private static Dictionary<string, object?> BuildEn(Device? d)
    {
        if (d is null) return EmptyData(false);

        var (alive, fresh) = Freshness(d);
        var data = new Dictionary<string, object?>
        {
            ["name"] = d.Name.Length > 0 ? d.Name : "Earbuds",
            ["lv"] = "--", ["rv"] = "--", ["cv"] = "--",
            ["lc"] = "default", ["rc"] = "default", ["cc"] = "default",
            ["ll"] = "Left", ["rl"] = "Right", ["cl"] = "Case",
            ["sl"] = "L", ["sr"] = "R", ["sc"] = "Case",
            ["lb"] = "", ["rb"] = "", ["cb"] = "",
            ["li"] = false, ["ri"] = false, ["ci"] = false,
        };

        if (!alive)
        {
            data["status"] = "Offline · open the case lid to refresh";
            return data;
        }

        if (fresh)
        {
            data["status"] = d.LeftInCase && d.RightInCase ? "Charging" : "Connected";
            data["lv"] = Pct(d.Left); data["rv"] = Pct(d.Right); data["cv"] = Pct(d.Case);
            data["lc"] = ColorOf(d.Left, true); data["rc"] = ColorOf(d.Right, true); data["cc"] = ColorOf(d.Case, true);
            data["li"] = d.LeftInCase; data["ri"] = d.RightInCase;
        }
        else
        {
            if (!d.IsAdapted && d.SystemBattery is { } sys)
                data["status"] = $"Whole-device {sys}% · not adapted";
            else if (d.SystemBattery is { } sys2)
                data["status"] = $"In use · whole-device {sys2}%";
            else
                data["status"] = "Connected · waiting for data";
        }
        return data;
    }

    private static string Pct(int? v) => v is { } x ? $"{x}%" : "--";
}
