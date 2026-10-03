namespace MiEarbuds.App.Core;

/// <summary>从一次 BLE 广播解析出的电池快照。</summary>
public sealed record BatterySnapshot(
    int? LeftPercent,
    int? RightPercent,
    int? CasePercent,
    bool LeftInCase,
    bool RightInCase,
    string ProductKey);

/// <summary>一次广播更新（已绑定设备）。</summary>
public sealed record EarbudsUpdate(string Mac, int Rssi, BatterySnapshot Snapshot, DateTime Timestamp);
