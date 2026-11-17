using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using DesktopTodo.Services;

namespace DesktopTodo;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            Log(e.Exception);
            // 不再把完整堆栈弹给用户；详细信息写入本地日志。
            MessageBox.Show("发生错误：" + e.Exception.Message,
                "DesktopTodo", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception ex) Log(ex);
        };
    }

    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = e.Args;
        // 更新器模式：由「暂存目录里的新 exe」启动，不加载主窗口。
        // 这样目标目录中的 DesktopTodo.exe 没有被占用，可以安全覆盖。
        if (args.Length >= 4 && args[0] == "--apply-update")
        {
            RunUpdater(args);
            Shutdown();
            return;
        }

        // 验收演练参数：--pretend-version 1.0.0 会把当前版本伪装成旧版，
        // 从而在已发布新版本时真实走一遍提醒与一键更新流程。正常启动不带此参数。
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals("--pretend-version", StringComparison.OrdinalIgnoreCase) &&
                Version.TryParse(args[i + 1], out var pretend))
                UpdateService.OverrideVersion = pretend;
        }

        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show("启动失败：" + ex.Message,
                "DesktopTodo 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown();
        }
    }

    /// <summary>
    /// --apply-update &lt;stagedDir&gt; &lt;destDir&gt; &lt;pid&gt; [--no-launch]
    /// 等待旧进程退出 → 备份旧文件 → 复制新文件（跳过 config.json）→ 重启程序。
    /// </summary>
    private static void RunUpdater(string[] args)
    {
        var staged = args[1];
        var dest = args[2];
        int pid = int.TryParse(args[3], out var p) ? p : 0;
        bool noLaunch = args.Skip(4).Contains("--no-launch", StringComparer.OrdinalIgnoreCase);

        void W(string msg)
        {
            try
            {
                var dir = LocalDir();
                Directory.CreateDirectory(dir);
                File.AppendAllText(Path.Combine(dir, "update.log"),
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {msg}{Environment.NewLine}");
            }
            catch { /* 日志失败不影响更新 */ }
        }

        try
        {
            W($"开始更新：staged={staged} dest={dest} pid={pid}");

            if (pid > 0)
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(15000))
                        W("警告：旧进程 15 秒内未退出，继续尝试覆盖");
                }
                catch (ArgumentException) { /* 已经退出 */ }
                catch (Exception ex) { W("等待旧进程异常：" + ex.Message); }
            }

            var backup = Path.Combine(UpdateService.UpdatesRoot,
                "backup-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            try
            {
                Directory.CreateDirectory(backup);
                foreach (var file in Directory.GetFiles(dest, "*", SearchOption.TopDirectoryOnly))
                {
                    var name = Path.GetFileName(file);
                    // 不把本机运行配置复制进备份目录。
                    if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                        name.EndsWith(".bad", StringComparison.OrdinalIgnoreCase)) continue;
                    File.Copy(file, Path.Combine(backup, name), true);
                }
                W("旧版本已备份到：" + backup);
            }
            catch (Exception ex) { W("备份失败（不阻断更新）：" + ex.Message); }

            int copied = 0;
            foreach (var file in Directory.GetFiles(staged, "*", SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(staged, file);
                if (Path.GetFileName(rel).Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    continue; // 本机配置永远不会被覆盖
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, true);
                copied++;
            }
            W($"完成：复制 {copied} 个文件，config.json 已保留");

            if (!noLaunch)
            {
                var exe = Path.Combine(dest, "DesktopTodo.exe");
                Process.Start(new ProcessStartInfo(exe)
                {
                    WorkingDirectory = dest,
                    UseShellExecute = true
                });
                W("已启动新版本");
            }
            else
            {
                W("演练模式：按要求未启动新版本");
            }
        }
        catch (Exception ex)
        {
            W("更新失败：" + ex);
            MessageBox.Show(
                "自动更新失败，旧版本文件仍可使用。\n\n" + ex.Message +
                "\n\n详情见 %LOCALAPPDATA%\\DesktopTodo\\update.log",
                "Desktop Todo 更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string LocalDir() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DesktopTodo");

    private static void Log(Exception ex)
    {
        try
        {
            var dir = LocalDir();
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex + Environment.NewLine);
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
