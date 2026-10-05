using System.IO;
using System.Threading;
using System.Windows;
using RonghuiEarbuds.App.Core;
using RonghuiEarbuds.App.UI;

namespace RonghuiEarbuds.App;

public partial class App : Application
{
    private static readonly Mutex SingleInstance = new(true, "RonghuiEarbuds_SingleInstance_E1B7", out _);

    private AppConfig _config = new();
    private EarbudsWatcher? _watcher;
    private TrayController? _tray;
    private MainWindow? _window;
    private MiniBarWindow? _miniBar;
    private LowBatteryMonitor? _monitor;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogCrash("UI", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogCrash("Domain", args.ExceptionObject as Exception);

        if (!SingleInstance.WaitOne(0))
        {
            Core.EarbudsWatcher.DiagLog("启动：单实例检查未通过，退出");
            Shutdown();
            return;
        }

        Core.EarbudsWatcher.DiagLog("启动：单实例检查通过");

        _config = AppConfig.Load();
        Core.L.Initialize(_config.Language);   // 界面语言：system/zh/en（system 按系统 UI 文化解析）
        Core.ThemeManager.Initialize(_config);   // 深浅主题：默认跟随 Windows，可手动切换
        AutoStartHelper.EnsureMinimizedFlag();   // 旧版自启动值升级为托盘启动
        _watcher = new EarbudsWatcher(_config);

        _window = new MainWindow(_watcher, _config,
            u => _tray?.Feed(u), alive => _tray?.SetConnected(alive),
            enabled => Dispatcher.Invoke(() =>
            {
                _miniBar?.ApplyEnabled();
                _tray?.SyncMiniBarChecked(enabled);
            }));

        _miniBar = new MiniBarWindow(_config)
        {
            OpenMainRequested = () => ShowMainWindow(),
        };

        _tray = new TrayController(_watcher, _config)
        {
            ToggleWindow = () =>
            {
                if (_window!.IsVisible && _window.WindowState != System.Windows.WindowState.Minimized)
                    _window.Hide();
                else
                    ShowMainWindow();
            },
            ShowWindowRequested = ShowMainWindow,
            ShowSettingsRequested = () =>
            {
                ShowMainWindow();
                _window.ShowSettingsView();
            },
            ToggleMiniBarRequested = ToggleMiniBar,
            ExitRequested = ExitApp,
        };

        // 低电量/骤降提醒：只处理关注设备，托盘气泡弹出
        _monitor = new LowBatteryMonitor(_config, (title, msg) => _tray?.ShowBalloon(title, msg));

        _watcher.DeviceBound += name => Dispatcher.Invoke(() =>
        {
            _config.BoundMac = _watcher!.BoundMac;
            _config.Save();
            Core.EarbudsWatcher.DiagLog($"App 层已保存配置: {_config.BoundMac}");
        });

        // 主面板在托盘时，耳机开盖拿到广播数据（左/右/仓）→ 主窗口拉起到最上层；
        // 同一份数据喂给悬浮条与低电量监控（都只跟随关注设备）
        var lastDataAt = DateTime.MinValue;
        var lastShowAt = DateTime.Now;   // 启动后第一波广播不弹，等真正"打开耳机"
        _watcher.UpdateReceived += u => Dispatcher.Invoke(() =>
        {
            if (u.Mac == _window?.ActiveMac)
            {
                _miniBar?.Push(u);
                _monitor?.OnUpdate(u);
            }

            var now = DateTime.Now;
            var quiet = now - lastDataAt > TimeSpan.FromSeconds(30);
            lastDataAt = now;
            if (!_config.OpenLidPopup) return;              // 设置里可关
            if (_window is { IsVisible: true }) return;
            if (u.Mac != _window?.ActiveMac) return;   // 多设备：只提示当前关注的设备
            var cooldown = TimeSpan.FromMinutes(Math.Clamp(_config.PopupCooldownMinutes, 1, 60));
            if (!quiet || now - lastShowAt < cooldown) return;
            lastShowAt = now;
            Core.EarbudsWatcher.DiagLog("开盖拿到广播数据：主窗口拉起到最上层");
            ShowMainWindowTop();
        });

        _watcher.Start();

        if (e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
        {
            Core.EarbudsWatcher.DiagLog("启动：--minimized，隐藏到托盘");
            _window.Hide();
        }
        else
        {
            Core.EarbudsWatcher.DiagLog($"启动：显示主窗口（IsVisible 前={_window.IsVisible}）");
            ShowMainWindow();
            Core.EarbudsWatcher.DiagLog($"启动：ShowMainWindow 返回（IsVisible 后={_window.IsVisible}）");
        }

        _miniBar.ApplyEnabled();   // 恢复悬浮条开关状态
        _ = CheckUpdateDailyAsync();
    }

    /// <summary>托盘/设置里切换悬浮条显示。</summary>
    private void ToggleMiniBar()
    {
        _config.MiniBarEnabled = !_config.MiniBarEnabled;
        _config.Save();
        _miniBar?.ApplyEnabled();
        _window?.SyncMiniBarCheck();
        Core.EarbudsWatcher.DiagLog($"悬浮条切换为：{(_config.MiniBarEnabled ? "显示" : "隐藏")}");
    }

    /// <summary>每天最多静默检查一次 GitHub Releases 更新，有新版弹托盘气泡。</summary>
    private async Task CheckUpdateDailyAsync()
    {
        if (_config.LastUpdateCheckUtc is { } last &&
            DateTime.UtcNow - last < TimeSpan.FromHours(24))
        {
            return;
        }
        _config.LastUpdateCheckUtc = DateTime.UtcNow;
        _config.Save();

        try
        {
            var info = await UpdateChecker.CheckAsync();
            if (info is null) return;
            await Dispatcher.InvokeAsync(() => _tray?.ShowUpdateBalloon(
                Core.L.F("update.availableFmt", info.Version),
                Core.L.T("update.clickMsg"), info.Url));
        }
        catch { /* 无网络/接口异常时静默跳过 */ }
    }

    private void ShowMainWindow()
    {
        _window!.Show();
        _window.Activate();
    }

    /// <summary>
    /// 弹主窗口到最上层：临时置顶 1.5 秒确保盖过前台应用，随后恢复用户的图钉设置。
    /// </summary>
    private void ShowMainWindowTop()
    {
        _window!.Show();
        _window.Activate();
        var restoreTo = _config.TopMost;
        _window.Topmost = true;
        var t = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        t.Tick += (_, _) =>
        {
            t.Stop();
            if (_window is not null) _window.Topmost = restoreTo;
        };
        t.Start();
    }

    private void ExitApp()
    {
        _window?.PersistPosition(_config);
        _window?.PersistKnownDevices();   // 设备名单随退出落盘
        _window?.DisposeCapture();
        try
        {
            _miniBar?.PersistPosition();
            _miniBar?.Close();
        }
        catch { /* 悬浮条收尾失败不阻断退出 */ }
        _config.Save();
        _tray?.Dispose();
        _watcher?.Dispose();
        Shutdown();
    }

    private static void LogCrash(string source, Exception? ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RonghuiEarbuds");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:HH:mm:ss}] {source}: {ex}\r\n\r\n");
        }
        catch { /* 日志失败只能放弃 */ }
    }
}
