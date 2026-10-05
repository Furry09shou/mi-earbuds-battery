using System.Windows;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 深浅主题管理：跟随 Windows 系统设置（注册表 AppsUseLightTheme），
/// 切换时刷新 Application 资源字典里的主题画刷并广播事件。
/// XAML 侧用 {DynamicResource T.*} 引用，代码侧用 GetColor/GetBrush。
/// </summary>
public static class ThemeManager
{
    private const string PersonalizeKey =
        @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    /// <summary>当前是否深色。默认深色（应用的主形态）。</summary>
    public static bool IsDark { get; private set; } = true;

    /// <summary>主题切换（含启动首次应用）后触发，订阅方刷新自己的硬编码颜色。</summary>
    public static event Action? ThemeChanged;

    private static readonly (string Key, string Dark, string Light)[] Palette =
    {
        ("T.WindowBg",      "#141417", "#F6F6F9"),
        ("T.WindowBorder",  "#2B2B31", "#E0E0E6"),
        ("T.CardBg",        "#1C1C21", "#FFFFFF"),
        ("T.CardBorder",    "#2B2B31", "#E6E6EC"),
        ("T.SoftBg",        "#19191E", "#F0F0F5"),
        ("T.HoverBg",       "#26262C", "#EBEBF1"),
        ("T.InputBg",       "#1C1C21", "#FFFFFF"),
        ("T.InputBorder",   "#3A3A42", "#D8D8E0"),
        ("T.SubtleBorder",  "#232329", "#E8E8EE"),
        ("T.TextPrimary",   "#EDEDF0", "#202027"),
        ("T.TextSecondary", "#8F8F98", "#66666F"),
        ("T.TextDim",       "#5C5C66", "#96969E"),
        ("T.TextGreen",     "#9FB58A", "#4E8A52"),
        ("T.TextOrange",    "#C98A5A", "#B4682F"),
        ("T.Accent",        "#E87A3E", "#E87A3E"),
        ("T.AccentHover",   "#F08B4F", "#EF8342"),
        ("T.AccentOn",      "#1A1A1A", "#FFFFFF"),
        ("T.RingTrack",     "#2A2A31", "#E4E4EA"),
        ("T.ToggleTrackOn", "#4A3223", "#F2DFCC"),
        ("T.Good",          "#6FBF73", "#3E9B44"),
        ("T.Mid",           "#D9A13B", "#B57F17"),
        ("T.Low",           "#D96A5B", "#C14E3F"),
        ("T.Unknown",       "#4A4A52", "#B4B4BC"),
    };

    private static System.Windows.Threading.DispatcherTimer? _pollTimer;

    public static void Initialize()
    {
        Apply(ReadSystemIsDark());

        // 注册表变更不一定发 WM_SETTINGCHANGE 广播，双保险：轮询 + 系统事件
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() => Apply(ReadSystemIsDark()));
        };
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _pollTimer.Tick += (_, _) => Apply(ReadSystemIsDark());
        _pollTimer.Start();
    }

    private static bool ReadSystemIsDark()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(PersonalizeKey);
            // AppsUseLightTheme：1=浅色，0=深色；读不到默认深色
            return k?.GetValue("AppsUseLightTheme") is not 1;
        }
        catch { return true; }
    }

    private static void Apply(bool dark)
    {
        if (dark == IsDark && Application.Current.Resources.Contains("T.WindowBg")) return;
        IsDark = dark;

        var res = Application.Current.Resources;
        foreach (var (key, darkHex, lightHex) in Palette)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    dark ? darkHex : lightHex));
            res[key] = brush;
        }
        ThemeChanged?.Invoke();
        EarbudsWatcher.DiagLog($"主题切换：{(dark ? "深色" : "浅色")}（跟随系统）");
    }

    /// <summary>代码侧取主题色（未命中回退深色值）。</summary>
    public static System.Windows.Media.Color GetColor(string key)
    {
        if (Application.Current.TryFindResource(key) is System.Windows.Media.SolidColorBrush b)
            return b.Color;
        var fallback = Palette.First(p => p.Key == key).Dark;
        return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(fallback);
    }

    public static System.Windows.Media.SolidColorBrush GetBrush(string key) =>
        Application.Current.TryFindResource(key) as System.Windows.Media.SolidColorBrush
        ?? new System.Windows.Media.SolidColorBrush(GetColor(key));

    /// <summary>电量分档颜色（跟随主题）。</summary>
    public static System.Windows.Media.Color ColorFor(int v) => v switch
    {
        >= 50 => GetColor("T.Good"),
        >= 20 => GetColor("T.Mid"),
        _ => GetColor("T.Low"),
    };
}
