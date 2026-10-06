using System.IO;
using Windows.Devices.Bluetooth.Advertisement;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 被动监听耳机 BLE 广播（不限品牌，解析档案见 XiaomiAdvParser.Profiles），
/// 把所有识别到的设备的解析结果对外推送（多设备场景由界面选择关注哪台）；
/// 尚无关注设备时，自动绑定第一台信号足够的设备。
/// </summary>
public sealed class EarbudsWatcher : IDisposable
{
    private readonly BluetoothLEAdvertisementWatcher _watcher = new()
    {
        ScanningMode = BluetoothLEScanningMode.Active,
    };

    private readonly AppConfig _config;

    /// <summary>已绑定设备的广播更新。</summary>
    public event Action<EarbudsUpdate>? UpdateReceived;

    /// <summary>自动绑定完成（参数为显示名）。</summary>
    public event Action<string>? DeviceBound;

    public string? BoundMac { get; private set; }

    // 信号弱于该值不参与自动绑定，避免绑到邻居耳机
    private const int AutoBindMinRssi = -85;

    public EarbudsWatcher(AppConfig config)
    {
        _config = config;
        BoundMac = config.BoundMac;
        _watcher.Received += OnReceived;
    }

    public void Start()
    {
        if (_watcher.Status == BluetoothLEAdvertisementWatcherStatus.Created)
            _watcher.Start();
    }

    public void Unbind()
    {
        BoundMac = null;
        _config.BoundMac = null;
        _config.Save();
    }

    /// <summary>多设备场景：手动切换关注的设备并持久化。</summary>
    public void SelectDevice(string mac)
    {
        BoundMac = mac;
        _config.BoundMac = mac;
        _config.Save();
        DiagLog($"切换关注设备 {mac}");
    }

    private void OnReceived(BluetoothLEAdvertisementWatcher sender,
        BluetoothLEAdvertisementReceivedEventArgs args)
    {
        var snapshot = XiaomiAdvParser.Parse(args.Advertisement.ManufacturerData);
        if (snapshot is null) return;

        // Apple 广播地址是随机轮换的（同一副耳机每次开盖换 MAC），
        // 用型号合成的稳定 ID 当设备标识，绑定/历史/悬浮条才能跟住同一台设备
        var mac = XiaomiAdvParser.IsAppleKey(snapshot.ProductKey)
            ? snapshot.ProductKey
            : FormatMac(args.BluetoothAddress);

        // 尚无关注设备时，自动绑定第一台信号足够的设备（避免绑到邻居耳机）
        if (BoundMac is null && args.RawSignalStrengthInDBm >= AutoBindMinRssi)
        {
            BoundMac = mac;
            DiagLog($"绑定设备 {mac} ({snapshot.ProductKey})");
            DeviceBound?.Invoke(XiaomiAdvParser.GetDisplayName(snapshot.ProductKey));
        }

        UpdateReceived?.Invoke(new EarbudsUpdate(
            mac, args.RawSignalStrengthInDBm, snapshot, DateTime.Now,
            XiaomiAdvParser.GetDisplayName(snapshot.ProductKey)));
    }

    internal static void DiagLog(string message)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RonghuiEarbuds");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "watcher.log"),
                $"[{DateTime.Now:HH:mm:ss}] {message}\r\n");
        }
        catch { /* 诊断日志失败不影响主流程 */ }
    }

    public static string FormatMac(ulong address) =>
        string.Join(":",
            Enumerable.Range(0, 6)
                .Select(i => (address >> (8 * i)) & 0xFF)
                .Select(b => b.ToString("X2")));

    public void Dispose() => _watcher.Received -= OnReceived;
}
