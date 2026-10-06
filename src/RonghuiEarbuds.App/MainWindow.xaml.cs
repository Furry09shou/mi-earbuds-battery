using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using RonghuiEarbuds.App.Core;
using RonghuiEarbuds.App.UI;
using Shapes = System.Windows.Shapes;

namespace RonghuiEarbuds.App;

public partial class MainWindow : Window
{
    private readonly EarbudsWatcher _watcher;
    private readonly AppConfig _config;
    private readonly Action<EarbudsUpdate>? _onUpdateApplied;
    private readonly Action<bool>? _onAliveChanged;
    private readonly Action<bool>? _onMiniBarToggle;
    private readonly Action<string, string>? _onActiveDeviceName;
    private readonly Action<bool>? _onHotKeyToggle;
    private readonly DispatcherTimer _aliveTimer;
    private readonly BatteryHistoryStore _history;
    private readonly UsageStore _usage;
    private bool _lastAlive = true;
    // 型号输入框当前是否显示占位文案（语言切换时替换为新占位符）
    private bool _modelShowingPlaceholder = true;

    private bool _initialized;

    // 多设备：MAC → 观测状态（本会话内出现过的所有设备 + 配置里持久化的设备名单）
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    // 已持久化到配置的设备（切换列表常驻显示，不受 10 分钟窗口限制）
    private readonly HashSet<string> _knownMacs = new(StringComparer.OrdinalIgnoreCase);
    private DeviceState? _active;
    private int _probeTick;

    /// <summary>当前面板正在显示的设备 MAC（托盘提示只对该设备弹出）。</summary>
    public string? ActiveMac => _active?.Mac;

    /// <summary>当前关注设备的型号名（无名字时 null）。</summary>
    public string? ActiveDeviceName =>
        _active is { Name.Length: > 0 } st ? st.Name : null;

    /// <summary>当前关注设备的系统整机电量（悬浮条兜底显示用）。</summary>
    public int? ActiveSystemBattery => _active?.SystemBattery;

    /// <summary>指定设备的系统整机电量（悬浮条多行兜底 / 低电量系统兜底用）。
    /// 仅在连接心跳仍新鲜（ConnSeen ≤15 秒，连接枚举每 5 秒刷）时才可信：
    /// 断开后 BTHENUM 属性仍在但值是缓存旧值，IsDeviceAlive 的 60 秒宽限窗口
    /// 会让悬浮条继续显示过时电量，必须用更紧的连接心跳门控。</summary>
    public int? SystemBatteryOf(string mac)
    {
        if (!_devices.TryGetValue(mac, out var d)) return null;
        return d.ConnSeen != DateTime.MinValue &&
               (DateTime.Now - d.ConnSeen).TotalSeconds <= 15
            ? d.SystemBattery : null;
    }

    /// <summary>设备是否存活：LastSeen（广播或连接枚举心跳刷新）60 秒内有更新。
    /// 广播不可靠（连接播放停发、未适配机型从不广播），连接心跳才是存活依据。</summary>
    public bool IsDeviceAlive(string mac)
    {
        if (!_devices.TryGetValue(mac, out var st)) return false;
        var now = DateTime.Now;
        // 广播心跳（LastSeen）或连接心跳（ConnSeen）任一在 60 秒内即在线：
        // 连接播放/合盖期间广播停发、LastSeen 停走，但连接枚举每 5 秒仍在刷 ConnSeen
        return (st.LastSeen != DateTime.MinValue && (now - st.LastSeen).TotalSeconds <= 60) ||
               (st.ConnSeen != DateTime.MinValue && (now - st.ConnSeen).TotalSeconds <= 60);
    }

    /// <summary>悬浮条右键菜单用：值得列出的设备（已持久化名单 + 近期见过的设备）。</summary>
    public IReadOnlyList<(string Name, string Mac)> KnownDeviceList() =>
        _devices.Values
            .Where(d => d.Name.Length > 0 &&
                        (_knownMacs.Contains(d.Mac) ||
                         (DateTime.Now - d.LastSeen).TotalMinutes <= 10))
            .Select(d => (d.Name, d.Mac))
            .ToList();

    /// <summary>悬浮条菜单切换关注设备。</summary>
    public void SelectDeviceByMac(string mac)
    {
        if (_devices.TryGetValue(mac, out var st) && !ReferenceEquals(st, _active))
            SetActive(st);
    }

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

