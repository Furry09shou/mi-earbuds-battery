using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Controls;
using MiEarbuds.App.Core;

namespace MiEarbuds.App;

/// <summary>
/// 最上层电量提示气泡：主面板隐藏时耳机开始广播（开盖/连上）自动弹出，
/// 约 5 秒自动淡出，点击打开主面板。
/// </summary>
public partial class ToastWindow : Window
{
    private readonly Action? _openMain;

    public ToastWindow(BatterySnapshot snapshot, Action? openMain)
    {
        InitializeComponent();
        _openMain = openMain;

        string? fmt(int? v) => v is null ? "--" : v.Value.ToString();
        AddRow("左耳", fmt(snapshot.LeftPercent), snapshot.LeftInCase);
        AddRow("右耳", fmt(snapshot.RightPercent), snapshot.RightInCase);
        AddRow("充电仓", fmt(snapshot.CasePercent), null);

        // 右下角，托盘上方
        var wa = SystemParameters.WorkArea;
        Left = wa.Right - Width - 16;
        Top = wa.Bottom - Height - 48;
    }

    private void AddRow(string label, string value, bool? charging)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 3, 0, 0) };
        panel.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98)),
            Width = 52,
        });
        panel.Children.Add(new TextBlock
        {
            Text = $"{value}%",
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
        });
        if (charging == true)
        {
            panel.Children.Add(new TextBlock
            {
                Text = "  ⚡充电中",
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3E)),
            });
        }
        Rows.Children.Add(panel);
    }

    protected override void OnContentRendered(EventArgs e)
    {
        base.OnContentRendered(e);
        // 进入动画
        Root.Opacity = 0;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));

        // 5 秒后淡出关闭
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(260));
            fade.Completed += (_, _) => Close();
            Root.BeginAnimation(OpacityProperty, fade);
        };
        timer.Start();
    }

    private void Root_Click(object sender, MouseButtonEventArgs e)
    {
        _openMain?.Invoke();
        Close();
    }
}
