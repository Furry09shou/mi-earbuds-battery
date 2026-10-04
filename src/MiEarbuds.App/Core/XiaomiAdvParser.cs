using Windows.Devices.Bluetooth.Advertisement;
using Windows.Storage.Streams;

namespace MiEarbuds.App.Core;

/// <summary>
/// 小米 TWS 耳机 0x038F 厂商广播解析器（档案制）。
///
/// Air2 SE 档案（productKey = 251867，实测破解）：
///   广播 0x038F 数据段（len >= 18）：
///   [0]=0x16 [1]=0x01 [2]=0x01 [3]=状态标志(0x01=左耳在仓外, 0x80=放回充电瞬间闪现)
///   [4]=恒定值字段（实测恒 0x4E，含义未知，不是电量）
///   [5]=左耳电量(低7位)   | 0x80(在仓充电)
///   [6]=右耳电量(低7位)   | 0x80(在仓充电)
///   [7]=充电仓电量（实测：仓给耳机充电时 70→60 缓慢下降，与真机显示一致）
///   [8..10]=产品标识 25 18 67
///   [11..17]=设备ID/序号，[18..23]=设备ID+尾标签
///   广播仅在充电仓盖打开、或耳机（主机）工作时发送。
/// 其他型号：补充新的 productKey 档案即可扩展。
/// </summary>
public static class XiaomiAdvParser
{
    public const string Air2SeProductKey = "251867";

    // productKey -> 显示名。新型号适配时在此登记档案。
    private static readonly Dictionary<string, string> Profiles = new()
    {
        [Air2SeProductKey] = "Mi Air2 SE",
    };

    public static string GetDisplayName(string productKey) =>
        Profiles.GetValueOrDefault(productKey, "未知耳机");

    /// <summary>当前已适配的全部机型显示名（用于界面上的已适配列表）。</summary>
    public static IReadOnlyCollection<string> GetSupportedNames() => Profiles.Values.ToArray();

    public static BatterySnapshot? Parse(IList<BluetoothLEManufacturerData> sections)
    {
        BluetoothLEManufacturerData? m = null;
        foreach (var s in sections)
        {
            if (s.CompanyId == 0x038F) { m = s; break; }
        }
        if (m is null) return null;

        var reader = DataReader.FromBuffer(m.Data);
        var b = new byte[reader.UnconsumedBufferLength];
        reader.ReadBytes(b);

        if (b.Length < 18 || b[0] != 0x16 || b[1] != 0x01 || b[2] != 0x01)
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

    // 值为 0 视为无效（充电触点瞬态会出现 0）
    private static int? ToPercent(byte v)
    {
        int p = v & 0x7F;
        if (p == 0) return null;
        return Math.Min(p, 100);
    }
}
