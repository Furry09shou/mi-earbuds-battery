using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Windows.Widgets.Providers;

namespace RonghuiEarbuds.Widgets;

/// <summary>
/// 耳机电量小组件提供程序。数据来自主程序写入的
/// %PROGRAMDATA%\RonghuiEarbuds\state.json（本进程不连接蓝牙）。
/// 生命周期：面板可见（Activate）期间 1 秒轮询推送；全部卡片失活后
/// 静置 10 秒退出进程，下次面板打开由 COM 再次拉起。
/// </summary>
[ComVisible(true)]
[ComDefaultInterface(typeof(IWidgetProvider))]
[Guid("7A1B9C42-8F5E-4D3A-B6C2-91E4A0F58D23")]
public sealed class EarbudsWidgetProvider : IWidgetProvider
{
    /// <summary>必须与 AppxManifest 里 Definition Id 一致。</summary>
    public const string DefinitionId = "RonghuiEarbuds_Battery_Widget";

    /// <summary>无小组件可服务时置位，Program.Main 收到后退出进程。</summary>
    public static readonly ManualResetEvent ExitEvent = new(false);

    private const string StateDir = "RonghuiEarbuds";
    private const string StateFile = "state.json";
    /// <summary>卡片失活后的静置宽限（秒），期间面板可能立刻重新 Activate。</summary>
    private const int IdleExitSeconds = 10;

    private static readonly object Gate = new();
    private static readonly Dictionary<string, bool> Widgets = new();   // widgetId → 面板是否正显示
    private static Timer? _pollTimer;
    private static Timer? _idleExitTimer;
    private static string _lastPushedState = "";
    private static bool _recovered;

    private static string StatePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), StateDir, StateFile);

    public EarbudsWidgetProvider()
    {
        // 进程被面板拉起时，已固定的卡片直接补推一次当前状态
        RecoverRunningWidgets();
    }

    private static void RecoverRunningWidgets()
    {
        lock (Gate)
        {
            if (_recovered) return;
            _recovered = true;
            try
            {
                var infos = WidgetManager.GetDefault().GetWidgetInfos();
                if (infos is null) return;
                foreach (var info in infos)
                {
                    var id = info.WidgetContext.Id;
                    if (!Widgets.ContainsKey(id) && info.WidgetContext.DefinitionId == DefinitionId)
                        Widgets[id] = false;
                }
                PushAllLocked();
            }
            catch { /* 面板服务暂不可达时下次 Activate 再推 */ }
        }
    }

    public void CreateWidget(WidgetContext widgetContext)
    {
        lock (Gate)
        {
            Widgets[widgetContext.Id] = true;
            StopIdleExit();
            EnsureTimer();
            PushLocked(widgetContext.Id);
        }
    }

    public void DeleteWidget(string widgetId, string customState)
    {
        lock (Gate)
        {
            Widgets.Remove(widgetId);
            if (Widgets.Count == 0)
            {
                StopTimer();
                ExitEvent.Set();   // 最后一张卡片被取消固定：进程可以退场
            }
        }
    }

    public void OnActionInvoked(WidgetActionInvokedArgs actionInvokedArgs)
    {
        if (actionInvokedArgs.Verb == "refresh")
        {
            lock (Gate)
            {
                _lastPushedState = "";
                PushAllLocked();
            }
        }
    }

    public void OnWidgetContextChanged(WidgetContextChangedArgs contextChangedArgs)
    {
        lock (Gate) PushLocked(contextChangedArgs.WidgetContext.Id);
    }

    public void Activate(WidgetContext widgetContext)
    {
        lock (Gate)
        {
            Widgets[widgetContext.Id] = true;
            StopIdleExit();
            EnsureTimer();
            PushLocked(widgetContext.Id);
        }
    }

    public void Deactivate(string widgetId)
    {
        lock (Gate)
        {
            if (Widgets.ContainsKey(widgetId)) Widgets[widgetId] = false;
            if (Widgets.Values.All(active => !active))
            {
                StopTimer();
                StopIdleExit();
                _idleExitTimer = new Timer(_ =>
                {
                    lock (Gate)
                    {
                        if (Widgets.Count == 0 || Widgets.Values.All(active => !active))
                            ExitEvent.Set();
                    }
                }, null, IdleExitSeconds * 1000, Timeout.Infinite);
            }
        }
    }

    // ---------- 状态轮询与推送 ----------

    private static void EnsureTimer()
    {
        _pollTimer ??= new Timer(_ => PollTick(), null, 200, 1000);
    }

    private static void StopTimer()
    {
        _pollTimer?.Dispose();
        _pollTimer = null;
    }

    private static void StopIdleExit()
    {
        _idleExitTimer?.Dispose();
        _idleExitTimer = null;
    }

    private static void PollTick()
    {
        try
        {
            var path = StatePath;
            if (!File.Exists(path)) return;
            var fi = new FileInfo(path);
            var stamp = $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}";
            if (stamp == _lastPushedState) return;

            lock (Gate)
            {
                if (stamp == _lastPushedState) return;
                PushAllLocked(stamp);
            }
        }
        catch { /* 文件正被主程序替换时可能 IO 冲突，下一秒重试 */ }
    }

    /// <summary>向所有已登记卡片推送当前状态。调用方须持有 Gate。</summary>
    private static void PushAllLocked(string? stamp = null)
    {
        foreach (var id in Widgets.Keys.ToList())
            PushLocked(id);
        if (stamp is not null)
        {
            _lastPushedState = stamp;
        }
        else
        {
            try
            {
                var fi = new FileInfo(StatePath);
                _lastPushedState = fi.Exists ? $"{fi.Length}:{fi.LastWriteTimeUtc.Ticks}" : "";
            }
            catch { _lastPushedState = ""; }
        }
    }

    private static void PushLocked(string widgetId)
    {
        try
        {
            var (template, data) = CardBuilder.Build();
            WidgetManager.GetDefault().UpdateWidget(new WidgetUpdateRequestOptions(widgetId)
            {
                Template = template,
                Data = data,
            });
        }
        catch { /* 卡片刚被删除等瞬态错误：忽略 */ }
    }
}
