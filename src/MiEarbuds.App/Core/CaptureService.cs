using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Devices.Bluetooth.Advertisement;

namespace MiEarbuds.App.Core;

/// <summary>
/// 原始广播采集器（适配新耳机用）：不限机型，记录所有厂商数据段，
/// 按阶段打标记，导出 JSONL + ZIP，供云端分析后登记解析档案。
/// </summary>
public sealed class CaptureService : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active,
    };
    private readonly List<string> _lines = new();
    private readonly object _gate = new();

    public int Count { get; private set; }
    public HashSet<ushort> CompanyIds { get; } = new();
    public HashSet<string> ProductKeys { get; } = new();   // 0x038F 帧的 [8..10] 产品标识

    /// <summary>每收到含厂商数据的广播触发一次（UI 节流显示）。</summary>
    public event Action? Ticked;

    public CaptureService()
    {
        _watcher.Received += OnReceived;
    }

    public void Start() => _watcher.Start();

    public void AddMarker(string phase)
    {
        lock (_gate)
            _lines.Add($"{{\"t\":\"{DateTime.Now:O}\",\"marker\":\"{phase}\"}}");
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var adv = args.Advertisement;
        if (adv.ManufacturerData.Count == 0) return;

        var mac = FormatMac(args.BluetoothAddress);
        foreach (var m in adv.ManufacturerData)
        {
            var data = m.Data.ToArray();
            var hex = Convert.ToHexString(data);
            string? productKey = null;
            if (m.CompanyId == 0x038F && data.Length >= 11)
            {
                productKey = $"{data[8]:X2} {data[9]:X2} {data[10]:X2}";
                lock (_gate) ProductKeys.Add(productKey);
            }

            lock (_gate)
            {
                Count++;
                CompanyIds.Add(m.CompanyId);
                _lines.Add(
                    $"{{\"t\":\"{DateTime.Now:O}\",\"mac\":\"{mac}\",\"rssi\":{args.RawSignalStrengthInDBm}," +
                    $"\"cid\":{m.CompanyId},\"hex\":\"{hex}\"" +
                    (productKey is null ? "" : $",\"pkey\":\"{productKey}\"") + "}}");
            }
        }
        Ticked?.Invoke();
    }

    /// <summary>导出到 文档\MiEarbudsCapture\，返回 jsonl 与 zip 路径。</summary>
    public (string JsonlPath, string ZipPath) Export(string modelName)
    {
        string[] meta =
        {
            $"{{\"type\":\"mi-earbuds-capture\",\"v\":1,\"model\":\"{modelName}\"," +
            $"\"captured\":\"{DateTime.Now:O}\",\"packets\":{Count}}}",
            "{\"privacy\":\"包含耳机广播 MAC（仅用于分析），不含用户个人数据\"}",
        };
        string[] snapshot;
        lock (_gate) snapshot = _lines.ToArray();

        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MiEarbudsCapture");
        Directory.CreateDirectory(dir);

        var safe = new string(modelName.Where(c => !Path.GetInvalidFileNameChars().Contains(c)).ToArray());
        if (safe.Length == 0) safe = "unknown";
        var jsonl = Path.Combine(dir, $"capture_{safe}_{DateTime.Now:yyyyMMdd_HHmmss}.jsonl");
        File.WriteAllLines(jsonl, meta.Concat(snapshot));

        var zip = Path.ChangeExtension(jsonl, ".zip");
        using var fs = new FileStream(zip, FileMode.Create);
        using var archive = new ZipArchive(fs, ZipArchiveMode.Create);
        archive.CreateEntryFromFile(jsonl, Path.GetFileName(jsonl));
        return (jsonl, zip);
    }

    private static string FormatMac(ulong address) =>
        string.Join(":", BitConverter.GetBytes(address).Take(6).Select(b => b.ToString("X2")));

    public void Dispose()
    {
        try { _watcher.Stop(); } catch { /* 已停止 */ }
    }
}
