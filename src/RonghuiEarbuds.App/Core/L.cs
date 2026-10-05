using System.Globalization;

namespace RonghuiEarbuds.App.Core;

/// <summary>
/// 应用内中英双语：Lang 为解析后的当前语言（zh/en），
/// SetLanguage 的入参为配置模式（system/zh/en），"system" 按系统 UI 文化解析。
/// 切换语言后广播 Changed，各界面订阅后立即刷新（无需重启）。
/// T 缺 key 时返回 key 本身，便于在界面上直接发现漏翻。
/// </summary>
public static class L
{
    /// <summary>当前语言（解析后的两字母码：zh / en）。</summary>
    public static string Lang { get; private set; } = "zh";

    /// <summary>配置里的语言模式（system / zh / en）。</summary>
    public static string Mode { get; private set; } = "system";

    /// <summary>语言切换后触发（在调用 SetLanguage 的线程上同步触发）。</summary>
    public static event Action? Changed;

    // key 约定：main.* 主面板 / settings.* 设置页 / theme.* 外观分段 / lang.* 语言卡片
    // adapter.* 适配名单与向导 / wizard.* 向导步骤 / state.* 连接状态行
    // switch.* 设备切换弹层 / tray.* 托盘 / mini.* 悬浮条 / battery.* 电量提醒与统计
    // update.* 更新提醒 / parser.* 解析器兜底名
    private static readonly Dictionary<string, (string zh, string en)> Table = new()
    {
        // ================= 主面板 =================
        ["main.title"] = ("绒汇耳机助手", "Ronghui Earbuds Assistant"),
        ["main.pin"] = ("窗口置顶", "Pin on top"),
        ["main.unpin"] = ("取消置顶", "Unpin"),
        ["main.searching"] = ("正在搜索…", "Searching…"),
        ["main.identifying"] = ("正在识别…", "Identifying…"),
        ["main.switchDeviceTip"] = ("切换显示的耳机", "Switch displayed earbuds"),
        ["main.left"] = ("左耳", "Left"),
        ["main.right"] = ("右耳", "Right"),
        ["main.caseTitle"] = ("充电仓", "Case"),
        ["main.caseHint"] = ("开盖后可见", "Visible after opening the lid"),
        ["main.usedToday"] = ("今日已用", "Used today"),
        ["main.estimateFmt"] = ("预计可用 {0}", "Est. {0} left"),
        ["main.settings"] = ("设置", "Settings"),
        ["main.checkUpdate"] = ("检查更新", "Check updates"),
        ["main.checking"] = ("检查中…", "Checking…"),
        ["main.upToDate"] = ("已是最新", "Up to date"),
        ["main.checkFailed"] = ("检查失败", "Check failed"),
        ["main.newVersionFmt"] = ("新版本 v{0} ↑", "New v{0} ↑"),
        ["main.adapterList"] = ("适配名单", "Compatibility"),
        ["main.rebind"] = ("重新绑定", "Rebind"),
        ["main.unadaptedHint"] = ("该机型不支持分耳电量 · 仅显示整机电量",
                                  "This model doesn't report per-ear battery · total only"),
        ["main.unadaptedTooltip"] = (
            "该机型的蓝牙广播不含左右耳电量数据（实测仅发送序列号帧），无法解析分耳电量；" +
            "系统整机电量来自蓝牙 HFP/AVRCP 上报，与系统蓝牙设置页同源，是当前机型能获得的全部电量信息。" +
            "可点「适配名单」提交适配申请，等待协议支持。",
            "This model's Bluetooth advertisements contain no per-ear battery data (measurements show " +
            "only serial-number frames), so per-ear battery can't be parsed. The system-level total battery " +
            "comes from Bluetooth HFP/AVRCP reporting — the same source as the Windows Bluetooth settings " +
            "page, and all the info this model can provide. Open the compatibility list to submit a support " +
            "request and wait for protocol support."),

        // ================= 连接状态行 =================
        ["state.openLid"] = ("请打开充电仓盖", "Open the charging case lid"),
        ["state.connectedUnadapted"] = ("已连接 · 该机型不支持分耳电量",
                                        "Connected · per-ear battery unsupported"),
        ["state.disconnected"] = ("未连接", "Disconnected"),
        ["state.liveFmt"] = ("实时更新 · 信号 {0} dBm", "Live · signal {0} dBm"),
        ["state.waitingBroadcast"] = ("蓝牙保持连接 · 等待新广播",
                                      "Bluetooth connected · waiting for broadcasts"),
        ["state.signalLost"] = ("信号丢失 · 请打开仓盖刷新电量",
                                "Signal lost · open the case lid to refresh"),
        ["state.signalLostShort"] = ("信号丢失 · 打开仓盖刷新", "Signal lost · open the case to refresh"),
        ["state.charging"] = ("充电中", "Charging"),
        ["state.inUse"] = ("使用中", "In use"),
        ["state.online"] = ("在线", "Online"),
        ["state.offline"] = ("离线", "Offline"),
        ["state.waitingCase"] = ("等待广播", "Waiting for broadcasts"),
        ["state.sysBatteryFmt"] = ("系统电量 {0}% · 放入充电仓重新开盖可刷新",
                                   "System battery {0}% · reopen the case lid to refresh"),
        ["state.sysBatteryUnadaptedFmt"] = ("系统电量 {0}%（该机型仅支持整机电量）",
                                            "System battery {0}% (this model reports total battery only)"),

        // ================= 设备切换弹层 =================
        ["switch.connectedNoSplit"] = ("已连接 · 不支持分耳电量", "Connected · no per-ear battery"),
        ["switch.disconnectedNoSplit"] = ("未连接 · 不支持分耳电量", "Disconnected · no per-ear battery"),
        ["switch.summaryFmt"] = ("左 {0} · 右 {1} · 仓 {2}", "L {0} · R {1} · Case {2}"),
        ["switch.current"] = ("当前", "Current"),
        ["switch.unsupported"] = ("不支持", "Unsupported"),

        // ================= 设置页 =================
        ["settings.sectionAlerts"] = ("提醒", "Alerts"),
        ["settings.lowThresholdTitle"] = ("低电量提醒阈值", "Alert threshold"),
        ["settings.lowThresholdSub"] = ("电量低于阈值时弹托盘提醒",
                                        "Tray alert when battery drops below the threshold"),
        ["settings.quietTitle"] = ("提醒勿扰时段", "Quiet hours"),
        ["settings.quietSub"] = ("时段内不弹低电量与骤降提醒",
                                 "No low-battery or drop alerts during this period"),
        ["settings.dropTitle"] = ("电量骤降提醒", "Sudden drop alert"),
        ["settings.dropSub"] = ("10 分钟内下降超过 20% 时提醒（每小时最多一次）",
                                "Alert when battery drops over 20% within 10 minutes (at most once per hour)"),
        ["settings.sectionPopup"] = ("弹窗", "Pop-up"),
        ["settings.popupTitle"] = ("开盖自动弹出主窗口", "Auto show on lid open"),
        ["settings.popupSub"] = ("耳机开盖广播到达时拉起主面板到最上层",
                                 "Bring the main panel to the front when the case opens"),
        ["settings.cooldownTitle"] = ("弹窗冷却时间", "Pop-up cooldown"),
        ["settings.cooldownSub"] = ("两次弹窗的最小间隔，避免反复打扰",
                                    "Minimum interval between pop-ups to avoid interruptions"),
        ["settings.minutesFmt"] = ("{0} 分钟", "{0} min"),
        ["settings.sectionMini"] = ("悬浮条", "Mini bar"),
        ["settings.miniTitle"] = ("显示悬浮电量条", "Show mini battery bar"),
        ["settings.miniSub"] = ("可拖动的极简置顶小条；拖动后位置自动记住，双击打开主面板",
                                "A draggable always-on-top mini bar; its position is remembered, double-click opens the main panel"),
        ["settings.sectionGeneral"] = ("通用", "General"),
        ["settings.autostartTitle"] = ("开机自启", "Start with Windows"),
        ["settings.autostartSub"] = ("登录 Windows 后自动启动并收进托盘",
                                     "Start automatically after Windows sign-in, minimized to tray"),
        ["settings.themeTitle"] = ("外观", "Appearance"),
        ["settings.themeSub"] = ("深浅主题可手动切换，默认跟随 Windows 系统设置",
                                 "Light/dark theme; defaults to the Windows system setting"),
        ["settings.historyTitle"] = ("电量记录", "Battery history"),
        ["settings.historySub"] = ("关闭后停止记录电量历史，已有数据保留",
                                   "When off, stop recording battery history; existing data is kept"),
        ["settings.openFolder"] = ("打开文件夹", "Open folder"),
        ["settings.historyNote"] = (
            "电量历史仅保存在本机 %APPDATA%\\RonghuiEarbuds\\history（保留 7 天），" +
            "用于「今日已用」「预计可用」与当日曲线，不上传任何数据。",
            "Battery history is stored only on this PC under %APPDATA%\\RonghuiEarbuds\\history " +
            "(kept for 7 days), used for “Used today”, “Est. remaining” and the daily curve. " +
            "Nothing is ever uploaded."),
        ["settings.back"] = ("‹ 返回", "‹ Back"),

        // ================= 外观 / 语言分段按钮 =================
        ["theme.system"] = ("跟随系统", "System"),
        ["theme.dark"] = ("深色", "Dark"),
        ["theme.light"] = ("浅色", "Light"),
        ["lang.title"] = ("语言", "Language"),
        ["lang.sub"] = ("切换界面显示语言，立即生效", "Switch the UI language, takes effect immediately"),
        ["lang.zh"] = ("中文", "中文"),
        ["lang.en"] = ("English", "English"),

        // ================= 适配名单 =================
        ["adapter.listTitle"] = ("适配名单", "Compatibility list"),
        ["adapter.wizardTitle"] = ("适配新耳机", "Add new earbuds"),
        ["adapter.listIntro"] = ("绿色机型已完成协议适配，红色机型因设备原因无法适配。",
                                 "Green models are fully supported; red models can't be supported due to the device itself."),
        ["adapter.supported"] = ("已支持", "Supported"),
        ["adapter.unsupportedBadge"] = ("不支持分耳", "No per-ear data"),
        ["adapter.supportedNote"] = ("厂商 BLE 广播私有协议，可实时解析左右耳与充电仓电量",
                                     "Vendor BLE advertising protocol parsed in real time for left/right/case battery"),
        ["adapter.listHint"] = ("没找到你的耳机？点右下角「适配新耳机」，按向导采集广播数据即可众包适配。",
                                "Can't find your earbuds? Click “Add new earbuds” at the bottom right and follow the wizard to crowdsource support."),
        ["adapter.unsupportedNote"] = (
            "两次采集共 244 包实测：蓝牙广播仅含序列号帧（ASCII SN）与自身 MAC 帧，20 分钟零变化，" +
            "不含电量数据。电量仅经经典蓝牙 AVRCP 上报整机电量，软件以绿色小字显示。" +
            "分析结论见 GitHub Issue #1。",
            "Verified across two captures totaling 244 packets: the Bluetooth advertisement only contains " +
            "serial-number frames (ASCII SN) and its own MAC frames, with zero changes over 20 minutes and " +
            "no battery data. Battery is reported only as a total via classic Bluetooth AVRCP, which the app " +
            "shows in small green text. See GitHub Issue #1 for the analysis."),

        // ================= 适配向导 =================
        ["wizard.modelTitle"] = ("先写下耳机型号", "First, note your earbuds model"),
        ["wizard.modelBody"] = ("你的耳机型号暂不受支持？按向导做几个简单动作，软件会记录耳机的广播数据。" +
                                "数据不含个人隐私，导出后上传到 GitHub 供开发者分析适配。",
                                "Your model isn't supported yet? Follow the wizard through a few simple moves " +
                                "and the app will record the earbuds' advertising data. It contains no personal " +
                                "information; export it and upload to GitHub for developers to analyze."),
        ["wizard.modelLabel"] = ("耳机型号名称", "Earbuds model name"),
        ["wizard.modelPlaceholder"] = ("例如：Redmi Buds 5", "e.g. Redmi Buds 5"),
        ["wizard.modelHint"] = ("请先填写耳机型号", "Please enter the earbuds model first"),
        ["wizard.layoutLabel"] = ("耳机形态", "Earbuds form factor"),
        ["wizard.layoutDualCase"] = ("双耳 + 充电仓（常见真无线）", "Two buds + charging case (common TWS)"),
        ["wizard.layoutDualNoCase"] = ("仅双耳，无充电仓或仓不广播电量",
                                       "Two buds only, no case or case doesn't broadcast battery"),
        ["wizard.layoutMono"] = ("仅单耳耳机，无充电仓", "Single earbud only, no charging case"),
        ["wizard.layoutNote"] = ("形态决定采集动作与电量字段数量（单耳没有左右之分；无仓形态不会出现充电仓电量字段）。" +
                                 "选择会随数据一起提交，帮助开发者正确识别布局。",
                                 "The form factor determines the capture moves and the number of battery fields " +
                                 "(a single bud has no left/right split; without a case there will be no case " +
                                 "battery field). Your choice is submitted with the data to help developers " +
                                 "identify the layout correctly."),
        ["wizard.stepFmt"] = ("步骤 {0} / 5", "Step {0} / 5"),
        ["wizard.waitingCapture"] = ("等待开始采集…", "Waiting to start capture…"),
        ["wizard.capturedFmt"] = ("已捕获 {0} 包 · 公司代号: {1} · 产品标识: {2}",
                                  "Captured {0} packets · company IDs: {1} · product keys: {2}"),
        ["wizard.startAdapter"] = ("适配新耳机 ›", "Add new earbuds ›"),
        ["wizard.beginCapture"] = ("开始采集", "Start capture"),
        ["wizard.finishUpload"] = ("完成并上传", "Finish & upload"),
        ["wizard.nextFmt"] = ("下一步（{0}/5）", "Next ({0}/5)"),
        ["wizard.cancel"] = ("取消", "Cancel"),
        ["wizard.cancelConfirm"] = ("采集进行中，确定取消并丢弃已采集的数据吗？",
                                    "A capture is in progress. Cancel and discard the data collected so far?"),
        ["wizard.unknownModel"] = ("未知型号", "Unknown model"),
        ["wizard.doneTitle"] = ("导出成功", "Export succeeded"),
        ["wizard.doneMsgFmt"] = ("采集完成，共 {0} 包。\n\n" +
                                 "本地分析已生成候选布局报告（{1}），已随 Issue 预填，通常无需人工逐包分析。\n\n" +
                                 "浏览器已打开 GitHub Issue 页面，请把该 zip 文件拖进评论框提交。" +
                                 "开发者复核后登记解析档案，随软件更新加入你的机型支持。",
                                 "Capture finished, {0} packets in total.\n\n" +
                                 "Local analysis generated a candidate layout report ({1}), pre-filled with the " +
                                 "Issue — manual packet-by-packet analysis is usually unnecessary.\n\n" +
                                 "The GitHub Issue page has opened in your browser; drag the zip file into the " +
                                 "comment box to submit. Once developers review it and register the parsing " +
                                 "profile, your model will be supported in an app update."),
        ["wizard.failTitle"] = ("错误", "Error"),
        ["wizard.failFmt"] = ("导出失败：{0}", "Export failed: {0}"),

        // ---- 向导步骤：双耳 + 充电仓 ----
        ["wizard.dual1T"] = ("双耳入仓，开盖等 10 秒", "Both buds in case, lid open, wait 10s"),
        ["wizard.dual1D"] = ("把两只耳机都放回充电仓，保持仓盖打开，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。",
                             "Put both earbuds back into the charging case, keep the lid open, and wait about " +
                             "10 seconds — this puts the earbuds in a uniform initial state with the most complete advertising."),
        ["wizard.dual2T"] = ("取出左耳，等 10 秒", "Take out the left bud, wait 10s"),
        ["wizard.dual2D"] = ("把左耳从仓中取出（戴或不戴都可以），右耳留在仓内，等待约 10 秒。",
                             "Take the left bud out of the case (wearing it is fine), keep the right bud inside, and wait about 10 seconds."),
        ["wizard.dual3T"] = ("左耳放回，等 10 秒", "Put the left bud back, wait 10s"),
        ["wizard.dual3D"] = ("把左耳放回仓内，等待约 10 秒。",
                             "Put the left bud back into the case and wait about 10 seconds."),
        ["wizard.dual4T"] = ("取出右耳，等 10 秒", "Take out the right bud, wait 10s"),
        ["wizard.dual4D"] = ("把右耳从仓中取出，左耳留在仓内，等待约 10 秒。",
                             "Take the right bud out of the case, keep the left bud inside, and wait about 10 seconds."),
        ["wizard.dual5T"] = ("右耳放回，完成采集", "Put the right bud back to finish"),
        ["wizard.dual5D"] = ("把右耳放回仓内，等待约 10 秒，然后点击「完成并上传」。",
                             "Put the right bud back into the case, wait about 10 seconds, then click “Finish & upload”."),

        // ---- 向导步骤：仅双耳（无仓或仓不广播） ----
        ["wizard.dnc1T"] = ("双耳就位，等 10 秒", "Both buds ready, wait 10s"),
        ["wizard.dnc1D"] = ("把两只耳机打开电源，或从充电仓取出（如果耳机有仓），放在电脑旁边，等待约 10 秒——" +
                            "让耳机处于统一的初始状态，广播最完整。这类耳机的充电仓不会提供电量数据（或没有充电仓），全程只需关注左右两只耳机。",
                            "Power both earbuds on, or take them out of the charging case (if they have one), " +
                            "place them next to the PC, and wait about 10 seconds — a uniform initial state " +
                            "gives the most complete advertising. This kind of case provides no battery data " +
                            "(or there is no case); only the two buds matter throughout."),
        ["wizard.dnc2T"] = ("隔离左耳，等 10 秒", "Isolate the left bud, wait 10s"),
        ["wizard.dnc2D"] = ("把左耳关机，或放回充电仓并合上仓盖（右耳保持在外），等待约 10 秒。",
                            "Power the left bud off, or put it back into the case and close the lid (right bud stays out), and wait about 10 seconds."),
        ["wizard.dnc3T"] = ("左耳归队，等 10 秒", "Reunite the left bud, wait 10s"),
        ["wizard.dnc3D"] = ("把左耳重新开机，或从仓中取出，恢复双耳在外，等待约 10 秒。",
                            "Power the left bud back on, or take it out of the case so both buds are out again, and wait about 10 seconds."),
        ["wizard.dnc4T"] = ("隔离右耳，等 10 秒", "Isolate the right bud, wait 10s"),
        ["wizard.dnc4D"] = ("把右耳关机，或放回充电仓并合上仓盖（左耳保持在外），等待约 10 秒。",
                            "Power the right bud off, or put it back into the case and close the lid (left bud stays out), and wait about 10 seconds."),
        ["wizard.dnc5T"] = ("右耳归队，完成采集", "Reunite the right bud to finish"),
        ["wizard.dnc5D"] = ("把右耳重新开机，或从仓中取出，双耳在外等待约 10 秒，然后点击「完成并上传」。" +
                            "数据应只有左右耳两个电量字段，没有充电仓电量。",
                            "Power the right bud back on, or take it out of the case; with both buds out wait " +
                            "about 10 seconds, then click “Finish & upload”. The data should contain only the " +
                            "left/right battery fields, without a case battery."),

        // ---- 向导步骤：仅单耳 ----
        ["wizard.mono1T"] = ("开机，靠近电脑等 10 秒", "Power on near the PC, wait 10s"),
        ["wizard.mono1D"] = ("打开耳机电源，放在电脑旁边，等待约 10 秒——让耳机处于统一的初始状态，广播最完整。" +
                             "这类耳机没有充电仓，全程只需关注这一只耳机的数据。",
                             "Power the earbud on, place it next to the PC, and wait about 10 seconds — a " +
                             "uniform initial state gives the most complete advertising. There is no charging " +
                             "case; only this one earbud's data matters throughout."),
        ["wizard.mono2T"] = ("戴上使用，等 10 秒", "Wear and use, wait 10s"),
        ["wizard.mono2D"] = ("戴上耳机正常使用（播放或暂停都可以），等待约 10 秒。",
                             "Wear the earbud and use it normally (playing or paused both fine), and wait about 10 seconds."),
        ["wizard.mono3T"] = ("摘下静置，等 10 秒", "Take off and rest, wait 10s"),
        ["wizard.mono3D"] = ("把耳机摘下来放在桌上（保持开机），等待约 10 秒。",
                             "Take the earbud off and place it on the desk (keep it powered on), and wait about 10 seconds."),
        ["wizard.mono4T"] = ("再戴上，等 10 秒", "Wear again, wait 10s"),
        ["wizard.mono4D"] = ("再次戴上耳机使用，等待约 10 秒。",
                             "Wear the earbud and use it again, and wait about 10 seconds."),
        ["wizard.mono5T"] = ("重启耳机，完成采集", "Restart the earbud to finish"),
        ["wizard.mono5D"] = ("把耳机关机，等约 5 秒后重新开机，再等待约 10 秒，然后点击「完成并上传」。",
                             "Power the earbud off, wait about 5 seconds, power it back on, wait about 10 more seconds, then click “Finish & upload”."),

        // ================= 托盘 =================
        ["tray.showPanel"] = ("显示主面板", "Show main panel"),
        ["tray.miniBar"] = ("显示悬浮电量条", "Show mini battery bar"),
        ["tray.exit"] = ("退出", "Exit"),
        ["tray.boundFmt"] = ("已绑定 {0}", "Bound to {0}"),
        ["tray.boundMsg"] = ("打开充电仓盖即可查看电量", "Open the case lid to see the battery"),
        ["tray.tipFmt"] = ("{0}\n左耳 {1}%   右耳 {2}%\n充电仓 {3}%",
                           "{0}\nLeft {1}%   Right {2}%\nCase {3}%"),

        // ================= 悬浮条 / 解析器 =================
        ["mini.title"] = ("绒汇电量条", "Ronghui Battery Bar"),
        ["mini.caseLabel"] = ("仓", "Case"),
        ["mini.earbuds"] = ("耳机", "Earbuds"),
        ["parser.unknown"] = ("未知耳机", "Unknown earbuds"),

        // ================= 电量提醒与统计 =================
        ["battery.charging"] = ("充电中", "Charging"),
        ["battery.hoursMinFmt"] = ("约 {0} 小时 {1} 分", "~{0} h {1} min"),
        ["battery.minutesFmt"] = ("约 {0} 分钟", "~{0} min"),
        ["battery.lessThan10"] = ("不足 10 分钟", "Under 10 min"),
        ["battery.dropTitle"] = ("耳机电量骤降", "Sudden battery drop"),
        ["battery.dropMsgFmt"] = ("约 {0} 分钟内下降 {1}%（当前 {2}%），可能异常耗电或触点误报",
                                  "{1}% dropped in about {0} minutes (now {2}%) — possible abnormal drain or a contact misread"),
        ["battery.lowTitleFmt"] = ("耳机电量不足 {0}%", "Earbuds battery below {0}%"),
        ["battery.lowMsg"] = ("建议把耳机放回充电仓", "Put the earbuds back into the case to charge"),

        // ================= 更新提醒 =================
        ["update.availableFmt"] = ("新版本 v{0} 可用", "New version v{0} available"),
        ["update.clickMsg"] = ("点击此气泡打开下载页，或在主面板点“检查更新”",
                               "Click this balloon to open the download page, or click “Check updates” in the main panel"),
    };

    /// <summary>取当前语言的文案；缺 key 返回 key 本身（便于发现漏翻）。</summary>
    public static string T(string key) =>
        Table.TryGetValue(key, out var v) ? (Lang == "en" ? v.en : v.zh) : key;

    /// <summary>取当前语言文案并格式化。</summary>
    public static string F(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>启动时按配置初始化（不触发 Changed，此时还没有订阅者）。</summary>
    public static void Initialize(string mode)
    {
        Mode = mode;
        Lang = Resolve(mode);
    }

    /// <summary>切换语言模式（system/zh/en），立即生效并广播 Changed。</summary>
    public static void SetLanguage(string mode)
    {
        Mode = mode;
        var lang = Resolve(mode);
        if (lang == Lang)
        {
            // 模式可能从 zh 改为 system(解析=zh)，界面无需刷新，但保持 Mode 已更新
            return;
        }
        Lang = lang;
        Changed?.Invoke();
    }

    private static string Resolve(string mode) => mode switch
    {
        "zh" => "zh",
        "en" => "en",
        _ => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase)
                ? "zh" : "en",
    };
}
