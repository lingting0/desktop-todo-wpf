using System;
using System.IO;
using System.Windows;

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
        try
        {
            new MainWindow().Show();
        }
        catch (Exception ex)
        {
            Log(ex);
            MessageBox.Show("启动失败：" + ex.Message,
                "DesktopTodo 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DesktopTodo");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                "[" + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "] " + ex + Environment.NewLine);
        }
        catch { /* 日志失败不影响主流程 */ }
    }
}
