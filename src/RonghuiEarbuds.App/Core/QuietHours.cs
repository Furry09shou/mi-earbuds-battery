namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 提醒勿扰时段共用判定（低电量/骤降/充满提醒都遵守同一个设置时段）。
/// </summary>
public static class QuietHours
{
    /// <summary>勿扰时段：跨零点窗口（如 22:00-08:00）内返回 true。</summary>
    public static bool InEffect(AppConfig config, DateTime? at = null)
    {
        if (!config.QuietHoursEnabled) return false;
        var t = (at ?? DateTime.Now).TimeOfDay;
        var start = new TimeSpan(Math.Clamp(config.QuietStartHour, 0, 23), 0, 0);
        var end = new TimeSpan(Math.Clamp(config.QuietEndHour, 0, 23), 0, 0);
        if (start == end) return false;
        return start < end ? t >= start && t < end : t >= start || t < end;
    }
}