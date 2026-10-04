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
            ExistText.Text = _isUpgrade
                ? $"检测到已安装 v{existing.Version}（位于 {existing.Path}），将升级到 v{VersionText.Text.TrimStart('v')}，配置与绑定保留"
                : $"检测到已安装 v{existing.Version}（位于 {existing.Path}），将重新安装（旧文件清理后重装）";
            ExistBanner.Visibility = Visibility.Visible;
            InstallButton.Content = _isUpgrade ? "升级" : "重新安装";
        }
        else
        {
            PathBox.Text = SetupLogic.DefaultTarget;
        }

        UpdateResolved();
        Loaded += (_, _) =>
        {
            PlayEntrance();
            StartSpinner();
        };
    }

    private void UpdateResolved()
    {
        try { ResolvedText.Text = "实际安装到：" + SetupLogic.ResolveTarget(PathBox.Text.Trim()); }
        catch { ResolvedText.Text = "安装位置无效"; }
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
        catch { ShowError("安装位置无效，请重新选择。"); return; }

        if (target.Length == 0)
        {
            ShowError("请选择安装位置。");
            return;
        }
        if (SetupLogic.ValidateTarget(target) is { } problem)
        {
            ShowError(problem);
            return;
        }

        _target = target;
        _busy = true;
        BusyText.Text = "正在安装…";
        SetState(BusyPanel);

        try
        {
            var autoStart = AutoStartCheck.IsChecked == true;
            var desktop = DesktopCheck.IsChecked == true;
            var startMenu = StartMenuCheck.IsChecked == true;
            var existingPath = _existingPath;
            var isUpgrade = _isUpgrade;
            await System.Threading.Tasks.Task.Run(() =>
            {
                // 旧安装清理（杀旧进程/自启/快捷方式/登记；换位置时删旧目录），再全新安装
                if (existingPath is not null)
                    SetupLogic.RemovePreviousInstall(existingPath, _target);
                SetupLogic.Install(_target, autoStart, desktop, startMenu);
            });

            _done = true;
            DoneSubText.Text = isUpgrade
                ? $"已升级到 v{VersionText.Text.TrimStart('v')}：{_target}"
                : $"程序已安装到 {_target}";
            SetState(DonePanel);
            PlayDoneAnimation();
        }
        catch (Exception ex)
        {
            ShowError($"安装失败：{ex.Message}");
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
                "未检测到 .NET 8 桌面运行时（绒汇耳机助手运行必需）。\n\n" +
                "点击「是」打开微软官方下载页，安装运行时后即可正常使用；" +
                "也可以稍后从开始菜单启动本程序。",
                "需要 .NET 8 桌面运行时", MessageBoxButton.YesNo, MessageBoxImage.Information);
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
