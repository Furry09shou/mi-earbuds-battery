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
    private readonly Action<EarbudsUpdate>? _onUpdateApplied;
    private readonly Action<bool>? _onAliveChanged;
    private readonly DispatcherTimer _aliveTimer;
    private bool _lastAlive = true;

    private bool _initialized;

    // 多设备：MAC → 观测状态（本会话内出现过的所有已识别设备）
    private readonly Dictionary<string, DeviceState> _devices = new(StringComparer.OrdinalIgnoreCase);
    private DeviceState? _active;

    /// <summary>当前面板正在显示的设备 MAC（托盘提示只对该设备弹出）。</summary>
    public string? ActiveMac => _active?.Mac;

    // 检查到的新版本（再点一次按钮打开下载页）
    private UpdateInfo? _updateInfo;

    private const double StaleAfterSeconds = 8;
    // 设备切换列表只显示 10 分钟内见过的设备（避免路人耳机长期滞留）
    private static readonly TimeSpan DeviceListWindow = TimeSpan.FromMinutes(10);
    private const int JumpThreshold = 25;
    private static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(3);

    private static readonly Color GoodColor = Color.FromRgb(0x6F, 0xBF, 0x73);
    private static readonly Color MidColor = Color.FromRgb(0xD9, 0xA1, 0x3B);
    private static readonly Color LowColor = Color.FromRgb(0xD9, 0x6A, 0x5B);
    private static readonly Color UnknownColor = Color.FromRgb(0x4A, 0x4A, 0x52);

    /// <summary>单台设备本会话内的观测状态。</summary>
    private sealed class DeviceState
    {
        public required string Mac { get; init; }
        public string Name = "";
        public DateTime LastSeen = DateTime.MinValue;
        public int? Rssi;

        // 抖动抑制：充电触点瞬态会造成个别字段跳变，跳变过大时先压住
        public (int Value, DateTime Time, int Suppressed)? Left, Right, Case;

        // 最近一次有效帧的在仓状态（无效帧期间保持显示）
        public bool? LeftInCase, RightInCase;
    }

    public MainWindow(EarbudsWatcher watcher, AppConfig config,
        Action<EarbudsUpdate>? onUpdateApplied = null, Action<bool>? onAliveChanged = null)
    {
        InitializeComponent();
        _watcher = watcher;
        _onUpdateApplied = onUpdateApplied;
        _onAliveChanged = onAliveChanged;

        RestorePosition(config);

        _capture.Ticked += () => Dispatcher.Invoke(RefreshCaptureLive);

        AutoStartCheck.IsChecked = AutoStartHelper.IsEnabled();
        _initialized = true;

        _watcher.UpdateReceived += u => Dispatcher.Invoke(() => ApplyUpdate(u));

        _aliveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _aliveTimer.Tick += (_, _) => RefreshAliveState();
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
        var st = GetOrAdd(u.Mac);
        st.LastSeen = u.Timestamp;
        st.Rssi = u.Rssi;
        var s = u.Snapshot;
        if (st.Name.Length == 0)
            st.Name = string.IsNullOrWhiteSpace(u.DisplayName) ? u.Mac : u.DisplayName;

        // 电量值（带抖动过滤；null 表示本帧无效，维持旧显示）
        var left = Filter(ref st.Left, s.LeftPercent);
        var right = Filter(ref st.Right, s.RightPercent);
        var cse = Filter(ref st.Case, s.CasePercent);

        // 在仓状态：只在收到有效值时更新，避免噪声帧闪烁
        if (s.LeftPercent is not null) st.LeftInCase = s.LeftInCase;
        if (s.RightPercent is not null) st.RightInCase = s.RightInCase;

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
        SetRingInstant(LeftRing, st.Left);
        SetRingInstant(RightRing, st.Right);
        SetRingInstant(CaseRing, st.Case);
        RefreshAliveState();
    }

    private static string InCaseText(bool? inCase) => inCase switch
    {
        null => "--",
        true => "在仓充电",
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

    private static Color ColorFor(int v) => v switch
    {
        >= 50 => GoodColor,
        >= 20 => MidColor,
        _ => LowColor,
    };

    // ---------- 存活状态 ----------

    private void RefreshAliveState()
    {
        var st = _active;
        // 断开判定：广播新鲜 OR 系统蓝牙仍保持连接（合盖后广播立停，但系统
        // ACL 会保持几秒；两者都失去才算断开，避免慢判/误判）
        bool hasData = st is not null && st.LastSeen != DateTime.MinValue;
        bool fresh = hasData && (DateTime.Now - st!.LastSeen).TotalSeconds <= StaleAfterSeconds;
        // 广播停了不代表断开（耳机连着电脑用时就不再广播）。系统连接判定两路：
        // Win32 蓝牙枚举（部分耳机查不到）+ 音频端点（耳机能出声就一定在）
        var probeName = st!.Name.Length > 0 ? st.Name : null;
        bool systemConnected = hasData && !fresh && (
            BtConnectionProbe.IsConnected(st.Mac, probeName) ||
            AudioEndpointProbe.HasActiveEndpoint(probeName));
        bool alive = fresh || systemConnected;

        LiveDot.Fill = new SolidColorBrush(alive ? GoodColor : UnknownColor);
        StartPulse(alive);

        StateText.Text = !hasData
            ? "请打开充电仓盖"
            : alive
                ? fresh
                    ? $"实时更新 · 信号 {st!.Rssi} dBm"
                    : "蓝牙保持连接 · 等待新广播"
                : "信号丢失 · 请打开仓盖刷新电量";

        CardsGrid.Opacity = hasData && !alive ? 0.45 : 1.0;

        if (hasData)
        {
            // 通道级离线只在广播新鲜时判定：广播停了可能只是合盖，
            // 不能据此说某只耳机离线
            RenderChannel(LeftCard, LeftStatusText, LeftBolt, st!.Left, st.LeftInCase, fresh);
            RenderChannel(RightCard, RightStatusText, RightBolt, st.Right, st.RightInCase, fresh);

            if (st.Case is { } c)
            {
                bool caseOnline = !fresh || (DateTime.Now - c.Time).TotalSeconds <= StaleAfterSeconds;
                CaseCard.Opacity = caseOnline ? 1.0 : 0.45;
                CaseStatusText.Text = caseOnline ? $"电量 {c.Value}%" : "离线";
            }
            else
            {
                CaseCard.Opacity = 1.0;
            }
        }

        if (!hasData || !alive)
            CaseStatusText.Text = "等待广播";

        // 连接/断开状态变化时通知托盘（断开后托盘悬浮提示不再挂旧电量）
        if (_lastAlive != alive)
        {
            _lastAlive = alive;
            _onAliveChanged?.Invoke(alive);
        }

        UpdateSwitcherVisibility();
    }

    /// <summary>
    /// 单通道渲染：从未见过有效值保持未知态（"--"，不算离线）；
    /// 广播新鲜但该通道长期无有效值 = 只有这一路离线，单独灰显。
    /// </summary>
    private static void RenderChannel(Border card, TextBlock status, System.Windows.Shapes.Path bolt,
        (int Value, DateTime Time, int Suppressed)? state, bool? inCase, bool judgeOffline)
    {
        if (state is null)
        {
            card.Opacity = 1.0;
            status.Text = "--";
            bolt.Visibility = Visibility.Collapsed;
            return;
        }

        bool online = !judgeOffline ||
                      (DateTime.Now - state.Value.Time).TotalSeconds <= StaleAfterSeconds;
        card.Opacity = online ? 1.0 : 0.45;
        status.Text = online ? InCaseText(inCase) : "离线";
        bolt.Visibility = online && inCase == true ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void SetRingInstant(BatteryRing ring, (int Value, DateTime Time, int Suppressed)? state)
    {
        if (state is { } s)
        {
            ring.RingColor = ColorFor(s.Value);
            ring.SetInstant(s.Value);
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

    private void CloseButton_Click(object sender, RoutedEventArgs e) => HideToTray();

    private void HideToTray() => Hide();

    private void RebindButton_Click(object sender, RoutedEventArgs e)
    {
        _watcher.Unbind();
        _devices.Clear();
        _active = null;
        DevicePopup.IsOpen = false;

        DeviceNameText.Text = "正在搜索…";
        MacText.Text = "";
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

    private List<DeviceState> RecentDevices() =>
        _devices.Values
            .Where(d => ReferenceEquals(d, _active) ||
                        DateTime.Now - d.LastSeen <= DeviceListWindow)
            .OrderByDescending(d => d.LastSeen)
            .ToList();

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
        string part((int Value, DateTime Time, int Suppressed)? s) =>
            s is null ? "--"
            : DateTime.Now - s.Value.Time <= TimeSpan.FromSeconds(StaleAfterSeconds)
                ? $"{s.Value.Value}%"
                : "离线";
        return $"左 {part(d.Left)} · 右 {part(d.Right)} · 仓 {part(d.Case)}";
    }

    /// <summary>设备切换列表中的一行。</summary>
    private sealed class DeviceOption
    {
        public required string Mac { get; init; }
        public required string Name { get; init; }
        public required string Summary { get; init; }
        public required bool IsCurrent { get; init; }
        public required bool Online { get; init; }

        public System.Windows.Media.Brush Dot =>
            new SolidColorBrush(Online ? GoodColor : UnknownColor);
        public string Badge => IsCurrent ? "当前" : "";
        public Visibility BadgeVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
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

    // ==================== 适配视图：名单 + 分页向导（窗口内跳转） ====================

    private const string IssueUrlBase = "https://github.com/Furry09shou/ronghui-earbuds/issues/new";
    private const double MainViewHeight = 448;
    private const double AdapterListHeight = 480;
    private const double AdapterWizardHeight = 560;
    private const string ModelPlaceholder = "例如：Redmi Buds 5";
    private static readonly Color PlaceholderColor = Color.FromRgb(0x5C, 0x5C, 0x66);
    private static readonly Color InputColor = Color.FromRgb(0xED, 0xED, 0xF0);

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
                ? Color.FromRgb(0xE8, 0x7A, 0x3E)
                : Color.FromRgb(0x3A, 0x3A, 0x42));
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
