using Microsoft.Windows.Widgets.Providers;
using RonghuiEarbuds.Widgets;

namespace RonghuiEarbuds.Widgets;

/// <summary>
/// 小组件提供程序入口。小组件面板（Widgets Board）通过 MSIX 里登记的
/// ExeServer COM 激活启动本进程（参数 -RegisterProcessAsComServer），
/// 本进程注册 IWidgetProvider 类工厂后待命，面板可见期间轮询
/// %PROGRAMDATA%\RonghuiEarbuds\state.json 推送卡片更新。
/// </summary>
internal static class Program
{
    [MTAThread]
    private static void Main(string[] args)
    {
        if (args.Length == 0 || args[0] != "-RegisterProcessAsComServer")
            return;   // 用户手动双击：无 UI、无动作，直接退出

        WinRT.ComWrappersSupport.InitializeComWrappers();

        using var manager = RegistrationManager<EarbudsWidgetProvider>.RegisterProvider();

        // 面板关闭后提供程序会请求退出（省电），这里阻塞到任一事件触发
        WaitHandle.WaitAny(
        [
            manager.GetDisposedEvent(),
            EarbudsWidgetProvider.ExitEvent,
        ]);
    }
}
