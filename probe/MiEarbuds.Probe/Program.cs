// MiEarbuds BLE 探针 —— 用于破解小米耳机（Air2 SE 起步）的电量协议
// 用法见 help 命令
using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.Advertisement;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Security.Cryptography;
using Windows.Storage.Streams;

Console.OutputEncoding = Encoding.UTF8;

var cmd = args.Length > 0 ? args[0].ToLowerInvariant() : "help";

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
        default: PrintHelp(); break;
    }
}
catch (Exception ex)
{
    Console.ForegroundColor = ConsoleColor.Red;
    Console.WriteLine($"[错误] {ex.Message}");
    Console.ResetColor();
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

static void PrintHelp()
{
    Console.WriteLine("""
        MiEarbuds BLE 探针 —— 破解小米耳机电量协议

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

        MAC 形如 12:34:56:78:9A:BC；短 UUID 形如 180F、2A19，长 UUID 用完整格式。
        """);
}
