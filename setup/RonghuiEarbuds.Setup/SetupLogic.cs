using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using Microsoft.Win32;

namespace RonghuiEarbuds.Setup;

/// <summary>
/// 安装/卸载核心逻辑（与 UI 解耦）：
///   安装 = 复制 payload → 自启动/快捷方式 → 注册表登记卸载入口；
///   卸载 = 只操作注册表中自己的键与自己的文件，防误卸。
/// 安装目录一定是本软件的专用文件夹（ResolveTarget 保证），卸载整目录删除不会误伤用户文件。
/// </summary>
internal static class SetupLogic
{
    public const string AppName = "绒汇耳机助手";
    public const string Publisher = "绒汇公共交通组织";
    public const string ExeName = "RonghuiEarbuds.exe";
    public const string RunValueName = "RonghuiEarbuds";

    /// <summary>
    /// 检测是否已安装 .NET 8+ 桌面运行时（主程序为框架依赖发布，缺它无法启动）。
    /// 探测异常时返回 true（用户可能装在自定义路径，不误拦）。
    /// </summary>
    public static bool IsDotNetDesktopRuntimeInstalled()
    {
        try
        {
            var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "dotnet", "shared", "Microsoft.WindowsDesktop.App");
            if (!Directory.Exists(dir)) return false;
            return Directory.GetDirectories(dir).Any(d =>
                Version.TryParse(Path.GetFileName(d).Split('-')[0], out var v) && v.Major >= 8);
        }
        catch { return true; }
    }
    public const string UninstallKeyPath =
        @"Software\Microsoft\Windows\CurrentVersion\Uninstall\RonghuiEarbuds";

    /// <summary>已安装信息（注册表登记 + 目录仍存在才视为有效）。</summary>
    public sealed record ExistingInstall(string Path, string Version);

    public static string DefaultTarget => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Programs", "RonghuiEarbuds");

    /// <summary>检测本机是否已安装（读注册表卸载登记；目录不存在视为残留，返回 null 并顺手清掉死登记）。</summary>
    public static ExistingInstall? DetectExistingInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            var loc = key?.GetValue("InstallLocation") as string;
            var ver = key?.GetValue("DisplayVersion") as string;
            if (string.IsNullOrWhiteSpace(loc))
                return null;
            if (!Directory.Exists(loc))
            {
                // 残留登记（文件已删）：清掉，避免"卸载不了"的死入口
                Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);
                return null;
            }
            return new ExistingInstall(
                loc.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                NormalizeVersion(ver));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>去掉版本号尾巴（如 "1.1.0+b903dbc" → "1.1.0"）。</summary>
    public static string NormalizeVersion(string? version)
    {
        if (string.IsNullOrWhiteSpace(version)) return "0.0";
        var v = version.Split('+')[0].Trim();
        return Version.TryParse(v, out var parsed) ? parsed.ToString() : v;
    }

    /// <summary>比较 a 与 b：>0 表示 a 更新，0 相同，<0 更旧。解析失败按 0 处理。</summary>
    public static int CompareVersions(string a, string b)
    {
        var va = Version.TryParse(a, out var pa) ? pa : new Version(0, 0);
        var vb = Version.TryParse(b, out var pb) ? pb : new Version(0, 0);
        return va.CompareTo(vb);
    }

    /// <summary>
    /// 清理旧安装：杀进程、移除自启动/快捷方式/卸载登记。
    /// 旧目录与新目标不同时，连旧程序目录一起删除（避免一台电脑重复安装）。
    /// 配置目录（%APPDATA%\RonghuiEarbuds）始终保留，绑定信息不丢。
    /// </summary>
    public static void RemovePreviousInstall(string oldPath, string newTarget)
    {
        KillRunningApp(oldPath);
        RemoveAutoStart();
        RemoveShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), $"{AppName}.lnk"));
        RemoveShortcut(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            @"Microsoft\Windows\Start Menu\Programs", $"{AppName}.lnk"));
        Registry.CurrentUser.DeleteSubKeyTree(UninstallKeyPath, false);

        var newFull = Path.GetFullPath(newTarget).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(oldPath, newFull, StringComparison.OrdinalIgnoreCase))
            TryDeleteDir(oldPath);
    }

    /// <summary>
    /// 解析最终安装目录：所选目录不存在/为空 → 原样使用；已是本软件安装目录（覆盖安装）→ 原样使用；
    /// 所选目录里已有其他内容（如直接选 D:\ 或某个常用文件夹）→ 自动追加 RonghuiEarbuds 子文件夹。
    /// </summary>
    public static string ResolveTarget(string selected)
    {
        if (selected.Length == 0) return selected;
        var full = Path.GetFullPath(selected).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

        // 已是本软件的安装目录（覆盖安装/升级）
        if (File.Exists(Path.Combine(full, ExeName)) ||
            File.Exists(Path.Combine(full, "RonghuiEarbuds.Setup.exe")))
            return full;

        // 目录不存在或为空：视为用户准备好的专用文件夹
        if (!Directory.Exists(full) || !Directory.EnumerateFileSystemEntries(full).Any())
            return full;

        // 目录里已有其他内容：必须装进专用子文件夹
        return Path.Combine(full, "RonghuiEarbuds");
    }

    /// <summary>校验目标目录，返回错误提示；null 表示可用。</summary>
    public static string? ValidateTarget(string full)
    {
        if (full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.Windows), StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(Environment.GetFolderPath(Environment.SpecialFolder.System), StringComparison.OrdinalIgnoreCase))
            return Loc.T("setup.errSystemDir");
        return null;
    }

    public static string GetAppVersion()
    {
        // 与主程序同版本号：从旁路的 RonghuiEarbuds.dll 读取；读不到用自身
        try
        {
            var beside = Path.Combine(AppContext.BaseDirectory, "app", "RonghuiEarbuds.dll");
            if (!File.Exists(beside))
                beside = Path.Combine(AppContext.BaseDirectory, "RonghuiEarbuds.dll");
            if (File.Exists(beside))
            {
                var info = FileVersionInfo.GetVersionInfo(beside);
                if (!string.IsNullOrEmpty(info.ProductVersion)) return info.ProductVersion!;
            }
        }
        catch { /* 回退到自身版本 */ }
        return Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
    }

    // ============================ 安装 ============================

    /// <summary>执行安装。失败抛异常，由 UI 呈现。</summary>
    public static void Install(string target, bool autoStart, bool desktop, bool startMenu)
    {
        KillRunningApp(target);

        Directory.CreateDirectory(target);
        CopyPayload(target);

        // 自校验：主程序必须真实存在，否则视为失败且不写任何注册表/快捷方式
        if (!File.Exists(Path.Combine(target, ExeName)))
            throw new IOException(Loc.T("setup.errPayloadMissing"));

        if (autoStart) SetAutoStart(target);
        if (desktop) MakeShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                $"{AppName}.lnk"), target);
        if (startMenu) MakeShortcut(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Windows\Start Menu\Programs", $"{AppName}.lnk"), target);

        RegisterUninstallEntry(target, GetAppVersion());
    }

    public static void StartApp(string target)
    {
        Process.Start(new ProcessStartInfo(Path.Combine(target, ExeName))
        {
            WorkingDirectory = target,
        });
    }

    /// <summary>
    /// 把 payload 复制到目标目录：优先取安装器内嵌的 payload zip（单文件分发），
    /// 否则回退到旁路 app\ 文件夹（开发运行）。并把自己也复制进去供卸载用。
    /// </summary>
    private static void CopyPayload(string target)
    {
        var embedded = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("RonghuiEarbuds.Payload.zip");
        if (embedded is not null)
        {
            using (embedded)
            using (var archive = new ZipArchive(embedded, ZipArchiveMode.Read))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.Name)) continue; // 目录条目
                    var dst = Path.Combine(target, entry.FullName);
                    Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
                    entry.ExtractToFile(dst, true);
                }
            }
        }
        else
        {
            // 回退：开发运行时 payload 以 app\ 文件夹形式在旁路
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
                foreach (var src in Directory.EnumerateFiles(AppContext.BaseDirectory, "RonghuiEarbuds.*"))
                {
                    File.Copy(src, Path.Combine(target, Path.GetFileName(src)), true);
                }
            }
        }

        // 卸载程序本体也放进安装目录（单文件发布时只有 exe 一个文件）
        if (Environment.ProcessPath is { } self)
        {
            var selfDir = Path.GetDirectoryName(self)!;
            var selfName = Path.GetFileNameWithoutExtension(self);
            foreach (var src in Directory.EnumerateFiles(selfDir, $"{selfName}.*"))
            {
                if (src.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase)) continue;
                File.Copy(src, Path.Combine(target, Path.GetFileName(src)), true);
            }
        }
    }

    private static void RegisterUninstallEntry(string target, string version)
    {
        using var key = Registry.CurrentUser.CreateSubKey(UninstallKeyPath);
        key.SetValue("DisplayName", AppName);
        key.SetValue("DisplayVersion", version);
        key.SetValue("DisplayIcon", Path.Combine(target, ExeName));
        key.SetValue("UninstallString", $"\"{Path.Combine(target, "RonghuiEarbuds.Setup.exe")}\" --uninstall");
        key.SetValue("Publisher", Publisher);
        key.SetValue("InstallLocation", target);
        key.SetValue("NoModify", 1, RegistryValueKind.DWord);
        key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
    }

    // ============================ 卸载 ============================

    /// <summary>
    /// 校验本程序是否为注册表登记的那个卸载器。
    /// 主判定：UninstallString 指向本程序自身；回退判定：InstallLocation 与本程序所在目录一致
    /// （修复旧版登记损坏导致"卸载不了"的情况）。
    /// </summary>
    public static bool VerifyOwnInstall()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            if (key is null) return false;
            var self = Environment.ProcessPath;
            if (self is null) return false;

            // 主判定：UninstallString 值形如 "C:\path\Setup.exe" --uninstall，取引号内（或空格前）的 exe 路径
            var uninstallString = key.GetValue("UninstallString") as string;
            if (!string.IsNullOrWhiteSpace(uninstallString))
            {
                var s = uninstallString.Trim();
                if (s.StartsWith('"'))
                {
                    var end = s.IndexOf('"', 1);
                    if (end >= 0) s = s[1..end];
                }
                else
                {
                    var space = s.IndexOf(' ');
                    if (space > 0) s = s[..space];
                }
                if (string.Equals(Path.GetFullPath(s), Path.GetFullPath(self),
                        StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            // 回退判定：InstallLocation 与本程序所在目录一致
            var loc = key.GetValue("InstallLocation") as string;
            if (!string.IsNullOrWhiteSpace(loc))
            {
                var locFull = Path.GetFullPath(loc.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                var selfDir = Path.GetDirectoryName(Path.GetFullPath(self))!;
                if (string.Equals(locFull, selfDir, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>执行卸载（本进程位于安装目录内，结束后由 SpawnSelfDelete 删除目录）。</summary>
    public static void Uninstall(bool deleteConfig)
    {
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

        // 4. 配置文件（按用户选择）
        if (deleteConfig)
        {
            var configDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "RonghuiEarbuds");
            TryDeleteDir(configDir);
        }

        // 5. 延迟删除安装目录（等本进程退出）
        SpawnSelfDelete(target.TrimEnd(Path.DirectorySeparatorChar));
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

    /// <summary>结束正在运行的主程序；仅当进程路径能确认是 RonghuiEarbuds.exe 时才结束。</summary>
    private static void KillRunningApp(string target)
    {
        foreach (var process in Process.GetProcessesByName("RonghuiEarbuds"))
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
                new object[] { $"{AppName} - {Loc.T("setup.shortcutDesc")}" });
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
            // ping -n 6 ≈ 5 秒，留给"卸载完成"界面展示与进程退出
            Process.Start(new ProcessStartInfo(
                "cmd.exe", $"/c ping -n 6 127.0.0.1 > nul & rd /s /q \"{dir}\"")
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
}
