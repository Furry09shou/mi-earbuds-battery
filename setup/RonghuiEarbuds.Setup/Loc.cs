using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace RonghuiEarbuds.Setup;

/// <summary>
/// 安装器/卸载器独立轻量双语表（与主应用 Core\L.cs 解耦，不共享代码）。
/// 默认语言按系统 UI 文化解析（zh→中文，否则英文），用户可在窗口右上角手动切换，切换即时生效；
/// 不持久化——每次打开安装器都按系统语言为默认。
/// 日志输出不翻译。
/// </summary>
internal static class Loc
{
    /// <summary>当前语言（zh / en）。</summary>
    public static string Lang { get; private set; } = "zh";

    /// <summary>语言切换后触发（各窗口订阅并重建文案）。</summary>
    public static event Action? Changed;

    private static readonly Dictionary<string, (string zh, string en)> Table = new()
    {
        // ---- 窗口标题 / 品牌 ----
        ["setup.appName"] = ("绒汇耳机助手", "Ronghui Earbuds"),
        ["setup.installer"] = ("安装程序", "Setup"),
        ["setup.uninstaller"] = ("卸载程序", "Uninstaller"),

        // ---- 安装器：就绪面板 ----
        ["setup.subtitle"] = ("耳机 BLE 电量监控 · 安装到你的电脑",
                              "Earbuds BLE battery monitoring · install on your PC"),
        ["setup.pathLabel"] = ("安装位置", "Install location"),
        ["setup.browse"] = ("浏览…", "Browse…"),
        ["setup.autostart"] = ("开机自启（登录后静默运行）",
                               "Start with Windows (runs silently after sign-in)"),
        ["setup.desktop"] = ("创建桌面快捷方式", "Create desktop shortcut"),
        ["setup.startmenu"] = ("创建开始菜单快捷方式", "Create Start menu shortcut"),
        ["setup.install"] = ("安装", "Install"),
        ["setup.upgrade"] = ("升级", "Upgrade"),
        ["setup.reinstall"] = ("重新安装", "Reinstall"),
        ["setup.resolvedFmt"] = ("实际安装到：{0}", "Will actually install to: {0}"),
        ["setup.existUpgradeFmt"] = ("检测到已安装 v{0}（位于 {1}），将升级到 v{2}，配置与绑定保留",
                                     "Found v{0} installed at {1}; it will be upgraded to v{2}. Settings and binding are kept."),
        ["setup.existReinstallFmt"] = ("检测到已安装 v{0}（位于 {1}），将重新安装（旧文件清理后重装）",
                                       "Found v{0} installed at {1}; it will be reinstalled (old files are cleaned first)."),

        // ---- 安装器：安装中 / 完成 / 错误 ----
        ["setup.busyInstall"] = ("正在安装…", "Installing…"),
        ["setup.doneTitle"] = ("安装完成", "Installed"),
        ["setup.doneSubDefault"] = ("程序已安装到所选位置",
                                    "The app has been installed to the chosen location"),
        ["setup.doneInstalledFmt"] = ("程序已安装到 {0}", "Installed to {0}"),
        ["setup.doneUpgradedFmt"] = ("已升级到 v{0}：{1}", "Upgraded to v{0}: {1}"),
        ["setup.finish"] = ("完成", "Finish"),
        ["setup.runNow"] = ("立即运行", "Run now"),
        ["setup.retry"] = ("重试", "Retry"),

        // ---- 安装器：错误 ----
        ["setup.errInstallFmt"] = ("安装失败：{0}", "Install failed: {0}"),
        ["setup.errInvalidPath"] = ("安装位置无效", "Invalid install location"),
        ["setup.errInvalidPathRetry"] = ("安装位置无效，请重新选择。",
                                         "Invalid install location. Please choose again."),
        ["setup.errEmptyPath"] = ("请选择安装位置。", "Please choose an install location."),
        ["setup.errSystemDir"] = ("不能安装到系统目录，请选择其他位置。",
                                  "Can't install into a Windows system folder. Pick another location."),
        ["setup.errPayloadMissing"] = ("主程序文件缺失（安装包 payload 不完整），请重新获取安装包。",
                                       "Main program files are missing (incomplete installer payload). Please get the installer again."),

        // ---- .NET 运行时检测弹窗 ----
        ["setup.netTitle"] = ("需要 .NET 10 桌面运行时", ".NET 10 desktop runtime required"),
        ["setup.netBody"] = ("未检测到 .NET 10 桌面运行时（绒汇耳机助手运行必需）。\n\n" +
                             "点击「前往下载」打开微软官方下载页，安装运行时后即可正常使用；" +
                             "也可以稍后从开始菜单启动本程序。",
                             "The .NET 10 desktop runtime wasn't found (required to run Ronghui Earbuds).\n\n" +
                             "Click \"Open download page\" to get it from Microsoft; once installed, " +
                             "the app will work. You can also launch the app later from the Start menu."),
        ["setup.netGo"] = ("前往下载", "Open download page"),
        ["setup.netSkip"] = ("跳过", "Skip"),

        // ---- 卸载器：确认面板 ----
        ["setup.confirmTitle"] = ("卸载 绒汇耳机助手？", "Uninstall Ronghui Earbuds?"),
        ["setup.confirmBody"] = ("将移除程序文件、开机自启项与快捷方式。你的耳机绑定信息可一并删除，或保留供下次安装使用。",
                                 "This removes the program files, the startup entry and shortcuts. Your earbud " +
                                 "binding data can be deleted as well, or kept for a future install."),
        ["setup.deleteConfig"] = ("同时删除配置与绑定信息", "Also delete settings and binding data"),
        ["setup.cancel"] = ("取消", "Cancel"),
        ["setup.uninstall"] = ("卸载", "Uninstall"),

        // ---- 卸载器：进行中 / 完成 / 异常 ----
        ["setup.busyUninstall"] = ("正在卸载…", "Uninstalling…"),
        ["setup.doneUninstallTitle"] = ("卸载完成", "Uninstalled"),
        ["setup.doneConfigRemoved"] = ("程序文件与配置已移除", "Program files and settings removed"),
        ["setup.doneFilesRemoved"] = ("程序文件已移除", "Program files removed"),
        ["setup.doneFilesKept"] = ("程序文件已移除（配置已保留）", "Program files removed (settings kept)"),
        ["setup.close"] = ("关闭", "Close"),
        ["setup.warnNotOurs"] = ("未找到与卸载程序匹配的安装信息，已取消操作。\n\n" +
                                 "（为防止误卸载其他软件，卸载前会校验注册表登记与本程序路径一致）",
                                 "No matching installation info was found for this uninstaller; " +
                                 "the operation was cancelled.\n\n" +
                                 "To protect other software, the uninstaller first verifies that its registry " +
                                 "entry matches its own path."),
        ["setup.errUninstallFmt"] = ("卸载失败：{0}", "Uninstall failed: {0}"),

        // ---- 快捷方式描述（写入 .lnk 元数据，资源管理器可见） ----
        ["setup.shortcutDesc"] = ("耳机 BLE 电量监控", "Earbuds BLE battery monitoring"),
    };

