using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using MiEarbuds.App.Core;
using MiEarbuds.App.UI;

namespace MiEarbuds.App;

public partial class MainWindow : Window
{
    private readonly EarbudsWatcher _watcher;
    private readonly Action<EarbudsUpdate>? _onUpdateApplied;
    private readonly DispatcherTimer _aliveTimer;

    private bool _initialized;
    private DateTime _lastSeen = DateTime.MinValue;
    private int? _rssi;

    // 抖动抑制：充电触点瞬态会造成个别字段跳变，跳变过大时先压住
    private (int Value, DateTime Time, int Suppressed)? _lastLeft;
    private (int Value, DateTime Time, int Suppressed)? _lastRight;
    private (int Value, DateTime Time, int Suppressed)? _lastCase;

    // 最近一次有效帧的在仓状态（无效帧期间保持显示）
    private bool? _lastLeftInCase;
    private bool? _lastRightInCase;

    private const double StaleAfterSeconds = 8;
    private const int JumpThreshold = 25;
    private static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(3);

    private static readonly Color GoodColor = Color.FromRgb(0x6F, 0xBF, 0x73);
    private static readonly Color MidColor = Color.FromRgb(0xD9, 0xA1, 0x3B);
    private static readonly Color LowColor = Color.FromRgb(0xD9, 0x6A, 0x5B);
    private static readonly Color UnknownColor = Color.FromRgb(0x4A, 0x4A, 0x52);

    public MainWindow(EarbudsWatcher watcher, AppConfig config,
        Action<EarbudsUpdate>? onUpdateApplied = null)
    {
        InitializeComponent();
        _watcher = watcher;
        _onUpdateApplied = onUpdateApplied;

        RestorePosition(config);

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
        _lastSeen = u.Timestamp;
        _rssi = u.Rssi;
        var s = u.Snapshot;

        DeviceNameText.Text = "Mi Air2 SE";
        MacText.Text = u.Mac;

        // 电量值（带抖动过滤；null 表示本帧无效，维持旧显示）
        ApplyRing(LeftRing, Filter(ref _lastLeft, s.LeftPercent));
        ApplyRing(RightRing, Filter(ref _lastRight, s.RightPercent));
        ApplyRing(CaseRing, Filter(ref _lastCase, s.CasePercent));

        // 在仓状态与状态文字：只在收到有效值时更新，避免噪声帧闪烁
        if (s.LeftPercent is not null) _lastLeftInCase = s.LeftInCase;
        if (s.RightPercent is not null) _lastRightInCase = s.RightInCase;

        LeftStatusText.Text = InCaseText(_lastLeftInCase);
        RightStatusText.Text = InCaseText(_lastRightInCase);
        LeftBolt.Visibility = _lastLeftInCase == true ? Visibility.Visible : Visibility.Collapsed;
        RightBolt.Visibility = _lastRightInCase == true ? Visibility.Visible : Visibility.Collapsed;
        CaseStatusText.Text = s.CasePercent is null
            ? CaseStatusText.Text
            : $"电量 {s.CasePercent}%";

        RefreshAliveState();
        _onUpdateApplied?.Invoke(u);
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
        bool alive = (DateTime.Now - _lastSeen).TotalSeconds <= StaleAfterSeconds;
        bool hasData = _lastSeen != DateTime.MinValue;

        LiveDot.Fill = new SolidColorBrush(alive ? GoodColor : UnknownColor);
        StartPulse(alive);

        StateText.Text = !hasData
            ? "请打开充电仓盖"
            : alive
                ? $"实时更新 · 信号 {_rssi} dBm"
                : "信号丢失 · 请打开仓盖刷新电量";

        CardsGrid.Opacity = hasData && !alive ? 0.45 : 1.0;

        if (!hasData || !alive)
            CaseStatusText.Text = "等待广播";
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
        _lastSeen = DateTime.MinValue;
        _lastLeft = _lastRight = _lastCase = null;
        _lastLeftInCase = _lastRightInCase = null;
        _rssi = null;

        DeviceNameText.Text = "正在搜索…";
        MacText.Text = "";
        LeftRing.SetInstant(-1); LeftRing.RingColor = UnknownColor;
        RightRing.SetInstant(-1); RightRing.RingColor = UnknownColor;
        CaseRing.SetInstant(-1); CaseRing.RingColor = UnknownColor;
        LeftBolt.Visibility = RightBolt.Visibility = Visibility.Collapsed;
        LeftStatusText.Text = RightStatusText.Text = CaseStatusText.Text = "--";
        RefreshAliveState();
    }

    private void AutoStartCheck_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized) return;
        AutoStartHelper.Set(AutoStartCheck.IsChecked == true);
    }

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
