using System;
using System.Windows;
using System.Windows.Threading;

namespace DesktopTodo;

public partial class App : Application
{
    public App()
    {
        DispatcherUnhandledException += (_, e) =>
        {
            MessageBox.Show($"错误: {e.Exception.Message}\n\n{e.Exception.StackTrace}",
                "DesktopTodo 崩溃", MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            var ex = e.ExceptionObject as Exception;
            MessageBox.Show($"未处理错误: {ex?.Message}", "DesktopTodo", MessageBoxButton.OK, MessageBoxImage.Error);
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
            MessageBox.Show($"启动失败: {ex.Message}\n\n{ex.StackTrace}",
                "DesktopTodo 启动错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
