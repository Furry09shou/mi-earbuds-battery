// RonghuiEarbuds BLE 探针 —— 破解耳机电量/控制协议（小米 Air2 SE 起步，AirPods 降噪通道诊断）
// 用法见 help 命令
using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Security.Principal;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Bluetooth.Rfcomm;
using Windows.Devices.Enumeration;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

Console.OutputEncoding = Encoding.UTF8;

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";
// 双击启动（无参数）：执行完不能秒退窗口，否则看起来像闪退
var interactiveLaunch = args.Length == 0;

try
{
    switch (cmd)
    {
        case "scan": await ScanAsync(); break;
        case "dump": await DumpAsync(RequireMac(args)); break;
        case "read": await ReadAsync(args); break;
        case "write": await WriteAsync(args); break;
        case "listen": await ListenAsync(args); break;
        case "writelisten": await WriteListenAsync(args); break;
        case "battery": await BatteryAsync(RequireMac(args)); break;
        case "diag": await DiagAsync(args); break;
        default: PrintHelp(); break;
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[错误] {ex}");
    Console.ResetColor();
}

if (interactiveLaunch || cmd == "help")
{
    Console.WriteLine();
    Console.WriteLine("按回车键关闭窗口…");
    Console.ReadLine();
}

return;

// ---------------------------------------------------------------- scan

static async Task ScanAsync()
{
    Directory.CreateDirectory("probe_logs");
    var logPath = Path.Combine("probe_logs", $"adv_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
    var interesting = new HashSet<ulong>();
    var lockObj = new object();

    using var logWriter = new StreamWriter(logPath, append: false, Encoding.UTF8) { AutoFlush = true };

    var watcher = new BluetoothLEAdvertisementWatcher
    {
        ScanningMode = BluetoothLEScanningMode.Active,
    };

    var lastPayload = new Dictionary<ulong, string>();

    watcher.Received += (_, e) =>
    {
        var name = e.Advertisement.LocalName;
        var mfrs = e.Advertisement.ManufacturerData;
        var mfrList = mfrs.Select(m =>
        {
            var reader = DataReader.FromBuffer(m.Data);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            return new { Id = m.CompanyId, Data = ToHex(bytes) };
        }).ToList();

        var record = new
        {
            ts = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
            mac = FmtMac(e.BluetoothAddress),
            rssi = e.RawSignalStrengthInDBm,
            name,
            type = e.AdvertisementType.ToString(),
            manufacturers = mfrList,
            services = e.Advertisement.ServiceUuids.Select(u => FmtUuid(u)).ToList(),
        };
        logWriter.WriteLine(JsonSerializer.Serialize(record));

        // 控制台只在小米设备的数据内容【变化】时打印，避免刷屏堵塞
        if (!mfrList.Any(m => m.Id == 0x038F)) return;

        var payloadKey = string.Join("|", mfrList.Select(m => $"{m.Id:X4}:{m.Data}"));
        lock (lockObj)
        {
            if (lastPayload.TryGetValue(e.BluetoothAddress, out var prev) && prev == payloadKey) return;
            lastPayload[e.BluetoothAddress] = payloadKey;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] {name} {FmtMac(e.BluetoothAddress)}  RSSI {e.RawSignalStrengthInDBm} dBm");
            foreach (var m in mfrList)
                Console.WriteLine($"    MFR 0x{m.Id:X4}: {m.Data}");
            Console.ResetColor();

            if (interesting.Add(e.BluetoothAddress))
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($">>> 发现小米设备！可用它做进一步探测: dump {FmtMac(e.BluetoothAddress)}");
                Console.ResetColor();
            }
        }
    };

    watcher.Stopped += (_, _) => { };

    Console.WriteLine($"BLE 广播扫描中...（全部原始数据已记录到 {logPath}）");
    Console.WriteLine("提示：耳机放入充电仓并【开盖】，或从仓中取出，才会发送 BLE 广播。");
    Console.WriteLine("按 Ctrl+C 停止。\n");
    watcher.Start();

    var exit = new ManualResetEventSlim(false);
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; watcher.Stop(); exit.Set(); };
    await Task.Run(() => exit.Wait());
    Console.WriteLine("\n扫描已停止。");
}

// ---------------------------------------------------------------- dump

