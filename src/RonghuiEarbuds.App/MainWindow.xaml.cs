using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using RonghuiEarbuds.App.Core;
using RonghuiEarbuds.App.UI;

namespace RonghuiEarbuds.App;

public partial class MainWindow : Window
{
    private readonly EarbudsWatcher _watcher;
    private readonly AppConfig _config;
    private readonly Action<EarbudsUpdate>? _onUpdateApplied;
    private readonly Action<bool>? _onAliveChanged;
    private readonly Action<bool>? _onMiniBarToggle;
    private readonly DispatcherTimer _aliveTimer;
    private readonly BatteryHistoryStore _history = new();
    private bool _lastAlive = true;

    private bool _initialized;

    // 多设备：MAC → 观测状态（本会话内出现过的所有设备 + 配置里持久化的设备名单）
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    // 已持久化到配置的设备（切换列表常驻显示，不受 10 分钟窗口限制）
    private readonly HashSet<string> _knownMacs = new(StringComparer.OrdinalIgnoreCase);
    private DeviceState? _active;
    private int _probeTick;

    /// <summary>当前面板正在显示的设备 MAC（托盘提示只对该设备弹出）。</summary>
    public string? ActiveMac => _active?.Mac;

    // 检查到的新版本（再点一次按钮打开下载页）
    private UpdateInfo? _updateInfo;

    private const double StaleAfterSeconds = 8;
    // 卡片显示的实时窗口：广播停 3 秒内数据即过期回「--」（用户要求不挂旧值）；
    // 设备级断连判定仍用 8 秒（容忍广播间隔，避免误判断连）
    private const double ChannelFreshSeconds = 3;
    // 未入名单的临时设备（信号弱的路人耳机）在切换列表只保留 10 分钟
    private static readonly TimeSpan DeviceListWindow = TimeSpan.FromMinutes(10);
    private const int JumpThreshold = 25;
    private static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(3);

    // 主题色经 ThemeManager 动态取值（跟随 Windows 深浅模式）
    private static Color GoodColor => ThemeManager.GetColor("T.Good");
    private static Color MidColor => ThemeManager.GetColor("T.Mid");
    private static Color LowColor => ThemeManager.GetColor("T.Low");
    private static Color UnknownColor => ThemeManager.GetColor("T.Unknown");

    /// <summary>单台设备的观测状态。</summary>
    private sealed class DeviceState
    {
        public required string Mac { get; init; }
        public string Name = "";
        public DateTime LastSeen = DateTime.MinValue;
        /// <summary>最后一次收到有效广播的时间（连接心跳不刷新此值，两者语义不同）。</summary>
        public DateTime BroadcastSeen = DateTime.MinValue;
        public int? Rssi;

        /// <summary>false = 广播格式未适配（系统连接枚举发现，只有在线状态无电量）。</summary>
        public bool IsAdapted = true;

        /// <summary>广播暂停时的兜底读数：系统级 HFP/AVRCP 电量（蓝牙设置页同源）。</summary>
        public int? SystemBattery;

        // 抖动抑制：充电触点瞬态会造成个别字段跳变，跳变过大时先压住
        public (int Value, DateTime Time, int Suppressed)? Left, Right, Case;

        // 最近一次有效帧的在仓状态（无效帧期间保持显示）
        public bool? LeftInCase, RightInCase;
    }

