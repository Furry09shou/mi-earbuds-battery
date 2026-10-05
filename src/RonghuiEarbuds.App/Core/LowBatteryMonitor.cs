using System.Collections.Concurrent;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 低电量提醒增强：可配置阈值 + 勿扰时段 + 电量骤降检测。
/// 仅处理当前关注设备的广播（与弹窗同一过滤），提醒通过托盘气泡弹出。
/// </summary>
public sealed class LowBatteryMonitor
{
    private readonly AppConfig _config;
    private readonly Action<string, string> _alert;

    private bool _lowWarned;
    private DateTime _lastDropAlert = DateTime.MinValue;
    private readonly ConcurrentQueue<(DateTime Time, int Value)> _recent = new();

    public LowBatteryMonitor(AppConfig config, Action<string, string> alert)
    {
        _config = config;
        _alert = alert;
    }

    public void OnUpdate(EarbudsUpdate u)
    {
        int? min = Min(u.Snapshot.LeftPercent, u.Snapshot.RightPercent, u.Snapshot.CasePercent);
        if (min is null) return;
        int v = min.Value;
        var now = DateTime.Now;

        // ---- 电量骤降（先于低电量判断，避免阈值内骤降被吞）----
        _recent.Enqueue((now, v));
        while (_recent.TryPeek(out var stale) && (now - stale.Time).TotalMinutes > 10)
            _recent.TryDequeue(out _);
        if (_config.SuddenDropAlert && _recent.Count >= 2 &&
            now - _lastDropAlert > TimeSpan.FromMinutes(60) && !InQuietHours(now) &&
            _recent.TryPeek(out var oldest))
        {
            var drop = oldest.Value - v;
            if (drop >= 20 && (now - oldest.Time).TotalMinutes <= 10)
            {
                _lastDropAlert = now;
                _alert(L.T("battery.dropTitle"),
                    L.F("battery.dropMsgFmt",
                        (int)Math.Round((now - oldest.Time).TotalMinutes), drop, v));
                return;
            }
        }

        // ---- 低电量提醒（可配置阈值 + 迟滞 5% 复位）----
        int threshold = Math.Clamp(_config.LowBatteryThreshold, 5, 50);
        if (v <= threshold && !_lowWarned && !InQuietHours(now))
        {
            _lowWarned = true;
            _alert(L.F("battery.lowTitleFmt", threshold), L.T("battery.lowMsg"));
        }
        else if (v > threshold + 5)
        {
            _lowWarned = false;
        }
    }

    /// <summary>勿扰时段：跨零点窗口（如 22:00-08:00）内不提醒。</summary>
    public bool InQuietHours(DateTime? at = null)
    {
        if (!_config.QuietHoursEnabled) return false;
        var t = (at ?? DateTime.Now).TimeOfDay;
        var start = new TimeSpan(Math.Clamp(_config.QuietStartHour, 0, 23), 0, 0);
        var end = new TimeSpan(Math.Clamp(_config.QuietEndHour, 0, 23), 0, 0);
        if (start == end) return false;
        return start < end ? t >= start && t < end : t >= start || t < end;
    }

    private static int? Min(params int?[] values) =>
        values.Where(v => v.HasValue).Select(v => v!.Value).Cast<int?>().DefaultIfEmpty(null).Min();
}
