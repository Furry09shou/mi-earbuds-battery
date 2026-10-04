using System.Diagnostics;
using System.Reflection;
using Microsoft.Win32;

namespace MiEarbuds.Setup;

/// <summary>
/// 安装/卸载程序（一个 exe 两用）：
///   MiEarbuds.Setup.exe            → 安装向导（自定义路径 / 自启动 / 快捷方式）
///   MiEarbuds.Setup.exe --uninstall → 卸载（只操作注册表中自己的键与自己的文件，防误卸）
/// </summary>
internal static class Program
{
    private const string AppName = "绒汇耳机助手";
    private const string ExeName = "MiEarbuds.exe";
    private const string RunValueName = "MiEarbuds";
    private const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\MiEarbuds";

    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Any(a => a.Equals("--uninstall", StringComparison.OrdinalIgnoreCase)))
            RunUninstaller();
        else
            RunInstaller();
    }

    // ============================ 安装 ============================

    private static void RunInstaller()
    {
        var defaultTarget = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Programs", "MiEarbuds");

        var form = new Form
        {
            Text = $"{AppName} 安装程序",
            FormBorderStyle = FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = FormStartPosition.CenterScreen,
            ClientSize = new Size(500, 236),
            Font = new Font("Microsoft YaHei UI", 9.5F),
        };

        var titleLabel = new Label
        {
            Text = $"{AppName}  v{GetAppVersion()}",
            Font = new Font("Microsoft YaHei UI", 13F, FontStyle.Bold),
            AutoSize = true,
            Location = new Point(20, 16),
        };

        var pathLabel = new Label { Text = "安装位置：", AutoSize = true, Location = new Point(20, 62) };
        var pathBox = new TextBox
        {
            Text = defaultTarget,
            Location = new Point(20, 84),
            Width = 380,
        };
        var browseButton = new Button { Text = "浏览…", Location = new Point(408, 82), Width = 70 };

        var autoStart = new CheckBox { Text = "开机自启（登录后静默运行）", AutoSize = true, Location = new Point(20, 122), Checked = true };
        var desktopLink = new CheckBox { Text = "创建桌面快捷方式", AutoSize = true, Location = new Point(20, 148), Checked = true };
        var startMenuLink = new CheckBox { Text = "创建开始菜单快捷方式", AutoSize = true, Location = new Point(20, 174), Checked = true };

        var installButton = new Button
        {
            Text = "安装",
            Location = new Point(380, 200),
            Size = new Size(98, 30),
            UseVisualStyleBackColor = true,
        };

        browseButton.Click += (_, _) =>
        {
            using var dialog = new FolderBrowserDialog { SelectedPath = pathBox.Text };
            if (dialog.ShowDialog(form) == DialogResult.OK) pathBox.Text = dialog.SelectedPath;
        };

        installButton.Click += (_, _) => DoInstall(form, pathBox.Text.Trim(), autoStart.Checked,
            desktopLink.Checked, startMenuLink.Checked, installButton);

        form.Controls.AddRange(new Control[]
        {
            titleLabel, pathLabel, pathBox, browseButton,
            autoStart, desktopLink, startMenuLink, installButton,
        });

        Application.Run(form);
    }

    private static void DoInstall(Form form, string target, bool autoStart,
        bool desktop, bool startMenu, Button installButton)
    {
        if (target.Length == 0)
        {
            MessageBox.Show(form, "请选择安装位置。", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        installButton.Enabled = false;
        try
        {
            KillRunningApp(target);

            Directory.CreateDirectory(target);
            CopyPayload(target);

            if (autoStart) SetAutoStart(target);
            if (desktop) MakeShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                    $"{AppName}.lnk"), target);
            if (startMenu) MakeShortcut(
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    @"Microsoft\Windows\Start Menu\Programs", $"{AppName}.lnk"), target);

            RegisterUninstallEntry(target, GetAppVersion());

            if (MessageBox.Show(form, "安装完成！是否立即运行？", AppName,
                    MessageBoxButtons.YesNo, MessageBoxIcon.Information) == DialogResult.Yes)
            {
                Process.Start(new ProcessStartInfo(Path.Combine(target, ExeName))
                {
                    WorkingDirectory = target,
                });
            }
            form.Close();
        }
        catch (Exception ex)
        {
            MessageBox.Show(form, $"安装失败：{ex.Message}", AppName,
                MessageBoxButtons.OK, MessageBoxIcon.Error);
            installButton.Enabled = true;
        }
    }

    /// <summary>把本目录 app\（或旁路文件）复制到目标目录，并把自己也复制进去供卸载用。</summary>
    private static void CopyPayload(string target)
    {
        var payload = Path.Combine(AppContext.BaseDirectory, "app");
        if (Directory.Exists(payload))
        {
            foreach (var src in Directory.EnumerateFiles(payload, "*", SearchOption.AllDirectories))
            {
                var dst = Path.Combine(target, Path.GetRelativePath(payload, src));
                Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                File.Copy(src, dst, true);
            }
        }
        else
        {
            foreach (var src in Directory.EnumerateFiles(AppContext.BaseDirectory, "MiEarbuds.*"))
            {
                File.Copy(src, Path.Combine(target, Path.GetFileName(src)), true);
            }
        }

        // 卸载程序本体也放进安装目录
        if (Environment.ProcessPath is { } self)
        {
            File.Copy(self, Path.Combine(target, "MiEarbuds.Setup.exe"), true);
        }
    }

    private static void RegisterUninstallEntry(string target, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", $"{AppName} (Mi Earbuds Battery)");
        key.SetValue("DisplayVersion", version);
        key.SetValue("DisplayIcon", Path.Combine(target, ExeName));
        key.SetValue("UninstallString", $"\"{Path.Combine(target, "MiEarbuds.Setup.exe")}\" --uninstall");
        key.SetValue("Publisher", "Furry09shou");
        key.SetValue("InstallLocation", target);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ============================ 卸载 ============================

    private static void RunUninstaller()
    {
        if (!VerifyOwnInstall())
        {
            MessageBox.Show(
                "未找到与卸载程序匹配的安装信息，已取消操作。\n\n" +
                "（为防止误卸载其他软件，卸载前会校验注册表登记与本程序路径一致）",
                AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        if (MessageBox.Show($"确定卸载 {AppName} 吗？", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            return;
        }

        var target = AppContext.BaseDirectory;

        KillRunningApp(target);

        // 1. 自启动：只有值内容指向我们的 exe 才删
        RemoveAutoStart();

        // 2. 快捷方式：校验目标指向我们的 exe 才删
        RemoveShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));
        RemoveShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs", $"{AppName}.lnk"));

        // 3. 卸载登记
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);

        // 4. 配置文件（询问）
        var configDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "MiEarbuds");
        if (Directory.Exists(configDir) && MessageBox.Show(
                "是否同时删除配置与绑定信息？", AppName,
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
        {
            TryDeleteDir(configDir);
        }

        // 5. 延迟删除安装目录（等本进程退出）
        SpawnSelfDelete(target.TrimEnd(Path.DirectorySeparatorChar));

        MessageBox.Show("卸载完成。", AppName, MessageBoxButtons.OK, MessageBoxIcon.Information);
        Application.Exit();
    }

    /// <summary>校验注册表 UninstallString 与本程序自身路径一致。</summary>
    private static bool VerifyOwnInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var uninstallString = key?.GetValue("UninstallString") as string;
            if (uninstallString is null) return false;

            var registered = uninstallString.Trim().Trim('"');
            var self = Environment.ProcessPath;
            return self is not null &&
                   string.Equals(Path.GetFullPath(registered),
                       Path.GetFullPath(self), StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void RemoveAutoStart()
    {
        try
        {
            using var run = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (run is null) return;
            var value = run.GetValue(RunValueName) as string;
            if (value is not null && value.Contains(ExeName, StringComparison.OrdinalIgnoreCase))
            {
                run.DeleteValue(RunValueName, false);
            }
        }
        catch { /* 没有该项即忽略 */ }
    }

    private static void RemoveShortcut(string lnkPath)
    {
        try
        {
            if (!File.Exists(lnkPath)) return;
            var target = ReadShortcutTarget(lnkPath);
            // 目标读不到（罕见）或确属我们的 exe 才删除
            if (target is null || IsOurExe(target))
            {
                File.Delete(lnkPath);
            }
        }
        catch { /* 删不掉就跳过 */ }
    }

    // ============================ 公共辅助 ============================

    /// <summary>结束正在运行的主程序；仅当进程路径能确认是 MiEarbuds.exe 时才结束。</summary>
    private static void KillRunningApp(string target)
    {
        foreach (var process in Process.GetProcessesByName("MiEarbuds"))
        {
            try
            {
                var path = process.MainModule?.FileName;
                var ours = path is not null && IsOurExe(path);
                // 无法读取路径时，退一步看安装目录里是否有对应 exe
                if (!ours && path is null && File.Exists(Path.Combine(target, ExeName)))
                    ours = true;
                if (ours) process.Kill(entireProcessTree: true);
            }
            catch { /* 未能确认归属的进程绝不动 */ }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static bool IsOurExe(string path) =>
        Path.GetFileName(path.Trim('"')).Equals(ExeName, StringComparison.OrdinalIgnoreCase);

    private static void SetAutoStart(string target)
    {
        using var run = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Run", true)!;
        run.SetValue(RunValueName, $"\"{Path.Combine(target, ExeName)}\" --minimized");
    }

    private static void MakeShortcut(string lnkPath, string target)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(lnkPath)!);
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { lnkPath })!;
            var linkType = link.GetType();
            linkType.InvokeMember("TargetPath", BindingFlags.SetProperty, null, link,
                new object[] { Path.Combine(target, ExeName) });
            linkType.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, link,
                new object[] { target });
            linkType.InvokeMember("Description", BindingFlags.SetProperty, null, link,
                new object[] { $"{AppName} - 耳机 BLE 电量监控" });
            linkType.InvokeMember("Save", BindingFlags.InvokeMethod, null, link, null);
        }
        catch { /* 快捷方式失败不影响安装 */ }
    }

    private static string? ReadShortcutTarget(string lnkPath)
    {
        try
        {
            var shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
            var link = shell.GetType().InvokeMember("CreateShortcut", BindingFlags.InvokeMethod,
                null, shell, new object[] { lnkPath })!;
            return link.GetType().InvokeMember("TargetPath", BindingFlags.GetProperty,
                null, link, null) as string;
        }
        catch
        {
            return null;
        }
    }

    private static void SpawnSelfDelete(string dir)
    {
        try
        {
            Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c ping -n 3 127.0.0.1 > nul & rd /s /q \"{dir}\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
            });
        }
        catch { /* 删除失败时目录会残留，可手动清理 */ }
    }

    private static void TryDeleteDir(string dir)
    {
        try { Directory.Delete(dir, true); }
        catch { /* 文件占用时跳过 */ }
    }

    private static string GetAppVersion()
    {
        // 与主程序同版本号：从旁路的 MiEarbuds.dll 读取；读不到用自身
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory,
                "app", "MiEarbuds.dll");
            if (!File.Exists(beside))
                beside = Path.Combine(AppContext.BaseDirectory, "MiEarbuds.dll");
            if (File.Exists(beside))
            {
                var info = FileVersionInfo.GetVersionInfo(beside);
                if (!string.IsNullOrEmpty(info.ProductVersion)) return info.ProductVersion!;
            }
        }
        catch { /* 回退到自身版本 */ }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }
}
