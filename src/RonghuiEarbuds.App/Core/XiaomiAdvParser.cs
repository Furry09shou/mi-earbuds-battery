using System.Globalization;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 耳机厂商广播解析器（档案制）。
///
/// 小米 TWS 0x038F 厂商广播 —— Air2 SE 档案（productKey = 251867，实测破解）：
///   广播 0x038F 数据段（len >= 18）：
///   [0]=0x16 [1]=0x01 [2]=0x01 [3]=状态标志(0x01=左耳在仓外, 0x80=放回充电瞬间闪现)
///   [4]=恒定值字段（实测恒 0x4E，含义未知，不是电量）
///   [5]=左耳电量(低7位)   | 0x80(在仓充电)
///   [6]=右耳电量(低7位)   | 0x80(在仓充电)
///   [7]=充电仓电量（实测：仓给耳机充电时 70→60 缓慢下降，与真机显示一致）
///   [8..10]=产品标识 25 18 67
///   [11..17]=设备ID/序号，[18..23]=设备ID+尾标签
///   广播仅在充电仓盖打开、或耳机（主机）工作时发送。
///
/// Apple AirPods 0x004C 厂商广播 —— Proximity Pairing Message（对照 LibrePods 逆向，
/// Pro 2 USB-C / Pro 3 采集数据逐字节验证）：
///   27 字节：[0]=0x07 [1]=0x19(长度) [2]=配对模式(0x01=已配对，0x00=配对中结构不同)
///   [3..4]=型号 ID(大端，如 2420=Pro 2 USB-C / 2720=Pro 3)
///   [5]=状态位域(bit2=双耳在仓 bit4=单耳在仓 bit5=1 主耳为左 bit6=本耳在仓)
///   [6]=双耳电量双 nibble（主耳电量固定在低 4 位，副耳在高 4 位）
///   [7]=仓电量(低 4 位)+充电标志(高 4 位：bit0/1=主/副耳充电随主耳翻转，bit2=仓充电)
///   [8]=盖指示(bit0-2=开合计数 bit3=盖态) [9]=颜色 [10]=连接状态(04=待机 05=听歌)
///   [11..26]=AES 加密段（不解）
///   电量 nibble：0-9=十位百分数，A-E=满电，F=无数据。
///   广播在连接音频播放期间持续发送（与小米不同）；左右耳各自广播，内容一致（bit6 除外）。
///   注意：仓电量在低 4 位、充电标志在高 4 位——部分社区文档表写反了，以物理验证为准。
/// </summary>
public static class XiaomiAdvParser
{
    public const string Air2SeProductKey = "251867";

    // productKey -> 显示名。新型号适配时在此登记档案。
    private static readonly Dictionary<string, string> Profiles = new()
    {
        [Air2SeProductKey] = "Mi Air2 SE",
    };

    // Apple 型号 ID -> 显示名（LibrePods 模型表 + Pro 3 采集实测）。
    // Apple 广播地址是随机轮换的（RPA），设备标识用型号合成稳定 ID："AP"+型号十六进制（如 AP2720）。
    private static readonly Dictionary<ushort, string> AppleModels = new()
    {
        [0x0220] = "AirPods 1",
        [0x0F20] = "AirPods 2",
        [0x1320] = "AirPods 3",
        [0x1920] = "AirPods 4",
        [0x1B20] = "AirPods 4 (ANC)",
        [0x0E20] = "AirPods Pro",
        [0x1420] = "AirPods Pro 2",
        [0x2420] = "AirPods Pro 2",
        [0x2720] = "AirPods Pro 3",
        [0x0A20] = "AirPods Max",
        [0x1F20] = "AirPods Max (USB-C)",
    };

    /// <summary>是否为 Apple 合成设备 ID（AP+型号十六进制，共 6 字符）。</summary>
    public static bool IsAppleKey(string productKey) =>
        productKey.StartsWith("AP", StringComparison.Ordinal) && productKey.Length == 6;

    /// <summary>设备名是否可视为真苹果设备（如 "AirPods Pro 3"、"张三的AirPods"——
    /// iOS 改名默认格式）。克隆苹果帧的杂牌自报名是纯型号名但系统连接名
    /// （如 "SOAIY GD31"）不含 AirPods 字样，不会被误放行。</summary>
    public static bool IsAppleProfileName(string name) =>
        name.Contains("AirPods", StringComparison.OrdinalIgnoreCase);

