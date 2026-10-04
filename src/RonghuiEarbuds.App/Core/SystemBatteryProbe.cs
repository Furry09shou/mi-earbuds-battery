using System.Runtime.InteropServices;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 读取 Windows 为已配对蓝牙设备记录的「系统电量」——蓝牙设置页里显示的
/// 百分比，由耳机经 HFP/AVRCP 上报，系统自动维护。
/// 存储位置：BTHENUM 设备节点属性 {104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2
/// （DEVPROP_TYPE_BYTE，单字节百分比）。实测挂在 "Hands-Free AG"（HFP 服务）
/// 子节点上而非 DEV_ 根节点，且部分节点返回 CR_NO_SUCH_VALUE，因此需按 MAC
/// 匹配全部 BTHENUM 实例、取第一个命中的值。
/// 耳机广播暂停（合盖/空闲）但仍在连接时，此值可作为电量兜底显示。
/// </summary>
public static class SystemBatteryProbe
{
    // DEVPKEY {104EA319-6EE2-4701-BD47-8DDBF425BBE5}, pid 2 = 蓝牙电量百分比
    private static readonly PropertyKey BatteryKey = new(
        0x104EA319, 0x6EE2, 0x4701, 0xBD, 0x47, 0x8D, 0xDB, 0xF4, 0x25, 0xBB, 0xE5, 2);

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;

        public PropertyKey(uint a, ushort b, ushort c, byte d, byte e, byte f, byte g,
            byte h, byte i, byte j, byte k, uint pid)
        {
            FormatId = new Guid(a, b, c, d, e, f, g, h, i, j, k);
            PropertyId = pid;
        }
    }

    private const uint CM_GETIDLIST_FILTER_PRESENT = 0x100;   // 枚举过滤器按设备名过滤无效，需全量后自行筛选
    private const uint CM_LOCATE_DEVNODE_NORMAL = 0x0;
    private const int CR_SUCCESS = 0;
    private const uint DEVPROP_TYPE_BYTE = 0x03;

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_List_SizeW(out uint len, string? filter, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Get_Device_ID_ListW(string? filter, IntPtr buffer, uint len, uint flags);

    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)]
    private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

    [DllImport("cfgmgr32.dll")]
    private static extern int CM_Get_DevNode_PropertyW(
        uint devInst, ref PropertyKey key, out uint propertyType,
        IntPtr buffer, ref uint size, uint flags);

    private static readonly object Gate = new();

    /// <summary>
    /// 查询指定耳机（按 MAC 匹配）的系统电量百分比；系统不知道时返回 null。
    /// MAC 文本允许 "AA:BB:.." / "AABB.." 任意分隔格式。
    /// </summary>
    public static int? GetLevel(string? macText)
    {
        var hex = new string((macText ?? "").Where(char.IsAsciiHexDigit).ToArray());
        if (hex.Length != 12) return null;

        lock (Gate)
        {
            try
            {
                // BTHENUM 实例 ID 内嵌 12 位十六进制 MAC，两种字节序各生成一个候选
                var forward = string.Join("", Enumerable.Range(0, 6).Select(i => hex.Substring(i * 2, 2)));
                var reverse = string.Join("", Enumerable.Range(0, 6).Select(i => hex.Substring(10 - i * 2, 2)));

                if (CM_Get_Device_ID_List_SizeW(out var len, null, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                    return null;

                var mem = Marshal.AllocHGlobal((int)(len + 2) * sizeof(char));
                try
                {
                    if (CM_Get_Device_ID_ListW(null, mem, len, CM_GETIDLIST_FILTER_PRESENT) != CR_SUCCESS)
                        return null;

                    foreach (var id in ReadMultiSz(mem))
                    {
                        if (!id.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase))
                            continue;
                        if (!id.Contains(forward, StringComparison.OrdinalIgnoreCase) &&
                            !id.Contains(reverse, StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (CM_Locate_DevNodeW(out var devInst, id, CM_LOCATE_DEVNODE_NORMAL) != CR_SUCCESS)
                            continue;

                        var type = 0u;
                        var size = 8u;   // 单字节属性，给足缓冲避免 CR_BUFFER_SMALL 往返
                        var vmem = Marshal.AllocHGlobal(8);
                        try
                        {
                            var key = BatteryKey;
                            if (CM_Get_DevNode_PropertyW(devInst, ref key, out type, vmem, ref size, 0) == CR_SUCCESS &&
                                type == DEVPROP_TYPE_BYTE && size >= 1)
                            {
                                return Marshal.ReadByte(vmem);
                            }
                        }
                        finally
                        {
                            Marshal.FreeHGlobal(vmem);
                        }
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(mem);
                }
            }
            catch { /* 任何枚举异常按"系统不知道"处理 */ }
            return null;
        }
    }

    /// <summary>读取 REG_MULTI_SZ 风格的宽字符列表（双 \0 结尾）。</summary>
    private static List<string> ReadMultiSz(IntPtr ptr)
    {
        var list = new List<string>();
        var cur = ptr;
        while (true)
        {
            var s = Marshal.PtrToStringUni(cur);
            if (string.IsNullOrEmpty(s)) break;
            list.Add(s);
            cur += (s.Length + 1) * sizeof(char);
        }
        return list;
    }
}
