using System.IO;
using System.Text.Json;

namespace RonghuiEarbuds.App.Core;

/// <summary>一条电量历史记录（电量变化时落盘，用于当日统计与曲线）。</summary>
public sealed record HistorySample(
    DateTime Time, int? Left, int? Right, int? Case, int? System, bool AnyInCase);

/// <summary>当日统计结果。</summary>
public sealed record BatteryDayStats(
    double UsedPercent,          // 今日累计耗电（各下降段之和）
    string EstimateText,         // 「预计可用 …」/「预计 … 充满」/「充电中」/「--」
    IReadOnlyList<(double X, double Y)> Curve,  // 归一化曲线点（0-100 空间，X=时间，Y=电量）
    int? MinutesToFull = null);  // 充电中：预计充满还需的分钟数（无有效斜率时为 null）

/// <summary>
/// 电量历史存储：按设备按天写 jsonl（%APPDATA%\RonghuiEarbuds\history\），
/// 仅记录电量变化（或超过 10 分钟无变化时的心跳点），保留 7 天。
/// 统计与曲线全部基于内存中的当日序列，不重复读文件。
/// </summary>
public sealed class BatteryHistoryStore
{
    private sealed class MacStream
    {
        public DateTime Day = DateTime.MinValue;
        public List<HistorySample> Samples = new();
        public DateTime LastWrite = DateTime.MinValue;
        public (int? L, int? R, int? C, int? S) Last;
    }

    private readonly Dictionary<string, MacStream> _streams = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly AppConfig? _config;

    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RonghuiEarbuds", "history");

    public BatteryHistoryStore(AppConfig? config = null)
    {
        _config = config;
        try { CleanupOldFiles(); }
        catch { /* 清理失败不影响主流程 */ }
    }

    /// <summary>广播快照落库（只在数值变化或超时心跳时写盘）。</summary>
    public void Record(string mac, BatterySnapshot s)
    {
        if (_config is { HistoryEnabled: false }) return;   // 设置里关闭：不记录、不建目录
        lock (_gate)
        {
            var st = StreamFor(mac, DateTime.Now);
            Append(st, mac, new HistorySample(
                DateTime.Now, s.LeftPercent, s.RightPercent, s.CasePercent, null,
                s.LeftInCase || s.RightInCase), s.LeftPercent, s.RightPercent, s.CasePercent);
        }
    }

    /// <summary>系统整机电量兜底值落库（广播停止期间保持历史连续）。</summary>
    public void RecordSystem(string mac, int? value)
    {
        if (value is null) return;
        if (_config is { HistoryEnabled: false }) return;
        lock (_gate)
        {
            var st = StreamFor(mac, DateTime.Now);
            Append(st, mac, new HistorySample(DateTime.Now, null, null, null, value, false),
                null, null, null, value);
        }
    }

    private MacStream StreamFor(string mac, DateTime now)
    {
        if (!_streams.TryGetValue(mac, out var st))
            _streams[mac] = st = new MacStream();
        if (st.Day != now.Date)
        {
            st.Day = now.Date;
            st.Samples = new List<HistorySample>();
            st.LastWrite = DateTime.MinValue;
            st.Last = (null, null, null, null);
            LoadToday(mac, st, now.Date);
        }
        return st;
    }

    private void Append(MacStream st, string mac, HistorySample sample,
        int? l, int? r, int? c, int? sys = null)
    {
        bool changed = l != st.Last.L || r != st.Last.R || c != st.Last.C || sys != st.Last.S;
        bool heartbeat = st.Samples.Count > 0 && (sample.Time - st.LastWrite).TotalMinutes >= 10;
        if (!changed && !heartbeat) return;
        if (st.Samples.Count > 0 && sample.Time - st.LastWrite < TimeSpan.FromSeconds(2)) return; // 防抖

        st.Samples.Add(sample);
        st.LastWrite = sample.Time;
        if (sys is not null && l is null && r is null)
            st.Last = (st.Last.L, st.Last.R, st.Last.C, sys);
        else
            st.Last = (l, r, c, st.Last.S);

        try
        {
            var dir = Path.Combine(RootDir, Sanitize(mac));
            Directory.CreateDirectory(dir);
            var line = JsonSerializer.Serialize(sample, JsonOpts);
            File.AppendAllText(Path.Combine(dir, $"{st.Day:yyyy-MM-dd}.jsonl"), line + "\n");
        }
        catch { /* 磁盘异常时只保留内存序列 */ }
    }

    private void LoadToday(string mac, MacStream st, DateTime day)
    {
        try
        {
            var path = Path.Combine(RootDir, Sanitize(mac), $"{day:yyyy-MM-dd}.jsonl");
            if (!File.Exists(path)) return;
            foreach (var line in File.ReadLines(path))
            {
                if (JsonSerializer.Deserialize<HistorySample>(line) is { } s)
                    st.Samples.Add(s);
            }
            if (st.Samples.Count > 0)
            {
                var last = st.Samples[^1];
                st.LastWrite = last.Time;
                st.Last = (last.Left, last.Right, last.Case, last.System);
            }
        }
        catch { /* 历史文件损坏则从空开始 */ }
    }

