using System.Drawing;
using RonghuiEarbuds.App.Core;
using WinForms = System.Windows.Forms;

namespace RonghuiEarbuds.App.UI;

/// <summary>
/// 托盘图标：动态显示最低电量数字 + 气泡通知（绑定成功 / 更新可用；
/// 低电量与骤降提醒由 LowBatteryMonitor 经 App 转发到此弹出）。
/// 左键单击切换主面板，右键菜单（主面板/悬浮条/设置/退出）。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private readonly AppConfig _config;
    private Bitmap? _currentBitmap;
    private Icon? _currentIcon;
    private int? _shownPercent = int.MinValue;

    public Action? ToggleWindow { get; init; }
    public Action? ShowWindowRequested { get; init; }
    public Action? ShowSettingsRequested { get; init; }
    public Action? ToggleMiniBarRequested { get; init; }
    public Action? VoiceRequested { get; init; }
    public Action? ExitRequested { get; init; }

    private WinForms.ToolStripMenuItem _miniBarItem = new();

    public TrayController(EarbudsWatcher watcher, AppConfig config)
    {
        _config = config;
        _icon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = L.T("main.title"),
        };
        SetIcon(null);

        BuildMenu();
        L.Changed += OnLanguageChanged;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ToggleWindow?.Invoke();
        };

        watcher.DeviceBound += name =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                ShowBalloon(L.F("tray.boundFmt", name), L.T("tray.boundMsg")));
    }

    /// <summary>按当前语言构建托盘右键菜单（语言切换时整体重建）。</summary>
    private void BuildMenu()
    {
        _miniBarItem = new WinForms.ToolStripMenuItem(L.T("tray.miniBar"))
        {
            CheckOnClick = true,
            Checked = _config.MiniBarEnabled,
        };
        _miniBarItem.Click += (_, _) => ToggleMiniBarRequested?.Invoke();

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add(L.T("tray.showPanel"), null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add(L.T("tray.speak"), null, (_, _) => VoiceRequested?.Invoke());
        menu.Items.Add(L.T("main.settings"), null, (_, _) => ShowSettingsRequested?.Invoke());
        menu.Items.Add(_miniBarItem);
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add(L.T("tray.exit"), null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;
    }

    private void OnLanguageChanged()
    {
        // L.Changed 在 UI 线程触发；托盘句柄也归 UI 线程，直接重建即可
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.Invoke(() =>
        {
            BuildMenu();
            _icon.Text = L.T("main.title");
            if (!_connected) _icon.Text = $"{L.T("main.title")}\n{L.T("state.signalLostShort")}";
            RefreshFromLast();
        });
    }

    /// <summary>设置界面开关悬浮条后同步托盘菜单勾选态。</summary>
    public void SyncMiniBarChecked(bool enabled) => _miniBarItem.Checked = enabled;

    private EarbudsUpdate? _last;
    private bool _connected = true;

    /// <summary>由 MainWindow 把最新数据回填给托盘（共享同一份状态）。</summary>
    public void Feed(EarbudsUpdate update)
    {
        _last = update;
        RefreshFromLast();
    }

    /// <summary>
    /// 连接状态变化时由主窗口通知：断开后悬浮提示不再显示过期电量，
    /// 恢复广播时下一次 RefreshFromLast 会自动覆盖。
    /// </summary>
    public void SetConnected(bool connected)
    {
        if (_connected == connected) return;
        _connected = connected;
        if (!connected)
        {
            _icon.Text = $"{L.T("main.title")}\n{L.T("state.signalLostShort")}";
        }
    }

    private void RefreshFromLast()
    {
        var s = _last?.Snapshot;
        if (s is null) return;

        int? display = MinOrNull(s.LeftPercent, s.RightPercent) ?? s.CasePercent;
        SetIcon(display);

        string? fmt(int? v) => v is null ? "--" : v.Value.ToString();
        var name = _last?.DisplayName;
        if (string.IsNullOrWhiteSpace(name)) name = L.T("mini.earbuds");
        var tip = L.F("tray.tipFmt", name, fmt(s.LeftPercent), fmt(s.RightPercent), fmt(s.CasePercent));
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];
    }

    private static int? MinOrNull(params int?[] values) =>
        values.Where(v => v.HasValue).Select(v => v!.Value).Cast<int?>().DefaultIfEmpty(null).Min();

    private void SetIcon(int? percent)
    {
        if (_shownPercent == percent) return;
        _shownPercent = percent;

        _currentBitmap?.Dispose();
        _currentIcon?.Dispose();
        _currentBitmap = TrayIconRenderer.Render(percent);
        _currentIcon = Icon.FromHandle(_currentBitmap.GetHicon());
        _icon.Icon = _currentIcon;
    }

    public void ShowBalloon(string title, string message) =>
        _icon.ShowBalloonTip(3000, title, message, WinForms.ToolTipIcon.Info);

    private string? _balloonUrl;

    /// <summary>更新提醒气泡：点击气泡打开下载页。</summary>
    public void ShowUpdateBalloon(string title, string message, string url)
    {
        _balloonUrl = url;
        _icon.BalloonTipClicked -= OnBalloonUrlClick;
        _icon.BalloonTipClicked += OnBalloonUrlClick;
        ShowBalloon(title, message);
    }

    private void OnBalloonUrlClick(object? sender, EventArgs e)
    {
        if (_balloonUrl is not { } url) return;
        try
        {
            System.Diagnostics.Process.Start(
                new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch { /* 打开浏览器失败只能忽略 */ }
    }

    public void Dispose()
    {
        L.Changed -= OnLanguageChanged;
        _icon.Dispose();
        _currentIcon?.Dispose();
        _currentBitmap?.Dispose();
    }
}
