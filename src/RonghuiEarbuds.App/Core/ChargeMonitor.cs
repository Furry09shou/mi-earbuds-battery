namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 充满提醒：最低通道 ≥95 且本充电周期内曾低于 95 → 提醒一次（带设备名）；
/// 掉回 90 以下重新武装。每台设备独立状态（App 全设备喂入），遵守勿扰时段。
/// </summary>
public sealed class ChargeMonitor
{
    private readonly AppConfig _config;
    private readonly Action<string, string> _alert;

    private readonly Dictionary<string, (bool FullSeen, DateTime LastAlert)> _state =
        new(StringComparer.OrdinalIgnoreCase);

    public ChargeMonitor(AppConfig config, Action<string, string> alert)
    {
        _config = config;
        _alert = alert;
    }

    public void OnUpdate(EarbudsUpdate u)
    {
        if (!_config.ChargeFullAlert) return;
        var s = u.Snapshot;

        // 取最小通道：整副齐 95+ 才算充满（避免单耳满、另一耳还在充时误报）
        int? min = Min(s.LeftPercent, s.RightPercent, s.CasePercent);
        if (min is null) return;

        var now = DateTime.Now;
        var st = _state.TryGetValue(u.Mac, out var prev)
            ? prev
            : (FullSeen: false, LastAlert: DateTime.MinValue);

        if (min.Value >= 95)
        {
            if (!st.FullSeen && !QuietHours.InEffect(_config, now) &&
                now - st.LastAlert > TimeSpan.FromHours(2))
            {
                string name = string.IsNullOrWhiteSpace(u.DisplayName)
                    ? L.T("parser.unknown") : u.DisplayName;
                _alert(L.F("battery.fullTitleFmt", name), L.F("battery.fullMsgFmt", name));
                _state[u.Mac] = (true, now);
            }
            else if (!st.FullSeen)
            {
                _state[u.Mac] = (true, st.LastAlert);   // 勿扰时段内也消耗"已满"标记，出时段不补报
            }
        }
        else if (min.Value <= 90)
        {
            _state[u.Mac] = (false, st.LastAlert);   // 明显放电 → 重新武装
        }
    }

    private static int? Min(params int?[] values) =>
        values.Where(v => v.HasValue).Select(v => v!.Value).Cast<int?>().DefaultIfEmpty(null).Min();
}