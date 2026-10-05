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
/// 悬浮迷你电量条：可拖动的极简置顶小条，显示关注设备的左右耳与充电仓电量。
/// 数据过期自动回「--」，断连整体灰显；双击打开主面板，位置随拖动持久化。
/// </summary>
public sealed class MiniBarWindow : Window
{
    private const double FreshSeconds = 3;
    private const double StaleSeconds = 8;

    private readonly AppConfig _config;
    private readonly TextBlock _nameText = new() { FontSize = 10.5, FontWeight = FontWeights.SemiBold };
    private readonly Canvas _nameCanvas = new();   // Canvas 用无限约束测量，保证拿到完整文字宽度
    private readonly Grid _nameHost = new() { ClipToBounds = true };
    private readonly TranslateTransform _nameShift = new();
    private string _lastName = "";
    private readonly (TextBlock Value, Ellipse Dot, Path Bolt)[] _cells = new (TextBlock, Ellipse, Path)[3];
    private readonly StackPanel?[] _panels = new StackPanel[3];   // 值格容器（系统电量单格模式时隐藏其余）
    private readonly DispatcherTimer _freshTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    private int?[] _lastValues = new int?[3];
    private bool[] _inCase = new bool[3];
    private DateTime[] _lastTimes = { DateTime.MinValue, DateTime.MinValue, DateTime.MinValue };
    private DateTime _broadcastSeen = DateTime.MinValue;
    private bool _themeHooked;
    private readonly TextBlock[] _labels = new TextBlock[3];

    /// <summary>悬浮条三列小标签：L / R / 仓（仓随语言切换）。</summary>
    private static string MiniLabel(int i) => i switch
    {
        0 => "L",
        1 => "R",
        _ => L.T("mini.caseLabel"),
    };

    /// <summary>App 注入：双击迷你条时显示主窗口。</summary>
    public Action? OpenMainRequested { get; set; }

    /// <summary>App 注入：当前关注设备的系统整机电量（广播停发/未适配时的兜底显示）。</summary>
    public Func<int?>? SystemBatteryProvider { get; set; }

    /// <summary>App 注入：右键菜单设备名单（名字, MAC）与当前关注 MAC，点击切换关注设备。</summary>
    public Func<IReadOnlyList<(string Name, string Mac)>>? DeviceListProvider { get; set; }
    public Func<string?>? ActiveMacProvider { get; set; }
    public Action<string>? DeviceSwitchRequested { get; set; }

    public MiniBarWindow(AppConfig config)
    {
        _config = config;

        Width = 324;   // 视觉条 272×44 + 四周留白，给阴影呼吸空间（否则阴影被窗口边界
        Height = 92;   // 硬裁成直角边，圆角条像贴图）；272 宽保证名字列不被值区饿死
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
            Child = BuildContent(),
        };
        root.SetResourceReference(Border.BackgroundProperty, "T.WindowBg");
        root.SetResourceReference(Border.BorderBrushProperty, "T.CardBorder");
        root.Effect = new System.Windows.Media.Effects.DropShadowEffect
        {
            BlurRadius = 16, ShadowDepth = 2, Opacity = 0.4, Direction = 270,
        };
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
        MouseRightButtonUp += (_, _) => OpenDeviceMenu();   // 右键：切换显示的设备