    /// <summary>当日统计：累计耗电（下降段求和）、预计可用时长、电量曲线。</summary>
    public BatteryDayStats GetTodayStats(string mac)
    {
        lock (_gate)
        {
            var st = StreamFor(mac, DateTime.Now);
            var samples = st.Samples;

            // 有效电量序列：广播分耳值左耳优先、缺了用右耳（双耳物理上同步耗电，
            // 单耳序列即可代表整副）；系统整机电量只画曲线，绝不参与累计/斜率——
            // 左右/系统三源混算时，来源切换的差值会被当成"耗电"反复累计（曾出现已用 140%）
            (int? V, char Src) Eff(HistorySample s)
            {
                if (s.Left is { } l) return (l, 'L');
                if (s.Right is { } r) return (r, 'R');
                return (s.System, 'S');
            }

            // 累计耗电：相邻样本的下降量求和（充电回升不计负数）；
            // 来源切换（左↔右）时重置基准不累计，避免通道差被计入
            double used = 0;
            (int V, char Src)? prev = null;
            var pts = new List<(double X, double Y)>();

            foreach (var s in samples)
            {
                var (v, src) = Eff(s);
                if (v is null) continue;
                if (prev is { } p && p.Src == src && v.Value < p.V)
                    used += p.V - v.Value;
                prev = (v.Value, src);
            }

            // 曲线点：归一化到 0-100 空间（X=当日进度，Y=电量），过滤过密点
            foreach (var s in samples)
            {
                var (v, _) = Eff(s);
                if (v is null) continue;
                double x = Math.Clamp((s.Time - st.Day.Date).TotalSeconds / 864.0, 0, 100);
                if (pts.Count == 0 || x - pts[^1].X >= 0.35 || v.Value != (int)pts[^1].Y)
                    pts.Add((x, Math.Clamp(v.Value, 0, 100)));
            }

            // 放电速率：最近 45 分钟内的分耳下降斜率 → 预计可用时长
            //（系统值 10 分钟心跳太稀疏，斜率不可信，排除）
            string estimate = "--";
            var now = DateTime.Now;
            var recent = samples
                .Where(s => (now - s.Time).TotalMinutes <= 45)
                .Select(s => (s.Time, Eff(s)))
                .Where(t => t.Item2.Src != 'S' && t.Item2.V is { })
                .Select(t => (t.Time, V: (int)t.Item2.V!))
                .ToList();
            bool charging = samples.Count > 0 && samples[^1].AnyInCase;
            int? minutesToFull = null;

            if (charging)
            {
                // 充电斜率：最近 45 分钟内的分耳上升斜率 → 预计充满时间
                if (recent.Count >= 3)
                {
                    var first = recent[0];
                    var lastV = recent[^1].V;
                    var gain = lastV - first.V;
                    var hours = (recent[^1].Time - first.Time).TotalHours;
                    if (gain >= 1 && hours > 0.02)
                    {
                        var rate = gain / hours;   // %/h
                        if (rate >= 3)
                        {
                            var mins = (100 - lastV) / rate * 60;
                            if (mins >= 5 && mins <= 720)
                                minutesToFull = (int)Math.Round(mins);
                        }
                    }
                }
                estimate = minutesToFull is { } m
                    ? L.F("battery.etaFullFmt", m)
                    : L.T("battery.charging");
            }
            else if (recent.Count >= 2)
            {
                var first = recent[0];
                var lastV = recent[^1].V;
                var delta = first.V - lastV;
                var hours = (recent[^1].Time - first.Time).TotalHours;
                if (delta >= 2 && hours > 0.05)
                {
                    var remainHours = lastV / (delta / hours);
                    estimate = remainHours switch
                    {
                        >= 1.5 => L.F("battery.hoursMinFmt",
                            (int)Math.Floor(remainHours),
                            (int)Math.Round((remainHours - Math.Floor(remainHours)) * 60)),
                        >= 0.2 => L.F("battery.minutesFmt", Math.Max(1, (int)Math.Round(remainHours * 60))),
                        _ => L.T("battery.lessThan10"),
                    };
                }
            }

            return new BatteryDayStats(Math.Min(used, 300), estimate, pts, minutesToFull);
        }
    }

    /// <summary>某设备某天是否有电量记录（周报「使用天数」统计用，只查文件存在性）。</summary>
    public static bool DayHasData(string mac, DateTime day)
    {
        try
        {
            var path = Path.Combine(RootDir, Sanitize(mac), $"{day:yyyy-MM-dd}.jsonl");
            return File.Exists(path) && new FileInfo(path).Length > 4;
        }
        catch { return false; }
    }

    private static string Sanitize(string mac) =>
        string.Concat(mac.Where(char.IsLetterOrDigit));

    private static void CleanupOldFiles()
    {
        var root = RootDir;
        if (!Directory.Exists(root)) return;
        var cutoff = DateTime.Now.AddDays(-7);
        foreach (var f in Directory.EnumerateFiles(root, "*.jsonl", SearchOption.AllDirectories))
        {
            var name = Path.GetFileNameWithoutExtension(f);
            if (DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                    System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
            {
                try { File.Delete(f); } catch { /* 占用则下次再清 */ }
            }
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new();
}