static async Task DumpAsync(ulong mac)
{
    var device = await ConnectAsync(mac);
    if (device == null) { Fail("无法连接设备，请确保耳机处于可连接状态（开盖或取出耳机）。"); return; }

    Console.WriteLine($"已连接: {device.Name} ({FmtMac(mac)})，连接状态: {device.ConnectionStatus}");

    var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
    if (session != null) session.MaintainConnection = true;

    var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
    if (svcResult.Status != GattCommunicationStatus.Success)
    {
        Fail($"枚举服务失败: {svcResult.Status} (协议错误码: {svcResult.ProtocolError})");
        return;
    }

    Console.WriteLine($"\n共 {svcResult.Services.Count} 个服务:\n");
    var dumpObj = new Dictionary<string, object>();

    foreach (var svc in svcResult.Services)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"■ 服务 {FmtUuid(svc.Uuid)}  (起始句柄 {svc.AttributeHandle})");
        Console.ResetColor();

        var svcEntry = new Dictionary<string, object>();
        var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        if (charsResult.Status != GattCommunicationStatus.Success)
        {
            Console.WriteLine($"    [特征枚举失败: {charsResult.Status}]");
            continue;
        }

        foreach (var ch in charsResult.Characteristics)
        {
            var props = ch.CharacteristicProperties.ToString();
            var line = $"    特征 {FmtUuid(ch.Uuid)}  (句柄 {ch.AttributeHandle})  [{props}]";
            if (ch.Uuid == GattCharacteristicUuids.BatteryLevel)
                line += "  ← 标准电量特征!";
            Console.WriteLine(line);

            var chEntry = new Dictionary<string, object> { ["props"] = props };

            if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Read))
            {
                var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                if (read.Status == GattCommunicationStatus.Success)
                {
                    var reader = DataReader.FromBuffer(read.Value);
                    var bytes = new byte[reader.UnconsumedBufferLength];
                    reader.ReadBytes(bytes);
                    Console.WriteLine($"      值: {ToHex(bytes)}   ASCII: {ToAscii(bytes)}");
                    chEntry["readValue"] = ToHex(bytes);
                }
                else
                {
                    Console.WriteLine($"      读取失败: {read.Status}");
                }
            }

            svcEntry[FmtUuid(ch.Uuid) + "@" + ch.AttributeHandle] = chEntry;
        }
        dumpObj[FmtUuid(svc.Uuid)] = svcEntry;
        Console.WriteLine();
    }

    Directory.CreateDirectory("probe_logs");
    var dumpPath = Path.Combine("probe_logs", $"gatt_{FmtMac(mac).Replace(":", "")}_{DateTime.Now:yyyyMMdd_HHmmss}.json");
    await File.WriteAllTextAsync(dumpPath, JsonSerializer.Serialize(dumpObj, new JsonSerializerOptions { WriteIndented = true }));
    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"GATT 结构已保存到 {dumpPath}");
    Console.ResetColor();
    session?.Dispose();
    device.Dispose();
}

// ---------------------------------------------------------------- battery

static async Task BatteryAsync(ulong mac)
{
    var device = await ConnectAsync(mac);
    if (device == null) { Fail("无法连接设备。"); return; }

    var result = await device.GetGattServicesForUuidAsync(GattServiceUuids.Battery, BluetoothCacheMode.Uncached);
    if (result.Status != GattCommunicationStatus.Success || result.Services.Count == 0)
    {
        Fail($"没有标准电量服务 (0x180F): {result.Status}");
        return;
    }

    var batterySvc = result.Services[0];
    var chars = await batterySvc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
    foreach (var ch in chars.Characteristics)
    {
        var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
        if (read.Status == GattCommunicationStatus.Success)
        {
            var reader = DataReader.FromBuffer(read.Value);
            var bytes = new byte[reader.UnconsumedBufferLength];
            reader.ReadBytes(bytes);
            Console.WriteLine($"电量特征 {FmtUuid(ch.Uuid)} = {ToHex(bytes)}  (若有多个字节，通常是 [左耳, 右耳, 仓] 或 [电量%, 状态])");
        }
        else
        {
            Console.WriteLine($"读取失败: {read.Status}");
        }

        if (ch.CharacteristicProperties.HasFlag(GattCharacteristicProperties.Notify))
        {
            ch.ValueChanged += (_, a) =>
            {
                var reader = DataReader.FromBuffer(a.CharacteristicValue);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                Console.WriteLine($"  [通知 {DateTime.Now:HH:mm:ss.fff}] {ToHex(bytes)}");
            };
            await ch.WriteClientCharacteristicConfigurationDescriptorAsync(
                GattClientCharacteristicConfigurationDescriptorValue.Notify);
            Console.WriteLine("  (已订阅电量通知，监听 15 秒...)");
            await Task.Delay(15000);
        }
    }
    device.Dispose();
}

