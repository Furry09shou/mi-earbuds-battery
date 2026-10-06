using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using RonghuiEarbuds.App.Core;

namespace RonghuiEarbuds.App;

/// <summary>
/// 悬浮迷你电量条：可拖动的极简置顶小条。默认只显示关注设备一行；
/// 右键菜单勾选多台设备后纵向展开为多行（每台一行：名字 + 三格电量或整机兜底）。
/// 数据过期自动回「--」，断连行灰显；双击打开主面板，位置与勾选名单均持久化。
/// </summary>
public sealed class MiniBarWindow : Window
{
    private const double FreshSeconds = 3;
    private const double StaleSeconds = 8;
    private const double RowHeight = 44;
    private const double RowGap = 8;

    private readonly AppConfig _config;
    private readonly StackPanel _rowsHost = new();
    private readonly List<BarRow> _rows = new();
    private readonly Dictionary<string, BarDevice> _devices = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _freshTimer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _themeHooked;

    /// <summary>单台设备的观测数据（广播喂入 + 系统电量兜底）。</summary>
    private sealed class BarDevice
    {
        public string Name = "";
        public int?[] Values = new int?[3];
        public bool[] InCase = new bool[3];
        public readonly DateTime[] Times = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
        public DateTime BroadcastSeen = DateTime.MinValue;
    }

    /// <summary>一行 UI（对应一台勾选显示的设备）。</summary>
    private sealed class BarRow
    {
        public Grid Root = null!;
        public TextBlock NameText = null!;
        public Canvas NameCanvas = null!;
        public Grid NameHost = null!;
        public TranslateTransform NameShift = null!;
        public string LastName = "";
        public readonly (TextBlock Value, Ellipse Dot, Path Bolt)[] Cells = new (TextBlock, Ellipse, Path)[3];
        public readonly StackPanel?[] Panels = new StackPanel[3];
        public readonly TextBlock[] Labels = new TextBlock[3];
    }

    /// <summary>悬浮条三列小标签：L / R / 仓（仓随语言切换）。</summary>
    private static string MiniLabel(int i) => i switch
    {
        0 => "L",
        1 => "R",
        _ => L.T("mini.caseLabel"),
    };

    /// <summary>App 注入：双击迷你条时显示主窗口。</summary>
    public Action? OpenMainRequested { get; set; }

    /// <summary>App 注入：按 MAC 取该设备的系统整机电量（广播停发/未适配时的兜底显示）。</summary>
    public Func<string, int?>? SystemBatteryProvider { get; set; }

    /// <summary>App 注入：设备是否存活（连接心跳信号），决定行灰显与名字颜色。</summary>
    public Func<string, bool>? AliveProvider { get; set; }

    /// <summary>App 注入：右键菜单设备名单（名字, MAC）。</summary>
    public Func<IReadOnlyList<(string Name, string Mac)>>? DeviceListProvider { get; set; }

    /// <summary>App 注入：当前主面板关注设备 MAC（勾选名单为空时显示它）。</summary>
    public Func<string?>? ActiveMacProvider { get; set; }

    /// <summary>App 注入：用户点悬浮条「播报电量」按钮时触发（App 负责组织语句并 TTS）。</summary>
    public Action? VoiceRequested { get; set; }

    public MiniBarWindow(AppConfig config)
    {
        _config = config;

        Width = 272;   // 窗口=卡片尺寸；高度由内容自动决定（SizeToContent），
                       // 手工公式算不准行堆栈+分隔线的总高，总会差几个像素
        SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.Manual;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        ShowActivated = false;
        Title = L.T("mini.title");

        L.Changed += () => Dispatcher.Invoke(ApplyLanguage);

        var root = new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            Child = _rowsHost,
        };
        root.SetResourceReference(Border.BackgroundProperty, "T.WindowBg");
        root.SetResourceReference(Border.BorderBrushProperty, "T.CardBorder");
        _rowsHost.Margin = new Thickness(0, 2, 0, 2);

        // 窗口=卡片尺寸，无呼吸边距。曾用"窗口放大留白画投影"的方案，但 AllowsTransparency
        // 窗口的 alpha=0 区域会被分层窗口按像素穿透，点击/拖动随机失灵（连 alpha=1 都救不了
        // 边缘带），只能放弃 DropShadowEffect——圆角+1px 边框同样干净，且全窗口可靠命中。
        Content = root;

