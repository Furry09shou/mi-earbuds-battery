using System.Collections.Concurrent;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 低电量提醒增强：可配置阈值 + 勿扰时段 + 电量骤降检测，每台已适配设备独立状态。
/// 分耳广播在时按具体通道报电量（左耳/右耳/充电仓各多少）；
/// 广播停发（连接播放期间频）由 App 定时喂入系统整机电量兜底（OnSystemBattery）。
/// </summary>
public sealed class LowBatteryMonitor
{
    private readonly AppConfig _config;
    private readonly Action<string, string> _alert;

    /// <summary>单台设备的状态：迟滞标记 + 骤降冷却 + 近期样本队列 + 最近分耳广播时间。</summary>
    private sealed class DevState
    {
        public bool LowWarned;
        public DateTime LastDropAlert = DateTime.MinValue;
        public DateTime LastPerSeen = DateTime.MinValue;
        public readonly ConcurrentQueue<(DateTime Time, int Value)> Recent = new();
    }

    private readonly ConcurrentDictionary<string, DevState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _names = new(StringComparer.OrdinalIgnoreCase);

    public LowBatteryMonitor(AppConfig config, Action<string, string> alert)
    {
        _config = config;
        _alert = alert;
    }

    /// <summary>记住每台设备最好的名字（有更好的就覆盖），报提醒时用于区分。</summary>
    private string Resolve(string mac, string? candidate)
    {
        if (!string.IsNullOrWhiteSpace(candidate) && candidate != mac)
            _names[mac] = candidate;
        return _names.TryGetValue(mac, out var n) ? n : L.T("parser.unknown");
    }

    public void OnUpdate(EarbudsUpdate u)
    {
        int? min = Min(u.Snapshot.LeftPercent, u.Snapshot.RightPercent, u.Snapshot.CasePercent);
        if (min is null) return;
        var st = _devices.GetOrAdd(u.Mac, _ => new DevState());
        string name = Resolve(u.Mac, u.DisplayName);
        int v = min.Value;
        var now = DateTime.Now;
        st.LastPerSeen = now;

        // ---- 电量骤降（先于低电量判断，避免阈值内骤降被吞）----
        var recent = st.Recent;
        recent.Enqueue((now, v));
        while (recent.TryPeek(out var stale) && (now - stale.Time).TotalMinutes > 10)
            recent.TryDequeue(out _);
        if (_config.SuddenDropAlert && recent.Count >= 2 &&
            now - st.LastDropAlert > TimeSpan.FromMinutes(60) && !InQuietHours(now) &&
            recent.TryPeek(out var oldest))
        {
            var drop = oldest.Value - v;
            if (drop >= 20 && (now - oldest.Time).TotalMinutes <= 10)
            {
                st.LastDropAlert = now;
                _alert(L.F("battery.dropTitleFmt", name),
                    L.F("battery.dropMsgFmt", name,
                        (int)Math.Round((now - oldest.Time).TotalMinutes), drop, v));
                return;
            }
        }

        // ---- 低电量提醒：报具体哪些通道低 ----
        int threshold = Math.Clamp(_config.LowBatteryThreshold, 5, 50);
        if (v <= threshold && !st.LowWarned && !InQuietHours(now))
        {
            st.LowWarned = true;
            _alert(L.F("battery.lowTitleFmt", name, threshold),
                L.F("battery.lowMsgFmt", name, ChannelDetail(u.Snapshot, threshold)));
        }
        else if (v > threshold + 5)
        {
            st.LowWarned = false;
        }
    }

    /// <summary>系统整机电量兜底：App 定时对所有在线设备喂入。
    /// 10 秒内有新鲜分耳广播的设备不参与（由 OnUpdate 路径负责，避免重复）。</summary>
    public void OnSystemBattery(string mac, string name, int? sys)
    {
        if (sys is not { } v) return;
        var st = _devices.GetOrAdd(mac, _ => new DevState());
        var now = DateTime.Now;
        if ((now - st.LastPerSeen).TotalSeconds <= 10) return;

        int threshold = Math.Clamp(_config.LowBatteryThreshold, 5, 50);
        if (v <= threshold && !st.LowWarned && !InQuietHours(now))
        {
            st.LowWarned = true;
            _alert(L.F("battery.lowTitleFmt", Resolve(mac, name), threshold),
                L.F("battery.lowMsgFmt", Resolve(mac, name), L.F("battery.wholeFmt", v)));
        }
        else if (v > threshold + 5)
        {
            st.LowWarned = false;
        }
    }

    /// <summary>列出所有不高于阈值的通道：「左耳 12%、右耳 9%」。</summary>
    private static string ChannelDetail(BatterySnapshot s, int threshold)
    {
        var parts = new List<string>();
        if (s.LeftPercent is { } l && l <= threshold)
            parts.Add(L.F("battery.channelFmt", L.T("main.left"), l));
        if (s.RightPercent is { } r && r <= threshold)
            parts.Add(L.F("battery.channelFmt", L.T("main.right"), r));
        if (s.CasePercent is { } c && c <= threshold)
            parts.Add(L.F("battery.channelFmt", L.T("main.caseTitle"), c));
        return string.Join(L.T("battery.join"), parts);
    }

    /// <summary>勿扰时段：跨零点窗口（如 22:00-08:00）内不提醒。见 QuietHours。</summary>
    public bool InQuietHours(DateTime? at = null) => QuietHours.InEffect(_config, at);

    private static int? Min(params int?[] values) =>
        values.Where(v => v.HasValue).Select(v => v!.Value).Cast<int?>().DefaultIfEmpty(null).Min();
}