// ---------------------------------------------------------------- read / write / listen

// read <MAC> <serviceUuid> <charUuid>
static async Task ReadAsync(string[] args)
{
    if (args.Length < 3) { Fail("用法: read <MAC> <serviceUuid> <charUuid>"); return; }
    var device = await ConnectAsync(RequireMac(args));
    if (device == null) { Fail("无法连接设备。"); return; }

    var svcUuid = ParseUuid(args[1]);
    var chUuid = ParseUuid(args[2]);

    var svc = (await device.GetGattServicesForUuidAsync(svcUuid, BluetoothCacheMode.Uncached)).Services.FirstOrDefault();
    if (svc == null) { Fail("找不到服务 " + args[1]); return; }
    var ch = (await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)).Characteristics.FirstOrDefault(c => c.Uuid == chUuid);
    if (ch == null) { Fail("找不到特征 " + args[2]); return; }

    var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
    if (read.Status != GattCommunicationStatus.Success) { Fail("读取失败: " + read.Status); return; }
    var reader = DataReader.FromBuffer(read.Value);
    var bytes = new byte[reader.UnconsumedBufferLength];
    reader.ReadBytes(bytes);
    Console.WriteLine($"读取成功: {ToHex(bytes)}   ASCII: {ToAscii(bytes)}");
}

// write <MAC> <serviceUuid> <charUuid> <hex> [--noresp]
static async Task WriteAsync(string[] args)
{
    if (args.Length < 4) { Fail("用法: write <MAC> <serviceUuid> <charUuid> <hex> [--noresp]"); return; }
    var device = await ConnectAsync(RequireMac(args));
    if (device == null) { Fail("无法连接设备。"); return; }

    var option = args.Contains("--noresp", StringComparer.OrdinalIgnoreCase)
        ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;
    await DoWriteAsync(device, ParseUuid(args[1]), ParseUuid(args[2]), FromHex(args[3]), option);
}

// listen <MAC> [秒数]
static async Task ListenAsync(string[] args)
{
    int seconds = args.Length > 2 ? int.Parse(args[2]) : 60;
    var device = await ConnectAsync(RequireMac(args));
    if (device == null) { Fail("无法连接设备。"); return; }
    await SubscribeAllAsync(device, seconds);
}

// writelisten <MAC> <serviceUuid> <charUuid> <hex> [监听秒数] [--noresp]
// 写入指令后监听所有通知 —— 破解"请求-应答"协议的主要手段
static async Task WriteListenAsync(string[] args)
{
    if (args.Length < 4) { Fail("用法: writelisten <MAC> <serviceUuid> <charUuid> <hex> [秒=10] [--noresp]"); return; }
    int seconds = 10;
    var idxSec = Array.FindIndex(args, a => int.TryParse(a, out _));
    if (idxSec > 3) seconds = int.Parse(args[idxSec]);

    var device = await ConnectAsync(RequireMac(args));
    if (device == null) { Fail("无法连接设备。"); return; }

    var option = args.Contains("--noresp", StringComparer.OrdinalIgnoreCase)
        ? GattWriteOption.WriteWithoutResponse : GattWriteOption.WriteWithResponse;

    // 先订阅所有可通知特征，再写指令，保证不漏掉回包
    await SubscribeAllAsync(device, 0); // 0 = 只订阅，不等待
    Console.WriteLine($">>> 写入: {args[3]} ({option})");
    await DoWriteAsync(device, ParseUuid(args[1]), ParseUuid(args[2]), FromHex(args[3]), option);

    Console.WriteLine($">>> 监听通知 {seconds} 秒...\n");
    await Task.Delay(seconds * 1000);
}

// ---------------------------------------------------------------- helpers

static async Task<BluetoothLEDevice?> ConnectAsync(ulong mac)
{
    Console.WriteLine($"正在连接 {FmtMac(mac)} ...");
    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
    if (device == null)
    {
        Console.ForegroundColor = ConsoleColor.Yellow;
        Console.WriteLine("第一次连接失败，等 3 秒重试一次（设备可能刚进入可连接状态）...");
        Console.ResetColor();
        await Task.Delay(3000);
        device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
    }
    if (device != null && device.ConnectionStatus == BluetoothConnectionStatus.Disconnected)
    {
        var session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId);
        if (session != null)
        {
            session.MaintainConnection = true;
            for (int i = 0; i < 20 && device.ConnectionStatus == BluetoothConnectionStatus.Disconnected; i++)
                await Task.Delay(250);
            session.Dispose();
        }
    }
    return device;
}

