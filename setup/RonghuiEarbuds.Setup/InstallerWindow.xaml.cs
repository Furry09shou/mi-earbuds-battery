using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace RonghuiEarbuds.Setup;

public partial class InstallerWindow : Window
{
    private string _target = "";
    private string? _existingPath;      // 检测到的旧安装位置
    private bool _isUpgrade;            // true=新版升级，false=同版重装
    private bool _busy;
    private bool _done;
    private string? _doneTarget;        // 安装/升级完成后记录的目标目录（语言切换时重建完成文案）

    public InstallerWindow()
    {
        InitializeComponent();
        LogoImage.Source = App.LoadLogo();
        VersionText.Text = "v" + SetupLogic.NormalizeVersion(SetupLogic.GetAppVersion());
        PathBox.TextChanged += (_, _) => UpdateResolved();

        // 检测本机是否已安装：预填旧位置并提示升级/重装，避免一台电脑重复安装
        var existing = SetupLogic.DetectExistingInstall();
        if (existing is not null)
        {
            _existingPath = existing.Path;
            _isUpgrade = SetupLogic.CompareVersions(VersionText.Text.TrimStart('v'), existing.Version) > 0;
            PathBox.Text = existing.Path;
            ExistBanner.Visibility = Visibility.Visible;
            UpdateExistText();
        }
        else
        {
            PathBox.Text = SetupLogic.DefaultTarget;
        }

        UpdateResolved();
        ApplyLanguage();
        L_ChangedHandler = () => Dispatcher.Invoke(ApplyLanguage);
        Loc.Changed += L_ChangedHandler;
        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartSpinner();
        };
    }

    private readonly Action L_ChangedHandler;

    // ---------- 中英双语：语言切换或启动时统一应用文案 ----------

    private void ApplyLanguage()
    {
        Title = $"{Loc.T("setup.appName")} {Loc.T("setup.installer")}";
        LblTitleBar.Text = Loc.T("setup.installer");
        LblAppName.Text = Loc.T("setup.appName");
        LblSubtitle.Text = Loc.T("setup.subtitle");
        LblPathLabel.Text = Loc.T("setup.pathLabel");
        BrowseButton.Content = Loc.T("setup.browse");
        AutoStartCheck.Content = Loc.T("setup.autostart");
        DesktopCheck.Content = Loc.T("setup.desktop");
        StartMenuCheck.Content = Loc.T("setup.startmenu");
        UpdateInstallButton();
        BusyText.Text = Loc.T("setup.busyInstall");
        LblDoneTitle.Text = Loc.T("setup.doneTitle");
        DoneSubText.Text = _done ? UpdateDoneText() : Loc.T("setup.doneSubDefault");
        FinishButton.Content = Loc.T("setup.finish");
        RunButton.Content = Loc.T("setup.runNow");
        RetryButton.Content = Loc.T("setup.retry");
        if (_existingPath is not null) UpdateExistText();
        UpdateResolved();
        Loc.SetSegState(LangBtnZh, LangBtnEn);
    }

    /// <summary>已安装横幅文案（升级 / 重装两种）。</summary>
    private void UpdateExistText()
    {
        if (_existingPath is null) return;
        var oldVer = SetupLogic.NormalizeVersion(SetupLogic.DetectExistingInstall()?.Version);
        ExistText.Text = _isUpgrade
            ? Loc.F("setup.existUpgradeFmt", oldVer, _existingPath, VersionText.Text.TrimStart('v'))
            : Loc.F("setup.existReinstallFmt", oldVer, _existingPath);
    }

    /// <summary>主按钮文案：安装 / 升级 / 重新安装（随检测状态与语言）。</summary>
    private void UpdateInstallButton() =>
        InstallButton.Content = _existingPath is not null
            ? (_isUpgrade ? Loc.T("setup.upgrade") : Loc.T("setup.reinstall"))
            : Loc.T("setup.install");

    /// <summary>完成面板副文案（需要 _doneTarget 已记录）；无记录时返回默认文案。</summary>
    private string UpdateDoneText()
    {
        if (!_done || _doneTarget is null) return Loc.T("setup.doneSubDefault");
        return _isUpgrade
            ? Loc.F("setup.doneUpgradedFmt", VersionText.Text.TrimStart('v'), _doneTarget)
            : Loc.F("setup.doneInstalledFmt", _doneTarget);
    }

    private void LangButton_Click(object sender, RoutedEventArgs e)
    {
        Loc.SetLanguage(((Button)sender).Tag?.ToString() ?? "zh");   // 触发 Changed → ApplyLanguage
    }

    private void UpdateResolved()
    {
        try { ResolvedText.Text = Loc.F("setup.resolvedFmt", SetupLogic.ResolveTarget(PathBox.Text.Trim())); }
        catch { ResolvedText.Text = Loc.T("setup.errInvalidPath"); }
    }

    private void SetState(UIElement current)
    {
        foreach (var el in new UIElement[] { ReadyPanel, BusyPanel, DonePanel, ErrorPanel })
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

    private void BrowseButton_Click(object sender, RoutedEventArgs e)
    {
        using var dialog = new System.Windows.Forms.FolderBrowserDialog { SelectedPath = PathBox.Text };
        if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
            PathBox.Text = dialog.SelectedPath;
    }

    private async void InstallButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _done) return;

        string target;
        try { target = SetupLogic.ResolveTarget(PathBox.Text.Trim()); }
        catch { ShowError(Loc.T("setup.errInvalidPathRetry")); return; }

        if (target.Length == 0)
        {
            ShowError(Loc.T("setup.errEmptyPath"));
            return;
        }
        if (SetupLogic.ValidateTarget(target) is { } problem)
        {
            ShowError(problem);
            return;
        }

        _target = target;
        _busy = true;
        BusyText.Text = Loc.T("setup.busyInstall");
        SetState(BusyPanel);

        try
        {
            var autoStart = AutoStartCheck.IsChecked == true;
            var desktop = DesktopCheck.IsChecked == true;
            var startMenu = StartMenuCheck.IsChecked == true;
            var existingPath = _existingPath;
            await System.Threading.Tasks.Task.Run(() =>
            {
                // 旧安装清理（杀旧进程/自启/快捷方式/登记；换位置时删旧目录），再全新安装
                if (existingPath is not null)
                    SetupLogic.RemovePreviousInstall(existingPath, _target);
                SetupLogic.Install(_target, autoStart, desktop, startMenu);
            });

            _done = true;
            _doneTarget = _target;
            DoneSubText.Text = UpdateDoneText();
            SetState(DonePanel);
            PlayDoneAnimation();
        }
        catch (Exception ex)
        {
            ShowError(Loc.F("setup.errInstallFmt", ex.Message));
        }
        finally
        {
            _busy = false;
        }
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        SetState(ErrorPanel);
    }

    private void RetryButton_Click(object sender, RoutedEventArgs e)
    {
        UpdateResolved();
        SetState(ReadyPanel);
    }

    private void RunButton_Click(object sender, RoutedEventArgs e)
    {
        if (!SetupLogic.IsDotNetDesktopRuntimeInstalled())
        {
            var r = MessageBox.Show(this,
                Loc.T("setup.netBody"),
                Loc.T("setup.netTitle"), MessageBoxButton.YesNo, MessageBoxImage.Information);
            if (r == MessageBoxResult.Yes)
            {
                try
                {
                    Process.Start(new ProcessStartInfo(
                        "https://dotnet.microsoft.com/zh-cn/download/dotnet/8.0/runtime")
                    { UseShellExecute = true });
                }
                catch { /* 打开浏览器失败忽略 */ }
            }
        }
        try { SetupLogic.StartApp(_target); }
        catch { /* 启动失败忽略，程序可从快捷方式打开 */ }
        Close();
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
        var spin = new DoubleAnimation(0, 360, TimeSpan.FromMilliseconds(0.9 * 1000))
        {
            RepeatBehavior = RepeatBehavior.Forever,
        };
        SpinnerRotate.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty, spin);
    }

    private void PlayDoneAnimation()
    {
        DoneScale.ScaleX = DoneScale.ScaleY = 0.3;
        var ease = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 1.6 };
        var grow = new DoubleAnimation(0.3, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
        DoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, grow);
        DoneScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleYProperty, grow);
    }
}
