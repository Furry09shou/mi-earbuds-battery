using System.Windows;
using System.Windows.Controls;
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

    public MiniBarWindow(AppConfig config)
    {
        _config = config;

        Width = 324;   // 视觉条 272 宽 + 四周留白给阴影呼吸空间（否则阴影被窗口边界硬裁
        Height = 92;   // 成直角边像贴图）；高度随勾选行数在 Render 时调整
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
            CornerRadius = new CornerRadius(22),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.SizeAll,
            Child = _rowsHost,
        };
        root.SetResourceReference(Border.BackgroundProperty, "T.WindowBg");
        root.SetResourceReference(Border.BorderBrushProperty, "T.CardBorder");
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 2, Opacity = 0.4, Direction = 270,
        };
        _rowsHost.Margin = new Thickness(0, 2, 0, 2);

        // 外层透明容器：定位阴影留白 + 透明区域也可拖动
        var outer = new Grid { Background = Brushes.Transparent, Margin = new Thickness(26, 20, 26, 28) };
        outer.Children.Add(root);
        Content = outer;

        MouseLeftButtonDown += (_, e) =>
        {
            if (e.ClickCount == 2)
            {
                OpenMainRequested?.Invoke();
                return;
            }
            try { DragMove(); } catch { /* 快速点击可能抛异常 */ }
            PersistPosition();
        };
        MouseRightButtonUp += (_, _) => OpenDeviceMenu();   // 右键：勾选展开显示的设备

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

        // 行数变化 → 调整窗口高度（宽度固定）
        double h = 48 + list.Count * RowHeight + Math.Max(0, list.Count - 1) * RowGap;
        if (Math.Abs(Height - h) > 0.5)
            Height = h;
    }

    private void RenderRow(BarRow row, string mac, BarDevice dev)
    {
        // 名字
        var name = dev.Name.Length > 0 ? dev.Name : L.T("mini.earbuds");
        if (name != row.LastName)
        {
            row.LastName = name;
            row.NameText.Text = name;
            Dispatcher.BeginInvoke(() => UpdateMarquee(row), DispatcherPriority.Loaded);
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

    /// <summary>确保行数匹配（多/少一台时重建行与分隔线）。</summary>
    private void EnsureRows(int count)
    {
        if (_rows.Count == count) return;
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
    }

    private BarRow BuildRow()
    {
        var row = new BarRow();
        var grid = new Grid { Margin = new Thickness(12, 0, 12, 0), Height = RowHeight };
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

    /// <summary>名字超出行宽时左右来回滑动；放得下则居中。</summary>
    private void UpdateMarquee(BarRow row)
    {
        row.NameShift.X = 0;
        double viewW = row.NameHost.ActualWidth;
        double textW = row.NameCanvas.DesiredSize.Width;
        if (viewW <= 0 || textW <= 0) return;

        row.NameCanvas.BeginAnimation(Canvas.LeftProperty, null);
        row.NameShift.BeginAnimation(TranslateTransform.XProperty, null);

        if (textW <= viewW + 0.5)
        {
            row.NameShift.X = Math.Max(0, (viewW - textW) / 2);   // 在名字区域内水平居中
            return;
        }

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

    // ---------- 右键菜单（多选勾选展开） ----------

    /// <summary>右键弹出设备菜单：勾选=在悬浮条显示该设备行（多选）。</summary>
    private void OpenDeviceMenu()
    {
        if (DeviceListProvider?.Invoke() is not { Count: > 0 } devices)
            return;
        var pinned = new HashSet<string>(_config.MiniBarPinned, StringComparer.OrdinalIgnoreCase);
        var menu = new ContextMenu
        {
            PlacementTarget = this,
            Placement = System.Windows.Controls.Primitives.PlacementMode.MousePoint,
            Template = MenuTemplate(),
        };
        foreach (var (name, mac) in devices)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = pinned.Contains(mac),
                StaysOpenOnClick = true,
                Template = MenuItemTemplate(),
            };
            item.Click += (_, _) =>
            {
                if (item.IsChecked) pinned.Add(mac);
                else pinned.Remove(mac);
                _config.MiniBarPinned = pinned.ToList();
                _config.Save();
                Render();
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    // 菜单模板：与应用弹层同风格（圆角卡片+阴影+悬停高亮+橙色勾选）。
    // 纯代码构建窗口拿不到隐式样式，用 XamlReader 加载模板并缓存。
    private static ControlTemplate? _menuTpl, _menuItemTpl;

    private static ControlTemplate MenuTemplate() => _menuTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type ContextMenu}">
              <Border CornerRadius="10" Padding="6"
                      Background="{DynamicResource T.WindowBg}"
                      BorderBrush="{DynamicResource T.CardBorder}" BorderThickness="1">
                <Border.Effect>
                  <DropShadowEffect BlurRadius="14" ShadowDepth="2" Opacity="0.35" Direction="270"/>
                </Border.Effect>
                <StackPanel IsItemsHost="True"/>
              </Border>
            </ControlTemplate>
            """);

    private static ControlTemplate MenuItemTemplate() => _menuItemTpl ??= (ControlTemplate)
        System.Windows.Markup.XamlReader.Parse("""
            <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                             TargetType="{x:Type MenuItem}">
              <Border x:Name="Row" CornerRadius="7" Padding="10,7" Background="Transparent">
                <StackPanel Orientation="Horizontal">
                  <TextBlock x:Name="Check" Text="&#xE73E;" FontFamily="Segoe MDL2 Assets"
                             FontSize="11" Foreground="#E87A3E" VerticalAlignment="Center"
                             Visibility="Collapsed"/>
                  <ContentPresenter ContentSource="Header" Margin="8,0,0,0"
                                    VerticalAlignment="Center"
                                    TextBlock.Foreground="{DynamicResource T.TextPrimary}"/>
                </StackPanel>
              </Border>
              <ControlTemplate.Triggers>
                <Trigger Property="IsChecked" Value="True">
                  <Setter TargetName="Check" Property="Visibility" Value="Visible"/>
                </Trigger>
                <Trigger Property="IsHighlighted" Value="True">
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
            Top = wa.Bottom - Height - 62;
        }
    }

    public void PersistPosition()
    {
        _config.MiniBarLeft = Left;
        _config.MiniBarTop = Top;
        _config.Save();
    }
}