        /// <summary>最后一次经连接枚举确认在线的时间（每 5 秒刷新）。
        /// 已适配设备的 LastSeen 只记「最后广播」，连接播放/合盖期间停走——
        /// 在线判定与系统电量兜底必须兼看 ConnSeen，否则连着的设备会被误判离线。</summary>
        public DateTime ConnSeen = DateTime.MinValue;

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
        Action<bool>? onMiniBarToggle = null, Action<string, string>? onActiveDeviceName = null,
        Action<bool>? onHotKeyToggle = null)
    {
        InitializeComponent();
        _watcher = watcher;
        _config = config;
        _history = new BatteryHistoryStore(config);
        _usage = new UsageStore();
        UsageStore.Cleanup();
        _onUpdateApplied = onUpdateApplied;
        _onAliveChanged = onAliveChanged;
        _onMiniBarToggle = onMiniBarToggle;
        _onActiveDeviceName = onActiveDeviceName;
        _onHotKeyToggle = onHotKeyToggle;

        RestorePosition(config);

        _capture.Ticked += () => Dispatcher.Invoke(RefreshCaptureLive);

        AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();
        InitSettingsControls();
        _initialized = true;

        // 语言切换即时生效：重建/重设界面文案（含托盘与悬浮条各自的订阅）
        L.Changed += () => Dispatcher.Invoke(ApplyLanguage);
        ApplyLanguage();

        // 设备名跑马灯：名字区宽度变化（窗口尺寸/箭头显隐）时重新计算滑动
        DeviceNameHost.SizeChanged += (_, _) => UpdateDeviceNameMarquee();

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
            // 在线的设备计入佩戴时长（每秒累加，UsageStore 攒满 60 秒才落盘）
            foreach (var d in _devices.Values)
                if (IsDeviceAlive(d.Mac)) _usage.AddSecond(d.Mac);
            // 每 5 秒枚举一次系统连接的音频设备，把未适配格式的耳机补进列表
            if (++_probeTick % 5 == 0) RefreshConnectedAudio();
            // 每 15 秒读一次系统电量（HFP/AVRCP 上报），作广播暂停时的兜底显示
            if (_probeTick % 15 == 0) ProbeSystemBattery();
            // 每 30 秒刷新一次用量统计（预计可用时长随时间推进而变化）
            if (_probeTick % 30 == 0) RefreshStats();
            // 每 2 秒向小组件面板广播一次状态（内容没变不写盘）
            if (_probeTick % 2 == 0) WidgetStatePublisher.Publish(CollectWidgetState());
        };
        _aliveTimer.Start();

        // 分享菜单行文案在模板应用后才能改写（Popup 首次打开时设置）
        StatsSharePopup.Opened += (_, _) =>
        {
            SetRowText(ExportImageItem, "stats.exportImage");
            SetRowText(CopyTextItem, "stats.copyText");
            SetRowText(ExportWeekItem, "stats.exportWeek");
            SetRowText(CopyWeekItem, "stats.copyWeek");
        };

        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartPulse(false);
        };
    }

    // ---------- 数据 ----------

    /// <summary>收集全部可见设备的状态，供 Windows 小组件面板显示。
    /// 名单口径与 KnownDeviceList 一致（已持久化名单 + 近 10 分钟见过的设备）。</summary>
    public WidgetStatePublisher.State CollectWidgetState()
    {
        var state = new WidgetStatePublisher.State();
        foreach (var d in _devices.Values)
        {
            if (d.Name.Length == 0 ||
                !(_knownMacs.Contains(d.Mac) || (DateTime.Now - d.LastSeen).TotalMinutes <= 10))
                continue;
            state.Devices.Add(new WidgetStatePublisher.DeviceInfo
            {
                Mac = d.Mac,
                Name = d.Name,
                Primary = ReferenceEquals(d, _active),
                Left = d.Left?.Value,
                Right = d.Right?.Value,
                Case = d.Case?.Value,
                LeftInCase = d.LeftInCase == true,
                RightInCase = d.RightInCase == true,
                LastBroadcastUtc = d.BroadcastSeen == DateTime.MinValue ? null : d.BroadcastSeen.ToUniversalTime(),
                ConnSeenUtc = d.ConnSeen == DateTime.MinValue ? null : d.ConnSeen.ToUniversalTime(),
                SystemBattery = SystemBatteryOf(d.Mac),
                IsAdapted = d.IsAdapted,
            });
        }
        // 无主选设备时指定第一台存活的，保证小组件有明确显示对象
        if (state.Devices.Count > 0 && !state.Devices.Any(d => d.Primary))
        {
            var first = state.Devices.FirstOrDefault(d => IsDeviceAlive(d.Mac)) ?? state.Devices[0];
            first.Primary = true;
        }
        return state;
    }

    private void ApplyUpdate(EarbudsUpdate u)
    {
        var isNew = !_devices.ContainsKey(u.Mac);
        var st = GetOrAdd(u.Mac);
        st.LastSeen = u.Timestamp;
        st.BroadcastSeen = u.Timestamp;
        st.Rssi = u.Rssi;
        var s = u.Snapshot;
        if (st.Name.Length == 0)
        {
            st.Name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Mac : u.DisplayName;
            _onActiveDeviceName?.Invoke(u.Mac, st.Name);   // 任何设备命名都推给悬浮条（多行显示）
        }

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

        // 改名合并：耳机在系统里被改名后，连接枚举的影子条目与广播显示名对不上，
        // 同名合并失效，导致同一副耳机出现两个条目、连接条目一直显示「未适配」。
        // 机型识别本就来自广播数据（productKey/型号 ID），与名字无关；
        // 这里在「连接中的未适配条目」唯一时自动视为同一副耳机合并。
        // 判据从严：条目从未收到过广播、名字对不上任何档案名、且当前正连着（ConnSeen 新鲜）；
        // 多于一个候选时不猜（避免把电量错绑到旁边其他耳机）。
        var renamed = _devices.Values
            .Where(d => !d.IsAdapted && !ReferenceEquals(d, st) &&
                        d.BroadcastSeen == DateTime.MinValue &&
                        d.Name.Length > 0 && !IsKnownFormatName(d.Name) &&
                        DateTime.Now - d.ConnSeen <= TimeSpan.FromSeconds(15))
            .ToList();
        if (renamed.Count == 1)
        {
            var shadow = renamed[0];
            st.Name = shadow.Name;   // 采用用户改的名字，与系统蓝牙设置一致，后续连接心跳按名字对上
            if (ReferenceEquals(st, _active))
            {
                DeviceNameText.Text = st.Name;
                Dispatcher.BeginInvoke(UpdateDeviceNameMarquee, DispatcherPriority.Render);
            }
            _onActiveDeviceName?.Invoke(u.Mac, st.Name);
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

    /// <summary>
    /// 设备名跑马灯：名字超出可用宽度时来回滑动，放得下则左对齐。
    /// 切换箭头固定在右侧独立列，长名字不再把它挤出窗口。
    /// 实现同悬浮条：Canvas 提供无限约束测量（Grid 测宽会被列宽截断），
    /// 文字宽度读 TextBlock.DesiredSize（Canvas 自身 DesiredSize 恒 0）。
    /// </summary>
    private void UpdateDeviceNameMarquee()
    {
        DeviceNameShift.X = 0;
        double viewW = DeviceNameHost.ActualWidth;
        double textW = DeviceNameText.DesiredSize.Width;
        if (viewW <= 0 || textW <= 0) return;

        DeviceNameCanvas.BeginAnimation(Canvas.LeftProperty, null);
        DeviceNameShift.BeginAnimation(TranslateTransform.XProperty, null);

        if (textW <= viewW + 0.5) return;   // 放得下：左对齐不动

        double overflow = textW - viewW + 12;   // 缓冲，让尾部完整滑入视野
        double dur = Math.Min(8, Math.Max(2.5, overflow / 24.0));   // 24 px/s 基速
        DeviceNameShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = TimeSpan.FromSeconds(dur),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        });
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
        DeviceNameText.Text = st.Name.Length > 0 ? st.Name : L.T("main.identifying");
        Dispatcher.BeginInvoke(UpdateDeviceNameMarquee, DispatcherPriority.Render);
        _onActiveDeviceName?.Invoke(st.Mac, st.Name.Length > 0 ? st.Name : "");
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
        true => L.T("state.charging"),
        false => L.T("state.inUse"),
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
            ? (alive ? L.T("state.connectedUnadapted") : L.T("state.disconnected"))
            : !hasData
                ? L.T("state.openLid")
                : alive
                    ? fresh && st!.Rssi is { } rssi
                        ? L.F("state.liveFmt", rssi)
                        : L.T("state.waitingBroadcast")
                    : L.T("state.signalLost");

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
                    CaseStatusText.Text = L.T("state.online");   // 电量数字在圆环上已显示，状态行不重复
                }
                else
                {
                    CaseStatusText.Text = fresh ? L.T("state.offline") : "--";
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
            CaseStatusText.Text = unadapted ? "--" : L.T("state.waitingCase");

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

    /// <summary>每 15 秒读一次系统级电量：所有在线设备都读（悬浮条多行兜底需要
    /// 每台设备的值，不只是关注设备），广播暂停/未适配时兜底显示。</summary>
    private void ProbeSystemBattery()
    {
        foreach (var st in _devices.Values)
        {
            // 离线设备跳过：BTHENUM 属性断连后仍在（旧值会误导），且 SystemBatteryOf
            // 的在线门控也挡住了它；关注设备无论在线与否都读，保持绿字行为不变
            if (!ReferenceEquals(st, _active) && !IsDeviceAlive(st.Mac)) continue;
            st.SystemBattery = SystemBatteryProbe.GetLevel(st.Mac);
            _history.RecordSystem(st.Mac, st.SystemBattery);   // 广播停止期间保持历史连续
        }
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
                ? L.F("state.sysBatteryFmt", lv)
                : L.F("state.sysBatteryUnadaptedFmt", lv);
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
            status.Text = judgeOffline ? L.T("state.offline") : "--";
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
        PinButton.ToolTip = pinned ? L.T("main.unpin") : L.T("main.pin");
    }

    // ---------- 中英双语：语言切换或启动时统一应用文案 ----------

    /// <summary>把当前语言（L.Lang）应用到主窗口全部静态文案；动态文案随各刷新方法重建。</summary>
    private void ApplyLanguage()
    {
        Title = L.T("main.title");
        LblAppTitle.Text = L.T("main.title");
        LblSettingsViewTitle.Text = L.T("main.settings");
        LblSettingsBtnText.Text = L.T("main.settings");
        PinButton.ToolTip = L.T("main.pin");
        DeviceSwitchButton.ToolTip = L.T("main.switchDeviceTip");
        UnadaptedHint.Text = L.T("main.unadaptedHint");
        UnadaptedHint.ToolTip = L.T("main.unadaptedTooltip");

        // 无设备在显示时刷新兜底名；有设备时名字是真实设备名，不动
        if (_active is null && !_devices.Values.Any())
        {
            DeviceNameText.Text = L.T("main.searching");
            Dispatcher.BeginInvoke(UpdateDeviceNameMarquee, DispatcherPriority.Render);
        }

        LblLeftTitle.Text = L.T("main.left");
        LblRightTitle.Text = L.T("main.right");
        LblCaseTitle.Text = L.T("main.caseTitle");
        CaseHintText.Text = L.T("main.caseHint");
        LblStatsUsedLabel.Text = L.T("main.usedToday");
        StatsShareButton.ToolTip = L.T("stats.shareTip");

        if (_updateInfo is null)
            UpdateButton.Content = L.T("main.checkUpdate");
        AdaptButton.Content = L.T("main.adapterList");
        RebindButton.Content = L.T("main.rebind");

        // 设置页
        LblSecAlerts.Text = L.T("settings.sectionAlerts");
        LblLowThresholdTitle.Text = L.T("settings.lowThresholdTitle");
        LblLowThresholdSub.Text = L.T("settings.lowThresholdSub");
        LblQuietTitle.Text = L.T("settings.quietTitle");
        LblQuietSub.Text = L.T("settings.quietSub");
        LblDropTitle.Text = L.T("settings.dropTitle");
        LblDropSub.Text = L.T("settings.dropSub");
        LblVoiceTitle.Text = L.T("settings.voiceTitle");
        LblVoiceSub.Text = L.T("settings.voiceSub");
        LblSecPopup.Text = L.T("settings.sectionPopup");
        LblPopupTitle.Text = L.T("settings.popupTitle");
        LblPopupSub.Text = L.T("settings.popupSub");
        LblCooldownTitle.Text = L.T("settings.cooldownTitle");
        LblCooldownSub.Text = L.T("settings.cooldownSub");
        CooldownValue.Text = L.F("settings.minutesFmt", (int)CooldownSlider.Value);
        LblSecMini.Text = L.T("settings.sectionMini");
        LblMiniTitle.Text = L.T("settings.miniTitle");
        LblMiniSub.Text = L.T("settings.miniSub");
        LblMediaTitle.Text = L.T("settings.mediaTitle");
        LblMediaSub.Text = L.T("settings.mediaSub");
        LblSecGeneral.Text = L.T("settings.sectionGeneral");
        LblAutoStartTitle.Text = L.T("settings.autostartTitle");
        LblAutoStartSub.Text = L.T("settings.autostartSub");
        LblThemeTitle.Text = L.T("settings.themeTitle");
        LblThemeSub.Text = L.T("settings.themeSub");
        LblAccentTitle.Text = L.T("settings.accentTitle");
        LblAccentSub.Text = L.T("settings.accentSub");
        LblHotKeyTitle.Text = L.T("settings.hotkeyTitle");
        LblHotKeySub.Text = L.F("settings.hotkeySubFmt", "Ctrl+Alt+B");
        LblHistoryTitle.Text = L.T("settings.historyTitle");
        LblHistorySub.Text = L.T("settings.historySub");
        OpenHistoryFolderButton.Content = L.T("settings.openFolder");
        LblLangTitle.Text = L.T("lang.title");
        LblLangSub.Text = L.T("lang.sub");
        LblAboutTitle.Text = L.T("settings.aboutTitle");
        var ver = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Version;
        LblAboutSub.Text = L.F("settings.aboutSub", ver is null ? "?" : ver.ToString(3));
        LblAboutGithub.Text = L.T("settings.aboutGithub");
        LblAboutQq.Text = L.T("settings.aboutQq");
        LblHistoryNote.Text = L.T("settings.historyNote");
        SettingsBackButton.Content = L.T("settings.back");
        RefreshSegmentSelections();
        RefreshQuietHoursLabel();

        // 适配视图静态文案 + 型号占位符
        LblAdapterListIntro.Text = L.T("adapter.listIntro");
        LblAdapterListHint.Text = L.T("adapter.listHint");
        LblWizardModelTitle.Text = L.T("wizard.modelTitle");
        LblWizardModelBody.Text = L.T("wizard.modelBody");
        LblWizardModelLabel.Text = L.T("wizard.modelLabel");
        LblWizardLayoutLabel.Text = L.T("wizard.layoutLabel");
        LayoutOptionDualCase.Content = L.T("wizard.layoutDualCase");
        LayoutOptionDualNoCase.Content = L.T("wizard.layoutDualNoCase");
        LayoutOptionMono.Content = L.T("wizard.layoutMono");
        LblWizardLayoutNote.Text = L.T("wizard.layoutNote");
        if (_modelShowingPlaceholder) ModelBox.Text = ModelPlaceholder;
        if (ModelHint.Visibility == Visibility.Visible)
            ModelHint.Text = L.T("wizard.modelHint");

        // 适配视图：可见时按当前页重建（名单条目、向导按钮、步骤文案）
        if (AdapterView.Visibility == Visibility.Visible)
        {
            if (_adapterPage < 0) ShowAdapterList(instant: true);
            else ShowAdapterPage(_adapterPage);
            RefreshCaptureLive();
        }

        // 动态状态行/统计条立即按新语言重绘
        RefreshStats();
        RefreshAliveState();
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray() => Hide();

    protected override void OnClosed(EventArgs e)
    {
        _usage.FlushAll();   // 退出前把不足 1 分钟的佩戴零头落盘
        base.OnClosed(e);
    }

    private void RebindButton_Click(object sender, RoutedEventArgs e)
    {
        _watcher.Unbind();
        _devices.Clear();
        _knownMacs.Clear();
        _config.KnownDevices = null;
        _config.Save();
        _active = null;
        DevicePopup.IsOpen = false;

        DeviceNameText.Text = L.T("main.searching");
        Dispatcher.BeginInvoke(UpdateDeviceNameMarquee, DispatcherPriority.Render);
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
            // 已适配设备（广播能解析出显示名）不重复登记（宽匹配，兼容系统名带后缀）；
            // 但连接心跳必须刷到它头上——连接播放/合盖期间广播停发、LastSeen 停走，
            // 悬浮条的在线灰显与系统电量兜底全靠 ConnSeen 维持
            var adapted = _devices.Values.FirstOrDefault(d => d.IsAdapted && d.Name.Length > 0 &&
                                                               NameMatchesProfile(name, d.Name));
            if (adapted is not null)
            {
                adapted.ConnSeen = DateTime.Now;
                continue;
            }
            if (_devices.TryGetValue(mac, out var st))
            {
                st.LastSeen = DateTime.Now;   // 已登记的设备：刷新在线心跳
                st.ConnSeen = DateTime.Now;
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
                ? L.T("switch.connectedNoSplit")
                : L.T("switch.disconnectedNoSplit");
        string part((int Value, DateTime Time, int Suppressed)? s) =>
            IsChannelFresh(s) ? $"{s!.Value.Value}%" : "--";
        return L.F("switch.summaryFmt", part(d.Left), part(d.Right), part(d.Case));
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
        public string Badge => IsCurrent ? L.T("switch.current") : !Adapted ? L.T("switch.unsupported") : "";
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
        UpdateButton.Content = L.T("main.checking");
        try
        {
            var update = await UpdateChecker.CheckAsync();
            if (update is null)
            {
                FlashUpdateButton(L.T("main.upToDate"));
            }
            else
            {
                _updateInfo = update;
                UpdateButton.IsEnabled = true;
                UpdateButton.Content = L.F("main.newVersionFmt", update.Version);
            }
        }
        catch
        {
            FlashUpdateButton(L.T("main.checkFailed"));
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
            UpdateButton.Content = L.T("main.checkUpdate");
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
        CooldownValue.Text = L.F("settings.minutesFmt", (int)CooldownSlider.Value);
        MiniBarCheck.IsChecked = _config.MiniBarEnabled;
        HistoryCheck.IsChecked = _config.HistoryEnabled;
        VoiceAlertsCheck.IsChecked = _config.VoiceAlerts;
        MediaControlsCheck.IsChecked = _config.MiniBarMediaControls;
        HotKeyCheck.IsChecked = _config.HotKeyEnabled;
        ApplyAccentDots();
        RefreshSegmentSelections();
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

    // 外观三态分段按钮：跟随系统（默认）/ 深色 / 浅色
    private void ThemeSegment_Click(object sender, RoutedEventArgs e)
    {
        var mode = ((Button)sender).Tag?.ToString() ?? "system";
        _config.ThemeMode = mode;
        _config.Save();
        ThemeManager.SetMode(mode);
        RefreshSegmentSelections();
    }

    // 语言三态分段按钮：跟随系统 / 中文 / English（写配置 + L.SetLanguage 即时刷新）
    private void LangSegment_Click(object sender, RoutedEventArgs e)
    {
        var lang = ((Button)sender).Tag?.ToString() ?? "system";
        _config.Language = lang;
        _config.Save();
        L.SetLanguage(lang);          // 触发 L.Changed → 各界面 ApplyLanguage
        RefreshSegmentSelections();   // 同语言时 Changed 不触发，也要让高亮移动给出反馈
    }

    private const string GitHubUrl = "https://github.com/Furry09shou/ronghui-earbuds";
    private readonly DispatcherTimer _qqResetTimer = new() { Interval = TimeSpan.FromSeconds(1.2) };

    private void GithubButton_Click(object sender, RoutedEventArgs e) =>
        Process.Start(new ProcessStartInfo { FileName = GitHubUrl, UseShellExecute = true });

    // 点 QQ 号复制到剪贴板，短暂显示「已复制」后还原
    private void QqButton_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText("1769711677"); } catch { /* 剪贴板被占用时忽略 */ }
        LblAboutQq.Text = L.T("settings.copied");
        _qqResetTimer.Stop();
        _qqResetTimer.Tick -= QqResetTimer_Tick;
        _qqResetTimer.Tick += QqResetTimer_Tick;
        _qqResetTimer.Start();
    }

    private void QqResetTimer_Tick(object? sender, EventArgs e)
    {
        _qqResetTimer.Stop();
        LblAboutQq.Text = L.T("settings.aboutQq");
    }

    /// <summary>刷新外观与语言分段按钮的选中态与文案。</summary>
    private void RefreshSegmentSelections()
    {
        ThemeBtnSystem.Content = L.T("theme.system");
        ThemeBtnDark.Content = L.T("theme.dark");
        ThemeBtnLight.Content = L.T("theme.light");
        LangBtnSystem.Content = L.T("theme.system");
        LangBtnZh.Content = L.T("lang.zh");
        LangBtnEn.Content = L.T("lang.en");
        ApplySegmentState(ThemeBtnSystem, _config.ThemeMode == "system");
        ApplySegmentState(ThemeBtnDark, _config.ThemeMode == "dark");
        ApplySegmentState(ThemeBtnLight, _config.ThemeMode == "light");
        ApplySegmentState(LangBtnSystem, _config.Language == "system");
        ApplySegmentState(LangBtnZh, _config.Language == "zh");
        ApplySegmentState(LangBtnEn, _config.Language == "en");
    }

    /// <summary>分段按钮选中态：橙色描边 + Accent 前色；未选为幽灵按钮态。</summary>
    private static void ApplySegmentState(Button b, bool selected)
    {
        if (selected)
        {
            b.SetResourceReference(Button.BorderBrushProperty, "T.Accent");
            b.SetResourceReference(Button.ForegroundProperty, "T.Accent");
            b.SetResourceReference(Button.BackgroundProperty, "T.HoverBg");
            b.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            b.SetResourceReference(Button.BorderBrushProperty, "T.InputBorder");
            b.SetResourceReference(Button.ForegroundProperty, "T.TextSecondary");
            b.Background = System.Windows.Media.Brushes.Transparent;
            b.FontWeight = FontWeights.Normal;
        }
    }

    // 电量历史记录开关（关闭后 BatteryHistoryStore 不再落库，已有数据保留）
    private void HistoryCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.HistoryEnabled = HistoryCheck.IsChecked == true;
        _config.Save();
    }

    // 在资源管理器中打开电量历史文件夹（不存在则先创建）
    private void OpenHistoryFolderButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "RonghuiEarbuds", "history");
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
        }
        catch { /* 打开失败忽略（目录不可创建等极端情况） */ }
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
        CooldownValue.Text = L.F("settings.minutesFmt", (int)e.NewValue);
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

    private void VoiceAlertsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.VoiceAlerts = VoiceAlertsCheck.IsChecked == true;
        _config.Save();
    }

    private void MediaControlsCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.MiniBarMediaControls = MediaControlsCheck.IsChecked == true;
        _config.Save();
        // 复用悬浮条开关通道让 MiniBar.ApllyEnabled 重读媒体栏配置
        _onMiniBarToggle?.Invoke(_config.MiniBarEnabled);
    }

    private void HotKeyCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        _config.HotKeyEnabled = HotKeyCheck.IsChecked == true;
        _config.Save();
        _onHotKeyToggle?.Invoke(_config.HotKeyEnabled);
    }

    private static ControlTemplate? _accentDotTpl;

    /// <summary>强调色圆点模板：纯色圆 + 鼠标悬停微降透明度。</summary>
    private static ControlTemplate AccentDotTemplate() => _accentDotTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Button}">
              <Ellipse x:Name="Dot" Fill="{TemplateBinding Background}"
                       Stroke="{TemplateBinding BorderBrush}" StrokeThickness="2.5"/>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Dot" Property="Opacity" Value="0.72"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);

    private void AccentDot_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b || !int.TryParse(b.Tag?.ToString(), out var idx)) return;
        _config.AccentIndex = idx;
        _config.Save();
        ThemeManager.SetAccent(idx);
        ApplyAccentDots();
    }

    /// <summary>构建六个强调色圆点：填充预设色，选中项加主题色描边环。</summary>
    private void ApplyAccentDots()
    {
        var dots = new[] { AccentDot0, AccentDot1, AccentDot2, AccentDot3, AccentDot4, AccentDot5 };
        for (var i = 0; i < dots.Length; i++)
        {
            var color = (Color)ColorConverter.ConvertFromString(ThemeManager.AccentPresets[i]);
            dots[i].Template = AccentDotTemplate();
            dots[i].Background = new SolidColorBrush(color);
            dots[i].BorderBrush = new SolidColorBrush(
                i == _config.AccentIndex ? ThemeManager.GetColor("T.TextPrimary") : Colors.Transparent);
        }
    }

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

        bool charging = stats.EstimateText == L.T("battery.charging");
        StatsEstimateText.Text = charging
            ? L.T("battery.charging")
            : L.F("main.estimateFmt", stats.EstimateText);
        StatsEstimateText.SetResourceReference(TextBlock.ForegroundProperty,
            charging ? "T.Accent" : "T.TextGreen");

        // 佩戴时长（今天已连接的累计分钟）
        int worn;
        try { worn = _usage.GetMinutes(st.Mac, DateTime.Now); }
        catch { worn = 0; }
        StatsWearText.Text = worn > 0 ? L.F("stats.wearFmt", worn) : "--";

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

    // ==================== 电量日报导出（分享用：图片 / 文本） ====================

    /// <summary>语音播报文案：把设备选择名单里的**全部在线设备**挨个报一遍
    /// （当前关注设备优先），每台带名字，便于多副耳机区分。
    /// 悬浮条按钮 / 托盘菜单共用。</summary>
    public string VoiceReportText()
    {
        bool Fresh((int Value, DateTime Time, int Suppressed)? s) =>
            s is { } v && (DateTime.Now - v.Time).TotalSeconds <= ChannelFreshSeconds;
        string Fmt((int Value, DateTime Time, int Suppressed)? s) =>
            Fresh(s) ? $"{s.Value.Value}" : L.T("voice.na");
        string NameOf(DeviceState st) =>
            st.Name.Length > 0 ? st.Name : L.T("parser.unknown");

        var macs = KnownDeviceList()
            .Select(k => k.Mac)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (_active is { } active && !macs.Contains(active.Mac))
            macs.Insert(0, active.Mac);
        if (macs.Count == 0) return L.T("voice.noDevice");

        var parts = new List<string>();
        foreach (var mac in macs)
        {
            if (!_devices.TryGetValue(mac, out var st) || !IsDeviceAlive(st.Mac)) continue;
            if (Fresh(st.Left) || Fresh(st.Right) || Fresh(st.Case))
                parts.Add(L.F("voice.reportFmt", NameOf(st), Fmt(st.Left), Fmt(st.Right), Fmt(st.Case)));
            else if (st.SystemBattery is { } sv)
                parts.Add(L.F("voice.sysFmt", NameOf(st), sv));
        }
        if (parts.Count == 0) return L.T("voice.noData");
        return string.Join(L.T("voice.sep"), parts);
    }

    private void StatsShareButton_Click(object sender, RoutedEventArgs e) =>
        StatsSharePopup.IsOpen = true;

    /// <summary>模板内的 TextBlock 只能经 Template.FindName 定位（x:Name 不会生成字段）。</summary>
    private static void SetRowText(Button b, string key)
    {
        if (b.Template?.FindName("Lbl", b) is TextBlock tb)
            tb.Text = L.T(key);
    }

    private void ExportImage_Click(object sender, RoutedEventArgs e)
    {
        StatsSharePopup.IsOpen = false;
        if (_active is not { } st) return;
        try
        {
            var fileName = $"{SafeFileToken(st.Name.Length > 0 ? st.Name : "RonghuiEarbuds")}-" +
                           $"{L.T("stats.report")}-{DateTime.Now:yyyyMMdd}.png";
            if (TrySaveCard(BuildExportCard(st), L.T("stats.exportImage"), fileName))
                FlashShareButton();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, L.T("wizard.failTitle"),
                L.F("wizard.failFmt", ex.Message), DialogKind.Error);
        }
    }

    private void ExportWeek_Click(object sender, RoutedEventArgs e)
    {
        StatsSharePopup.IsOpen = false;
        if (_active is not { } st) return;
        try
        {
            var fileName = $"{SafeFileToken(st.Name.Length > 0 ? st.Name : "RonghuiEarbuds")}-" +
                           $"{L.T("stats.weekTitle")}-{DateTime.Now:yyyyMMdd}.png";
            if (TrySaveCard(BuildExportWeekCard(st), L.T("stats.exportWeek"), fileName))
                FlashShareButton();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, L.T("wizard.failTitle"),
                L.F("wizard.failFmt", ex.Message), DialogKind.Error);
        }
    }

    /// <summary>离屏卡片渲染为 2x 超采样 PNG 并弹保存框，保存成功返回 true。</summary>
    private bool TrySaveCard(Border card, string dialogTitle, string defaultFileName)
    {
        const double w = 640;
        card.Measure(new Size(w, double.PositiveInfinity));
        card.Arrange(new Rect(0, 0, w, card.DesiredSize.Height));
        card.UpdateLayout();
        const double scale = 2;   // 2x 超采样，高分屏/系统缩放下导出不糊
        var rtb = new RenderTargetBitmap(
            (int)Math.Round(w * scale), (int)Math.Round(card.DesiredSize.Height * scale),
            96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(card);

        var dlg = new SaveFileDialog
        {
            Filter = "PNG|*.png",
            Title = dialogTitle,
            FileName = defaultFileName,
        };
        if (dlg.ShowDialog(this) != true) return false;

        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using (var fs = File.Create(dlg.FileName))
            enc.Save(fs);
        return true;
    }

    private void CopyText_Click(object sender, RoutedEventArgs e)
    {
        StatsSharePopup.IsOpen = false;
        if (_active is not { } st) return;
        try
        {
            Clipboard.SetText(BuildExportText(st));
            FlashShareButton();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, L.T("wizard.failTitle"),
                L.F("wizard.failFmt", ex.Message), DialogKind.Error);
        }
    }

    private void CopyWeek_Click(object sender, RoutedEventArgs e)
    {
        StatsSharePopup.IsOpen = false;
        if (_active is not { } st) return;
        try
        {
            Clipboard.SetText(BuildExportWeekText(st));
            FlashShareButton();
        }
        catch (Exception ex)
        {
            MessageDialog.Show(this, L.T("wizard.failTitle"),
                L.F("wizard.failFmt", ex.Message), DialogKind.Error);
        }
    }

    /// <summary>分享按钮图标短暂变绿勾，给无弹窗的操作一个轻反馈。</summary>
    private void FlashShareButton()
    {
        if (StatsShareButton.Template?.FindName("Ico", StatsShareButton) is not TextBlock ico) return;
        ico.Text = "\uE73E";
        ico.Foreground = new SolidColorBrush(ThemeManager.GetColor("T.Good"));
        var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.4) };
        t.Tick += (_, _) =>
        {
            t.Stop();
            ico.Text = "\uE72D";
            ico.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        };
        t.Start();
    }

    private string BuildExportText(DeviceState st)
    {
        static string Part((int Value, DateTime Time, int Suppressed)? s) =>
            s is { } v && (DateTime.Now - v.Time).TotalSeconds <= ChannelFreshSeconds
                ? $"{v.Value}%" : "--";

        var sb = new StringBuilder();
        sb.Append(L.T("main.title")).Append(" · ").Append(L.T("stats.report"))
          .Append(' ').AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
        sb.Append(L.T("stats.device")).Append(L.T("stats.colon"))
          .AppendLine(st.Name.Length > 0 ? st.Name : L.T("main.identifying"));
        sb.Append(L.T("main.left")).Append(' ').Append(Part(st.Left)).Append(" · ")
          .Append(L.T("main.right")).Append(' ').Append(Part(st.Right)).Append(" · ")
          .Append(L.T("main.caseTitle")).Append(' ').AppendLine(Part(st.Case));
        if (IsDeviceAlive(st.Mac) && st.SystemBattery is { } sv)
            sb.AppendLine(L.F("state.sysBatteryFmt", sv));
        try
        {
            var stats = _history.GetTodayStats(st.Mac);
            if (stats.UsedPercent > 0)
                sb.Append(L.T("main.usedToday")).Append(' ')
                  .Append($"{(int)Math.Round(stats.UsedPercent)}%").Append(" · ");
            bool charging = stats.EstimateText == L.T("battery.charging");
            sb.AppendLine(charging ? L.T("battery.charging")
                                   : L.F("main.estimateFmt", stats.EstimateText));
        }
        catch { /* 无历史时跳过统计行 */ }
        return sb.ToString().TrimEnd();
    }

    /// <summary>导出图片卡片：标题 / 设备 / 三格大电量 / 统计行 / 当日曲线。
    /// 离屏渲染（不进视觉树），颜色全部用 ThemeManager 实色——DynamicResource
    /// 在游离元素上不可靠。</summary>
    private Border BuildExportCard(DeviceState st)
    {
        SolidColorBrush Brush(string key) => new(ThemeManager.GetColor(key));

        var card = new Border
        {
            Background = Brush("T.WindowBg"),
            BorderBrush = Brush("T.CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(36, 28, 36, 24),
            Width = 640,
        };
        var root = new StackPanel();

        // 标题行：应用名 + 日期
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = L.T("main.title"), FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("T.TextPrimary"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        var dateText = new TextBlock
        {
            Text = DateTime.Now.ToString("yyyy-MM-dd"), FontSize = 12,
            Foreground = Brush("T.TextDim"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(dateText, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(dateText);
        root.Children.Add(titleRow);

        // 设备行
        root.Children.Add(new TextBlock
        {
            Text = L.T("stats.device") + L.T("stats.colon") +
                   (st.Name.Length > 0 ? st.Name : L.T("main.identifying")),
            FontSize = 13, Margin = new Thickness(0, 7, 0, 0),
            Foreground = Brush("T.TextSecondary"),
        });

        // 三格大数字（电量色随电量值，与主面板圆环同源）
        var chGrid = new Grid { Margin = new Thickness(0, 20, 0, 12) };
        var channels = new (string Label, (int Value, DateTime Time, int Suppressed)? Ch)[]
        {
            (L.T("main.left"), st.Left),
            (L.T("main.right"), st.Right),
            (L.T("main.caseTitle"), st.Case),
        };
        for (var i = 0; i < 3; i++)
        {
            chGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var (label, tup) = channels[i];
            int? v = tup is { } t && (DateTime.Now - t.Time).TotalSeconds <= ChannelFreshSeconds
                ? t.Value : null;
            var panel = new StackPanel();
            panel.Children.Add(new TextBlock
            {
                Text = v is { } fv ? $"{fv}%" : "--",
                FontSize = 34, FontWeight = FontWeights.Bold,
                Foreground = v is { } fv2 ? new SolidColorBrush(ThemeManager.ColorFor(fv2)) : Brush("T.TextDim"),
            });
            panel.Children.Add(new TextBlock
            {
                Text = label, FontSize = 12, Margin = new Thickness(0, 2, 0, 0),
                Foreground = Brush("T.TextSecondary"),
            });
            Grid.SetColumn(panel, i);
            chGrid.Children.Add(panel);
        }
        root.Children.Add(chGrid);

        // 统计行
        string statsLine;
        BatteryDayStats? dayStats = null;
        try { dayStats = _history.GetTodayStats(st.Mac); }
        catch { /* 无历史时统计行退化为「--」 */ }
        if (dayStats is { } ds)
        {
            bool charging = ds.EstimateText == L.T("battery.charging");
            statsLine =
                $"{L.T("main.usedToday")} {(ds.UsedPercent > 0 ? $"{(int)Math.Round(ds.UsedPercent)}%" : "--")}" +
                $" · {(charging ? L.T("battery.charging") : L.F("main.estimateFmt", ds.EstimateText))}";
        }
        else statsLine = "--";
        root.Children.Add(new TextBlock
        {
            Text = statsLine, FontSize = 13, Foreground = Brush("T.TextSecondary"),
            Margin = new Thickness(0, 0, 0, 18),
        });

        // 当日曲线（大图）：网格线 + 淡填充 + 主题色折线
        if (dayStats is { Curve.Count: >= 2 })
        {
            const double cw = 566, chh = 128;
            var host = new Canvas { Width = cw, Height = chh };
            for (int lv = 25; lv < 100; lv += 25)
            {
                var gy = chh - chh * lv / 100.0;
                host.Children.Add(new Shapes.Line
                {
                    X1 = 0, X2 = cw, Y1 = gy, Y2 = gy,
                    StrokeThickness = 1, Stroke = Brush("T.SubtleBorder"),
                    StrokeDashArray = new DoubleCollection { 2, 3 }, Opacity = 0.8,
                });
            }
            var pts = dayStats.Curve
                .Select(p => new Point(p.X / 100.0 * cw, chh - p.Y / 100.0 * chh))
                .ToList();
            var accent = ThemeManager.GetColor("T.Accent");
            var area = new StreamGeometry();
            using (var ctx = area.Open())
            {
                ctx.BeginFigure(pts[0], true, false);
                foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, false);
                ctx.LineTo(new Point(pts[^1].X, chh), true, false);
                ctx.LineTo(new Point(pts[0].X, chh), true, false);
            }
            area.Freeze();
            host.Children.Add(new Shapes.Path
            {
                Data = area, StrokeThickness = 0,
                Fill = new SolidColorBrush(Color.FromArgb(26, accent.R, accent.G, accent.B)),
            });
            var lineGeo = new StreamGeometry();
            using (var ctx = lineGeo.Open())
            {
                ctx.BeginFigure(pts[0], false, false);
                foreach (var p in pts.Skip(1)) ctx.LineTo(p, true, false);
            }
            lineGeo.Freeze();
            host.Children.Add(new Shapes.Path
            {
                Data = lineGeo,
                Stroke = new SolidColorBrush(accent), StrokeThickness = 2.4,
                StrokeLineJoin = PenLineJoin.Round,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            });
            root.Children.Add(host);
        }

        // 页脚
        root.Children.Add(new TextBlock
        {
            Text = "RonghuiEarbuds · github.com/Furry09shou/ronghui-earbuds",
            FontSize = 10.5, Margin = new Thickness(0, 16, 0, 0), Foreground = Brush("T.TextDim"),
        });

        card.Child = root;
        return card;
    }

    /// <summary>周报文本：范围 / 设备 / 7 天佩戴分钟 / 汇总。</summary>
    private string BuildExportWeekText(DeviceState st)
    {
        var sb = new StringBuilder();
        var today = DateTime.Now.Date;
        sb.Append(L.T("main.title")).Append(" · ").AppendLine(L.T("stats.weekTitle"));
        sb.AppendLine(L.F("stats.weekRangeFmt", today.AddDays(-6), today));
        sb.Append(L.T("stats.device")).Append(L.T("stats.colon"))
          .AppendLine(st.Name.Length > 0 ? st.Name : L.T("main.identifying"));

        var days = _usage.GetRecentDays(st.Mac, 7);
        foreach (var (day, m) in days)
            sb.Append($"{day:MM-dd}  ")
              .AppendLine(m > 0 ? L.F("stats.durMinsFmt", m) : "--");

        int total = days.Sum(d => d.Minutes);
        var trackDays = Enumerable.Range(0, 7)
            .Count(i => BatteryHistoryStore.DayHasData(st.Mac, today.AddDays(-i)));
        if (total > 0)
        {
            var dur = total >= 60
                ? L.F("stats.durHoursFmt", total / 60, total % 60)
                : L.F("stats.durMinsFmt", total);
            sb.AppendLine(L.F("stats.weekTotalFmt", dur));
            sb.AppendLine(L.F("stats.weekDaysFmt", trackDays));
        }
        else sb.AppendLine(L.T("stats.weekNoData"));
        return sb.ToString().TrimEnd();
    }

    /// <summary>周报图片卡片：标题 / 设备 / 7 天佩戴柱状图 / 汇总行。离屏渲染，
    /// 颜色全部用 ThemeManager 实色（DynamicResource 在游离元素上不可靠）。</summary>
    private Border BuildExportWeekCard(DeviceState st)
    {
        SolidColorBrush Brush(string key) => new(ThemeManager.GetColor(key));

        var card = new Border
        {
            Background = Brush("T.WindowBg"),
            BorderBrush = Brush("T.CardBorder"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(36, 28, 36, 24),
            Width = 640,
        };
        var root = new StackPanel();
        var today = DateTime.Now.Date;

        // 标题行：周报 + 日期范围
        var titleRow = new Grid();
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        titleRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var titleText = new TextBlock
        {
            Text = L.T("stats.weekTitle"), FontSize = 19, FontWeight = FontWeights.SemiBold,
            Foreground = Brush("T.TextPrimary"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        var rangeText = new TextBlock
        {
            Text = L.F("stats.weekRangeFmt", today.AddDays(-6), today), FontSize = 12,
            Foreground = Brush("T.TextDim"), VerticalAlignment = VerticalAlignment.Bottom,
        };
        Grid.SetColumn(titleText, 0);
        Grid.SetColumn(rangeText, 1);
        titleRow.Children.Add(titleText);
        titleRow.Children.Add(rangeText);
        root.Children.Add(titleRow);

        // 设备行
        root.Children.Add(new TextBlock
        {
            Text = L.T("stats.device") + L.T("stats.colon") +
                   (st.Name.Length > 0 ? st.Name : L.T("main.identifying")),
            FontSize = 13, Margin = new Thickness(0, 7, 0, 0),
            Foreground = Brush("T.TextSecondary"),
        });

        // 7 天佩戴柱状图（柱高 ∝ 分钟/天，上限 240 分钟）
        var days = _usage.GetRecentDays(st.Mac, 7);
        const double cw = 566, plotH = 132, maxMin = 240, barW = 26;
        var chart = new Canvas { Width = cw, Height = plotH + 24, Margin = new Thickness(0, 22, 0, 4) };
        var accent = ThemeManager.GetColor("T.Accent");
        var slot = cw / 7.0;
        for (var i = 0; i < 7; i++)
        {
            var (day, m) = days[i];
            var h = m > 0 ? Math.Max(8, plotH * Math.Min(m, maxMin) / maxMin) : 3;
            var bar = new Border
            {
                Width = barW,
                Height = h,
                CornerRadius = new CornerRadius(6, 6, 0, 0),
                Background = new SolidColorBrush(m > 0 ? accent : ThemeManager.GetColor("T.SubtleBorder")),
            };
            Canvas.SetLeft(bar, slot * i + (slot - barW) / 2);
            Canvas.SetTop(bar, plotH - h);
            chart.Children.Add(bar);

            var label = new TextBlock
            {
                Text = $"{day:MM-dd}", FontSize = 10, Width = slot,
                TextAlignment = TextAlignment.Center, Foreground = Brush("T.TextDim"),
            };
            Canvas.SetLeft(label, slot * i);
            Canvas.SetTop(label, plotH + 6);
            chart.Children.Add(label);
        }
        root.Children.Add(chart);

        // 汇总行
        int total = days.Sum(d => d.Minutes);
        var trackDays = Enumerable.Range(0, 7)
            .Count(i => BatteryHistoryStore.DayHasData(st.Mac, today.AddDays(-i)));
        string summary;
        if (total > 0)
        {
            var dur = total >= 60
                ? L.F("stats.durHoursFmt", total / 60, total % 60)
                : L.F("stats.durMinsFmt", total);
            summary = $"{L.F("stats.weekTotalFmt", dur)} · {L.F("stats.weekDaysFmt", trackDays)}";
        }
        else summary = L.T("stats.weekNoData");
        root.Children.Add(new TextBlock
        {
            Text = summary, FontSize = 13, Foreground = Brush("T.TextSecondary"),
            Margin = new Thickness(0, 10, 0, 0),
        });

        // 页脚
        root.Children.Add(new TextBlock
        {
            Text = "RonghuiEarbuds · github.com/Furry09shou/ronghui-earbuds",
            FontSize = 10.5, Margin = new Thickness(0, 16, 0, 0), Foreground = Brush("T.TextDim"),
        });

        card.Child = root;
        return card;
    }

    /// <summary>文件名安全化：保留字母数字/中文/空格与 -_，其余替换为 _。</summary>
    private static string SafeFileToken(string s)
    {
        var clean = string.Concat(s.Select(ch =>
            char.IsLetterOrDigit(ch) || ch is ' ' or '-' or '_' ? ch : '_'));
        return clean.Length > 0 ? clean : "RonghuiEarbuds";
    }

    // ==================== 适配视图：名单 + 分页向导（窗口内跳转） ====================

    private const string IssueUrlBase = "https://github.com/Furry09shou/ronghui-earbuds/issues/new";
    private const double MainViewHeight = 508;
    private const double SettingsViewHeight = 640;
    private const double AdapterListHeight = 480;
    private const double AdapterWizardHeight = 560;
    private static string ModelPlaceholder => L.T("wizard.modelPlaceholder");
    private static Color PlaceholderColor => ThemeManager.GetColor("T.TextDim");
    private static Color InputColor => ThemeManager.GetColor("T.TextPrimary");

    // 耳机形态（写入采集元数据，供分析时区分电量字段数量）
    private const string LayoutDualCase = "dual_case";
    private const string LayoutDualNoCase = "dual_nocase";
    private const string LayoutMono = "mono";

    // 双耳 + 充电仓（仓也广播电量的常见真无线）——存 key，取值时经 L.T 解析当前语言
    // 步骤含独立的「戴上使用 / 摘下静置」段：佩戴态广播可能与静置不同，需单独采集
    private static readonly (string Key, string DetailKey)[] StepsDual =
    {
        ("wizard.dual1T", "wizard.dual1D"),
        ("wizard.dual2T", "wizard.dual2D"),
        ("wizard.dual3T", "wizard.dual3D"),
        ("wizard.dual4T", "wizard.dual4D"),
        ("wizard.dual5T", "wizard.dual5D"),
        ("wizard.dual6T", "wizard.dual6D"),
        ("wizard.dual7T", "wizard.dual7D"),
    };

    // 仅双耳（无仓或仓不广播电量）：动作兼容两种耳机——开关机或入仓出仓均可
    private static readonly (string Key, string DetailKey)[] StepsDualNoCase =
    {
        ("wizard.dnc1T", "wizard.dnc1D"),
        ("wizard.dnc2T", "wizard.dnc2D"),
        ("wizard.dnc3T", "wizard.dnc3D"),
        ("wizard.dnc4T", "wizard.dnc4D"),
        ("wizard.dnc5T", "wizard.dnc5D"),
        ("wizard.dnc6T", "wizard.dnc6D"),
        ("wizard.dnc7T", "wizard.dnc7D"),
    };

    // 仅单耳（无充电仓）：只有一只耳机，观察开机/使用/静置/重启的状态差别
    private static readonly (string Key, string DetailKey)[] StepsMono =
    {
        ("wizard.mono1T", "wizard.mono1D"),
        ("wizard.mono2T", "wizard.mono2D"),
        ("wizard.mono3T", "wizard.mono3D"),
        ("wizard.mono4T", "wizard.mono4D"),
        ("wizard.mono5T", "wizard.mono5D"),
    };

    private readonly CaptureService _capture = new();
    private int _adapterPage = -1;      // -1=不在适配视图，0=型号页，1..N=动作步骤（N 按形态）
    private bool _adapterRunning;
    private string _layout = LayoutDualCase;
    private int _captureShown = -1;
    private Border[]? _dots;

    private (string Title, string Detail)[] CurrentSteps() =>
        (_layout switch
        {
            LayoutMono => StepsMono,
            LayoutDualNoCase => StepsDualNoCase,
            _ => StepsDual,
        }).Select(s => (L.T(s.Key), L.T(s.DetailKey))).ToArray();

    private string SelectedLayout() =>
        LayoutOptionDualNoCase.IsChecked == true ? LayoutDualNoCase :
        LayoutOptionMono.IsChecked == true ? LayoutMono : LayoutDualCase;

    private void ShowAdapterList(bool instant)
    {
        _adapterPage = -1;
        AdapterTitle.Text = L.T("adapter.listTitle");
        // 已适配（绿色）+ 实测确认不支持分耳的机型（红色，悬停看分析结论）
        var entries = XiaomiAdvParser.GetSupportedNames()
            .Select(n => new ModelEntry(n, true, L.T("adapter.supportedNote")))
            .Concat(UnsupportedModels.Select(m => new ModelEntry(
                m.Name, false, L.T(m.NoteKey))))
            .ToList();
        ModelList.ItemsSource = entries;
        ShowOnlyAdapterPage(AdapterListPage);
        DotsRow.Visibility = Visibility.Collapsed;
        LiveBox.Visibility = Visibility.Collapsed;
        AdapterBackButton.Content = L.T("wizard.cancel");
        AdapterMainButtonText.Text = L.T("wizard.startAdapter");
        AnimateWindowHeight(AdapterListHeight, instant);
        AnimateAdapterView(instant);
    }

    /// <summary>适配名单条目（含已验证不支持分耳电量的机型）。Badge/Note 随当前语言解析。</summary>
    public sealed record ModelEntry(string Name, bool Supported, string Note)
    {
        public string Badge => Supported ? L.T("adapter.supported") : L.T("adapter.unsupportedBadge");
    }

    /// <summary>
    /// 实测确认广播不含电量、无法适配分耳的机型（随 Issue 分析结论更新）。
    /// Note 存文案 key，显示时经 L.T 按当前语言解析。
    /// </summary>
    private static readonly (string Name, string NoteKey)[] UnsupportedModels =
    {
        ("SOAIY GD31", "adapter.unsupportedNote"),
    };

    private void ShowAdapterPage(int page)
    {
        _adapterPage = page;
        AdapterTitle.Text = L.T("adapter.wizardTitle");

        if (page >= 1)
        {
            // 步骤标题与详情（按耳机形态区分文案）
            var steps = CurrentSteps();
            var (title, detail) = steps[page - 1];
            ((TextBlock)FindName($"StepTitle{page}")!).Text = title;
            ((TextBlock)FindName($"StepDetail{page}")!).Text = detail;
            ((TextBlock)FindName($"StepNum{page}")!).Text = L.F("wizard.stepFmt", page, steps.Length);
        }

        ShowOnlyAdapterPage(page == 0 ? (UIElement)AdapterPage0 : (UIElement)FindName($"AdapterPage{page}")!);
        DotsRow.Visibility = Visibility.Visible;
        BuildAdapterDots(page);
        LiveBox.Visibility = page >= 1 ? Visibility.Visible : Visibility.Collapsed;
        AdapterBackButton.Content = _adapterRunning ? L.T("wizard.cancel") : L.T("settings.back");
        var total = CurrentSteps().Length;
        AdapterMainButtonText.Text = page switch
        {
            0 => L.T("wizard.beginCapture"),
            var p when p == total => L.T("wizard.finishUpload"),
            _ => L.F("wizard.nextFmt", page, total),
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
        for (var i = 0; i <= 7; i++)
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
        // 点数 = 步骤数 + 型号页，按当前形态动态重建（双耳 8 点 / 单耳 6 点）
        var total = CurrentSteps().Length + 1;
        if (_dots is null || _dots.Length != total)
        {
            DotsRow.Children.Clear();
            _dots = new Border[total];
            for (var i = 0; i < total; i++)
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
        for (var i = 0; i < total; i++)
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
        if (!UI.MessageDialog.Show(this, L.T("wizard.cancelConfirm"), L.T("adapter.wizardTitle"),
                UI.DialogKind.Question, showCancel: true))
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
                ModelHint.Text = L.T("wizard.modelHint");
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
        if (_adapterPage < CurrentSteps().Length)
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
            ? L.F("wizard.capturedFmt", _capture.Count, cids, keys)
            : L.T("wizard.waitingCapture");
    }

    private void ModelBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (_modelShowingPlaceholder)
        {
            ModelBox.Text = "";
            ModelBox.Foreground = new SolidColorBrush(InputColor);
            _modelShowingPlaceholder = false;
        }
    }

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ModelBox.Text.Trim().Length == 0)
        {
            ModelBox.Text = ModelPlaceholder;
            ModelBox.Foreground = new SolidColorBrush(PlaceholderColor);
            _modelShowingPlaceholder = true;
        }
    }

    private void FinishCapture()
    {
        AdapterMainButton.IsEnabled = false;
        try
        {
            var model = ModelBox.Text.Trim();
            if (model.Length == 0) model = L.T("wizard.unknownModel");
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

            UI.MessageDialog.Show(this,
                L.F("wizard.doneMsgFmt", _capture.Count, Path.GetFileName(analysisPath)),
                L.T("wizard.doneTitle"), UI.DialogKind.Success);
            _adapterRunning = false;
            _capture.Dispose();
            ShowMainView();
        }
        catch (Exception ex)
        {
            UI.MessageDialog.Show(this, L.F("wizard.failFmt", ex.Message), L.T("wizard.failTitle"),
                UI.DialogKind.Error);
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
