using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;

namespace RonghuiEarbuds.Setup;

/// <summary>
/// 安装器/卸载器主题弹窗，替代系统 MessageBox：与安装器深色设计一体，
/// 淡入缩放入场动画、Esc=取消。文案按钮由调用方从 Loc 取，保持双语解耦。
/// </summary>
internal static class SetupDialog
{
    /// <summary>显示模态弹窗。返回 true=主按钮/回车；false=次按钮/Esc。</summary>
    public static bool Show(Window owner, string title, string message,
        string confirmText, string? cancelText = null)
    {
        var dlg = new DialogWindow(title, message, confirmText, cancelText)
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

        // 与 InstallerWindow 一致的固定深色板
        private static readonly Brush Bg = new SolidColorBrush(Color.FromRgb(0x14, 0x14, 0x17));
        private static readonly Brush Border = new SolidColorBrush(Color.FromRgb(0x2B, 0x2B, 0x31));
        private static readonly Brush TextMain = new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0));
        private static readonly Brush TextSub = new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98));
        private static readonly Brush Accent = new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x3E));
        private static readonly Brush AccentHover = new SolidColorBrush(Color.FromRgb(0xF0, 0x8B, 0x4F));
        private static readonly Brush AccentOn = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
        private static readonly Brush HoverBg = new SolidColorBrush(Color.FromRgb(0x26, 0x26, 0x2C));
        private static readonly Brush InputBorder = new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42));

        public DialogWindow(string title, string message, string confirmText, string? cancelText)
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

            var circle = new Border
            {
                Width = 38, Height = 38, CornerRadius = new CornerRadius(19),
                Background = new SolidColorBrush(Color.FromArgb(38, 0xE8, 0x7A, 0x3E)),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = "\uE946", FontFamily = new FontFamily("Segoe MDL2 Assets"),
                    FontSize = 15, Foreground = Accent,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                },
            };

            var titleText = new TextBlock
            {
                Text = title, FontSize = 15, FontWeight = FontWeights.SemiBold,
                Foreground = TextMain, VerticalAlignment = VerticalAlignment.Center,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(13, 0, 0, 0),
            };

            var msgText = new TextBlock
            {
                Text = message, FontSize = 12.5, Foreground = TextSub,
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 13, 0, 0),
            };

            var buttons = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Right,
                Margin = new Thickness(0, 22, 0, 0),
            };
            if (cancelText is not null)
            {
                var cancel = MakeButton(cancelText, primary: false);
                cancel.MouseLeftButtonUp += (_, _) => CloseWith(false);
                buttons.Children.Add(cancel);
            }
            var ok = MakeButton(confirmText, primary: true);
            if (cancelText is not null) ok.Margin = new Thickness(10, 0, 0, 0);
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
                Background = Bg, BorderBrush = Border, BorderThickness = new Thickness(1),
                Padding = new Thickness(24, 20, 24, 20),
                Margin = new Thickness(18),   // 阴影呼吸空间，避免被窗口边界裁切
                Child = content,
                RenderTransform = new TransformGroup { Children = { _scale, _slide } },
                RenderTransformOrigin = new Point(0.5, 0.5),
                Effect = new DropShadowEffect
                {
                    Color = Colors.Black, Direction = 270, ShadowDepth = 4,
                    BlurRadius = 26, Opacity = 0.45,
                },
            };

            Content = new Grid { Children = { _card } };

            Loaded += OnLoaded;
            KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CloseWith(true);
                else if (e.Key == Key.Escape) CloseWith(false);
            };
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

        private void CloseWith(bool result)
        {
            if (_closing) return;
            _closing = true;
            Result = result;

            var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(130));
            fade.Completed += (_, _) => Close();
            _card.BeginAnimation(OpacityProperty, fade);
            _scale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(130)));
            _scale.BeginAnimation(ScaleTransform.ScaleYProperty,
                new DoubleAnimation(1, 0.96, TimeSpan.FromMilliseconds(130)));
        }

        private static Border MakeButton(string text, bool primary)
        {
            var overlay = new Border
            {
                CornerRadius = new CornerRadius(9),
                Background = primary ? AccentHover : HoverBg,
                Opacity = 0, IsHitTestVisible = false,
            };
            var label = new TextBlock
            {
                Text = text, FontSize = 12.5,
                Foreground = primary ? AccentOn : TextSub,
                FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            var root = new Border
            {
                CornerRadius = new CornerRadius(9),
                MinWidth = 96, Height = 36,
                Padding = new Thickness(14, 0, 14, 0),
                Cursor = Cursors.Hand,
                Background = primary ? Accent : Brushes.Transparent,
                BorderThickness = primary ? new Thickness(0) : new Thickness(1),
                BorderBrush = primary ? null : InputBorder,
                Child = new Grid { Children = { label, overlay } },
                Tag = overlay,
            };
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