    public MainWindow(EarbudsWatcher watcher, AppConfig config,
        Action<EarbudsUpdate>? onUpdateApplied = null, Action<bool>? onAliveChanged = null,
        Action<bool>? onMiniBarToggle = null)
    {
        InitializeComponent();
        _watcher = watcher;
        _config = config;
        _onUpdateApplied = onUpdateApplied;
        _onAliveChanged = onAliveChanged;
        _onMiniBarToggle = onMiniBarToggle;

        RestorePosition(config);

        _capture.Ticked += () => Dispatcher.Invoke(RefreshCaptureLive);

        AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();
        InitSettingsControls();
        _initialized = true;

        ApplyPinState();   // 恢复用户的图钉置顶设置

        LoadKnownDevices(config);
        // 启动时优先显示上次关注的设备（即使它还没开始广播）
        if (_active is null && config.BoundMac is { } bound)
        {
            var last = _devices.Values.FirstOrDefault(
                d => d.Mac.Equals(bound, StringComparison.OrdinalIgnoreCase));
            if (last is not null) SetActive(last);
        }

        _watcher.UpdateReceived += u => Dispatcher.Invoke(() => ApplyUpdate(u));

        _aliveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _aliveTimer.Tick += (_, _) =>
        {
            RefreshAliveState();
            // 每 5 秒枚举一次系统连接的音频设备，把未适配格式的耳机补进列表
            if (++_probeTick % 5 == 0) RefreshConnectedAudio();
            // 每 15 秒读一次系统电量（HFP/AVRCP 上报），作广播暂停时的兜底显示
            if (_probeTick % 15 == 0) ProbeSystemBattery();
            // 每 30 秒刷新一次用量统计（预计可用时长随时间推进而变化）
            if (_probeTick % 30 == 0) RefreshStats();
        };
        _aliveTimer.Start();

        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartPulse(false);
        };
    }

    // ---------- 数据 ----------

    private void ApplyUpdate(EarbudsUpdate u)
    {
        var isNew = !_devices.ContainsKey(u.Mac);
        var st = GetOrAdd(u.Mac);
        st.LastSeen = u.Timestamp;
        st.BroadcastSeen = u.Timestamp;
        st.Rssi = u.Rssi;
        var s = u.Snapshot;
        if (st.Name.Length == 0)
            st.Name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Mac : u.DisplayName;

        _history.Record(u.Mac, s);   // 电量历史落库（内部自带节流）

        // 电量值（带抖动过滤；null 表示本帧无效，维持旧显示）
        var left = Filter(ref st.Left, s.LeftPercent);
        var right = Filter(ref st.Right, s.RightPercent);
        var cse = Filter(ref st.Case, s.CasePercent);

        // 在仓状态：只在收到有效值时更新，避免噪声帧闪烁
        if (s.LeftPercent is not null) st.LeftInCase = s.LeftInCase;
        if (s.RightPercent is not null) st.RightInCase = s.RightInCase;

        // 广播能被解析 = 该格式已适配：探测登记的条目原位升级
        // （多数耳机广播 MAC 与经典蓝牙 MAC 相同，同一个条目）
        var upgraded = false;
        if (!st.IsAdapted)
        {
            st.IsAdapted = true;
            upgraded = true;
            if (ReferenceEquals(st, _active))
                UnadaptedHint.Visibility = Visibility.Collapsed;
        }

        // 合并同名"未适配"影子条目（经典蓝牙 MAC ≠ 广播 MAC 时系统枚举另登记的）；
        // 排除 st 自己，避免把正在更新的条目删掉
        var merged = false;
        foreach (var shadow in _devices.Values
                     .Where(d => !d.IsAdapted && !ReferenceEquals(d, st) &&
                                 d.Name.Length > 0 &&
                                 d.Name.Equals(st.Name, StringComparison.OrdinalIgnoreCase))
                     .ToList())
        {
            _devices.Remove(shadow.Mac);
            _knownMacs.Remove(shadow.Mac);
            if (ReferenceEquals(shadow, _active)) _active = null;
            merged = true;
        }

        if (isNew || merged || upgraded)
        {
            PersistKnownDevices();   // 名单变化落盘
            UpdateSwitcherVisibility();
        }

        if (_active is null)
            SetActive(st);   // 第一台设备自动选中

        if (!ReferenceEquals(st, _active))
        {
            UpdateSwitcherVisibility();
            return;          // 非当前设备：只记录状态，不渲染
        }

        ApplyRing(LeftRing, left);
        ApplyRing(RightRing, right);
        ApplyRing(CaseRing, cse);

        RefreshStats();
        RefreshAliveState();
        _onUpdateApplied?.Invoke(u);
    }

    private DeviceState GetOrAdd(string mac)
    {
        if (!_devices.TryGetValue(mac, out var st))
            _devices[mac] = st = new DeviceState { Mac = mac };
        return st;
    }

    /// <summary>切换当前显示的设备，立即按该设备状态重绘。</summary>
    private void SetActive(DeviceState st)
    {
        _active = st;
        DeviceNameText.Text = st.Name.Length > 0 ? st.Name : "正在识别…";
        MacText.Text = st.Mac;
        UnadaptedHint.Visibility = st.IsAdapted ? Visibility.Collapsed : Visibility.Visible;
        SetRingInstant(LeftRing, st.Left);
        SetRingInstant(RightRing, st.Right);
        SetRingInstant(CaseRing, st.Case);
        RefreshAliveState();
        RefreshStats();
        ProbeSystemBattery();   // 切换设备立即读一次系统电量，不等 15 秒心跳
    }

    private static string InCaseText(bool? inCase) => inCase switch
    {
        null => "--",
        true => "充电中",
        false => "使用中",
    };

    /// <summary>带抖动抑制的电量值过滤；返回 null 表示维持现状。</summary>
    private static int? Filter(ref (int Value, DateTime Time, int Suppressed)? state, int? incoming)
    {
        if (incoming is null) return null; // 无效帧：维持旧值
        int v = incoming.Value;
        var now = DateTime.Now;

        if (state is { } prev && now - prev.Time < JumpWindow &&
            Math.Abs(v - prev.Value) > JumpThreshold && prev.Suppressed < 4)
        {
            state = (prev.Value, prev.Time, prev.Suppressed + 1);
            return null; // 视为瞬态，忽略
        }

        state = (v, now, 0);
        return v;
    }

    private void ApplyRing(BatteryRing ring, int? value)
    {
        if (value is null)
        {
            // 保持旧值（或未知态）
            if (ring.Level < 0) ring.RingColor = UnknownColor;
            return;
        }
        ring.RingColor = ColorFor(value.Value);
        ring.AnimateTo(value.Value);
    }

    private static Color ColorFor(int v) => ThemeManager.ColorFor(v);

    // ---------- 存活状态 ----------

    private void RefreshAliveState()
    {
        var st = _active;
        bool unadapted = st is { IsAdapted: false };
        // 断开判定：广播新鲜 OR 系统蓝牙仍保持连接（合盖后广播立停，但系统
        // ACL 会保持几秒；两者都失去才算断开，避免慢判/误判）
        bool hasData = st is not null && st.LastSeen != DateTime.MinValue;
        bool fresh = hasData && (DateTime.Now - st!.LastSeen).TotalSeconds <= StaleAfterSeconds;
        // 广播停了不代表断开（耳机连着电脑用时就不再广播）。系统连接判定两路：
        // Win32 蓝牙枚举（部分耳机查不到）+ 音频端点（耳机能出声就一定在）
        var probeName = st is { Name.Length: > 0 } ? st.Name : null;
        bool systemConnected = hasData && !fresh && (
            BtConnectionProbe.IsConnected(st!.Mac, probeName) ||
            AudioEndpointProbe.HasActiveEndpoint(probeName));
        bool alive = fresh || systemConnected;

        LiveDot.Fill = new SolidColorBrush(alive ? GoodColor : UnknownColor);
        StartPulse(alive);

        StateText.Text = unadapted
            ? (alive ? "已连接 · 该机型不支持分耳电量" : "未连接")
            : !hasData
                ? "请打开充电仓盖"
                : alive
                    ? fresh && st!.Rssi is { } rssi
                        ? $"实时更新 · 信号 {rssi} dBm"
                        : "蓝牙保持连接 · 等待新广播"
                    : "信号丢失 · 请打开仓盖刷新电量";

        CardsGrid.Opacity = hasData && !alive ? 0.45 : 1.0;

        if (hasData)
        {
            // 通道渲染：数据过期即回未知态「--」，不挂旧值（旧电量会误导）。
            // 广播新鲜期间单通道超时=这一路真离线（灰显+「离线」）；
            // 广播整体停止=数据过期（设备可能仍连着），只回「--」不标离线。
            bool leftFresh = RenderChannel(LeftCard, LeftStatusText, LeftBolt, st!.Left, st.LeftInCase, fresh);
            bool rightFresh = RenderChannel(RightCard, RightStatusText, RightBolt, st.Right, st.RightInCase, fresh);
            if (!leftFresh) { LeftRing.SetInstant(-1); LeftRing.RingColor = UnknownColor; }
            if (!rightFresh) { RightRing.SetInstant(-1); RightRing.RingColor = UnknownColor; }

            if (st.Case is { } c)
            {
                bool caseOnline = (DateTime.Now - c.Time).TotalSeconds <= ChannelFreshSeconds;
                CaseCard.Opacity = 1.0;
                if (caseOnline)
                {
                    CaseStatusText.Text = "在线";   // 电量数字在圆环上已显示，状态行不重复
                }
                else
                {
                    CaseStatusText.Text = fresh ? "离线" : "--";
                    CaseRing.SetInstant(-1);
                    CaseRing.RingColor = UnknownColor;
                }
            }
            else if (!fresh)
            {
                CaseStatusText.Text = "--";
            }
        }

        if (!hasData || !alive)
            CaseStatusText.Text = unadapted ? "--" : "等待广播";

        // 连接/断开状态变化时通知托盘（断开后托盘悬浮提示不再挂旧电量）
        if (_lastAlive != alive)
        {
            _lastAlive = alive;
            _onAliveChanged?.Invoke(alive);
        }

        if (st is not null) RefreshSystemBatteryText(st, alive);
        UpdateSwitcherVisibility();
    }

    // ---------- 系统电量兜底（HFP/AVRCP 上报，蓝牙设置页同源） ----------

    /// <summary>每 15 秒读一次当前设备的系统级电量，广播暂停/未适配时兜底显示。</summary>
    private void ProbeSystemBattery()
    {
        if (_active is not { } st) return;
        st.SystemBattery = SystemBatteryProbe.GetLevel(st.Mac);
        _history.RecordSystem(st.Mac, st.SystemBattery);   // 广播停止期间保持历史连续
        RefreshAliveState();   // 重新渲染（alive 状态可能未变，但电量值更新了）
    }

    /// <summary>
    /// 系统电量行：仅在设备仍连接且广播数据不新鲜时显示。
    /// 用 BroadcastSeen（而非 LastSeen）判定广播新鲜度——未适配设备的 LastSeen
    /// 被连接心跳持续刷新，若用它判定，未适配设备永远无法显示系统电量。
    /// 设备彻底断连后系统电量是过时旧值（耳机在仓内可能已充到更高），不显示。
    /// </summary>
    private void RefreshSystemBatteryText(DeviceState st, bool alive)
    {
        // 只在完全没获取到数据时兜底：三个通道都没有新鲜值（从未见过值，
        // 或数据已过期回「--」）；任一通道还在实时更新就不显示，避免信息重复
        bool anyFreshChannel = IsChannelFresh(st.Left) || IsChannelFresh(st.Right) ||
                               IsChannelFresh(st.Case);
        if (alive && st.SystemBattery is { } lv && !anyFreshChannel)
        {
            SystemBatteryText.Text = st.IsAdapted
                ? $"系统电量 {lv}% · 放入充电仓重新开盖可刷新"
                : $"系统电量 {lv}%（该机型仅支持整机电量）";
            SystemBatteryText.Visibility = Visibility.Visible;
        }
        else
        {
            SystemBatteryText.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>通道数据是否仍然新鲜（3 秒内有有效值，卡片显示窗口）。</summary>
    private static bool IsChannelFresh((int Value, DateTime Time, int Suppressed)? s) =>
        s is { } v && (DateTime.Now - v.Time).TotalSeconds <= ChannelFreshSeconds;

    /// <summary>
    /// 单通道渲染，返回该通道数据是否新鲜（供调用方同步电量圆环）。
    /// 从未见过值保持未知态（"--"）；数据过期不挂旧值——广播新鲜期间单独
    /// 超时=这一路真离线（灰显+「离线」），广播整体停止=数据过期只回「--」
    /// （设备可能仍连着，不算离线）。
    /// </summary>
    private static bool RenderChannel(Border card, TextBlock status, System.Windows.Shapes.Path bolt,
        (int Value, DateTime Time, int Suppressed)? state, bool? inCase, bool judgeOffline)
    {
        bolt.Visibility = Visibility.Collapsed;
        if (state is null)
        {
            card.Opacity = 1.0;
            status.Text = "--";
            return false;
        }

        bool fresh = IsChannelFresh(state);
        if (!fresh)
        {
            card.Opacity = judgeOffline ? 0.45 : 1.0;
            status.Text = judgeOffline ? "离线" : "--";
            return false;
        }

        card.Opacity = 1.0;
        status.Text = InCaseText(inCase);
        bolt.Visibility = inCase == true ? Visibility.Visible : Visibility.Collapsed;
        return true;
    }

    private static void SetRingInstant(BatteryRing ring, (int Value, DateTime Time, int Suppressed)? state)
    {
        if (IsChannelFresh(state))
        {
            ring.RingColor = ColorFor(state!.Value.Value);
            ring.SetInstant(state.Value.Value);
        }
        else
        {
            ring.SetInstant(-1);
            ring.RingColor = UnknownColor;
        }
    }

    private void StartPulse(bool animate)
    {
        if (!animate)
        {
            LiveDot.BeginAnimation(OpacityProperty, null);
            LiveDot.Opacity = 1;
            return;
        }
        var anim = new DoubleAnimation(1, 0.25, TimeSpan.FromMilliseconds(700))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        };
        LiveDot.BeginAnimation(OpacityProperty, anim);
    }

    // ---------- 窗口行为 ----------

    private void PlayEntrance()
    {
        RootBorder.Opacity = 0;
        RootTranslate.Y = 18;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        var slide = new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        RootBorder.BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 快速点击时可能抛异常，忽略 */ }
        }
    }

    private void MinButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    // ---------- 图钉置顶（微信式） ----------

    private void PinButton_Click(object sender, RoutedEventArgs e)
    {
        _config.TopMost = !_config.TopMost;
        _config.Save();
        ApplyPinState();
    }

    /// <summary>应用置顶状态：窗口 Topmost + 图钉图标/提示切换（置顶=实心钉橙色）。</summary>
    private void ApplyPinState()
    {
        bool pinned = _config.TopMost;
        Topmost = pinned;
        PinButton.Content = pinned ? "\uE841" : "\uE718";
        PinButton.Foreground = new SolidColorBrush(
            pinned ? Color.FromRgb(0xE8, 0x7A, 0x3E) : Color.FromRgb(0x8F, 0x8F, 0x98));
        PinButton.ToolTip = pinned ? "取消置顶" : "窗口置顶";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray() => Hide();

    private void RebindButton_Click(object sender, RoutedEventArgs e)
    {
        _watcher.Unbind();
        _devices.Clear();
        _knownMacs.Clear();
        _config.KnownDevices = null;
        _config.Save();
        _active = null;
        DevicePopup.IsOpen = false;

        DeviceNameText.Text = "正在搜索…";
        MacText.Text = "";
        UnadaptedHint.Visibility = Visibility.Collapsed;
        SetRingInstant(LeftRing, null);
        SetRingInstant(RightRing, null);
        SetRingInstant(CaseRing, null);
        LeftBolt.Visibility = RightBolt.Visibility = Visibility.Collapsed;
        LeftStatusText.Text = RightStatusText.Text = CaseStatusText.Text = "--";
        LeftCard.Opacity = RightCard.Opacity = CaseCard.Opacity = 1.0;
        CardsGrid.Opacity = 1.0;
        UpdateSwitcherVisibility();
        RefreshAliveState();
    }

    // ---------- 多设备切换 ----------

    /// <summary>
    /// 切换列表内容：当前设备 + 配置名单里的设备（常驻）+ 短窗口内见过的路人设备。
    /// </summary>
    private List<DeviceState> RecentDevices() =>
        _devices.Values
            .Where(d => ReferenceEquals(d, _active) ||
                        _knownMacs.Contains(d.Mac) ||
                        DateTime.Now - d.LastSeen <= DeviceListWindow)
            .OrderByDescending(d => d.LastSeen)
            .ToList();

    /// <summary>启动时从配置恢复设备名单（跨重启，"不同时段连接"的耳机也能切回）。</summary>
    private void LoadKnownDevices(AppConfig config)
    {
        if (config.KnownDevices is null) return;
        foreach (var k in config.KnownDevices)
        {
            if (string.IsNullOrWhiteSpace(k.Mac) || _devices.ContainsKey(k.Mac)) continue;
            _devices[k.Mac] = new DeviceState
            {
                Mac = k.Mac,
                Name = k.Name ?? "",
                LastSeen = k.LastSeen == default ? DateTime.MinValue : k.LastSeen,
                IsAdapted = k.Adapted,
            };
            _knownMacs.Add(k.Mac);
        }
        UpdateSwitcherVisibility();
    }

    /// <summary>
    /// 把设备名单写入配置（退出时和新设备出现时调用）。
    /// 上限 8 条；信号过弱的路人耳机（可能是邻居的）不入名单。
    /// </summary>
    public void PersistKnownDevices()
    {
        const int persistMinRssi = -85;
        var list = _devices.Values
            .Where(d => ReferenceEquals(d, _active) ||
                        d.Rssi is null || d.Rssi >= persistMinRssi)
            .OrderByDescending(d => d.LastSeen)
            .Take(8)
            .Select(d => new KnownDeviceEntry
            {
                Mac = d.Mac,
                Name = d.Name,
                LastSeen = d.LastSeen == DateTime.MinValue ? DateTime.Now : d.LastSeen,
                Adapted = d.IsAdapted,
            })
            .ToList();
        _config.KnownDevices = list;
        _knownMacs.Clear();
        foreach (var k in list) _knownMacs.Add(k.Mac);
        _config.Save();
    }

    /// <summary>
    /// 系统设备名与档案名的宽松匹配：同型号耳机在系统里的名字可能带后缀
    /// （如 "Mi Air2 SE Stereo"、"Mi Air2 SE（立体声）"）或被用户重命名，
    /// 全等会漏判成「未适配」。双向包含 + 大小写不敏感；短名（<3 字符）只允许正向。
    /// </summary>
    private static bool NameMatchesProfile(string deviceName, string profileName) =>
        deviceName.Contains(profileName, StringComparison.OrdinalIgnoreCase) ||
        (deviceName.Length >= 3 &&
         profileName.Contains(deviceName, StringComparison.OrdinalIgnoreCase));

    /// <summary>设备名是否对上任意已登记的档案（用于「同型号即已适配」判定）。</summary>
    private static bool IsKnownFormatName(string name) =>
        name.Length > 0 && XiaomiAdvParser.GetSupportedNames().Any(n => NameMatchesProfile(name, n));

    /// <summary>
    /// 探测系统里连着的音频设备：已配对蓝牙音频 ∩ 活动音频端点（端点名包含设备名）。
    /// 连着但广播格式未适配的耳机也补进设备列表（只有在线状态，无电量数据）。
    /// Win32 的 fConnected 对部分 TWS 不可靠，连接与否以音频端点为准；
    /// 未连接的配对设备不登记，已登记的靠心跳过期自然变灰。
    /// </summary>
    private void RefreshConnectedAudio()
    {
        List<(string Mac, string Name)> paired;
        List<string> endpoints;
        try
        {
            paired = BtConnectionProbe.ListPairedAudioDevices();
            endpoints = AudioEndpointProbe.ListActiveEndpointNames();
        }
        catch { return; }   // 枚举失败不影响主流程，下个周期再试

        var changed = false;
        foreach (var (mac, name) in paired)
        {
            if (name.Length == 0) continue;
            // 没有任何活动端点提到它 → 未连接
            if (!endpoints.Any(ep => ep.Contains(name, StringComparison.OrdinalIgnoreCase)))
                continue;
            // 已适配设备（广播能解析出显示名）不重复登记（宽匹配，兼容系统名带后缀）
            if (_devices.Values.Any(d => d.IsAdapted && d.Name.Length > 0 &&
                                         NameMatchesProfile(name, d.Name)))
                continue;
            if (_devices.TryGetValue(mac, out var st))
            {
                st.LastSeen = DateTime.Now;   // 已登记的设备：刷新在线心跳
                // 之前按未适配登记的条目，名字对上已知格式就补升级（无需等广播）
                if (!st.IsAdapted && IsKnownFormatName(name))
                {
                    st.IsAdapted = true;
                    changed = true;
                    if (ReferenceEquals(st, _active))
                        UnadaptedHint.Visibility = Visibility.Collapsed;
                }
            }
            else
            {
                // 名字能对上已登记的耳机格式（如 Air2 SE）就算已适配：
                // 只是目前没收到广播，显示 "--" 等数据，而不是误导性的「未适配」
                var knownFormat = IsKnownFormatName(name);
                _devices[mac] = st = new DeviceState
                {
                    Mac = mac,
                    Name = name,
                    LastSeen = DateTime.Now,
                    IsAdapted = knownFormat,
                };
                changed = true;
            }
            // 面板还没有任何设备在显示时，自动选中连着的耳机
            if (_active is null)
                SetActive(st);
        }
        if (changed)
        {
            PersistKnownDevices();
            UpdateSwitcherVisibility();
        }
    }

    private void UpdateSwitcherVisibility()
    {
        bool show = RecentDevices().Count >= 2;
        DeviceSwitchButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show && DevicePopup.IsOpen) DevicePopup.IsOpen = false;
    }

    private void DeviceSwitchButton_Click(object sender, RoutedEventArgs e)
    {
        DeviceList.ItemsSource = RecentDevices().Select(d => new DeviceOption
        {
            Mac = d.Mac,
            Name = d.Name.Length > 0 ? d.Name : d.Mac,
            Summary = Summarize(d),
            IsCurrent = ReferenceEquals(d, _active),
            Adapted = d.IsAdapted,
            Online = DateTime.Now - d.LastSeen <= TimeSpan.FromSeconds(StaleAfterSeconds),
        }).ToList();
        DevicePopup.IsOpen = true;
    }

    private void DeviceOption_Click(object sender, RoutedEventArgs e)
    {
        DevicePopup.IsOpen = false;
        if (((Button)sender).Tag is DeviceOption opt &&
            _devices.TryGetValue(opt.Mac, out var st))
        {
            _watcher.SelectDevice(opt.Mac);
            SetActive(st);
        }
    }

    private static string Summarize(DeviceState d)
    {
        if (!d.IsAdapted)
            return (DateTime.Now - d.LastSeen).TotalSeconds <= StaleAfterSeconds
                ? "已连接 · 不支持分耳电量"
                : "未连接 · 不支持分耳电量";
        string part((int Value, DateTime Time, int Suppressed)? s) =>
            IsChannelFresh(s) ? $"{s!.Value.Value}%" : "--";
        return $"左 {part(d.Left)} · 右 {part(d.Right)} · 仓 {part(d.Case)}";
    }

    /// <summary>设备切换列表中的一行。</summary>
    private sealed class DeviceOption
    {
        public required string Mac { get; init; }
        public required string Name { get; init; }
        public required string Summary { get; init; }
        public required bool IsCurrent { get; init; }
        public required bool Adapted { get; init; }
        public required bool Online { get; init; }

        public System.Windows.Media.Brush Dot =>
            new SolidColorBrush(Online ? GoodColor : UnknownColor);
        public string Badge => IsCurrent ? "当前" : !Adapted ? "不支持" : "";
        public Visibility BadgeVisibility =>
            Badge.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        AutoStartHelper.Set(AutoStartCheck.IsChecked == true);
    }

    private async void UpdateButton_Click(object sender, RoutedEventArgs e)
    {
        // 已检查到新版本：再次点击打开下载页
        if (_updateInfo is { } info)
        {
            try
            {
                Process.Start(new ProcessStartInfo(info.Url) { UseShellExecute = true });
            }
            catch { /* 打开浏览器失败忽略 */ }
            return;
        }

        UpdateButton.IsEnabled = false;
        UpdateButton.Content = "检查中…";
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update is null)
            {
                FlashUpdateButton("已是最新");
            }
            else
            {
                _updateInfo = update;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = $"新版本 v{update.Version} ↑";
            }
        }
        catch
        {
            FlashUpdateButton("检查失败");
        }
    }

    private void FlashUpdateButton(string text)
    {
        UpdateButton.Content = text;
        UpdateButton.IsEnabled = true;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            UpdateButton.Content = "检查更新";
        };
        timer.Start();
    }

    private void AdaptButton_Click(object sender, RoutedEventArgs e) => ShowAdapterList(instant: false);

    // ==================== 设置视图（同窗口内跳转） ====================

    // 勿扰时段预设（循环切换）：开始小时 / 结束小时（支持跨零点）
    private static readonly (int Start, int End)[] QuietPresets =
    {
        (22, 8), (23, 7), (0, 6), (21, 9), (13, 14),
    };

    private void InitSettingsControls()
    {
        LowThresholdSlider.Value = Math.Clamp(_config.LowBatteryThreshold, 10, 50);
        LowThresholdValue.Text = $"{(int)LowThresholdSlider.Value}%";
        QuietHoursCheck.IsChecked = _config.QuietHoursEnabled;
        RefreshQuietHoursLabel();
        DropAlertCheck.IsChecked = _config.SuddenDropAlert;
        PopupCheck.IsChecked = _config.OpenLidPopup;
        CooldownSlider.Value = Math.Clamp(_config.PopupCooldownMinutes, 1, 30);
        CooldownValue.Text = $"{(int)CooldownSlider.Value} 分钟";
        MiniBarCheck.IsChecked = _config.MiniBarEnabled;
    }

    private void SettingsButton_Click(object sender, RoutedEventArgs e) => ShowSettingsView();

    private void SettingsBackButton_Click(object sender, RoutedEventArgs e) => ShowMainView();

    /// <summary>托盘「设置」入口：显示主面板后直接跳到设置视图。</summary>
    public void ShowSettingsView()
    {
        _adapterPage = -1;
        MainView.Visibility = Visibility.Collapsed;
        AdapterView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Visible;
        SettingsViews.Opacity = 0;
        SettingsTranslate.Y = 14;
        SettingsViews.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        SettingsTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } });
        AnimateWindowHeight(SettingsViewHeight, instant: false);
    }

    private void LowThresholdSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        LowThresholdValue.Text = $"{(int)e.NewValue}%";
        _config.LowBatteryThreshold = (int)e.NewValue;
        _config.Save();
    }

    private void QuietHoursCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.QuietHoursEnabled = QuietHoursCheck.IsChecked == true;
        _config.Save();
    }

    private void QuietHoursButton_Click(object sender, RoutedEventArgs e)
    {
        // 循环切换预设时段；先确保勿扰开启
        QuietHoursCheck.IsChecked = true;
        var cur = (_config.QuietStartHour, _config.QuietEndHour);
        var idx = Array.FindIndex(QuietPresets, p => p.Start == cur.Item1 && p.End == cur.Item2);
        var next = QuietPresets[(idx + 1) % QuietPresets.Length];
        _config.QuietStartHour = next.Start;
        _config.QuietEndHour = next.End;
        _config.Save();
        RefreshQuietHoursLabel();
    }

    private void RefreshQuietHoursLabel()
    {
        QuietHoursButton.Content = $"{_config.QuietStartHour:00}:00 – {_config.QuietEndHour:00}:00";
        QuietHoursButton.Opacity = _config.QuietHoursEnabled ? 1.0 : 0.45;
    }

    private void DropAlertCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.SuddenDropAlert = DropAlertCheck.IsChecked == true;
        _config.Save();
    }

    private void PopupCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.OpenLidPopup = PopupCheck.IsChecked == true;
        _config.Save();
    }

    private void CooldownSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_initialized) return;
        CooldownValue.Text = $"{(int)e.NewValue} 分钟";
        _config.PopupCooldownMinutes = (int)e.NewValue;
        _config.Save();
    }

    private void MiniBarCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.MiniBarEnabled = MiniBarCheck.IsChecked == true;
        _config.Save();
        _onMiniBarToggle?.Invoke(_config.MiniBarEnabled);
    }

    /// <summary>托盘切换悬浮条后同步设置页开关（不触发事件）。</summary>
    public void SyncMiniBarCheck() => MiniBarCheck.IsChecked = _config.MiniBarEnabled;

    // ==================== 电量统计（今日已用 / 预计可用 / 曲线） ====================

    private void RefreshStats()
    {
        if (_active is not { } st || st.Mac.Length == 0) return;
        BatteryDayStats stats;
        try { stats = _history.GetTodayStats(st.Mac); }
        catch { return; }

        StatsUsedText.Text = stats.UsedPercent > 0
            ? $"{(int)Math.Round(stats.UsedPercent)}%"
            : "--";

        bool charging = stats.EstimateText == "充电中";
        StatsEstimateText.Text = charging ? "充电中" : $"预计可用 {stats.EstimateText}";
        StatsEstimateText.SetResourceReference(TextBlock.ForegroundProperty,
            charging ? "T.Accent" : "T.TextGreen");

        // 曲线：当日有效电量序列映射到 150×34
        if (stats.Curve.Count >= 2)
        {
            const double w = 150, h = 34;
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(new Point(0, h), false, false);
                foreach (var (x, y) in stats.Curve)
                    ctx.LineTo(new Point(x / 100.0 * w, h - y / 100.0 * h), true, false);
            }
            StatsCurve.Data = geo;
            StatsCurve.Visibility = Visibility.Visible;
        }
        else
        {
            StatsCurve.Visibility = Visibility.Collapsed;
        }
    }

    // ==================== 适配视图：名单 + 分页向导（窗口内跳转） ====================

    private const string IssueUrlBase = "https://github.com/Furry09shou/ronghui-earbuds/issues/new";
    private const double MainViewHeight = 508;
    private const double SettingsViewHeight = 640;
    private const double AdapterListHeight = 480;
    private const double AdapterWizardHeight = 560;
    private const string ModelPlaceholder = "例如：Redmi Buds 5";
    private static Color PlaceholderColor => ThemeManager.GetColor("T.TextDim");
    private static Color InputColor => ThemeManager.GetColor("T.TextPrimary");

    // 耳机形态（写入采集元数据，供分析时区分电量字段数量）
    private const string LayoutDualCase = "dual_case";
    private const string LayoutDualNoCase = "dual_nocase";
    private const string LayoutMono = "mono";

    // 双耳 + 充电仓（仓也广播电量的常见真无线）
    private static readonly (string Title, string Detail)[] StepsDual =
    {
        ("双耳入仓，开盖等 10 秒", "把两只耳机都放回充电仓，保持仓盖打开，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。"),
        ("取出左耳，等 10 秒", "把左耳从仓中取出（戴或不戴都可以），右耳留在仓内，等待约 10 秒。"),
        ("左耳放回，等 10 秒", "把左耳放回仓内，等待约 10 秒。"),
        ("取出右耳，等 10 秒", "把右耳从仓中取出，左耳留在仓内，等待约 10 秒。"),
        ("右耳放回，完成采集", "把右耳放回仓内，等待约 10 秒，然后点击「完成并上传」。"),
    };

    // 仅双耳（无仓或仓不广播电量）：动作兼容两种耳机——开关机或入仓出仓均可
    private static readonly (string Title, string Detail)[] StepsDualNoCase =
    {
        ("双耳就位，等 10 秒", "把两只耳机打开电源，或从充电仓取出（如果耳机有仓），放在电脑旁边，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。这类耳机的充电仓不会提供电量数据（或没有充电仓），全程只需关注左右两只耳机。"),
        ("隔离左耳，等 10 秒", "把左耳关机，或放回充电仓并合上仓盖（右耳保持在外），等待约 10 秒。"),
        ("左耳归队，等 10 秒", "把左耳重新开机，或从仓中取出，恢复双耳在外，等待约 10 秒。"),
        ("隔离右耳，等 10 秒", "把右耳关机，或放回充电仓并合上仓盖（左耳保持在外），等待约 10 秒。"),
        ("右耳归队，完成采集", "把右耳重新开机，或从仓中取出，双耳在外等待约 10 秒，然后点击「完成并上传」。数据应只有左右耳两个电量字段，没有充电仓电量。"),
    };

    // 仅单耳（无充电仓）：只有一只耳机，观察开机/使用/静置/重启的状态差别
    private static readonly (string Title, string Detail)[] StepsMono =
    {
        ("开机，靠近电脑等 10 秒", "打开耳机电源，放在电脑旁边，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。这类耳机没有充电仓，全程只需关注这一只耳机的数据。"),
        ("戴上使用，等 10 秒", "戴上耳机正常使用（播放或暂停都可以），等待约 10 秒。"),
        ("摘下静置，等 10 秒", "把耳机摘下来放在桌上（保持开机），等待约 10 秒。"),
        ("再戴上，等 10 秒", "再次戴上耳机使用，等待约 10 秒。"),
        ("重启耳机，完成采集", "把耳机关机，等约 5 秒后重新开机，再等待约 10 秒，然后点击「完成并上传」。"),
    };

    private readonly CaptureService _capture = new();
    private int _adapterPage = -1;      // -1=不在适配视图，0=型号页，1..5=动作步骤
    private bool _adapterRunning;
    private string _layout = LayoutDualCase;
    private int _captureShown = -1;
    private Border[]? _dots;

    private (string Title, string Detail)[] CurrentSteps() => _layout switch
    {
        LayoutMono => StepsMono,
        LayoutDualNoCase => StepsDualNoCase,
        _ => StepsDual,
    };

    private string SelectedLayout() =>
        LayoutOptionDualNoCase.IsChecked == true ? LayoutDualNoCase :
        LayoutOptionMono.IsChecked == true ? LayoutMono : LayoutDualCase;

    private void ShowAdapterList(bool instant)
    {
        _adapterPage = -1;
        AdapterTitle.Text = "适配名单";
        ModelList.ItemsSource = XiaomiAdvParser.GetSupportedNames();
        ShowOnlyAdapterPage(AdapterListPage);
        DotsRow.Visibility = Visibility.Collapsed;
        LiveBox.Visibility = Visibility.Collapsed;
        AdapterBackButton.Content = "取消";
        AdapterMainButtonText.Text = "适配新耳机 ›";
        AnimateWindowHeight(AdapterListHeight, instant);
        AnimateAdapterView(instant);
    }

    private void ShowAdapterPage(int page)
    {
        _adapterPage = page;
        AdapterTitle.Text = "适配新耳机";

        if (page >= 1)
        {
            // 步骤标题与详情（按耳机形态区分文案）
            var (title, detail) = CurrentSteps()[page - 1];
            ((TextBlock)FindName($"StepTitle{page}")!).Text = title;
            ((TextBlock)FindName($"StepDetail{page}")!).Text = detail;
        }

        ShowOnlyAdapterPage(page == 0 ? (UIElement)AdapterPage0 : (UIElement)FindName($"AdapterPage{page}")!);
        DotsRow.Visibility = Visibility.Visible;
        BuildAdapterDots(page);
        LiveBox.Visibility = page >= 1 ? Visibility.Visible : Visibility.Collapsed;
        AdapterBackButton.Content = _adapterRunning ? "取消" : "‹ 返回";
        AdapterMainButtonText.Text = page switch
        {
            0 => "开始采集",
            5 => "完成并上传",
            _ => $"下一步（{page}/5）",
        };
        AnimateWindowHeight(AdapterWizardHeight, instant: false);
        AnimateAdapterView(instant: false);
    }

    private void ShowOnlyAdapterPage(UIElement current)
    {
        MainView.Visibility = Visibility.Collapsed;
        AdapterView.Visibility = Visibility.Visible;
        AdapterListPage.Visibility = ReferenceEquals(current, AdapterListPage)
            ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i <= 5; i++)
        {
            var page = (UIElement)FindName($"AdapterPage{i}")!;
            page.Visibility = ReferenceEquals(page, current) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void ShowMainView()
    {
        _adapterPage = -1;
        AdapterView.Visibility = Visibility.Collapsed;
        SettingsView.Visibility = Visibility.Collapsed;
        MainView.Visibility = Visibility.Visible;
        AnimateWindowHeight(MainViewHeight, instant: false);
    }

    private void BuildAdapterDots(int active)
    {
        if (_dots is null)
        {
            _dots = new Border[6];
            for (var i = 0; i < 6; i++)
            {
                _dots[i] = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(4, 0, 4, 0),
                };
                DotsRow.Children.Add(_dots[i]);
            }
        }
        for (var i = 0; i < 6; i++)
        {
            _dots[i].Background = new SolidColorBrush(i <= active
                ? ThemeManager.GetColor("T.Accent")
                : ThemeManager.GetColor("T.InputBorder"));
        }
    }

    private void AnimateAdapterView(bool instant)
    {
        if (instant)
        {
            AdapterViews.Opacity = 1;
            AdapterTranslate.Y = 0;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        AdapterViews.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
        AdapterTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240)) { EasingFunction = ease });
    }

    private void AnimateWindowHeight(double target, bool instant)
    {
        // 底边固定：加高时上移 Top，避免底栏超出屏幕
        var delta = target - ActualHeight;
        var newTop = Math.Max(SystemParameters.WorkArea.Top, Top - delta);

        if (instant || Math.Abs(Height - target) < 0.5)
        {
            Height = target;
            Top = newTop;
            return;
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        BeginAnimation(HeightProperty,
            new DoubleAnimation(Height, target, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
        BeginAnimation(TopProperty,
            new DoubleAnimation(Top, newTop, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease });
    }

    private void AdapterBackButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adapterPage < 0 || (_adapterPage == 0 && !_adapterRunning))
        {
            ShowMainView();
            return;
        }
        TryCancelAdapter();
    }

    private void TryCancelAdapter()
    {
        if (!_adapterRunning)
        {
            ShowAdapterList(instant: false);
            return;
        }
        if (MessageBox.Show(this, "采集进行中，确定取消并丢弃已采集的数据吗？", "适配新耳机",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        _adapterRunning = false;
        _capture.Dispose();
        ShowMainView();
    }

    private void AdapterMainButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adapterPage < 0)
        {
            ShowAdapterPage(0);
            return;
        }

        if (_adapterPage == 0)
        {
            var model = ModelBox.Text.Trim();
            if (model.Length == 0 || model == ModelPlaceholder)
            {
                ModelHint.Visibility = Visibility.Visible;
                ModelBox.Focus();
                return;
            }
            ModelHint.Visibility = Visibility.Collapsed;
            _layout = SelectedLayout();
            _capture.Start();
            _adapterRunning = true;
            _capture.AddMarker("开始采集");
            ShowAdapterPage(1);
            return;
        }

        _capture.AddMarker($"完成阶段{_adapterPage}:{CurrentSteps()[_adapterPage - 1].Title}");
        if (_adapterPage < 5)
        {
            ShowAdapterPage(_adapterPage + 1);
        }
        else
        {
            FinishCapture();
        }
    }

    private void RefreshCaptureLive()
    {
        if (_capture.Count == _captureShown) return;
        _captureShown = _capture.Count;

        var cids = _capture.CompanyIds.Count == 0
            ? "—"
            : string.Join(", ", _capture.CompanyIds.Select(c => $"0x{c:X4}"));
        var keys = _capture.ProductKeys.Count == 0
            ? "—"
            : string.Join(" / ", _capture.ProductKeys);

        LiveText.Text = _adapterRunning
            ? $"已捕获 {_capture.Count} 包 · 公司代号: {cids} · 产品标识: {keys}"
            : "等待开始采集…";
    }

    private void ModelBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ModelBox.Text == ModelPlaceholder)
        {
            ModelBox.Text = "";
            ModelBox.Foreground = new SolidColorBrush(InputColor);
        }
    }

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ModelBox.Text.Trim().Length == 0)
        {
            ModelBox.Text = ModelPlaceholder;
            ModelBox.Foreground = new SolidColorBrush(PlaceholderColor);
        }
    }

    private void FinishCapture()
    {
        AdapterMainButton.IsEnabled = false;
        try
        {
            var model = ModelBox.Text.Trim();
            if (model.Length == 0) model = "未知型号";
            _capture.AddMarker("结束采集");
            var (jsonl, zip) = _capture.Export(model, _layout);

            // 本地差分分析：自动生成候选布局报告，随 Issue 一起提交
            string analysis;
            try { analysis = FormatAnalyzer.AnalyzeFile(jsonl); }
            catch { analysis = "本地分析失败（数据仍完整保留，可人工分析）。"; }
            var analysisPath = Path.ChangeExtension(jsonl, ".analysis.txt");
            File.WriteAllText(analysisPath, analysis);

            // 原始数据（jsonl）+ 分析报告一起打进 zip，本地亦永久保留两份
            using (var fs = new FileStream(zip, FileMode.Open, FileAccess.ReadWrite))
            using (var archive = new ZipArchive(fs, ZipArchiveMode.Update))
            {
                archive.CreateEntryFromFile(analysisPath, Path.GetFileName(analysisPath));
            }

            var body = $"机型：{model}\n" +
                       $"耳机形态：{CaptureService.LayoutLabel(_layout)}\n" +
                       $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n" +
                       $"捕获包数：{_capture.Count}\n" +
                       $"产品标识：{string.Join(" / ", _capture.ProductKeys)}\n\n" +
                       $"### 本地差分分析报告\n```text\n{analysis}\n```\n\n" +
                       $"请把数据文件（jsonl 与 zip）拖进评论（由应用内向导生成）：\n`{zip}`";
            var url = $"{IssueUrlBase}?title={Uri.EscapeDataString($"适配新耳机：{model}")}" +
                      $"&body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            MessageBox.Show(this,
                $"采集完成，共 {_capture.Count} 包。\n\n" +
                $"本地分析已生成候选布局报告（{Path.GetFileName(analysisPath)}），" +
                "已随 Issue 预填，通常无需人工逐包分析。\n\n" +
                "浏览器已打开 GitHub Issue 页面，请把该 zip 文件拖进评论框提交。" +
                "开发者复核后登记解析档案，随软件更新加入你的机型支持。",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            _adapterRunning = false;
            _capture.Dispose();
            ShowMainView();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            AdapterMainButton.IsEnabled = true;
        }
    }

    /// <summary>退出应用时释放采集器（App.ExitApp 调用）。</summary>
    public void DisposeCapture() => _capture.Dispose();

    public void PersistPosition(AppConfig config)
    {
        config.WindowLeft = Left;
        config.WindowTop = Top;
        config.Save();
    }

    private void RestorePosition(AppConfig config)
    {
        var wa = SystemParameters.WorkArea;
        if (config.WindowLeft is { } l && config.WindowTop is { } t &&
            l > -Width && l < wa.Right && t > -20 && t < wa.Bottom)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = wa.Right - Width - 18;
            Top = wa.Bottom - Height - 18;
        }
    }
}