        _freshTimer.Tick += (_, _) => Render();
    }

    private UIElement BuildContent()
    {
        var grid = new Grid { Margin = new Thickness(12, 0, 12, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _nameText.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        _nameText.RenderTransform = _nameShift;
        _nameCanvas.Height = 14;                       // 贴合文字行高：行高偏大时文字顶部对齐会
        _nameCanvas.Children.Add(_nameText);           // 整体偏上，与右侧电量值不在一条水平线
        _nameHost.Children.Add(_nameCanvas);
        _nameHost.MinHeight = 14;
        _nameHost.VerticalAlignment = VerticalAlignment.Center;
        _nameHost.SizeChanged += (_, _) => UpdateMarquee();
        Grid.SetColumn(_nameHost, 0);
        grid.Children.Add(_nameHost);

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
            _labels[i] = label;

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

            _cells[i] = (value, dot, bolt);
            _panels[i] = panel;
        }
        return grid;
    }

    /// <summary>跟随关注设备更新型号名（连接枚举即可得，不必等广播）；null=保持现名。</summary>
    public void SetDeviceName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name == _lastName) return;
        _lastName = name;
        _nameText.Text = name;
        Dispatcher.BeginInvoke(UpdateMarquee, DispatcherPriority.Loaded);
    }

    /// <summary>接收关注设备的广播数据。</summary>
    public void Push(EarbudsUpdate u)
    {
        var name = string.IsNullOrWhiteSpace(u.DisplayName) ? L.T("mini.earbuds") : u.DisplayName!;
        if (name != _lastName)
        {
            _lastName = name;
            _nameText.Text = name;
            // 等布局完成后再判断是否需要跑马灯
            Dispatcher.BeginInvoke(UpdateMarquee, DispatcherPriority.Loaded);
        }
        _broadcastSeen = u.Timestamp;
        var s = u.Snapshot;
        _lastValues[0] = s.LeftPercent;
        _lastValues[1] = s.RightPercent;
        _lastValues[2] = s.CasePercent;
        _inCase[0] = s.LeftInCase;
        _inCase[1] = s.RightInCase;
        _inCase[2] = false;
        _lastTimes[0] = _lastTimes[1] = _lastTimes[2] = u.Timestamp;
        Render();
    }

    /// <summary>
    /// 名字放不下时左右来回滑动（跑马灯）；放得下则复位静止。
    /// Canvas 测量不受列宽约束，ActualWidth 即完整文字宽度；
    /// Canvas 高度=文字高度且宿主垂直居中，无需手动定位。
    /// </summary>
    private void UpdateMarquee()
    {
        _nameShift.BeginAnimation(TranslateTransform.XProperty, null);
        double textW = _nameText.ActualWidth;
        double viewW = _nameHost.ActualWidth;
        if (viewW <= 0 || textW <= 0)
            return;   // 尚未完成布局，SizeChanged/Loaded 时机回来重判
        if (textW <= viewW + 0.5)
        {
            _nameShift.X = Math.Max(0, (viewW - textW) / 2);   // 在名字区域内水平居中
            return;
        }
        double overflow = textW - viewW + 10;   // 末尾留一点缓冲
        _nameShift.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation
        {
            From = 0,
            To = -overflow,
            Duration = TimeSpan.FromSeconds(Math.Clamp(overflow / 22, 2.5, 8)),
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
        });
    }

    private void Render()
    {
        if (!_themeHooked)
        {
            ThemeManager.ThemeChanged += () => Dispatcher.Invoke(Render);
            _themeHooked = true;
        }
        bool offline = _broadcastSeen == DateTime.MinValue ||
                       (DateTime.Now - _broadcastSeen).TotalSeconds > StaleSeconds;
        Opacity = offline ? 0.55 : 1.0;

        // 三格是否有新鲜分耳数据；没有时若系统整机电量可得 → 单格兜底模式
        // （未适配机型广播无电量、或连接播放广播停发时，悬浮条不至于全是 --）
        bool anyFresh = false;
        for (var i = 0; i < 3; i++)
        {
            if (_lastValues[i] is not null &&
                (DateTime.Now - _lastTimes[i]).TotalSeconds <= FreshSeconds)
                anyFresh = true;
        }
        int? sys = anyFresh ? null : SystemBatteryProvider?.Invoke();

        for (var i = 0; i < 3; i++)
        {
            var (value, dot, bolt) = _cells[i];
            var panel = _panels[i];
            bool fresh = anyFresh && _lastValues[i] is not null &&
                         (DateTime.Now - _lastTimes[i]).TotalSeconds <= FreshSeconds;

            if (sys is { } sv && i == 0)
            {
                // 系统整机电量单格：标签切「整机」，隐藏闪电（系统值无在仓概念）
                panel!.Visibility = Visibility.Visible;
                value.Text = $"{sv}%";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
                dot.Fill = new SolidColorBrush(ThemeManager.ColorFor(sv));
                bolt.Visibility = Visibility.Collapsed;
                _labels[0].Text = L.T("mini.all");
            }
            else if (sys is { })
            {
                panel!.Visibility = Visibility.Collapsed;   // 单格模式隐藏其余两格
            }
            else if (fresh)
            {
                int v = _lastValues[i]!.Value;
                panel!.Visibility = Visibility.Visible;
                value.Text = $"{v}%";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
                dot.Fill = new SolidColorBrush(ThemeManager.ColorFor(v));
                bolt.Visibility = _inCase[i] ? Visibility.Visible : Visibility.Collapsed;
                _labels[i].Text = MiniLabel(i);
            }
            else
            {
                panel!.Visibility = Visibility.Visible;
                value.Text = "--";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextDim");
                dot.Fill = new SolidColorBrush(ThemeManager.GetColor("T.Unknown"));
                bolt.Visibility = Visibility.Collapsed;
                _labels[i].Text = MiniLabel(i);
            }
        }
    }

    /// <summary>右键弹出设备切换菜单（列出已登记设备，勾选当前关注设备）。</summary>
    private void OpenDeviceMenu()
    {
        if (DeviceListProvider?.Invoke() is not { Count: > 0 } devices)
            return;
        var active = ActiveMacProvider?.Invoke();
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
                IsChecked = string.Equals(mac, active, StringComparison.OrdinalIgnoreCase),
                StaysOpenOnClick = true,
                Template = MenuItemTemplate(),
            };
            item.Click += (_, _) => DeviceSwitchRequested?.Invoke(mac);
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

    /// <summary>语言切换：更新悬浮条标题与小标签（设备名随下一次广播刷新）。</summary>
    private void ApplyLanguage()
    {
        Title = L.T("mini.title");
        for (var i = 0; i < 3; i++)
            _labels[i].Text = MiniLabel(i);
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
