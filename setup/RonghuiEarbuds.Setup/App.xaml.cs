using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Media.Imaging;

namespace RonghuiEarbuds.Setup;

public partial class App : Application
{
    /// <summary>从内嵌资源加载 logo（安装/卸载窗口徽标共用）。</summary>
    public static BitmapImage? LoadLogo()
    {
        try
        {
            var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("RonghuiEarbuds.Logo.png");
            if (stream is null) return null;
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = stream;
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch
        {
            return null;
        }
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        Loc.Initialize();   // 界面语言：默认按系统 UI 文化（zh→中文，否则英文），窗口内可手动切换

        Window window = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            ? new UninstallWindow()
            : new InstallerWindow();
        window.Show();
    }
}