    public static string GetDisplayName(string productKey)
    {
        if (IsAppleKey(productKey) &&
            ushort.TryParse(productKey.AsSpan(2), NumberStyles.HexNumber, null, out var model))
            return AppleModels.GetValueOrDefault(model, L.T("parser.unknown"));
        return Profiles.GetValueOrDefault(productKey, L.T("parser.unknown"));
    }

    /// <summary>当前已适配的全部机型显示名（用于界面上的已适配列表）。</summary>
    public static IReadOnlyCollection<string> GetSupportedNames() =>
        Profiles.Values.Concat(AppleModels.Values).Distinct().ToArray();

    public static BatterySnapshot? Parse(IList<BluetoothLEManufacturerData> sections)
    {
        foreach (var s in sections)
        {
            if (s.CompanyId == 0x038F)
            {
                var snap = ParseXiaomi(s);
                if (snap is not null) return snap;
            }
            else if (s.CompanyId == 0x004C)
            {
                var snap = ParseApple(s);
                if (snap is not null) return snap;
            }
        }
        return null;
    }

    private static BatterySnapshot? ParseXiaomi(BluetoothLEManufacturerData m)
    {
        var b = ReadBytes(m);
        if (b is null || b.Length < 18 || b[0] != 0x16 || b[1] != 0x01 || b[2] != 0x01)
            return null;

        var key = $"{b[8]:X2}{b[9]:X2}{b[10]:X2}";
        if (!Profiles.ContainsKey(key))
            return null; // 未知型号：留待扩展档案

        if (key == Air2SeProductKey)
        {
            return new BatterySnapshot(
                LeftPercent: ToPercent(b[5]),
                RightPercent: ToPercent(b[6]),
                CasePercent: ToPercent(b[7]),
                LeftInCase: (b[5] & 0x80) != 0,
                RightInCase: (b[6] & 0x80) != 0,
                ProductKey: key);
        }

        return null;
    }

    private static BatterySnapshot? ParseApple(BluetoothLEManufacturerData m)
    {
        var b = ReadBytes(m);
        if (b is null || b.Length < 11 || b[0] != 0x07) return null;
        if (b[2] == 0x00) return null;   // 配对模式帧字段结构不同，跳过

        var model = (ushort)((b[3] << 8) | b[4]);
        if (!AppleModels.ContainsKey(model)) return null;   // 未知型号：留待扩展档案

        byte status = b[5], pods = b[6], flagsCase = b[7];

        // bit5=1 主耳为左；主耳电量固定占低 4 位，左右耳显示位置需按主耳翻转
        bool flipped = (status & 0x20) == 0;
        var leftNib = flipped ? (pods >> 4) & 0x0F : pods & 0x0F;
        var rightNib = flipped ? pods & 0x0F : (pods >> 4) & 0x0F;

        // 充电标志：bit0/bit1=主耳/副耳充电（随主耳翻转），bit2=仓充电
        var flags = (flagsCase >> 4) & 0x0F;
        var leftCharging = flipped ? (flags & 0x02) != 0 : (flags & 0x01) != 0;
        var rightCharging = flipped ? (flags & 0x01) != 0 : (flags & 0x02) != 0;

        // Apple 只在仓内充电，充电标志即「在仓」，与小米 0x80 位语义对齐
        return new BatterySnapshot(
            LeftPercent: NibbleToPercent(leftNib),
            RightPercent: NibbleToPercent(rightNib),
            CasePercent: NibbleToPercent(flagsCase & 0x0F),
            LeftInCase: leftCharging,
            RightInCase: rightCharging,
            ProductKey: $"AP{model:X4}");
    }

    private static byte[]? ReadBytes(BluetoothLEManufacturerData m)
    {
        var reader = DataReader.FromBuffer(m.Data);
        var b = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(b);
        return b;
    }

    // 值为 0 视为无效（充电触点瞬态会出现 0）
    private static int? ToPercent(byte v)
    {
        int p = v & 0x7F;
        if (p == 0) return null;
        return Math.Min(p, 100);
    }

    // Apple 电量 nibble：0-9=十位百分数，A-E=满电，F=无数据
    private static int? NibbleToPercent(int n)
    {
        if (n >= 0x0F) return null;
        if (n >= 0x0A) return 100;
        return n * 10;
    }
}
