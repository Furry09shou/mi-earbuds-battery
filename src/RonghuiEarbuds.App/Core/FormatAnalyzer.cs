using System.IO;
using System.Text;
using System.Text.Json;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 本地启发式格式分析器：对采集 JSONL 做系统性差分分析，生成候选电量布局报告。
/// 原理（不依赖 AI，纯统计学）：
///   1. 恒定字节 → 帧前缀/产品标识；
///   2. 值域 ⊆ [0,100] 且放电时单调下降 → 电量字节候选；
///   3. 阶段差分：向导各阶段（双耳入仓/取左耳/放回/取右耳）之间值域分离的
///      字节 → 左耳/右耳候选；bit7 随阶段跳变 → 在仓/充电标志候选。
/// 复杂格式（加密、校验、位打包）仍需人工/AI 复核，但常见布局可自动定位。
/// </summary>
public static class FormatAnalyzer
{
    private sealed record Packet(DateTime T, string Mac, ushort Cid, byte[] Data, string Stage);

    public static string AnalyzeFile(string jsonlPath)
    {
        var packets = new List<Packet>();
        var stage = "";
        var layout = "";
        foreach (var line in File.ReadLines(jsonlPath))
        {
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                if (root.TryGetProperty("layout", out var layoutEl))
                {
                    layout = layoutEl.GetString() ?? "";
                    continue;
                }
                if (root.TryGetProperty("marker", out var marker))
                {
                    stage = marker.GetString() ?? "";
                    continue;
                }
                if (!root.TryGetProperty("hex", out var hexEl)) continue; // meta 行
                packets.Add(new Packet(
                    root.GetProperty("t").GetDateTime(),
                    root.GetProperty("mac").GetString() ?? "?",
                    (ushort)root.GetProperty("cid").GetInt32(),
                    Convert.FromHexString(hexEl.GetString()!),
                    stage));
            }
            catch { /* 跳过坏行 */ }
        }

        if (packets.Count == 0)
            return "本地分析：未捕获到任何广播包，报告不可用。";

        var sb = new StringBuilder();
        sb.AppendLine($"本地差分分析：共 {packets.Count} 包。");
        if (layout.Length > 0)
        {
            // 形态决定电量字段数量，是判断候选字节的重要先验
            var expected = layout switch
            {
                "dual_case" => "3 个电量值（左耳/右耳/充电仓）",
                "dual_nocase" => "2 个电量值（左耳/右耳，无仓或仓不广播）",
                "mono" => "1 个电量值（单耳，无左右差分）",
                _ => null,
            };
            sb.AppendLine($"耳机形态：{CaptureService.LayoutLabel(layout)}" +
                          (expected is null ? "" : $"，预期包含 {expected}。"));
        }

        // 主设备 = 包数最多的 MAC；主帧 = 该 MAC 下最常见的 (cid, 长度)
        var byMac = packets.GroupBy(p => p.Mac).OrderByDescending(g => g.Count()).ToList();
        sb.AppendLine($"捕获设备数：{byMac.Count}（" +
                      string.Join(", ", byMac.Take(4).Select(g => $"{g.Key}×{g.Count()}")) +
                      (byMac.Count > 4 ? " …" : "") + "）");

        var main = byMac[0];
        var frame = main.GroupBy(p => (p.Cid, p.Data.Length))
                        .OrderByDescending(g => g.Count()).First();
        var (cid, len) = frame.Key;
        var frames = frame.OrderBy(p => p.T).ToList();
        sb.AppendLine($"主设备 {main.Key}，公司代号 0x{cid:X4}，帧长 {len}，取 {frames.Count} 包分析。");
        sb.AppendLine();

        // 字节位统计
        var distinct = new HashSet<byte>[len];
        var stageValues = new Dictionary<string, HashSet<byte>>[len];
        for (var i = 0; i < len; i++)
        {
            distinct[i] = [];
            stageValues[i] = [];
        }
        foreach (var p in frames)
        {
            for (var i = 0; i < len; i++)
            {
                distinct[i].Add(p.Data[i]);
                if (!stageValues[i].TryGetValue(p.Stage, out var set))
                    stageValues[i][p.Stage] = set = [];
                set.Add(p.Data[i]);
            }
        }

        // 阶段名缩短显示
        static string Short(string s) => s.Length == 0 ? "未标记" :
            s.StartsWith("完成阶段") ? s[4..7].TrimEnd(':') : s.Length > 6 ? s[..6] : s;

