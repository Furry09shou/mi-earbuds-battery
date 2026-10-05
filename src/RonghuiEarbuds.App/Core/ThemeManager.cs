using System.Windows;
using System.Windows.Media;

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

    /// <summary>可选强调色（设置页六色点）。默认橙，与品牌视觉一致。</summary>
    public static readonly string[] AccentPresets =
    {
        "#E87A3E",   // 橙
        "#4C8DD6",   // 蓝
        "#8E7CC3",   // 紫
        "#45A29E",   // 青
        "#D9738F",   // 粉
        "#6E8FB5",   // 蓝灰
    };

    private static string _accentHex = AccentPresets[0];

    /// <summary>当前强调色（代码取色用，主题切换后与资源一致）。</summary>
    public static Color AccentColor { get; private set; }

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
    private static string _mode = "system";   // system / dark / light

    public static void Initialize(AppConfig config)
    {
        _mode = config.ThemeMode switch
        {
            "dark" => "dark",
            "light" => "light",
            _ => "system",
        };
        _accentHex = AccentPresets[Math.Clamp(config.AccentIndex, 0, AccentPresets.Length - 1)];
        Apply(Resolve());

        // 注册表变更不一定发 WM_SETTINGCHANGE 广播，双保险：轮询 + 系统事件；
        // 手动指定深/浅时不跟随系统
        Microsoft.Win32.SystemEvents.UserPreferenceChanged += (_, args) =>
        {
            if (args.Category == Microsoft.Win32.UserPreferenceCategory.General)
                Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (_mode == "system") Apply(ReadSystemIsDark());
                });
        };
        _pollTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(10),
        };
        _pollTimer.Tick += (_, _) => { if (_mode == "system") Apply(ReadSystemIsDark()); };
        _pollTimer.Start();
    }

    /// <summary>切换外观模式：system=跟随系统 / dark / light。</summary>
    public static void SetMode(string mode)
    {
        _mode = mode switch { "dark" => "dark", "light" => "light", _ => "system" };
        Apply(Resolve());
    }

    /// <summary>切换强调色（设置页色点）；即时重刷所有 T.Accent 系画刷。</summary>
    public static void SetAccent(int index)
    {
        if (index < 0 || index >= AccentPresets.Length) return;
        _accentHex = AccentPresets[index];
        Apply(IsDark);
    }

    private static bool Resolve() => _mode switch
    {
        "dark" => true,
        "light" => false,
        _ => ReadSystemIsDark(),
    };

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
        if (dark == IsDark && Application.Current.Resources.Contains("T.WindowBg") &&
            AccentColor == (Color)ColorConverter.ConvertFromString(_accentHex))
            return;
        IsDark = dark;
        AccentColor = (Color)ColorConverter.ConvertFromString(_accentHex);

        var res = Application.Current.Resources;
        foreach (var (key, darkHex, lightHex) in Palette)
        {
            var brush = new System.Windows.Media.SolidColorBrush(
                (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(
                    dark ? darkHex : lightHex));
            res[key] = brush;
        }
        // 强调色系覆盖：主色 + 悬停提亮 + 开关选中轨；深浅模式共用（选色均适配两种背景）
        var hover = Lighten(AccentColor, 0.12);
        res["T.Accent"] = new System.Windows.Media.SolidColorBrush(AccentColor);
        res["T.AccentHover"] = new System.Windows.Media.SolidColorBrush(hover);
        res["T.ToggleTrackOn"] = new System.Windows.Media.SolidColorBrush(Blend(AccentColor, dark, 0.32));
        ThemeChanged?.Invoke();
        EarbudsWatcher.DiagLog($"主题切换：{(dark ? "深色" : "浅色")}，强调色 {_accentHex}（跟随系统）");
    }

    private static Color Lighten(Color c, double f) => Color.FromRgb(
        (byte)Math.Min(255, c.R + (255 - c.R) * f),
        (byte)Math.Min(255, c.G + (255 - c.G) * f),
        (byte)Math.Min(255, c.B + (255 - c.B) * f));

    /// <summary>向背景色混合：深色底提亮、浅色底压暗，保证开关选中轨可辨识。</summary>
    private static Color Blend(Color accent, bool dark, double f)
    {
        byte bg = (byte)(dark ? 0x1A : 0xFF);
        return Color.FromRgb(
            (byte)(accent.R * (1 - f) + bg * f),
            (byte)(accent.G * (1 - f) + bg * f),
            (byte)(accent.B * (1 - f) + bg * f));
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
