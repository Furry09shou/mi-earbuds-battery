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
        _watcher = new EarbudsWatcher(_config);

        _window = new MainWindow(_watcher, _config,
            u => _tray?.Feed(u), alive => _tray?.SetConnected(alive));

        _tray = new TrayController(_watcher)
        {
            ToggleWindow = () =>
            {
                if (_window!.IsVisible && _window.WindowState != System.Windows.WindowState.Minimized)
                    _window.Hide();
                else
                    ShowMainWindow();
            },
            ShowWindowRequested = ShowMainWindow,
            ExitRequested = ExitApp,
        };

        _watcher.DeviceBound += name => Dispatcher.Invoke(() =>
        {
            _config.BoundMac = _watcher!.BoundMac;
            _config.Save();
            Core.EarbudsWatcher.DiagLog($"App 层已保存配置: {_config.BoundMac}");
        });

        // 主面板隐藏时，耳机从休眠中醒来（开盖/重新连上）→ 最上层电量提示
        var lastDataAt = DateTime.MinValue;
        var lastToastAt = DateTime.Now;   // 启动后第一波广播不弹，等真正"打开耳机"
        _watcher.UpdateReceived += u => Dispatcher.Invoke(() =>
        {
            var now = DateTime.Now;
            var quiet = now - lastDataAt > TimeSpan.FromSeconds(30);
            lastDataAt = now;
            if (_window is { IsVisible: true }) return;
            if (u.Mac != _window?.ActiveMac) return;   // 多设备：只提示当前关注的设备
            if (!quiet || now - lastToastAt < TimeSpan.FromMinutes(5)) return;
            lastToastAt = now;
            var toast = new ToastWindow(u.Snapshot, ShowMainWindow);
            toast.Show();
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

        _ = CheckUpdateDailyAsync();
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
                $"新版本 v{info.Version} 可用",
                "点击此气泡打开下载页，或在主面板点“检查更新”", info.Url));
        }
        catch { /* 无网络/接口异常时静默跳过 */ }
    }

    private void ShowMainWindow()
    {
        _window!.Show();
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.PersistPosition(_config);
        _window?.DisposeCapture();
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
