using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using RonghuiEarbuds.App.Core;

namespace RonghuiEarbuds.App.UI;

/// <summary>弹窗类型：决定图标与强调色。</summary>
public enum DialogKind { Success, Question, Error, Info }

/// <summary>
/// 应用内主题化模态弹窗，替代系统 MessageBox：原生弹窗与应用深浅主题割裂、观感生硬。
/// 跟随 ThemeManager 深浅色（T.* 动态资源）与 L 双语；
/// 淡入 + 缩放入场动画，点击后淡出退场；Enter=主按钮、Esc=取消。
/// </summary>
public static class MessageDialog
{
    /// <summary>
    /// 显示模态弹窗（阻塞直到关闭，与 MessageBox 语义一致）。
    /// 返回 true=点了主按钮（确定）或按回车；false=点了取消或按 Esc。
    /// </summary>
    public static bool Show(Window owner, string title, string message,
        DialogKind kind = DialogKind.Info, bool showCancel = false)
    {
        var dlg = new DialogWindow(title, message, kind, showCancel)
        {
            Owner = owner,
            WindowStartupLocation = owner is null
                ? WindowStartupLocation.CenterScreen
                : WindowStartupLocation.CenterOwner,
        };
        dlg.ShowDialog();
        return dlg.Result;
    }

    private sealed class DialogWindow : Window
    {
        public bool Result { get; private set; }

        private readonly Border _card;
        private readonly ScaleTransform _scale = new(0.94, 0.94);
        private readonly TranslateTransform _slide = new(0, 10);
        private bool _closing;

        public DialogWindow(string title, string message, DialogKind kind, bool showCancel)
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            SizeToContent = SizeToContent.Height;
            Width = 380;
            ShowActivated = true;
            UseLayoutRounding = true;

            var (glyph, colorKey) = kind switch
            {
                DialogKind.Success => ("\uE73E", "T.Good"),      // 对勾
                DialogKind.Question => ("\uE11B", "T.Accent"),   // 问号
                DialogKind.Error   => ("\uE7BA", "T.Low"),       // 叉
                _                   => ("\uE946", "T.TextOrange"),// 信息
            };

            // 图标圆底：强调色低透明度，深浅主题下都成立
            var ic = ThemeManager.GetColor(colorKey);
            var tint = new SolidColorBrush(Color.FromArgb(38, ic.R, ic.G, ic.B));
            var icon = new TextBlock
            {
                Text = glyph,
                FontFamily = new FontFamily("Segoe MDL2 Assets"),
                FontSize = 15,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            icon.SetResourceReference(ForegroundProperty, colorKey);
            var circle = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19),
                Background = tint, VerticalAlignment = VerticalAlignment.Center,
                Child = icon,
            };

            var titleText = new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(13, 0, 0, 0),
            };
            titleText.SetResourceReference(ForegroundProperty, "T.TextPrimary");

            var msgText = new TextBlock
            {
                Text = message, FontSize = 12.5, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 13, 0, 0),
            };
            msgText.SetResourceReference(ForegroundProperty, "T.TextSecondary");

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0),
            };
            if (showCancel)
            {
                var cancel = MakeButton(L.T("dialog.cancel"), primary: false);
                cancel.MouseLeftButtonUp += (_, _) => CloseWith(false);
                buttons.Children.Add(cancel);
            }
            var ok = MakeButton(L.T("dialog.ok"), primary: true);
            var okMargin = showCancel ? new Thickness(10, 0, 0, 0) : new Thickness(0);
            ok.Margin = okMargin;
            ok.MouseLeftButtonUp += (_, _) => CloseWith(true);
            buttons.Children.Add(ok);

            var content = new StackPanel();
            content.Children.Add(new DockPanel { Children = { circle, titleText } });
            content.Children.Add(msgText);
            content.Children.Add(buttons);
            DockPanel.SetDock(circle, Dock.Left);

            _card = new Border
            {
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Margin = new Thickness(18),   // 给阴影留呼吸空间，避免被窗口边界裁切
                Child = content,
                RenderTransform = new TransformGroup { Children = { _scale, _slide } },
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black, Direction = 270, ShadowDepth = 4,
                    BlurRadius = 26, Opacity = 0.45,
                },
            };
            _card.SetResourceReference(BackgroundProperty, "T.WindowBg");
            _card.SetResourceReference(BorderBrushProperty, "T.WindowBorder");

            Content = new Grid { Children = { _card } };

            Loaded += OnLoaded;
            KeyDown += OnKeyDown;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(0.94, 1, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _slide.BeginAnimation(TranslateTransform.YProperty,
                new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(260)) { EasingFunction = ease });
            _card.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)));
        }

        private void OnKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) CloseWith(true);
            else if (e.Key == Key.Escape) CloseWith(false);
        }

        private void CloseWith(bool result)
        {
            if (_closing) return;
            _closing = true;
            Result = result;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130));
            var shrink = new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => Close();
            _card.BeginAnimation(OpacityProperty, fade);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty, shrink);
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty, shrink);
        }

        /// <summary>代码构建弹窗按钮：主按钮=橙色实底，次按钮=描边，悬停用覆盖层渐显（保留主题动态资源）。</summary>
        private static Border MakeButton(string text, bool primary)
        {
            var overlay = new Border
            {
                CornerRadius = new CornerRadius(9),
                Opacity = 0,
                IsHitTestVisible = false,
            };
            overlay.SetResourceReference(BackgroundProperty, primary ? "T.AccentHover" : "T.HoverBg");

            var label = new TextBlock
            {
                Text = text, FontSize = 12.5,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            if (primary) label.FontWeight = FontWeights.SemiBold;
            label.SetResourceReference(ForegroundProperty, primary ? "T.AccentOn" : "T.TextSecondary");

            var root = new Border
            {
                CornerRadius = new CornerRadius(9),
                MinWidth = 96, Height = 36,
                Padding = new Thickness(14, 0, 14, 0),
                Cursor = Cursors.Hand,
                BorderThickness = primary ? new Thickness(0) : new Thickness(1),
                Child = new Grid { Children = { label, overlay } },
                Tag = overlay,
            };
            root.SetResourceReference(BackgroundProperty, primary ? "T.Accent" : "T.CardBg");
            if (!primary) root.SetResourceReference(BorderBrushProperty, "T.InputBorder");

            root.MouseEnter += (_, _) =>
            {
                if (root.Tag is Border ov)
                    ov.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(1, TimeSpan.FromMilliseconds(110)));
            };
            root.MouseLeave += (_, _) =>
            {
                if (root.Tag is Border ov)
                    ov.BeginAnimation(OpacityProperty,
                        new DoubleAnimation(0, TimeSpan.FromMilliseconds(140)));
            };
            return root;
        }
    }
}
