using System.Globalization;
using System.Windows;
using System.Windows.Media;
using RonghuiEarbuds.App.Core;

namespace RonghuiEarbuds.App.UI;

/// <summary>
/// 自绘电量圆环：底环 + 进度弧 + 居中百分比文字。
/// Level = -1 表示未知（显示 --）。
/// </summary>
public sealed class BatteryRing : FrameworkElement
{
    // 主题画刷每次渲染时动态取（跟随深浅模式）
    private static Brush TrackBrush =>
    System.Windows.Application.Current.TryFindResource("T.RingTrack") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x2A, 0x2A, 0x31));
    private static Brush TextBrush =>
        System.Windows.Application.Current.TryFindResource("T.TextPrimary") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
    private static Brush DimBrush =>
        System.Windows.Application.Current.TryFindResource("T.TextSecondary") as Brush
        ?? new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98));

    public static readonly DependencyProperty LevelProperty = DependencyProperty.Register(
        nameof(Level), typeof(double), typeof(BatteryRing),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));

    public static readonly DependencyProperty RingColorProperty = DependencyProperty.Register(
        nameof(RingColor), typeof(Color), typeof(BatteryRing),
        new FrameworkPropertyMetadata(Color.FromRgb(0x6F, 0xBF, 0x73),
            FrameworkPropertyMetadataOptions.AffectsRender));

    public BatteryRing()
    {
        // 主题切换时重绘（画刷是渲染时动态取的）
        ThemeManager.ThemeChanged += () => Dispatcher.Invoke(InvalidateVisual);
    }

    public double Level
    {
        get => (double)GetValue(LevelProperty);
        set => SetValue(LevelProperty, value);
    }

    public Color RingColor
    {
        get => (Color)GetValue(RingColorProperty);
        set => SetValue(RingColorProperty, value);
    }

    /// <summary>平滑动画过渡到目标电量。</summary>
    public void AnimateTo(double value)
    {
        var anim = new System.Windows.Media.Animation.DoubleAnimation(value,
            TimeSpan.FromMilliseconds(600))
        {
            EasingFunction = new System.Windows.Media.Animation.CubicEase
            {
                EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut,
            },
        };
        BeginAnimation(LevelProperty, anim);
    }

    /// <summary>无动画直接设置（未知态等）。</summary>
    public void SetInstant(double value)
    {
        BeginAnimation(LevelProperty, null);
        Level = value;
    }

    protected override void OnRender(DrawingContext dc)
    {
        double w = RenderSize.Width, h = RenderSize.Height;
        if (w <= 0 || h <= 0) return;

        double stroke = Math.Max(6, Math.Min(w, h) * 0.088);
        double r = (Math.Min(w, h) - stroke) / 2;
        var center = new Point(w / 2, h / 2);

        // 底环
        var trackPen = new Pen(TrackBrush, stroke);
        dc.DrawEllipse(null, trackPen, center, r, r);

        // 进度弧
        double lvl = Math.Clamp(Level, 0, 100);
        if (Level >= 0 && lvl > 0.5)
        {
            double sweep = lvl / 100.0 * 2 * Math.PI * 0.9999;
            var start = new Point(center.X, center.Y - r);
            var end = new Point(center.X + r * Math.Sin(sweep), center.Y - r * Math.Cos(sweep));
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(start, false, false);
                ctx.ArcTo(end, new Size(r, r), 0, sweep > Math.PI,
                    SweepDirection.Clockwise, true, false);
            }
            var pen = new Pen(new SolidColorBrush(RingColor), stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawGeometry(null, pen, geo);
        }

        // 居中文字
        var dip = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        string number = Level >= 0 ? ((int)Math.Round(lvl)).ToString(CultureInfo.InvariantCulture) : "--";
        var typeface = new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal,
            FontWeights.SemiBold, FontStretches.Normal);

        var ftNum = new FormattedText(number, CultureInfo.InvariantCulture,
            FlowDirection.LeftToRight, typeface, w * (Level >= 100 ? 0.27 : 0.32),
            Level >= 0 ? TextBrush : DimBrush, dip);
        double numY = center.Y - ftNum.Height / 2 - h * 0.02;

        if (Level >= 0)
        {
            dc.DrawText(ftNum, new Point(center.X - (ftNum.Width + w * 0.10) / 2, numY));
            var ftPct = new FormattedText("%", CultureInfo.InvariantCulture,
                FlowDirection.LeftToRight, typeface, w * 0.13, DimBrush, dip);
            dc.DrawText(ftPct, new Point(center.X + ftNum.Width / 2 + 1,
                center.Y - ftPct.Height / 2 - h * 0.02 + h * 0.055));
        }
        else
        {
            dc.DrawText(ftNum, new Point(center.X - ftNum.Width / 2, numY));
        }
    }
}
