using System;
using System.Collections.Generic;
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
    /// 等待旧进程退出 → 完整备份旧文件 → 按清单复制新文件（跳过 config.json）→ 失败时全量回滚 → 重启程序。
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

        string? backupPath = null;
        string updatePhase = "初始化";

        try
        {
            W($"开始更新：staged={staged} dest={dest} pid={pid}");

            // ── 阶段 1：等待旧进程退出 ──
            updatePhase = "等待旧进程退出";
            if (pid > 0)
            {
                try
                {
                    using var old = Process.GetProcessById(pid);
                    if (!old.WaitForExit(15000))
                    {
                        throw new TimeoutException("旧进程 15 秒内未退出，更新已取消");
                    }
                }
                catch (ArgumentException) { /* 已经退出 */ }
                catch (TimeoutException) { throw; }
                catch (Exception ex) { W("等待旧进程异常：" + ex.Message); }
            }

            // ── 阶段 2：完整备份 ──
            updatePhase = "备份旧版本";
            backupPath = Path.Combine(UpdateService.UpdatesRoot,
                "backup-" + DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            var backupFiles = new List<(string destFile, string backupFile)>();
            Directory.CreateDirectory(backupPath);
            foreach (var file in Directory.GetFiles(dest, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.Equals("config.json", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bad", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
                var bk = Path.Combine(backupPath, name);
                File.Copy(file, bk, true);
                backupFiles.Add((file, bk));
            }
            W($"旧版本已完整备份到：{backupPath}（{backupFiles.Count} 个文件）");

            // ── 阶段 3：按清单复制新文件 ──
            updatePhase = "复制新文件";
            var newFiles = Directory.GetFiles(staged, "*", SearchOption.AllDirectories);
            var copiedFiles = new List<(string target, bool existedBefore)>();

            foreach (var file in newFiles)
            {
                var rel = Path.GetRelativePath(staged, file);
                if (Path.GetFileName(rel).Equals("config.json", StringComparison.OrdinalIgnoreCase))
                    continue;
                var target = Path.Combine(dest, rel);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                bool existedBefore = File.Exists(target);
                File.Copy(file, target, true);
                copiedFiles.Add((target, existedBefore));
            }
            W($"完成：复制 {copiedFiles.Count} 个文件，config.json 已保留");

            // ── 阶段 4：验证必需文件 ──
            updatePhase = "验证安装";
            foreach (var required in new[] { "DesktopTodo.exe", "DesktopTodo.dll" })
            {
                if (!File.Exists(Path.Combine(dest, required)))
                    throw new InvalidDataException("更新后缺少必需文件：" + required);
            }

            // ── 阶段 5：启动新版本 ──
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
            W($"更新失败（阶段：{updatePhase}）：{ex}");

            // 全量回滚：恢复备份的旧文件，删除新增文件。
            string rollbackStatus = PerformRollback(dest, backupPath, W);
            W("回滚结果：" + rollbackStatus);

            string message = updatePhase switch
            {
                "等待旧进程退出" => $"更新未开始：{ex.Message}",
                "备份旧版本" => $"备份失败，更新未执行：{ex.Message}",
                _ => backupPath != null
                    ? $"更新失败（{updatePhase}），已回滚到旧版本。{ex.Message}\n\n备份目录：{backupPath}"
                    : $"更新失败（{updatePhase}），回滚状态：{rollbackStatus}\n\n{ex.Message}"
            };

            if (rollbackStatus.Contains("回滚失败"))
            {
                message += $"\n\n⚠ 回滚可能不完整，备份目录：{backupPath}\n建议手动从备份恢复。";
            }

            MessageBox.Show(message,
                "Desktop Todo 更新失败", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>全量回滚：恢复备份文件，删除新增文件。</summary>
    private static string PerformRollback(string dest, string? backupPath, Action<string> log)
    {
        if (backupPath == null || !Directory.Exists(backupPath))
            return "无备份可回滚";

        try
        {
            int restored = 0, deleted = 0;

            // 恢复所有备份文件。
            foreach (var bk in Directory.GetFiles(backupPath, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(bk);
                var target = Path.Combine(dest, name);
                File.Copy(bk, target, true);
                restored++;
            }

            // 删除备份目录中不存在的文件（即本次新增的文件）。
            var backupNames = new HashSet<string>(
                Directory.GetFiles(backupPath, "*", SearchOption.TopDirectoryOnly)
                    .Select(f => Path.GetFileName(f) ?? ""), StringComparer.OrdinalIgnoreCase);
            foreach (var file in Directory.GetFiles(dest, "*", SearchOption.TopDirectoryOnly))
            {
                var name = Path.GetFileName(file);
                if (name.EndsWith(".log", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".bad", StringComparison.OrdinalIgnoreCase) ||
                    name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("config.json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!backupNames.Contains(name))
                {
                    try { File.Delete(file); deleted++; }
                    catch (Exception ex) { log($"无法删除新增文件 {name}：{ex.Message}"); }
                }
            }

            return $"已恢复 {restored} 个文件，删除 {deleted} 个新增文件";
        }
        catch (Exception ex)
        {
            return $"回滚失败：{ex.Message}（备份目录：{backupPath}）";
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