        sb.AppendLine("逐字节判定：");
        for (var i = 0; i < len; i++)
        {
            var values = frames.Select(p => p.Data[i]).ToList();
            var seq = values.Where(v => v != 0).ToList(); // 0x00 视为过渡噪声

            if (distinct[i].Count == 1)
            {
                sb.AppendLine($"  b{i,-2} = 0x{values[0]:X2}   恒定（前缀/标识/填充）");
                continue;
            }

            var role = new List<string>();
            // 电量特征：常见编码 = 低 7 位电量 + bit7 在仓/充电标志。
            // distinct 含 0（在仓标志/电量同值的不同编码），但要求存在 ≥20 的有效值，
            // 以排除 0x00/0x01 之类的状态枚举字节。
            var magAll = values.Select(v => v & 0x7F).ToList();
            var magNz = magAll.Where(v => v != 0).ToList();
            var batteryLike = magAll.Max() <= 100 && magAll.Distinct().Count() >= 2 &&
                              magNz.Count > 0 && magNz.Max() >= 20;
            var hasFlag = values.Any(v => v >= 0x80) && values.Any(v => v < 0x80);
            // 放电单调趋势：相邻有效值（剥位后），下降步占比
            var down = 0;
            var up = 0;
            for (var k = 1; k < magNz.Count; k++)
            {
                if (magNz[k] < magNz[k - 1]) down++;
                else if (magNz[k] > magNz[k - 1]) up++;
            }
            var monotonic = down >= 3 && down >= up * 4;

            // 阶段差分：与"其他阶段"值域是否分离
            var stages = stageValues[i].Where(kv => kv.Key.Length > 0).ToList();
            string? sepStage = null;
            foreach (var (st, set) in stages)
            {
                var others = stages.Where(kv => kv.Key != st).SelectMany(kv => kv.Value).ToList();
                if (others.Count == 0 || set.Count == 0) continue;
                if (set.Min() > others.Max() || set.Max() < others.Min())
                {
                    sepStage = Short(st);
                    break;
                }
            }

            if (batteryLike) role.Add(hasFlag ? "电量候选（低7位+bit7标志）" : "电量候选");
            if (monotonic) role.Add($"放电单调（降{down}/升{up}）");
            if (sepStage is not null) role.Add($"仅阶段[{sepStage}]值域分离");
            if (values.Any(v => v >= 0x80) && values.Any(v => v < 0x80))
                role.Add("含 bit7 跳变（疑似在仓/充电标志）");

            sb.AppendLine($"  b{i,-2} 0x{values.Min():X2}..0x{values.Max():X2}  " +
                          $"不同值 {distinct[i].Count} 个 → {(role.Count > 0 ? string.Join("；", role) : "无明确特征")}");
        }

        // 自动候选布局
        var scored = Enumerable.Range(0, len)
            .Select(i => (Idx: i, Score:
                (nz(i) ? 1 : 0) +
                (mono(i) ? 2 : 0) +
                (stagesep(i) ? 2 : 0)))
            .Where(x => x.Score > 0 && distinct[x.Idx].Count > 1)
            .OrderByDescending(x => x.Score).ToList();

        bool nz(int i)
        {
            var all = frames.Select(p => p.Data[i]).Select(b => b & 0x7F).ToList();
            var nzv = all.Where(v => v != 0).ToList();
            return all.Max() <= 100 && all.Distinct().Count() >= 2 && nzv.Count > 0 && nzv.Max() >= 20;
        }
        bool mono(int i)
        {
            var v = frames.Select(p => p.Data[i]).Where(b => b != 0).Select(b => b & 0x7F).ToList();
            int down = 0, up = 0;
            for (var k = 1; k < v.Count; k++)
            {
                if (v[k] < v[k - 1]) down++;
                else if (v[k] > v[k - 1]) up++;
            }
            return down >= 3 && down >= up * 4;
        }
        bool stagesep(int i)
        {
            var stages = stageValues[i].Where(kv => kv.Key.Length > 0).ToList();
            return stages.Any(s =>
            {
                var others = stages.Where(o => o.Key != s.Key).SelectMany(o => o.Value).ToList();
                return others.Count > 0 && (s.Value.Min() > others.Max() || s.Value.Max() < others.Min());
            });
        }

        sb.AppendLine();
        if (scored.Count >= 3)
        {
            sb.AppendLine("候选布局（置信度从高到低，需人工复核）：");
            foreach (var (idx, score) in scored.Take(6))
                sb.AppendLine($"  b{idx}  得分 {score}" +
                              (mono(idx) ? "  [放电单调]" : "") +
                              (stagesep(idx) ? "  [阶段差分]" : ""));
        }
        else
        {
            sb.AppendLine("未能自动定位候选电量字节——格式可能加密/校验/位打包，需人工分析原始数据。");
        }
        return sb.ToString();
    }
}
