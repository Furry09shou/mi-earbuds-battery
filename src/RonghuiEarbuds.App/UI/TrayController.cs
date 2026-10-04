using System.Drawing;
using RonghuiEarbuds.App.Core;
using WinForms = System.Windows.Forms;

namespace RonghuiEarbuds.App.UI;

/// <summary>
/// 托盘图标：动态显示最低电量数字 + 气泡通知（绑定成功 / 低电量提醒）。
/// 左键单击切换主面板，右键菜单。
/// </summary>
public sealed class TrayController : IDisposable
{
    private readonly WinForms.NotifyIcon _icon;
    private Bitmap? _currentBitmap;
    private Icon? _currentIcon;
    private int? _shownPercent = int.MinValue;
    private bool _lowWarned;

    public Action? ToggleWindow { get; init; }
    public Action? ShowWindowRequested { get; init; }
    public Action? ExitRequested { get; init; }

    public TrayController(EarbudsWatcher watcher)
    {
        _icon = new WinForms.NotifyIcon
        {
            Visible = true,
            Text = "绒汇耳机助手",
        };
        SetIcon(null);

        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主面板", null, (_, _) => ShowWindowRequested?.Invoke());
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitRequested?.Invoke());
        _icon.ContextMenuStrip = menu;

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == WinForms.MouseButtons.Left)
                ToggleWindow?.Invoke();
        };

        watcher.DeviceBound += name =>
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
                ShowBalloon($"已绑定 {name}", "打开充电仓盖即可查看电量"));

        watcher.UpdateReceived += _ =>
            System.Windows.Application.Current.Dispatcher.Invoke(RefreshFromLast);
    }

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
            _icon.Text = "绒汇耳机助手\n信号丢失 · 打开仓盖刷新";
        }
    }

    private void RefreshFromLast()
    {
        var s = _last?.Snapshot;
        if (s is null) return;

        int? display = MinOrNull(s.LeftPercent, s.RightPercent) ?? s.CasePercent;
        SetIcon(display);

        string? fmt(int? v) => v is null ? "--" : v.Value.ToString();
        var tip = $"Mi Air2 SE\n左耳 {fmt(s.LeftPercent)}%   右耳 {fmt(s.RightPercent)}%\n充电仓 {fmt(s.CasePercent)}%";
        _icon.Text = tip.Length <= 63 ? tip : tip[..63];

        int? lowest = MinOrNull(s.LeftPercent, s.RightPercent, s.CasePercent);
        if (lowest is < 20 && !_lowWarned)
        {
            _lowWarned = true;
            ShowBalloon("耳机电量不足 20%", "建议把耳机放回充电仓");
        }
        else if (lowest is > 25)
        {
            _lowWarned = false;
        }
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
        _icon.Dispose();
        _currentIcon?.Dispose();
        _currentBitmap?.Dispose();
    }
}
