using System.IO;
using System.Text.Json;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 佩戴时长统计：按设备按天累计「已连接分钟数」，落盘到
/// %APPDATA%\RonghuiEarbuds\history\usage\<mac>\<yyyy-MM-dd>.json（保留 7 天）。
/// 主窗口每 1 秒把「仍在线」的设备喂进来，攒够 60 秒才落盘一次（减少写放大）。
/// </summary>
public sealed class UsageStore
{
    private sealed class Counter
    {
        public int Seconds;
        public DateTime LastFlushed = DateTime.MinValue;
        public int MinutesFile;
        public DateTime DayFile = DateTime.MinValue;
    }

    private sealed record DayFile(int Minutes);

    private readonly Dictionary<string, Counter> _counters = new(StringComparer.OrdinalIgnoreCase);

    private static string RootDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "RonghuiEarbuds", "history", "usage");

    /// <summary>设备在线时每秒调用一次；满 60 秒自动落盘。</summary>
    public void AddSecond(string mac)
    {
        var c = Get(mac);
        c.Seconds++;
        var now = DateTime.Now;
        if (now - c.LastFlushed >= TimeSpan.FromSeconds(60) && c.Seconds >= 60)
            FlushCounter(mac, c, now);
    }

    /// <summary>退出前把不足 1 分钟的零头也落盘。</summary>
    public void FlushAll()
    {
        try
        {
            foreach (var (mac, c) in _counters)
            {
                if (c.Seconds >= 60) FlushCounter(mac, c, DateTime.Now);
            }
        }
        catch { /* 落盘失败不阻塞退出 */ }
    }

    private Counter Get(string mac)
    {
        if (!_counters.TryGetValue(mac, out var c))
            _counters[mac] = c = new Counter();
        return c;
    }

    private void FlushCounter(string mac, Counter c, DateTime now)
    {
        var minutes = c.Seconds / 60;
        c.Seconds %= 60;
        if (minutes <= 0) { c.LastFlushed = now; return; }

        try
        {
            if (c.DayFile != now.Date)
            {
                c.DayFile = now.Date;
                c.MinutesFile = ReadFileMinutes(mac, now.Date);
            }
            c.MinutesFile += minutes;
            var dir = Path.Combine(RootDir, Sanitize(mac));
            Directory.CreateDirectory(dir);
            File.WriteAllText(
                Path.Combine(dir, $"{now.Date:yyyy-MM-dd}.json"),
                JsonSerializer.Serialize(new DayFile(c.MinutesFile)));
        }
        catch { /* 磁盘异常保持内存计数 */ }
        c.LastFlushed = now;
    }

    /// <summary>某天的已连接分钟数（无记录 0）。</summary>
    public int GetMinutes(string mac, DateTime day)
    {
        var c = Get(mac);
        if (c.DayFile == day.Date && c.Seconds < 60) return c.MinutesFile + c.Seconds / 60;
        var fromFile = ReadFileMinutes(mac, day.Date);
        if (day.Date == DateTime.Now.Date && c.Seconds >= 60) fromFile += c.Seconds / 60;
        return fromFile;
    }

    /// <summary>最近 N 天（含今天）的分钟序列，旧→新。</summary>
    public IReadOnlyList<(DateTime Day, int Minutes)> GetRecentDays(string mac, int days)
    {
        var list = new List<(DateTime, int)>();
        var today = DateTime.Now.Date;
        for (var i = days - 1; i >= 0; i--)
        {
            var day = today.AddDays(-i);
            list.Add((day, GetMinutes(mac, day)));
        }
        return list;
    }

    private static int ReadFileMinutes(string mac, DateTime day)
    {
        try
        {
            var path = Path.Combine(RootDir, Sanitize(mac), $"{day:yyyy-MM-dd}.json");
            if (!File.Exists(path)) return 0;
            return JsonSerializer.Deserialize<DayFile>(File.ReadAllText(path))?.Minutes ?? 0;
        }
        catch { return 0; }
    }

    /// <summary>首次使用时清理 7 天前的文件。</summary>
    public static void Cleanup()
    {
        try
        {
            if (!Directory.Exists(RootDir)) return;
            var cutoff = DateTime.Now.AddDays(-7);
            foreach (var f in Directory.EnumerateFiles(RootDir, "*.json", SearchOption.AllDirectories))
            {
                var name = Path.GetFileNameWithoutExtension(f);
                if (DateTime.TryParseExact(name, "yyyy-MM-dd", null,
                        System.Globalization.DateTimeStyles.None, out var day) && day < cutoff)
                {
                    try { File.Delete(f); } catch { /* 占用下次再清 */ }
                }
            }
        }
        catch { /* 忽略 */ }
    }

    private static string Sanitize(string mac) =>
        string.Concat(mac.Where(char.IsLetterOrDigit));
}