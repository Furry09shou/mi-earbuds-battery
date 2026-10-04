using System.Runtime.InteropServices;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 查询系统蓝牙 ACL 连接状态（经典蓝牙），不依赖耳机自身广播。
/// 耳机合盖/关机后 Windows 会在数秒内断开 ACL，fConnected 立即翻为 false，
/// 比等广播超时（合盖即停广播）更快、更可靠地判断断开。
/// </summary>
public static class BtConnectionProbe
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLUETOOTH_FIND_RADIO_PARAMS
    {
        public uint dwSize;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLUETOOTH_DEVICE_SEARCH_PARAMS
    {
        public uint dwSize;
        public int fReturnAuthenticated;
        public int fReturnRemembered;
        public int fReturnUnknown;
        public int fReturnConnected;
        public int fIssueInquiry;
        public byte cTimeoutMultiplier;
        public IntPtr hRadio;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct SYSTEMTIME
    {
        public ushort wYear, wMonth, wDayOfWeek, wDay, wHour, wMinute, wSecond, wMilliseconds;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Pack = 1)]
    private struct BLUETOOTH_DEVICE_INFO
    {
        public uint dwSize;
        public ulong Address;
        public uint ulClassOfDevice;
        public int fConnected;
        public int fRemembered;
        public int fAuthenticated;
        public SYSTEMTIME stLastSeen;
        public SYSTEMTIME stLastUsed;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 248)]
        public string szName;
    }

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstRadio(ref BLUETOOTH_FIND_RADIO_PARAMS params_, out IntPtr hRadio);

    [DllImport("bluetoothapis.dll")]
    private static extern int BluetoothFindRadioClose(IntPtr hRadio);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern IntPtr BluetoothFindFirstDevice(ref BLUETOOTH_DEVICE_SEARCH_PARAMS params_, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bluetoothapis.dll", SetLastError = true)]
    private static extern int BluetoothFindNextDevice(IntPtr hFind, ref BLUETOOTH_DEVICE_INFO info);

    [DllImport("bluetoothapis.dll")]
    private static extern int BluetoothFindDeviceClose(IntPtr hFind);

    private static IntPtr _radio;
    private static readonly object Gate = new();

    private static IntPtr GetRadio()
    {
        if (_radio != IntPtr.Zero) return _radio;
        var p = new BLUETOOTH_FIND_RADIO_PARAMS { dwSize = (uint)Marshal.SizeOf<BLUETOOTH_FIND_RADIO_PARAMS>() };
        _radio = BluetoothFindFirstRadio(ref p, out var h) != IntPtr.Zero ? h : IntPtr.Zero;
        return _radio;
    }

    /// <summary>查询该 MAC（"AA:BB:CC:DD:EE:FF"）当前是否与系统保持蓝牙连接。</summary>
    public static bool IsConnected(string? macText)
    {
        var addr = ParseMac(macText);
        if (addr == 0) return false;
        lock (Gate)
        {
            var radio = GetRadio();
            if (radio == IntPtr.Zero) return false;

            var sp = new BLUETOOTH_DEVICE_SEARCH_PARAMS
            {
                dwSize = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_SEARCH_PARAMS>(),
                fReturnAuthenticated = 1,
                fReturnRemembered = 1,
                fReturnUnknown = 0,
                fReturnConnected = 1,
                fIssueInquiry = 0,
                cTimeoutMultiplier = 0,
                hRadio = radio,
            };
            var size = (uint)Marshal.SizeOf<BLUETOOTH_DEVICE_INFO>();
            var di = new BLUETOOTH_DEVICE_INFO { dwSize = size };
            var h = BluetoothFindFirstDevice(ref sp, ref di);
            if (h == IntPtr.Zero) return false;

            var connected = false;
            while (true)
            {
                if (di.Address == addr)
                {
                    connected = di.fConnected != 0;
                    break;
                }
                di = new BLUETOOTH_DEVICE_INFO { dwSize = size };
                if (BluetoothFindNextDevice(h, ref di) == 0) break;
            }
            BluetoothFindDeviceClose(h);
            return connected;
        }
    }

    /// <summary>"AA:BB:CC:DD:EE:FF" → BLUETOOTH_ADDRESS（低字节在前）。</summary>
    private static ulong ParseMac(string? macText)
    {
        if (string.IsNullOrEmpty(macText)) return 0;
        var parts = macText.Split(':');
        if (parts.Length != 6) return 0;
        ulong addr = 0;
        for (var i = 5; i >= 0; i--)
        {
            if (!byte.TryParse(parts[i], System.Globalization.NumberStyles.HexNumber, null, out var b))
                return 0;
            addr = (addr << 8) | b;
        }
        return addr;
    }
}