static async Task SubscribeAllAsync(BluetoothLEDevice device, int listenSeconds)
{
    var svcs = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
    int subscribed = 0;
    foreach (var svc in svcs.Services)
    {
        var chars = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
        foreach (var ch in chars.Characteristics)
        {
            var prop = ch.CharacteristicProperties;
            GattClientCharacteristicConfigurationDescriptorValue cccd = 0;
            if (prop.HasFlag(GattCharacteristicProperties.Notify)) cccd = GattClientCharacteristicConfigurationDescriptorValue.Notify;
            else if (prop.HasFlag(GattCharacteristicProperties.Indicate)) cccd = GattClientCharacteristicConfigurationDescriptorValue.Indicate;
            if (cccd == 0) continue;

            ch.ValueChanged += (_, a) =>
            {
                var reader = DataReader.FromBuffer(a.CharacteristicValue);
                var bytes = new byte[reader.UnconsumedBufferLength];
                reader.ReadBytes(bytes);
                Console.ForegroundColor = ConsoleColor.Magenta;
                Console.WriteLine($"[通知 {DateTime.Now:HH:mm:ss.fff}] {FmtUuid(svc.Uuid)} / {FmtUuid(ch.Uuid)}: {ToHex(bytes)}");
                Console.ResetColor();
            };
            try
            {
                var status = await ch.WriteClientCharacteristicConfigurationDescriptorAsync(cccd);
                if (status == GattCommunicationStatus.Success) subscribed++;
                else Console.WriteLine($"  (订阅失败 {FmtUuid(ch.Uuid)}: {status})");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  (订阅异常 {FmtUuid(ch.Uuid)}: {ex.Message})");
            }
        }
    }
    Console.WriteLine($"已订阅 {subscribed} 个通知特征。");
}

static async Task DoWriteAsync(BluetoothLEDevice device, Guid svcUuid, Guid chUuid, byte[] payload, GattWriteOption option)
{
    var svc = (await device.GetGattServicesForUuidAsync(svcUuid, BluetoothCacheMode.Uncached)).Services.FirstOrDefault();
    if (svc == null) { Fail("找不到服务 " + svcUuid); return; }
    var ch = (await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached)).Characteristics.FirstOrDefault(c => c.Uuid == chUuid);
    if (ch == null) { Fail("找不到特征 " + chUuid); return; }

    using var writer = new DataWriter();
    writer.WriteBytes(payload);
    var status = await ch.WriteValueAsync(writer.DetachBuffer(), option);
    Console.WriteLine(status == GattCommunicationStatus.Success ? "写入成功。" : $"写入失败: {status}");
}

static ulong RequireMac(string[] args)
{
    if (args.Length < 2) throw new ArgumentException("缺少 MAC 地址参数");
    return ulong.Parse(args[1].Replace(":", "").Replace("-", ""), NumberStyles.HexNumber);
}

static Guid ParseUuid(string s)
{
    if (s.Length == 4) // 16 位短 UUID
        return new Guid($"0000{s}-0000-1000-8000-00805f9b34fb");
    return Guid.Parse(s);
}

static string FmtUuid(Guid g)
{
    var s = g.ToString();
    return s.StartsWith("0000") && s.EndsWith("-0000-1000-8000-00805f9b34fb")
        ? s.Substring(4, 4) + " (0x" + s.Substring(4, 4) + ")" : s;
}

static string FmtMac(ulong mac) =>
    string.Join(":", BitConverter.GetBytes(mac).Take(6).Reverse().Select(b => b.ToString("X2")));

static string ToHex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", " ");

static string ToAscii(byte[] bytes) =>
    new string(bytes.Select(b => b is >= 0x20 and <= 0x7E ? (char)b : '.').ToArray());

static byte[] FromHex(string hex) =>
    hex.Replace(" ", "").Replace(":", "").Chunk(2).Select(c => byte.Parse(new string(c), NumberStyles.HexNumber)).ToArray();

static void Fail(string msg)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine("[错误] " + msg);
    Console.ResetColor();
}

