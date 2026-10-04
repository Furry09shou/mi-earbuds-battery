using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using MiEarbuds.App.Core;

namespace MiEarbuds.App;

/// <summary>
/// 适配新耳机向导：引导用户完成 6 步动作，全程采集原始广播，
/// 结束后导出 zip 并打开 GitHub Issue 上传。
/// </summary>
public partial class CaptureWindow : Window
{
    private const string IssueUrlBase = "https://github.com/Furry09shou/mi-earbuds-battery/issues/new";

    private static readonly string[] StepHints =
    {
        "双耳入仓开盖",
        "取出左耳",
        "左耳放回",
        "取出右耳",
        "右耳放回",
        "导出上传",
    };

    private readonly CaptureService _capture = new();
    private int _step;          // 当前高亮步骤 0..5
    private bool _running;
    private int _shownCount = -1;

    public CaptureWindow()
    {
        InitializeComponent();
        _capture.Ticked += () => Dispatcher.Invoke(RefreshLive);
        UpdateStepUi();
        RefreshLive();
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _capture.Dispose();
        Close();
    }

    private void MainButton_Click(object sender, RoutedEventArgs e)
    {
        if (!_running)
        {
            _capture.Start();
            _running = true;
            _capture.AddMarker("开始采集");
            AdvanceStep();
            return;
        }

        _capture.AddMarker($"完成阶段{_step + 1}:{StepHints[_step]}");
        if (_step < 5)
        {
            AdvanceStep();
        }
        else
        {
            Finish();
        }
    }

    private void AdvanceStep()
    {
        _step = Math.Min(_step + 1, 5);
        UpdateStepUi();
        RefreshLive();
    }

    private void UpdateStepUi()
    {
        for (var i = 0; i < 6; i++)
        {
            var dot = (Border)FindName($"StepDot{i}")!;
            var num = (TextBlock)FindName($"StepNum{i}")!;
            var text = (TextBlock)FindName($"StepText{i}")!;
            var row = (StackPanel)FindName($"Step{i}")!;

            if (i < _step)
            {
                dot.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x3A, 0x28));
                num.Text = "\uE73E";                       // Segoe 勾
                num.FontFamily = new FontFamily("Segoe MDL2 Assets");
                num.FontSize = 10;
                num.Foreground = new SolidColorBrush(Color.FromRgb(0x6F, 0xBF, 0x73));
                text.Foreground = new SolidColorBrush(Color.FromRgb(0xC9, 0xC9, 0xCF));
                row.Opacity = 1;
            }
            else if (i == _step)
            {
                dot.Background = new SolidColorBrush(Color.FromRgb(0x4A, 0x32, 0x23));
                dot.BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x3E));
                dot.BorderThickness = new Thickness(1);
                num.FontFamily = new FontFamily("Segoe UI");
                num.FontSize = 11;
                num.Text = (i + 1).ToString();
                num.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x3E));
                text.Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
                text.FontWeight = FontWeights.SemiBold;
                row.Opacity = 1;
            }
            else
            {
                dot.Background = new SolidColorBrush(Color.FromRgb(0x23, 0x23, 0x29));
                num.FontFamily = new FontFamily("Segoe UI");
                num.FontSize = 11;
                num.Text = (i + 1).ToString();
                num.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98));
                text.Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98));
                text.FontWeight = FontWeights.Normal;
                row.Opacity = 0.75;
            }
        }

        MainButtonText.Text = !_running
            ? "开始采集"
            : _step < 5 ? $"下一步（{_step + 1}/6）" : "完成并上传";
    }

    private void RefreshLive()
    {
        if (_capture.Count == _shownCount) return;
        _shownCount = _capture.Count;

        var cids = _capture.CompanyIds.Count == 0
            ? "—"
            : string.Join(", ", _capture.CompanyIds.Select(c => $"0x{c:X4}"));
        var keys = _capture.ProductKeys.Count == 0
            ? "—"
            : string.Join(" / ", _capture.ProductKeys);

        LiveText.Text = _running
            ? $"已捕获 {_capture.Count} 包 · 公司代号: {cids} · 产品标识: {keys}"
            : "等待开始采集…";
    }

    private void Finish()
    {
        MainButton.IsEnabled = false;
        try
        {
            var model = ModelBox.Text.Trim();
            if (model.Length == 0) model = "未知型号";
            _capture.AddMarker("结束采集");
            var (_, zip) = _capture.Export(model);

            var body = $"机型：{model}\n" +
                       $"采集时间：{DateTime.Now:yyyy-MM-dd HH:mm}\n" +
                       $"捕获包数：{_capture.Count}\n" +
                       $"产品标识：{string.Join(" / ", _capture.ProductKeys)}\n\n" +
                       $"请把数据文件拖进评论（由应用内向导生成）：\n`{zip}`";
            var url = $"{IssueUrlBase}?title={Uri.EscapeDataString($"适配新耳机：{model}")}" +
                      $"&body={Uri.EscapeDataString(body)}";
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

            MessageBox.Show(this,
                $"采集完成，共 {_capture.Count} 包。\n\n数据已导出：\n{zip}\n\n" +
                "浏览器已打开 GitHub Issue 页面，请注册/登录 GitHub 账号，把该 zip 文件拖进评论框提交。" +
                "维护者分析后会通过软件更新加入你的机型支持。",
                "导出成功", MessageBoxButton.OK, MessageBoxImage.Information);
            _capture.Dispose();
            Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"导出失败：{ex.Message}", "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
            MainButton.IsEnabled = true;
        }
    }
}