    /// <summary>取当前语言文案；缺 key 返回 key 本身（便于发现漏翻）。</summary>
    public static string T(string key) =>
        Table.TryGetValue(key, out var v) ? (Lang == "en" ? v.en : v.zh) : key;

    /// <summary>取当前语言文案并格式化。</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>启动时按系统 UI 文化初始化（zh → 中文，否则英文）。</summary>
    public static void Initialize() =>
        Lang = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
            ? "zh" : "en";

    /// <summary>切换语言（zh/en），立即生效并广播 Changed。</summary>
    public static void SetLanguage(string lang)
    {
        if (lang != "en" && lang != "zh") return;
        if (lang == Lang) return;
        Lang = lang;
        Changed?.Invoke();
    }

    /// <summary>标题栏语言分段按钮选中态：选中=橙色描边亮字，未选=灰描边灰字。</summary>
    public static void SetSegState(Button zhBtn, Button enBtn)
    {
        var (selBrush, unselBrush) =
            (new SolidColorBrush(Color.FromRgb(0xE8, 0x7A, 0x3E)),
             new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x42)));
        var (selFg, unselFg) =
            (new SolidColorBrush(Color.FromRgb(0xED, 0xED, 0xF0)),
             new SolidColorBrush(Color.FromRgb(0x8F, 0x8F, 0x98)));
        ApplySeg(zhBtn, Lang == "zh", selBrush, unselBrush, selFg, unselFg);
        ApplySeg(enBtn, Lang == "en", selBrush, unselBrush, selFg, unselFg);
    }

    private static void ApplySeg(Button b, bool selected, Brush selBorder, Brush unselBorder,
        Brush selFg, Brush unselFg)
    {
        b.BorderBrush = selected ? selBorder : unselBorder;
        b.Foreground = selected ? selFg : unselFg;
        b.FontWeight = selected ? FontWeights.SemiBold : FontWeights.Normal;
    }
}
