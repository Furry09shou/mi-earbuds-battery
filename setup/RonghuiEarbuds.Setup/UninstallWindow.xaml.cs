using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RonghuiEarbuds.Setup;

public partial class UninstallWindow : Window
{
    private bool _busy;
    private bool _finished;
    private bool _verifyFailed;     // 校验失败进入异常面板（语言切换时需重建该文案）
    private bool? _deleteConfigChoice;  // 完成后的勾选状态（语言切换时重建完成文案）

    public UninstallWindow()
    {
        InitializeComponent();
        LogoImage.Source = App.LoadLogo();
        ApplyLanguage();
        Loc.Changed += () => Dispatcher.Invoke(ApplyLanguage);
        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartSpinner();
        };
        if (!SetupLogic.VerifyOwnInstall())
        {
            _verifyFailed = true;
            WarnText.Text = Loc.T("setup.warnNotOurs");
            SetState(WarnPanel);
        }
    }

    // ---------- 中英双语：语言切换或启动时统一应用文案 ----------

    private void ApplyLanguage()
    {
        Title = $"{Loc.T("setup.appName")} {Loc.T("setup.uninstaller")}";
        LblTitleBar.Text = Loc.T("setup.uninstaller");
        LblConfirmTitle.Text = Loc.T("setup.confirmTitle");
        LblConfirmBody.Text = Loc.T("setup.confirmBody");
        DeleteConfigCheck.Content = Loc.T("setup.deleteConfig");
        CancelButton.Content = Loc.T("setup.cancel");
        UninstallConfirmButton.Content = Loc.T("setup.uninstall");
        LblBusyText.Text = Loc.T("setup.busyUninstall");
        LblDoneTitle.Text = Loc.T("setup.doneUninstallTitle");
        DoneSubText.Text = _finished && _deleteConfigChoice is { } del
            ? del ? Loc.T("setup.doneConfigRemoved") : Loc.T("setup.doneFilesKept")
            : Loc.T("setup.doneFilesRemoved");
        CloseWarnButton.Content = Loc.T("setup.close");
        if (_verifyFailed) WarnText.Text = Loc.T("setup.warnNotOurs");
        Loc.SetSegState(LangBtnZh, LangBtnEn);
    }

    private void LangButton_Click(object sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(((Button)sender).Tag?.ToString() ?? "zh");   // 触发 Changed → ApplyLanguage
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
            _deleteConfigChoice = deleteConfig;
            DoneSubText.Text = deleteConfig ? Loc.T("setup.doneConfigRemoved") : Loc.T("setup.doneFilesKept");
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
            WarnText.Text = Loc.F("setup.errUninstallFmt", ex.Message);
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