        // 左键：单击（未拖动）弹设备选择菜单；拖动换位置；双击开主面板
        Point? pressPos = null;
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                pressPos = null;
                OpenMainRequested?.Invoke();
                return;
            }
            pressPos = e.GetPosition(this);
        };
        MouseLeftButtonUp += (_, e) =>
        {
            if (pressPos is { } p && (e.GetPosition(this) - p).Length < 4)
                OpenDeviceMenu();   // 点击（非拖动）→ 设备选择
            pressPos = null;
        };
        MouseMove += (_, e) =>
        {
            if (e.LeftButton == MouseButtonState.Pressed && pressPos is { } p &&
                (e.GetPosition(this) - p).Length >= 4)
            {
                pressPos = null;   // 进入拖动，松手不再算点击
                try { DragMove(); } catch { /* 快速点击可能抛异常 */ }
                PersistPosition();
            }
        };

        ThemeManager.ThemeChanged += () => Dispatcher.Invoke(Render);   // 换肤重刷硬刷的颜色
        _freshTimer.Tick += (_, _) => Render();
    }

    // ---------- 数据 ----------

    /// <summary>接收任意已解析设备的广播数据（App 全量转发）。</summary>
    public void Push(EarbudsUpdate u)
    {
        var dev = GetOrAdd(u.Mac);
        if (dev.Name.Length == 0)
            dev.Name = string.IsNullOrWhiteSpace(u.DisplayName) ? L.T("mini.earbuds") : u.DisplayName!;
        dev.BroadcastSeen = u.Timestamp;
        var s = u.Snapshot;
        dev.Values[0] = s.LeftPercent;
        dev.Values[1] = s.RightPercent;
        dev.Values[2] = s.CasePercent;
        if (s.LeftPercent is not null) dev.InCase[0] = s.LeftInCase;
        if (s.RightPercent is not null) dev.InCase[1] = s.RightInCase;   // 仓无「在仓」概念，InCase[2] 恒 false
        dev.Times[0] = dev.Times[1] = dev.Times[2] = u.Timestamp;
    }

    /// <summary>连接枚举解析出设备名时补投（不必等广播）。</summary>
    public void SetDeviceName(string mac, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var dev = GetOrAdd(mac);
        if (dev.Name != name)
            dev.Name = name;
    }

    private BarDevice GetOrAdd(string mac)
    {
        if (!_devices.TryGetValue(mac, out var dev))
            _devices[mac] = dev = new BarDevice();
        return dev;
    }

    /// <summary>
    /// 当前应显示的设备序列：勾选名单 ∩ 已知设备（按名单顺序）；
    /// 勾选为空时回退到主面板关注设备，再不行就第一台已知设备。
    /// </summary>
    private List<(string Mac, BarDevice Dev)> VisibleDevices()
    {
        var pinned = new HashSet<string>(_config.MiniBarPinned, StringComparer.OrdinalIgnoreCase);
        var result = new List<(string, BarDevice)>();
        if (DeviceListProvider is { } provider)
        {
            foreach (var (name, mac) in provider.Invoke())
            {
                // 名单接口给出的设备直接信任（占位行显示 --/系统值，广播到达自动填充）；
                // 若还要求"观测过才显示"，启动时没广播的设备永远进不了悬浮条
                if (!pinned.Contains(mac)) continue;
                var dev = GetOrAdd(mac);
                if (dev.Name.Length == 0) dev.Name = name;
                result.Add((mac, dev));
            }
        }
        // 勾选了但名单接口还没给出的（少见）：只要有观测数据也显示
        foreach (var mac in _config.MiniBarPinned)
        {
            if (!result.Any(r => r.Item1.Equals(mac, StringComparison.OrdinalIgnoreCase)) &&
                _devices.TryGetValue(mac, out var dev2))
                result.Add((mac, dev2));
        }
        if (result.Count == 0)
        {
            var active = ActiveMacProvider?.Invoke();
            if (active is { } am && _devices.TryGetValue(am, out var dev3))
                result.Add((am, dev3));
            else if (_devices.Count > 0)
            {
                var first = _devices.First();
                result.Add((first.Key, first.Value));
            }
        }
        return result;
    }

    // ---------- 渲染 ----------

    private void Render()
    {
        var list = VisibleDevices();
        EnsureRows(list.Count);

        for (var i = 0; i < list.Count; i++)
            RenderRow(_rows[i], list[i].Mac, list[i].Dev);

        SyncVolume();   // 每秒把系统真实音量同步到滑条（拖拽中除外）

        // 高度由 SizeToContent 自动贴合内容，无需手工计算
    }

    private void RenderRow(BarRow row, string mac, BarDevice dev)
    {
        // 名字
        var name = dev.Name.Length > 0 ? dev.Name : L.T("mini.earbuds");
        if (name != row.LastName)
        {
            row.LastName = name;
            row.NameText.Text = name;
            Dispatcher.BeginInvoke(() => UpdateMarquee(row), DispatcherPriority.Render);
        }

        // 行级存活：连接心跳信号（AliveProvider），而非广播——广播不可靠
        //（连接播放停发、未适配机型从不广播，但设备都活着）
        bool alive = AliveProvider?.Invoke(mac) ?? true;
        row.Root.Opacity = alive ? 1.0 : 0.75;
        // 名字颜色跟随状态：在线主文字色，离线才灰显（之前固定灰色，状态分不清）
        row.NameText.SetResourceReference(TextBlock.ForegroundProperty,
            alive ? "T.TextPrimary" : "T.TextDim");

        // 三格是否有新鲜分耳数据；没有时若系统整机电量可得 → 单格兜底模式
        bool anyFresh = false;
        for (var i = 0; i < 3; i++)
        {
            if (dev.Values[i] is not null &&
                (DateTime.Now - dev.Times[i]).TotalSeconds <= FreshSeconds)
                anyFresh = true;
        }
        int? sys = anyFresh ? null : SystemBatteryProvider?.Invoke(mac);

        for (var i = 0; i < 3; i++)
        {
            var (value, dot, bolt) = row.Cells[i];
            var panel = row.Panels[i];
            bool fresh = anyFresh && dev.Values[i] is not null &&
                         (DateTime.Now - dev.Times[i]).TotalSeconds <= FreshSeconds;

            if (sys is { } sv && i == 0)
            {
                // 系统整机电量单格：标签切「整机」，隐藏闪电（系统值无在仓概念）
                panel!.Visibility = Visibility.Visible;
                value.Text = $"{sv}%";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
                dot.Fill = new SolidColorBrush(ThemeManager.ColorFor(sv));
                bolt.Visibility = Visibility.Collapsed;
                row.Labels[0].Text = L.T("mini.all");
            }
            else if (sys is { })
            {
                panel!.Visibility = Visibility.Collapsed;   // 单格模式隐藏其余两格
            }
            else if (fresh)
            {
                int v = dev.Values[i]!.Value;
                panel!.Visibility = Visibility.Visible;
                value.Text = $"{v}%";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
                dot.Fill = new SolidColorBrush(ThemeManager.ColorFor(v));
                bolt.Visibility = dev.InCase[i] ? Visibility.Visible : Visibility.Collapsed;
                row.Labels[i].Text = MiniLabel(i);
            }
            else
            {
                panel!.Visibility = Visibility.Visible;
                value.Text = "--";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextDim");
                dot.Fill = new SolidColorBrush(ThemeManager.GetColor("T.Unknown"));
                bolt.Visibility = Visibility.Collapsed;
                row.Labels[i].Text = MiniLabel(i);
            }
        }
    }

    /// <summary>确保行数匹配（多/少一台时重建行与分隔线）。媒体栏恒在末尾重建。</summary>
    private void EnsureRows(int count)
    {
        // 启动时可能 count==0（尚无设备）：行区无需重建，但媒体栏仍要建出来
        if (_rows.Count == count && _mediaBar is not null) return;
        _rows.Clear();
        _rowsHost.Children.Clear();
        for (var i = 0; i < count; i++)
        {
            if (i > 0)
            {
                var sep = new Border
                {
                    Height = 1,
                    Margin = new Thickness(14, 0, 14, 0),
                    Opacity = 0.6,
                };
                sep.SetResourceReference(Border.BackgroundProperty, "T.CardBorder");
                _rowsHost.Children.Add(sep);
            }
            var row = BuildRow();
            _rows.Add(row);
            _rowsHost.Children.Add(row.Root);
        }
        _mediaBar = BuildMediaBar();
        ApplyMediaBar();
        _rowsHost.Children.Add(_mediaBar);
    }

    private BarRow BuildRow()
    {
        var row = new BarRow();
        var grid = new Grid { Margin = new Thickness(12, 0, 12, 0), Height = RowHeight };
        // 中文字形在字体行框里偏上约 1px，整行内容下移补偿，使上下留白视觉相等
        grid.RenderTransform = new TranslateTransform(0, 1);
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var nameText = new TextBlock { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
        nameText.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        var nameShift = new TranslateTransform();
        nameText.RenderTransform = nameShift;
        var nameCanvas = new Canvas { Height = 14 };   // 贴合文字行高，避免文字偏上不对齐
        nameCanvas.Children.Add(nameText);
        var nameHost = new Grid { ClipToBounds = true, MinHeight = 14, VerticalAlignment = VerticalAlignment.Center };
        nameHost.Children.Add(nameCanvas);
        nameHost.SizeChanged += (_, _) => UpdateMarquee(row);
        Grid.SetColumn(nameHost, 0);
        grid.Children.Add(nameHost);

        row.NameText = nameText;
        row.NameCanvas = nameCanvas;
        row.NameHost = nameHost;
        row.NameShift = nameShift;

        for (var i = 0; i < 3; i++)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(6, 0, 0, 0) };

            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Text = "--",
            };
            value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
            var label = new TextBlock
            {
                Text = MiniLabel(i),
                FontSize = 9.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(3, 1, 0, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "T.TextDim");
            row.Labels[i] = label;

            var bolt = new Path
            {
                Data = Geometry.Parse("M 7.2 0 L 0.8 8.4 L 4.6 8.4 L 3.6 15 L 10.4 5.6 L 6.4 5.6 Z"),
                Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3E)),
                Stretch = Stretch.Uniform,
                Width = 8,
                Height = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
                Visibility = Visibility.Collapsed,
            };

            panel.Children.Add(dot);
            panel.Children.Add(value);
            panel.Children.Add(label);
            panel.Children.Add(bolt);
            Grid.SetColumn(panel, i + 1);
            grid.Children.Add(panel);

            row.Cells[i] = (value, dot, bolt);
            row.Panels[i] = panel;
        }
        row.Root = grid;
        return row;
    }

    // ---------- 跑马灯 ----------

    /// <summary>名字超出行宽时左右来回滑动；放得下则左对齐（各行起点一致，也与下方媒体栏左缘对齐）。</summary>
    private void UpdateMarquee(BarRow row)
    {
        row.NameShift.X = 0;
        double viewW = row.NameHost.ActualWidth;
        double textW = row.NameText.DesiredSize.Width;   // 文字宽度读 TextBlock（Canvas 自身 DesiredSize 恒 0）
        if (viewW <= 0 || textW <= 0) return;

        row.NameCanvas.BeginAnimation(Canvas.LeftProperty, null);
        row.NameShift.BeginAnimation(TranslateTransform.XProperty, null);

        if (textW <= viewW + 0.5) return;   // 放得下：左对齐。
        // 曾按文字宽度在名字区域内水平居中——多台设备名字长短不一时，
        // 每行起点各不相同，纵向看名字列是参差的（用户要求对齐）。

        double overflow = textW - viewW + 12;   // 缓冲，让尾部完整滑入视野
        double speed = 24;                       // px/s 基速
        double dur = Math.Min(8, Math.Max(2.5, overflow / speed));
        var anim = new DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = TimeSpan.FromSeconds(dur),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        row.NameShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    // ---------- 媒体控制栏 ----------

    private Border? _mediaBar;
    private TextBlock? _playGlyph;
    private TextBlock? _mediaTitle;
    private Grid? _mediaHost;
    private Canvas? _mediaCanvas;
    private TranslateTransform? _mediaShift;
    private bool _playing;
    private Slider? _volumeSlider;
    private TextBlock? _muteGlyph;
    private TextBlock? _volumeValueText;
    private bool _volumeDragging;   // 用户正拖拽滑条：自刷同步不覆盖
    private bool _volumeSyncing;    // 程序写入滑条值：避免 ValueChanged 回声写系统

    /// <summary>媒体控制栏开关（设置页切换时由 ApplyEnabled 统一应用）。</summary>
    private void ApplyMediaBar()
    {
        if (_mediaBar is null) return;
        _mediaBar.Visibility = _config.MiniBarMediaControls ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>同步系统当前播放内容：曲目标题显示 + 播放/暂停图标跟随真实状态。</summary>
    public void SetMediaInfo(string? title, string? artist, bool playing)
    {
        if (_mediaTitle is { } t)
        {
            var text = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} — {title}";
            if (string.IsNullOrWhiteSpace(text))
            {
                t.Text = "";
                t.Visibility = Visibility.Collapsed;
            }
            else
            {
                t.Text = text;
                t.Visibility = Visibility.Visible;
            }
            // 布局完成后按实际宽度决定是否滚动（短标题原地显示）
            Dispatcher.BeginInvoke(UpdateMediaMarquee, DispatcherPriority.Render);
        }
        _playing = playing;
        if (_playGlyph is { } g) g.Text = playing ? "\uE769" : "\uE768";
    }

    /// <summary>媒体标题跑马灯：Canvas 提供无限约束测量（Grid 测宽会被列宽截断），
    /// 文字宽度读 TextBlock.DesiredSize（Canvas 自身 DesiredSize 恒 0）。</summary>
    private void UpdateMediaMarquee()
    {
        if (_mediaShift is null || _mediaHost is null || _mediaTitle is null) return;
        _mediaShift.X = 0;
        double viewW = _mediaHost.ActualWidth;
        double textW = _mediaTitle.DesiredSize.Width;
        _mediaCanvas?.BeginAnimation(Canvas.LeftProperty, null);
        _mediaShift.BeginAnimation(TranslateTransform.XProperty, null);

        if (_mediaTitle.Text.Length == 0) return;          // 未显示：原地
        if (viewW <= 0 || textW <= 0)
        {
            // 文本刚设置、尚未完成布局测量：挂一次性重试
            _mediaTitle.LayoutUpdated -= RetryMediaMarquee;
            _mediaTitle.LayoutUpdated += RetryMediaMarquee;
            return;
        }
        if (textW <= viewW + 0.5) return;                  // 放得下：左对齐不动

        double overflow = textW - viewW + 12;              // 缓冲，让尾部完整滑入视野
        double speed = 28;                                 // px/s 基速（标题可稍快）
        double dur = Math.Min(8, Math.Max(2.5, overflow / speed));
        var anim = new DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = TimeSpan.FromSeconds(dur),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        _mediaShift.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void RetryMediaMarquee(object? sender, EventArgs e)
    {
        _mediaTitle!.LayoutUpdated -= RetryMediaMarquee;
        Dispatcher.BeginInvoke(UpdateMediaMarquee, DispatcherPriority.Render);
    }

    /// <summary>底部控制栏：上一曲 / 播放暂停 / 下一曲 / 语音播报电量，标题列同步系统媒体会话。
    /// 媒体键全局生效（与当前播放器/耳机无关），播报走 App 注入回调。</summary>
    private Border BuildMediaBar()
    {
        var row = new Grid { Height = 34, Margin = new Thickness(10, 6, 10, 4) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 4; i++)
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        // 标题：超宽时跑马灯循环滚动（Canvas 无限约束测宽，宿主裁切）
        var title = new TextBlock
        {
            FontSize = 10.5,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        title.SetResourceReference(TextBlock.ForegroundProperty, "T.TextDim");
        var mediaShift = new TranslateTransform();
        title.RenderTransform = mediaShift;
        var titleCanvas = new Canvas { Height = 14, Margin = new Thickness(6, 0, 14, 0) };
        titleCanvas.Children.Add(title);
        var titleHost = new Grid
        {
            ClipToBounds = true,
            MinHeight = 14,
            VerticalAlignment = VerticalAlignment.Center,
        };
        titleHost.Children.Add(titleCanvas);
        titleHost.SizeChanged += (_, _) => UpdateMediaMarquee();
        _mediaTitle = title;
        _mediaCanvas = titleCanvas;
        _mediaShift = mediaShift;
        _mediaHost = titleHost;
        Grid.SetColumn(titleHost, 0);
        row.Children.Add(titleHost);

        var prev = MakeCtrlButton("\uE892", L.T("mini.prevTip"), MediaKeys.Prev);
        var play = MakeCtrlButton("\uE768", L.T("mini.playTip"), () =>
        {
            MediaKeys.PlayPause();
            // 先本地翻转给即时反馈，SMTC 回推事件会再校正为真实状态
            _playing = !_playing;
            if (_playGlyph is not null)
                _playGlyph.Text = _playing ? "\uE769" : "\uE768";
        });
        _playGlyph = (TextBlock)play.Content;
        var next = MakeCtrlButton("\uE893", L.T("mini.nextTip"), MediaKeys.Next);
        var speak = MakeCtrlButton("\uE767", L.T("mini.speakTip"), () => VoiceRequested?.Invoke());

        Grid.SetColumn(prev, 1);
        Grid.SetColumn(play, 2);
        Grid.SetColumn(next, 3);
        Grid.SetColumn(speak, 4);
        row.Children.Add(prev);
        row.Children.Add(play);
        row.Children.Add(next);
        row.Children.Add(speak);

        var sep = new Border
        {
            Height = 1,
            Margin = new Thickness(14, 0, 14, 0),
            Opacity = 0.6,
        };
        sep.SetResourceReference(Border.BackgroundProperty, "T.CardBorder");

        var wrap = new StackPanel();
        wrap.Children.Add(sep);
        wrap.Children.Add(row);
        wrap.Children.Add(BuildVolumeRow());
        var bar = new Border { Child = wrap };
        return bar;
    }

    /// <summary>媒体栏第二行：静音按钮 + 音量滑条，控制系统默认播放设备主音量（拖拽实时生效）。</summary>
    private Grid BuildVolumeRow()
    {
        var row = new Grid { Height = 28, Margin = new Thickness(10, 0, 10, 6) };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var mute = MakeCtrlButton("\uE767", L.T("mini.muteTip"), ToggleMute);
        _muteGlyph = (TextBlock)mute.Content;
        Grid.SetColumn(mute, 0);
        row.Children.Add(mute);

        // 右侧百分比数字：拖拽/自刷时随时更新
        var valueText = new TextBlock
        {
            Text = "--",
            FontSize = 10,
            FontWeight = FontWeights.SemiBold,
            TextAlignment = TextAlignment.Right,
            MinWidth = 28,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(2, 0, 2, 0),
        };
        valueText.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        _volumeValueText = valueText;
        Grid.SetColumn(valueText, 2);
        row.Children.Add(valueText);

        var slider = new Slider
        {
            Minimum = 0,
            Maximum = 100,
            Value = 50,
            Height = 18,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(4, 0, 0, 0),
            Cursor = Cursors.Hand,
            IsMoveToPointEnabled = true,   // 点轨道任意位置直接跳到该音量
            Template = VolumeSliderTemplate(),
        };
        slider.ValueChanged += (_, e) =>
        {
            var percent = Math.Round(e.NewValue);
            slider.ToolTip = $"{percent}%";
            if (_volumeValueText is { } vt) vt.Text = $"{percent}%";
            if (_volumeSyncing) return;   // 程序同步回写，不回声到系统
            VolumeControl.SetScalar((float)(e.NewValue / 100.0));
        };
        slider.PreviewMouseDown += (_, _) => _volumeDragging = true;
        slider.PreviewMouseUp += (_, _) => _volumeDragging = false;
        slider.LostMouseCapture += (_, _) => _volumeDragging = false;
        _volumeSlider = slider;
        Grid.SetColumn(slider, 1);
        row.Children.Add(slider);
        return row;
    }

    /// <summary>切换系统静音并即时更新图标（静音=斜线喇叭 E74F）。</summary>
    private void ToggleMute()
    {
        if (!VolumeControl.TryGetMute(out var muted)) return;
        VolumeControl.SetMute(!muted);
        if (_muteGlyph is not null)
            _muteGlyph.Text = muted ? "\uE767" : "\uE74F";
    }

    /// <summary>1s 自刷时把系统真实音量/静音状态同步回滑条与图标（默认设备可能被系统音量键改动）。</summary>
    private void SyncVolume()
    {
        if (_mediaBar is null || _mediaBar.Visibility != Visibility.Visible) return;
        if (_volumeSlider is not null && !_volumeDragging)
        {
            _volumeSyncing = true;
            if (VolumeControl.TryGetScalar(out var scalar))
                _volumeSlider.Value = Math.Round(scalar * 100);
            _volumeSyncing = false;
        }
        if (_muteGlyph is not null && VolumeControl.TryGetMute(out var muted))
            _muteGlyph.Text = muted ? "\uE74F" : "\uE767";
    }

    private static ControlTemplate? _volSliderTpl;

    private static ControlTemplate VolumeSliderTemplate() => _volSliderTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Slider}">
              <Grid Height="18" Background="Transparent">
                <Track x:Name="PART_Track" VerticalAlignment="Center">
                  <Track.DecreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.DecreaseLarge}" IsTabStop="False" Focusable="False" Height="4">
                      <RepeatButton.Template>
                        <ControlTemplate TargetType="{x:Type RepeatButton}">
                          <Border Background="{DynamicResource T.Accent}" CornerRadius="2"/>
                        </ControlTemplate>
                      </RepeatButton.Template>
                    </RepeatButton>
                  </Track.DecreaseRepeatButton>
                  <Track.IncreaseRepeatButton>
                    <RepeatButton Command="{x:Static Slider.IncreaseLarge}" IsTabStop="False" Focusable="False" Height="4">
                      <RepeatButton.Template>
                        <ControlTemplate TargetType="{x:Type RepeatButton}">
                          <Border Background="{DynamicResource T.CardBorder}" CornerRadius="2" Opacity="0.9"/>
                        </ControlTemplate>
                      </RepeatButton.Template>
                    </RepeatButton>
                  </Track.IncreaseRepeatButton>
                  <Track.Thumb>
                    <Thumb Width="12" Height="12" Focusable="False">
                      <Thumb.Template>
                        <ControlTemplate TargetType="{x:Type Thumb}">
                          <Ellipse Fill="{DynamicResource T.Accent}"/>
                        </ControlTemplate>
                      </Thumb.Template>
                    </Thumb>
                  </Track.Thumb>
                </Track>
              </Grid>
            </ControlTemplate>
            """);

    private static Button MakeCtrlButton(string glyph, string tip, Action action)
    {
        var ico = new TextBlock
        {
            Text = glyph,
            FontFamily = new FontFamily("Segoe MDL2 Assets"),
            FontSize = 13,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var btn = new Button
        {
            Width = 30,
            Height = 26,
            Margin = new Thickness(1, 0, 1, 0),
            Cursor = Cursors.Hand,
            ToolTip = tip,
            Content = ico,
            Template = CtrlButtonTemplate(),
        };
        ico.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        btn.Click += (_, _) => action();
        return btn;
    }

    private static ControlTemplate? _ctrlBtnTpl;

    private static ControlTemplate CtrlButtonTemplate() => _ctrlBtnTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Button}">
              <Border x:Name="Row" CornerRadius="7" Background="Transparent">
                <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Center"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Row" Property="Background" Value="#1426262D"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);

    // ---------- 设备选择浮层 ----------

    /// <summary>左键点击弹出设备选择浮层：勾选=在悬浮条显示该设备行（多选）。
    /// 用 Popup 而非 ContextMenu——后者弹出窗口不透明，圆角外的四角和被裁的阴影很难看。</summary>
    private Popup? _devicePopup;

    private void OpenDeviceMenu()
    {
        if (DeviceListProvider?.Invoke() is not { Count: > 0 } devices)
            return;
        if (_devicePopup is { } old) old.IsOpen = false;

        var pinned = new HashSet<string>(_config.MiniBarPinned, StringComparer.OrdinalIgnoreCase);
        var stack = new StackPanel();
        foreach (var (name, mac) in devices)
        {
            // 自绘按钮行：整行可点（MenuItem 的点击只认 Header 文本区），橙色勾标示当前显示
            var check = new TextBlock
            {
                Text = "\uE73E",
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x3E)),
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = pinned.Contains(mac) ? Visibility.Visible : Visibility.Collapsed,
            };
            var label = new TextBlock
            {
                Text = name,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
            var rowContent = new StackPanel { Orientation = Orientation.Horizontal };
            rowContent.Children.Add(check);
            rowContent.Children.Add(label);

            var item = new Button
            {
                Content = rowContent,
                Cursor = Cursors.Hand,
                Template = RowButtonTemplate(),
            };
            item.Click += (_, _) =>
            {
                bool nowPinned = check.Visibility == Visibility.Collapsed;
                check.Visibility = nowPinned ? Visibility.Visible : Visibility.Collapsed;
                if (nowPinned) pinned.Add(mac);
                else pinned.Remove(mac);
                _config.MiniBarPinned = pinned.ToList();
                _config.Save();
                Render();
            };
            stack.Children.Add(item);
        }

        var card = new Border
        {
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(6),
            Child = stack,
            // 阴影呼吸空间：没有它阴影会被 Popup 矩形边界硬裁，看起来像不透明的系统矩形
            Margin = new Thickness(14, 10, 14, 16),
        };
        card.SetResourceReference(Border.BackgroundProperty, "T.WindowBg");
        card.SetResourceReference(Border.BorderBrushProperty, "T.CardBorder");
        card.BorderThickness = new Thickness(1);
        card.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 14, ShadowDepth = 2, Opacity = 0.35, Direction = 270,
        };

        // Placement 用 AbsolutePoint+物理坐标：MousePoint 在高 DPI 下有偏移 bug
        //（150% 缩放实测弹出点左偏一个 popup 宽度）。AbsolutePoint 的偏移按逻辑 DIP
        // 计算，物理光标坐标需除以 DPI 缩放
        var mouse = System.Windows.Forms.Cursor.Position;
        var dpi = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1.0;
        _devicePopup = new Popup
        {
            Placement = System.Windows.Controls.Primitives.PlacementMode.AbsolutePoint,
            HorizontalOffset = mouse.X / dpi,
            VerticalOffset = mouse.Y / dpi,
            StaysOpen = false,             // 点外部自动关闭
            AllowsTransparency = true,     // 分层窗口：圆角外透明、阴影完整渲染
            PopupAnimation = PopupAnimation.Fade,
            Child = card,
        };
        _devicePopup.IsOpen = true;
    }

    // 行按钮模板：整行命中+悬停高亮，与应用弹层同风格。
    // 纯代码构建窗口拿不到隐式样式，用 XamlReader 加载模板并缓存。
    private static ControlTemplate? _rowBtnTpl;

    private static ControlTemplate RowButtonTemplate() => _rowBtnTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type Button}">
              <Border x:Name="Row" CornerRadius="7" Padding="10,7" Background="Transparent">
                <ContentPresenter VerticalAlignment="Center" HorizontalAlignment="Stretch"/>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsMouseOver" Value="True">
                  <Setter TargetName="Row" Property="Background" Value="#1426262D"/>
                </Trigger>
              </ControlTemplate.Triggers>
            </ControlTemplate>
            """);

    // ---------- 语言 / 主题 / 开关 / 位置 ----------

    /// <summary>语言切换：更新标题与各标签（Render 会刷新行内标签）。</summary>
    private void ApplyLanguage()
    {
        Title = L.T("mini.title");
        Render();
    }

    /// <summary>按配置显示/隐藏（跟随设置开关，App 启动与设置变更时调用）。</summary>
    public void ApplyEnabled()
    {
        if (_config.MiniBarEnabled)
        {
            RestorePosition();
            Render();
            ApplyMediaBar();
            Show();
            _freshTimer.Start();
        }
        else
        {
            _freshTimer.Stop();
            Hide();
        }
    }

    private void RestorePosition()
    {
        var wa = SystemParameters.WorkArea;
        if (_config.MiniBarLeft is { } l && _config.MiniBarTop is { } t &&
            l > -Width + 40 && l < wa.Right - 40 && t > -10 && t < wa.Bottom - 20)
        {
            Left = l;
            Top = t;
        }
        else
        {
            Left = wa.Right - Width - 18;
            Top = wa.Bottom - ActualHeight - 62;   // 高度由内容自适应，用实际值
        }
    }

    public void PersistPosition()
    {
        _config.MiniBarLeft = Left;
        _config.MiniBarTop = Top;
        _config.Save();
    }
}
