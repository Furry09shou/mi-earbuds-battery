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

        Window window = e.Args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase))
            ? new UninstallWindow()
            : new InstallerWindow();
        window.Show();
    }
}
