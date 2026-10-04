using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MiEarbuds.App.Core;

namespace MiEarbuds.App;

/// <summary>
/// 适配窗口：第一屏为已适配机型名单；「适配新耳机」进入分页向导——
/// 一页一步（页 0 写入型号，页 1-5 动作步骤），全程采集原始广播，
/// 结束后导出 zip 并打开 GitHub Issue 上传。途中可随时取消。
/// </summary>
public partial class CaptureWindow : Window
{
    private const string IssueUrlBase = "https://github.com/Furry09shou/mi-earbuds-battery/issues/new";

    private static readonly (string Title, string Detail)[] Steps =
    {
        ("双耳入仓，开盖等 10 秒", "把两只耳机都放回充电仓，保持仓盖打开，等待约 10 秒——让耳机处于统一的初始状态。"),
        ("取出左耳，等 10 秒", "把左耳从仓中取出、戴或不戴都可以，右耳留在仓内，等待约 10 秒。"),
        ("左耳放回，等 10 秒", "把左耳放回仓内，等待约 10 秒。"),
        ("取出右耳，等 10 秒", "把右耳从仓中取出，左耳留在仓内，等待约 10 秒。"),
        ("右耳放回，完成采集", "把右耳放回仓内，等待约 10 秒，然后点击「完成并上传」。"),
    };

    private const double ListHeight = 480;
    private const double WizardHeight = 520;

    private readonly CaptureService _capture = new();
    private int _page;          // 向导当前页 0..5
    private bool _running;
    private int _shownCount = -1;

    public CaptureWindow()
    {
        InitializeComponent();
        ModelList.ItemsSource = XiaomiAdvParser.GetSupportedNames();
        _capture.Ticked += () => Dispatcher.Invoke(RefreshLive);
        ShowList(instant: true);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) => DragMove();

    // ---------- 视图切换 ----------

    private void ShowList(bool instant = false)
    {
        _page = -1;
        TitleText.Text = "适配名单";
        ShowOnly(ListPage);
        DotsRow.Visibility = Visibility.Collapsed;
        LiveBox.Visibility = Visibility.Collapsed;
        BackButton.Content = "取消";
        MainButtonText.Text = "适配新耳机 ›";
        AnimateHeight(ListHeight, instant);
        AnimateView(instant);
    }

    private void ShowPage(int page)
    {
        _page = page;
        TitleText.Text = "适配新耳机";
        ShowOnly(page == 0 ? (UIElement)Page0 : (UIElement)FindName($"Page{page}")!);

        // 进度点：0=型号，1..5=动作
        DotsRow.Visibility = Visibility.Visible;
        BuildDots(page);

        LiveBox.Visibility = page >= 1 ? Visibility.Visible : Visibility.Collapsed;
        BackButton.Content = page == 0 && !_running ? "‹ 返回" : "取消";

        MainButtonText.Text = page switch
        {
            0 => "开始采集",
            5 => "完成并上传",
            _ => $"下一步（{_page}/5）",
        };
        AnimateHeight(WizardHeight, instant: false);
        AnimateView(instant: false);
    }

    private void ShowOnly(UIElement current)
    {
        ListPage.Visibility = current == ListPage ? Visibility.Visible : Visibility.Collapsed;
        for (var i = 0; i <= 5; i++)
        {
            var page = (UIElement)FindName($"Page{i}")!;
            page.Visibility = ReferenceEquals(page, current) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private Border[]? _dots;

    private void BuildDots(int active)
    {
        if (_dots is null)
        {
            _dots = new Border[6];
            for (var i = 0; i < 6; i++)
            {
                _dots[i] = new Border
                {
                    Width = 8,
                    Height = 8,
                    CornerRadius = new CornerRadius(4),
                    Margin = new Thickness(4, 0, 4, 0),
                };
                DotsRow.Children.Add(_dots[i]);
            }
        }
        for (var i = 0; i < 6; i++)
        {
            _dots[i].Background = new SolidColorBrush(i <= active
                ? Color.FromRgb(0xE8, 0x7A, 0x3E)
                : Color.FromRgb(0x3A, 0x3A, 0x42));
        }
    }

    private void AnimateView(bool instant)
    {
        if (instant)
        {
            Views.Opacity = 1;
            ViewsTranslate.Y = 0;
            return;
        }
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        var slide = new DoubleAnimation(14, 0, TimeSpan.FromMilliseconds(240))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Views.BeginAnimation(OpacityProperty, fade);
        ViewsTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    private void AnimateHeight(double target, bool instant)
    {
        if (instant || Math.Abs(Height - target) < 0.5)
        {
            Height = target;
            return;
        }
        var anim = new DoubleAnimation(Height, target, TimeSpan.FromMilliseconds(220))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        BeginAnimation(HeightProperty, anim);
    }

    // ---------- 型号输入占位符 ----------

    private const string ModelPlaceholder = "例如：Redmi Buds 5";
    private static readonly Color PlaceholderColor = Color.FromRgb(0x5C, 0x5C, 0x66);
    private static readonly Color InputColor = Color.FromRgb(0xED, 0xED, 0xF0);

    private void ModelBox_GotFocus(object sender, RoutedEventArgs e)
    {
        if (ModelBox.Text == ModelPlaceholder)
        {
            ModelBox.Text = "";
            ModelBox.Foreground = new SolidColorBrush(InputColor);
        }
    }

    private void ModelBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (ModelBox.Text.Trim().Length == 0)
        {
            ModelBox.Text = ModelPlaceholder;
            ModelBox.Foreground = new SolidColorBrush(PlaceholderColor);
        }
    }

    // ---------- 按钮 ----------

    private void BackButton_Click(object sender, RoutedEventArgs e)
    {
        // 标题栏 ✕ 与左下角按钮共用：向导中=取消/返回，名单页=关闭
        if (_page < 0)
        {
            CloseWindow();
            return;
        }
        if (_page == 0 && !_running)
        {
            ShowList();
            return;
        }
        TryCancel();
    }

    private void TryCancel()
    {
        if (!_running)
        {
            ShowList();
            return;
        }
        if (MessageBox.Show(this, "采集进行中，确定取消并丢弃已采集的数据吗？", "适配新耳机",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }
        CloseWindow();
    }

    private void CloseWindow()
    {
        _capture.Dispose();
        Close();
    }

    private void MainButton_Click(object sender, RoutedEventArgs e)
    {
        // 名单页 → 进入向导
        if (_page < 0)
        {
            ShowPage(0);
            return;
        }

        // 页 0：写入型号 + 开始
        if (_page == 0)
        {
            if (ModelBox.Text.Trim().Length == 0 || ModelBox.Text.Trim() == ModelPlaceholder)
            {
                ModelHint.Visibility = Visibility.Visible;
                ModelBox.Focus();
                return;
            }
            ModelHint.Visibility = Visibility.Collapsed;
            _capture.Start();
            _running = true;
            _capture.AddMarker("开始采集");
            ShowPage(1);
            return;
        }

        // 页 1..5：下一步 / 完成上传
        _capture.AddMarker($"完成阶段{_page}:{Steps[_page - 1].Title}");
        if (_page < 5)
        {
            ShowPage(_page + 1);
        }
        else
        {
            Finish();
        }
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
