using System.IO;
using System.Threading;
using System.Windows;
using MiEarbuds.App.Core;
using MiEarbuds.App.UI;

namespace MiEarbuds.App;

public partial class App : Application
{
    private static readonly Mutex SingleInstance = new(true, "MiEarbuds_SingleInstance_E1B7", out _);

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
            Shutdown();
            return;
        }

        _config = AppConfig.Load();
        _watcher = new EarbudsWatcher(_config);

        _window = new MainWindow(_watcher, _config, u => _tray?.Feed(u));

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

        _watcher.Start();

        if (e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)))
            _window.Hide();
        else
            ShowMainWindow();
    }

    private void ShowMainWindow()
    {
        _window!.Show();
        _window.Activate();
    }

    private void ExitApp()
    {
        _window?.PersistPosition(_config);
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
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiEarbuds");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:HH:mm:ss}] {source}: {ex}\r\n\r\n");
        }
        catch { /* 日志失败只能放弃 */ }
    }
}
