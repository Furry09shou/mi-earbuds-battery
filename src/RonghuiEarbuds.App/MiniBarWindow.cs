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

    public MiniBarWindow(AppConfig config)
    {
        _config = config;

        Width = 240;
        Height = 44;
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
        Content = root;

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

        _freshTimer.Tick += (_, _) => Render();
    }

    private UIElement BuildContent()
    {
        var grid = new Grid { Margin = new Thickness(15, 0, 15, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        for (var i = 0; i < 3; i++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _nameText.SetResourceReference(TextBlock.ForegroundProperty, "T.TextSecondary");
        _nameText.RenderTransform = _nameShift;
        _nameCanvas.Height = 17;                       // 固定行高：desired 链不可靠，文字曾因此被裁成 0 高
        _nameCanvas.Children.Add(_nameText);
        _nameHost.Children.Add(_nameCanvas);
        _nameHost.MinHeight = 17;
        _nameHost.VerticalAlignment = VerticalAlignment.Center;
        _nameHost.SizeChanged += (_, _) => UpdateMarquee();
        Grid.SetColumn(_nameHost, 0);
        grid.Children.Add(_nameHost);

        for (var i = 0; i < 3; i++)
        {
            var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(9, 0, 0, 0) };

            var dot = new Ellipse { Width = 7, Height = 7, VerticalAlignment = VerticalAlignment.Center };
            var value = new TextBlock
            {
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(5, 0, 0, 0),
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
            _nameShift.X = 0;
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

        for (var i = 0; i < 3; i++)
        {
            var (value, dot, bolt) = _cells[i];
            bool fresh = _lastValues[i] is not null &&
                         (DateTime.Now - _lastTimes[i]).TotalSeconds <= FreshSeconds;
            if (fresh)
            {
                int v = _lastValues[i]!.Value;
                value.Text = $"{v}%";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextPrimary");
                dot.Fill = new SolidColorBrush(ThemeManager.ColorFor(v));
                bolt.Visibility = _inCase[i] ? Visibility.Visible : Visibility.Collapsed;
            }
            else
            {
                value.Text = "--";
                value.SetResourceReference(TextBlock.ForegroundProperty, "T.TextDim");
                dot.Fill = new SolidColorBrush(ThemeManager.GetColor("T.Unknown"));
                bolt.Visibility = Visibility.Collapsed;
            }
        }
    }

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
