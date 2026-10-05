using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Threading;
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
    private ChargeMonitor? _charge;
    private VoiceService? _voice;
    private MediaSession? _media;
    private readonly GlobalHotKey _hotKey = new();
    private DispatcherTimer? _sysTicker;

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
        _voice = new VoiceService();   // 系统 TTS（在选择语言后创建，便于挑对应语音）
        AutoStartHelper.EnsureMinimizedFlag();   // 旧版自启动值升级为托盘启动
        _watcher = new EarbudsWatcher(_config);

        _window = new MainWindow(_watcher, _config,
            u => _tray?.Feed(u), alive => _tray?.SetConnected(alive),
            enabled => Dispatcher.Invoke(() =>
            {
                _miniBar?.ApplyEnabled();
                _tray?.SyncMiniBarChecked(enabled);
            }),
            (mac, devName) => Dispatcher.Invoke(() => _miniBar?.SetDeviceName(mac, devName)),
            enabled => _hotKey.SetEnabled(_window!, enabled, () => ShowMainWindowTop()));

        _miniBar = new MiniBarWindow(_config)
        {
            OpenMainRequested = () => ShowMainWindow(),
            SystemBatteryProvider = mac => _window?.SystemBatteryOf(mac),
            AliveProvider = mac => _window?.IsDeviceAlive(mac) ?? false,
            DeviceListProvider = () => _window?.KnownDeviceList() ?? Array.Empty<(string, string)>(),
            ActiveMacProvider = () => _window?.ActiveMac,
            VoiceRequested = SpeakActive,
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
            VoiceRequested = SpeakActive,
            ExitRequested = ExitApp,
        };

        // 低电量/骤降提醒：只处理关注设备，托盘气泡弹出（语音播报开关在设置页）
        _monitor = new LowBatteryMonitor(_config, (title, msg) =>
        {
            _tray?.ShowBalloon(title, msg);
            if (_config.VoiceAlerts) _voice?.Speak(msg);
        });

        // 充满提醒：三通道齐满后报一次（同样遵守语音播报开关）
        _charge = new ChargeMonitor(_config, (title, msg) =>
        {
            _tray?.ShowBalloon(title, msg);
            if (_config.VoiceAlerts) _voice?.Speak(msg);
        });

        _watcher.DeviceBound += name => Dispatcher.Invoke(() =>
        {
            _config.BoundMac = _watcher!.BoundMac;
            _config.Save();
            Core.EarbudsWatcher.DiagLog($"App 层已保存配置: {_config.BoundMac}");
        });

        // 主面板在托盘时，耳机开盖拿到广播数据（左/右/仓）→ 主窗口拉起到最上层；
        // 同一份数据喂给悬浮条与低电量监控：悬浮条吃全量（多行显示勾选设备），
        // 低电量监控只跟随关注设备
        var lastDataAt = DateTime.MinValue;
        var lastShowAt = DateTime.Now;   // 启动后第一波广播不弹，等真正"打开耳机"
        _watcher.UpdateReceived += u => Dispatcher.Invoke(() =>
        {
            _miniBar?.Push(u);
            // 低电量 / 骤降 / 充满：全设备监控（每台独立状态、消息带名字），不只关注设备
            _monitor?.OnUpdate(u);
            _charge?.OnUpdate(u);

            var now = DateTime.Now;
            var quiet = now - lastDataAt > TimeSpan.FromSeconds(30);
            lastDataAt = now;
            if (!_config.OpenLidPopup) return;              // 设置里可关
            if (_window is { IsVisible: true }) return;
            if (u.Mac != _window?.ActiveMac) return;   // 多设备：只提示当前关注的设备
            var cooldown = TimeSpan.FromMinutes(Math.Clamp(_config.PopupCooldownMinutes, 1, 60));
            if (!quiet || now - lastShowAt < cooldown) return;
            if (Core.FullscreenProbe.IsForegroundFullscreen())
            {
                // 前台是全屏应用（游戏/视频）：不打断用户；不消耗冷却，
                // 退出全屏后下次开盖仍会弹
                Core.EarbudsWatcher.DiagLog("前台全屏，开盖弹窗抑制");
                return;
            }
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
        if (_window.ActiveMac is { } startMac)   // 补投启动时已知的设备名
            _miniBar.SetDeviceName(startMac, _window.ActiveDeviceName);
        _hotKey.SetEnabled(_window!, _config.HotKeyEnabled, () => ShowMainWindowTop());

        // 悬浮条媒体栏同步系统播放内容（SMTC）；事件在后台线程，切回 UI 再喂
        _media = new MediaSession();
        _media.Changed += info => Dispatcher.Invoke(() =>
            _miniBar?.SetMediaInfo(info.Title, info.Artist, info.Playing));
        _ = _media.InitializeAsync();

        // 低电量系统整机兜底：每隔 15 秒把各在线设备的系统电量喂给监控
        //（连接播放期间分耳广播停发，低电量判断改用整机电量）
        _sysTicker = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _sysTicker.Tick += (_, _) =>
        {
            if (_window is null || _monitor is null) return;
            foreach (var (name, mac) in _window.KnownDeviceList())
                _monitor.OnSystemBattery(mac, name, _window.SystemBatteryOf(mac));
        };
        _sysTicker.Start();

        _ = CheckUpdateDailyAsync();
    }

    /// <summary>语音播报当前显示设备的电量（悬浮条按钮 / 托盘菜单共用）。</summary>
    private void SpeakActive() => _voice?.Speak(_window?.VoiceReportText() ?? L.T("voice.noDevice"));

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
        _sysTicker?.Stop();
        _tray?.Dispose();
        _watcher?.Dispose();
        _voice?.Dispose();
        _media?.Dispose();
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
