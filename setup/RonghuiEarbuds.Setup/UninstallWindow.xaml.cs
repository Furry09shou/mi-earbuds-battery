using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RonghuiEarbuds.Setup;

public partial class UninstallWindow : Window
{
    private bool _busy;
    private bool _finished;

    public UninstallWindow()
    {
        InitializeComponent();
        LogoImage.Source = App.LoadLogo();
        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartSpinner();
        };
        if (!SetupLogic.VerifyOwnInstall())
        {
            WarnText.Text =
                "未找到与卸载程序匹配的安装信息，已取消操作。\n\n" +
                "（为防止误卸载其他软件，卸载前会校验注册表登记与本程序路径一致）";
            SetState(WarnPanel);
        }
    }

    private void SetState(UIElement current)
    {
        foreach (var el in new UIElement[] { ConfirmPanel, BusyPanel, DonePanel, WarnPanel })
            el.Visibility = ReferenceEquals(el, current) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try { DragMove(); } catch { /* 快速点击时可能抛异常，忽略 */ }
        }
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _finished) return;
        _busy = true;
        SetState(BusyPanel);

        try
        {
            var deleteConfig = DeleteConfigCheck.IsChecked == true;
            await System.Threading.Tasks.Task.Run(() => SetupLogic.Uninstall(deleteConfig));
            _finished = true;
            DoneSubText.Text = deleteConfig ? "程序文件与配置已移除" : "程序文件已移除（配置已保留）";
            SetState(DonePanel);
            PlayDoneAnimation();

            // 展示片刻后自动关闭（安装目录由延迟任务在进程退出后删除）
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.6) };
            timer.Tick += (_, _) =>
            {
                timer.Stop();
                Close();
            };
            timer.Start();
        }
        catch (Exception ex)
        {
            WarnText.Text = $"卸载失败：{ex.Message}";
            SetState(WarnPanel);
        }
        finally
        {
            _busy = false;
        }
    }

    // ---------- 动画 ----------

    private void PlayEntrance()
    {
        RootBorder.Opacity = 0;
        RootTranslate.Y = 18;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        RootBorder.BeginAnimation(OpacityProperty,
            new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
        RootTranslate.BeginAnimation(TranslateTransform.YProperty,
            new DoubleAnimation(18, 0, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease });
    }

    private void StartSpinner()
    {
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(900))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotate.BeginAnimation(RotateTransform.AngleProperty, spin);
    }

    private void PlayDoneAnimation()
    {
        DoneScale.ScaleX = DoneScale.ScaleY = 0.3;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1.6 };
        var grow = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
        DoneScale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        DoneScale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }
}
