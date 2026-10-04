using System.Diagnostics;
using System.IO;
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
    private DateTime _lastSeen = DateTime.MinValue;
    private int? _rssi;

    // 抖动抑制：充电触点瞬态会造成个别字段跳变，跳变过大时先压住
    private (int Value, DateTime Time, int Suppressed)? _lastLeft;
    private (int Value, DateTime Time, int Suppressed)? _lastRight;
    private (int Value, DateTime Time, int Suppressed)? _lastCase;

    // 最近一次有效帧的在仓状态（无效帧期间保持显示）
    private bool? _lastLeftInCase;
    private bool? _lastRightInCase;

    // 检查到的新版本（再点一次按钮打开下载页）
    private UpdateInfo? _updateInfo;

    private const double StaleAfterSeconds = 8;
    private const int JumpThreshold = 25;
    private static readonly TimeSpan JumpWindow = TimeSpan.FromSeconds(3);

    private static readonly Color GoodColor = Color.FromRgb(0x6F, 0xBF, 0x73);
    private static readonly Color MidColor = Color.FromRgb(0xD9, 0xA1, 0x3B);
    private static readonly Color LowColor = Color.FromRgb(0xD9, 0x6A, 0x5B);
    private static readonly Color UnknownColor = Color.FromRgb(0x4A, 0x4A, 0x52);

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

        // 连接/断开状态变化时通知托盘（断开后托盘悬浮提示不再挂旧电量）
        if (_lastAlive != alive)
        {
            _lastAlive = alive;
            _onAliveChanged?.Invoke(alive);
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
    private const double AdapterWizardHeight = 520;
    private const string ModelPlaceholder = "例如：Redmi Buds 5";
    private static readonly Color PlaceholderColor = Color.FromRgb(0x5C, 0x5C, 0x66);
    private static readonly Color InputColor = Color.FromRgb(0xED, 0xED, 0xF0);

    private static readonly (string Title, string Detail)[] AdapterSteps =
    {
        ("双耳入仓，开盖等 10 秒", "把两只耳机都放回充电仓，保持仓盖打开，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。"),
        ("取出左耳，等 10 秒", "把左耳从仓中取出（戴或不戴都可以），右耳留在仓内，等待约 10 秒。"),
        ("左耳放回，等 10 秒", "把左耳放回仓内，等待约 10 秒。"),
        ("取出右耳，等 10 秒", "把右耳从仓中取出，左耳留在仓内，等待约 10 秒。"),
        ("右耳放回，完成采集", "把右耳放回仓内，等待约 10 秒，然后点击「完成并上传」。"),
    };

    private readonly CaptureService _capture = new();
    private int _adapterPage = -1;      // -1=不在适配视图，0=型号页，1..5=动作步骤
    private bool _adapterRunning;
    private int _captureShown = -1;
    private Border[]? _dots;

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
            // 步骤标题与详情
            var (title, detail) = AdapterSteps[page - 1];
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
            _capture.Start();
            _adapterRunning = true;
            _capture.AddMarker("开始采集");
            ShowAdapterPage(1);
            return;
        }

        _capture.AddMarker($"完成阶段{_adapterPage}:{AdapterSteps[_adapterPage - 1].Title}");
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
            var (jsonl, zip) = _capture.Export(model);

            // 本地差分分析：自动生成候选布局报告，随 Issue 一起提交
            string analysis;
            try { analysis = FormatAnalyzer.AnalyzeFile(jsonl); }
            catch { analysis = "本地分析失败（数据仍完整保留，可人工分析）。"; }
            var analysisPath = Path.ChangeExtension(jsonl, ".analysis.txt");
            File.WriteAllText(analysisPath, analysis);

            var body = $"机型：{model}\n" +
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