// ---------------------------------------------------------------- diag
// 连接通道诊断（AirPods 降噪切换可行性探测）：
//   [1] RFCOMM 拨号对照 —— 证明 radio 可达、能真实发起经典蓝牙连接
//   [2] L2CAP PSM 0x1001 —— AACP 降噪控制通道（LibrePods 协议），实测 Windows 是否放行
//   [3] RFCOMM 服务发现（SDP）
//   [4] BLE GATT 服务全枚举 —— 找可能存在的厂商模式切换特征
// 生成 probe_logs/diag_*.txt 报告，可直接附到 GitHub Issue。

static async Task DiagAsync(string[] args)
{
    var paired = await ListPairedAsync();
    if (paired.Count == 0) { Fail("没有已配对的蓝牙设备。请先在系统设置里配对耳机。"); return; }

    // 选目标：MAC / 名字片段 / 序号 / 无参交互选择
    (string Name, ulong Address) target;
    if (args.Length > 1)
    {
        var key = string.Join(' ', args.Skip(1));
        var hex = key.Replace(":", "").Replace("-", "");
        if (hex.Length == 12 && ulong.TryParse(hex, NumberStyles.HexNumber, null, out var m))
        {
            target = paired.FirstOrDefault(p => p.Address == m);
            if (target.Name == null) { Fail($"配对列表里没有 {key}"); return; }
        }
        else if (int.TryParse(key, out var idx) && idx >= 0 && idx < paired.Count)
        {
            target = paired[idx];
        }
        else
        {
            target = paired.FirstOrDefault(p => p.Name.Contains(key, StringComparison.OrdinalIgnoreCase));
            if (target.Name == null) { Fail($"配对列表里没有名字含「{key}」的设备"); return; }
        }
    }
    else
    {
        Console.WriteLine("已配对的蓝牙设备:");
        for (int i = 0; i < paired.Count; i++)
            Console.WriteLine($"  [{i}] {paired[i].Name}  ({FmtMac(paired[i].Address)})");
        Console.Write("选择要诊断的设备序号（回车=0）: ");
        var line = Console.ReadLine();
        var idx = int.TryParse(line, out var i2) ? i2 : 0;
        if (idx < 0 || idx >= paired.Count) { Fail("无效序号"); return; }
        target = paired[idx];
    }

    var r = new StringBuilder();
    r.AppendLine("==========================================================");
    r.AppendLine("绒汇耳机助手 连接诊断报告（probe diag）");
    r.AppendLine($"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
    bool isAdmin = new WindowsPrincipal(WindowsIdentity.GetCurrent())
        .IsInRole(WindowsBuiltInRole.Administrator);
    r.AppendLine($"系统: {Environment.OSVersion.VersionString}  管理员: {(isAdmin ? "是" : "否")}");
    r.AppendLine($"目标: {target.Name} ({FmtMac(target.Address)})");
    r.AppendLine();

    // 经典蓝牙连接状态（上下文信息）
    string classicState = "未知";
    try
    {
        var bt = await BluetoothDevice.FromBluetoothAddressAsync(target.Address);
        if (bt != null) classicState = bt.ConnectionStatus.ToString();
    }
    catch { }
    r.AppendLine($"经典蓝牙连接状态: {classicState}");
    r.AppendLine();

    // [1] RFCOMM 对照：同结构体拨号通道 1，验证 radio 与套接字路径本身可用
    Console.WriteLine("[1/4] RFCOMM 对照拨号（通道 1，最多等 9 秒）...");
    r.AppendLine("[1] 经典蓝牙 RFCOMM 拨号对照测试（目标通道 1）");
    r.AppendLine("    作用: 证明本机蓝牙 radio 可达、能真实发起拨号（对照组）");
    r.AppendLine("    " + TestBthConnect(BuildSockAddrBth(target.Address, 1),
        SocketType.Stream, (ProtocolType)0x0003, 9000));
    r.AppendLine();

    // [2] L2CAP PSM 0x1001：LibrePods AACP 降噪控制通道
    Console.WriteLine("[2/4] L2CAP PSM 0x1001（AACP 降噪控制通道）...");
    r.AppendLine("[2] 经典蓝牙 L2CAP PSM 0x1001 测试（AACP 降噪控制通道）");
    r.AppendLine("    作用: 检测 Windows 是否允许用户态建立经典 L2CAP 连接");
    var l2capResult = TestL2Cap(target.Address);
    r.AppendLine("    " + l2capResult);
    r.AppendLine("    解读: 若为 WSA 10050/10044 —— 协议栈本地拒绝，换任何 Windows 机器结果相同；" +
                 "降噪切换需内核驱动，纯用户态软件无法实现（预期结果）");
    r.AppendLine();

    // [3] RFCOMM 服务发现（SDP）
    Console.WriteLine("[3/4] RFCOMM 服务发现（SDP）...");
    r.AppendLine("[3] RFCOMM 服务发现（SDP 记录）");
    try
    {
        var bt = await BluetoothDevice.FromBluetoothAddressAsync(target.Address);
        if (bt == null)
        {
            r.AppendLine("    经典蓝牙设备对象: null（系统未记录该地址）");
        }
        else
        {
            var rf = await bt.GetRfcommServicesAsync(BluetoothCacheMode.Uncached);
            r.AppendLine($"    服务数: {rf.Services.Count}  (状态: {rf.Error})");
            foreach (var s in rf.Services)
                r.AppendLine($"      - {s.ServiceId.AsString()}  (UUID {FmtUuid(s.ServiceId.Uuid)})");
            if (rf.Services.Count == 0)
                r.AppendLine("      （设备可能离线，SDP 查询无结果——连上后再跑一次数据更全）");
        }
    }
    catch (Exception ex)
    {
        r.AppendLine("    服务发现失败: " + ex.Message);
    }
    r.AppendLine();

    // [4] BLE GATT 全枚举
    Console.WriteLine("[4/4] BLE GATT 服务枚举（LE 通道）...");
    r.AppendLine("[4] BLE GATT 服务全枚举（LE 通道）");
    r.AppendLine("    作用: 列出耳机暴露的全部 GATT 特征；出现未知「可写」特征即有探测价值");
    await DiagGattAsync(target.Address, r);
    r.AppendLine();

    r.AppendLine("结论速查:");
    r.AppendLine("  - [2] 出现 WSA 10050/10044 → 确认 Windows 用户态无法建经典 L2CAP（降噪切换受阻的平台限制）");
    r.AppendLine("  - [1] 出现 WSA 10060/连接成功 → radio 与拨号路径正常，排除本机环境问题");
    r.AppendLine("  - [4] 出现未知可写特征 → 有进一步探测价值，把本报告发到");
    r.AppendLine("        https://github.com/Furry09shou/ronghui-earbuds/issues");
    r.AppendLine("==========================================================");

    Directory.CreateDirectory("probe_logs");
    var safeName = string.Join("", target.Name.Split(Path.GetInvalidFileNameChars()));
    var reportPath = Path.Combine("probe_logs", $"diag_{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.txt");
    await File.WriteAllTextAsync(reportPath, r.ToString());

    Console.ForegroundColor = ConsoleColor.Green;
    Console.WriteLine($"\n诊断完成，报告已保存: {reportPath}");
    Console.WriteLine("把该文件直接附到 GitHub Issue 即可参与降噪通道探测。");
    Console.ResetColor();
}

static async Task<List<(string Name, ulong Address)>> ListPairedAsync()
{
    var infos = await DeviceInformation.FindAllAsync(
        BluetoothDevice.GetDeviceSelector(),
        new List<string> { "System.DeviceInterface.Bluetooth.DeviceAddress" });

    var list = new List<(string Name, ulong Address)>();
    foreach (var info in infos)
    {
        ulong addr = 0;
        if (info.Properties.TryGetValue("System.DeviceInterface.Bluetooth.DeviceAddress", out var str)
            && ulong.TryParse(str?.ToString()?.Replace(":", "").Replace("-", ""),
                NumberStyles.HexNumber, null, out var parsed))
        {
            addr = parsed;
        }
        if (addr != 0 && !string.IsNullOrEmpty(info.Name))
            list.Add((info.Name, addr));
    }
    return list.GroupBy(p => p.Address).Select(g => g.First()).OrderBy(p => p.Name).ToList();
}

// SOCKADDR_BTH 是 Pack=1 紧凑布局共 30 字节:
//   family(2) + btAddr(8, 低位在前) + serviceClassId GUID(16, 全零) + port/PSM(4, 小端)
static byte[] BuildSockAddrBth(ulong mac, uint port)
{
    var sa = new byte[30];
    sa[0] = 32; // AF_BTH = 32 (小端)
    for (int i = 0; i < 6; i++) sa[2 + i] = (byte)(mac >> (8 * i));
    sa[26] = (byte)port;
    sa[27] = (byte)(port >> 8);
    sa[28] = (byte)(port >> 16);
    sa[29] = (byte)(port >> 24);
    return sa;
}

// 非阻塞 connect + Poll，返回带解读的结果行
static string TestBthConnect(byte[] saBody, SocketType socketType, ProtocolType protocol, int timeoutMs)
{
    Socket s;
    try
    {
        s = new Socket((AddressFamily)32, socketType, protocol);
    }
    catch (SocketException se)
    {
        return $"套接字创建失败: WSA {se.ErrorCode} ({WsaName(se.ErrorCode)})";
    }

    using (s)
    {
        try
        {
            s.Blocking = false;
            s.Connect(new BthEndPoint(saBody));
            return "connect 立即成功（设备接受了该通道！）";
        }
        catch (SocketException se) when (se.SocketErrorCode == SocketError.WouldBlock)
        {
            // 连接进行中，等 Poll 结果
        }
        catch (SocketException se)
        {
            return $"connect 失败: WSA {se.ErrorCode} ({WsaName(se.ErrorCode)})";
        }

        bool writable = s.Poll(timeoutMs * 1000, SelectMode.SelectWrite);
        int code = Convert.ToInt32(s.GetSocketOption(SocketOptionLevel.Socket, SocketOptionName.Error) ?? 0);
        if (writable)
            return code == 0
                ? $"连接成功（{timeoutMs}ms 内握手完成——设备接受了该通道！）"
                : $"connect 结果: WSA {code} ({WsaName(code)})";
        if (s.Poll(0, SelectMode.SelectError))
            return $"connect 失败（select 异常）: WSA {code} ({WsaName(code)})";
        return $"connect 超时（{timeoutMs}ms 无结果——radio 已拨号但设备未应答，可能离线）";
    }
}

// L2CAP 测试：SEQPACKET 创建被拒（部分系统报 10038/10044）时降级 STREAM 复测——
// STREAM 能走到 connect 步骤，错误码更有判定价值（实测 STREAM+L2CAP connect 恒 WSA 10050）
static string TestL2Cap(ulong mac)
{
    var body = BuildSockAddrBth(mac, 0x1001);
    var seq = TestBthConnect(body, SocketType.Seqpacket, (ProtocolType)0x0100, 4000);
    if (!seq.Contains("套接字创建失败")) return seq;
    var stream = TestBthConnect(body, SocketType.Stream, (ProtocolType)0x0100, 4000);
    return $"SOCK_SEQPACKET: {seq}；改用 SOCK_STREAM 复测 → {stream}";
}

static string WsaName(int code) => code switch
{
    10044 => "WSAESOCKTNOSUPPORT（系统不支持此套接字类型）",
    10048 => "WSAEADDRINUSE",
    10049 => "WSAEADDRNOTAVAIL",
    10050 => "WSAENETDOWN（协议栈本地拒绝——Windows 用户态不开放此通道）",
    10051 => "WSAENETUNREACH",
    10060 => "WSAETIMEDOUT（真实拨号超时——radio 可达，设备无应答）",
    10061 => "WSAECONNREFUSED",
    10065 => "WSAEHOSTUNREACH",
    10106 => "WSAEPROVIDERFAILEDINIT",
    _ => $"WSAE {(SocketError)code}",
};

static async Task DiagGattAsync(ulong mac, StringBuilder r)
{
    var device = await BluetoothLEDevice.FromBluetoothAddressAsync(mac);
    if (device == null)
    {
        r.AppendLine("    LE 设备对象: null（系统未记录该地址的 LE 通道——耳机可能未做 BLE 配对，正常，不影响 [1][2] 结果）");
        return;
    }
    r.AppendLine($"    LE 设备对象: {device.Name}，连接状态 {device.ConnectionStatus}");

    GattSession? session = null;
    try { session = await GattSession.FromDeviceIdAsync(device.BluetoothDeviceId); } catch { }
    if (session != null) session.MaintainConnection = true;
    try
    {
        // 给 LE 连接一点建立时间
        for (int i = 0; i < 20 && device.ConnectionStatus == BluetoothConnectionStatus.Disconnected; i++)
            await Task.Delay(250);

        var svcResult = await device.GetGattServicesAsync(BluetoothCacheMode.Uncached);
        if (svcResult.Status != GattCommunicationStatus.Success)
        {
            r.AppendLine($"    服务枚举失败: {svcResult.Status} (协议错误码: {svcResult.ProtocolError})" +
                         "（耳机离线或 LE 未配对——连上耳机后重跑数据更全）");
            return;
        }
        r.AppendLine($"    服务数: {svcResult.Services.Count}");

        foreach (var svc in svcResult.Services)
        {
            r.AppendLine($"    ■ {FmtUuid(svc.Uuid)}{(string.IsNullOrEmpty(SvcName(svc.Uuid)) ? "" : "  " + SvcName(svc.Uuid))}  (起始句柄 0x{svc.AttributeHandle:X4})");

            var charsResult = await svc.GetCharacteristicsAsync(BluetoothCacheMode.Uncached);
            if (charsResult.Status != GattCommunicationStatus.Success)
            {
                r.AppendLine($"        [特征枚举失败: {charsResult.Status}]");
                continue;
            }

            foreach (var ch in charsResult.Characteristics)
            {
                var props = ch.CharacteristicProperties;
                var writable = props.HasFlag(GattCharacteristicProperties.Write)
                            || props.HasFlag(GattCharacteristicProperties.WriteWithoutResponse);
                r.AppendLine($"        特征 {FmtUuid(ch.Uuid)}  (句柄 0x{ch.AttributeHandle:X4})  [{props}]" +
                             (writable ? "  ★可写" : "") + (ch.Uuid == GattCharacteristicUuids.BatteryLevel ? "  ←标准电量" : ""));

                if (props.HasFlag(GattCharacteristicProperties.Read))
                {
                    var read = await ch.ReadValueAsync(BluetoothCacheMode.Uncached);
                    if (read.Status == GattCommunicationStatus.Success)
                    {
                        var reader = DataReader.FromBuffer(read.Value);
                        var bytes = new byte[reader.UnconsumedBufferLength];
                        reader.ReadBytes(bytes);
                        r.AppendLine($"            值: {ToHex(bytes)}   ASCII: {ToAscii(bytes)}");
                    }
                    else
                    {
                        r.AppendLine($"            读取失败: {read.Status}");
                    }
                }
            }
        }
    }
    finally
    {
        session?.Dispose();
        device.Dispose();
    }
}

static string SvcName(Guid g) =>
    g == GattServiceUuids.Battery ? "电池服务 (BAS)"
    : g == GattServiceUuids.DeviceInformation ? "设备信息 (DIS)"
    : g == GattServiceUuids.GenericAccess ? "通用访问 (GAP)"
    : g == GattServiceUuids.GenericAttribute ? "通用属性 (GATT)"
    : g == GattServiceUuids.HumanInterfaceDevice ? "人机接口 (HID)"
    : g == GattServiceUuids.ImmediateAlert ? "即时告警 (IAS)"
    : "";

static void PrintHelp()
{
    Console.WriteLine("""
        RonghuiEarbuds BLE 探针 —— 破解耳机协议（电量 / 降噪控制通道）

        用法:
          probe scan                          扫描 BLE 广播（耳机开盖/取出时才有广播）
          probe dump <MAC>                    连接并完整枚举 GATT 服务/特征 + 读取可读值
          probe battery <MAC>                 专门尝试标准电量服务 0x180F
          probe read <MAC> <svc> <char>       读取指定特征
          probe write <MAC> <svc> <char> <hex> [--noresp]
                                              写入十六进制指令
          probe listen <MAC> [秒=60]          订阅所有通知特征并监听
          probe writelisten <MAC> <svc> <char> <hex> [秒=10]
                                              写指令后监听通知（探测请求-应答协议）
          probe diag [MAC|名字片段|序号]      连接通道诊断（AirPods 降噪切换探测）:
                                              RFCOMM 对照拨号 + L2CAP AACP 通道实测 +
                                              SDP 服务发现 + GATT 全枚举，
                                              生成 probe_logs/diag_*.txt 报告，可直接附到 GitHub Issue

        MAC 形如 12:34:56:78:9A:BC；短 UUID 形如 180F、2A19，长 UUID 用完整格式。
        """);
}

/// <summary>AF_BTH 原始端点：让 Socket.Connect 直接使用预构造的 30 字节 SOCKADDR_BTH。</summary>
internal sealed class BthEndPoint(byte[] body) : EndPoint
{
    private readonly SocketAddress _sa = Build(body);

    public override AddressFamily AddressFamily => (AddressFamily)32;

    public override SocketAddress Serialize() => _sa;

    private static SocketAddress Build(byte[] body)
    {
        var sa = new SocketAddress((AddressFamily)32, body.Length);
        for (int i = 2; i < body.Length; i++) sa[i] = body[i];
        return sa;
    }
}
