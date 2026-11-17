using System;
using System.Diagnostics;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using DesktopTodo.Services;

namespace DesktopTodo;

/// <summary>
/// 只在用户主动点击更新角标 / 托盘菜单时出现；自动检测绝不弹出此窗口。
/// 「立即更新」一键完成下载 → 校验 → 退出 → 替换 → 重启。
/// </summary>
public class UpdateDialog : Window
{
    private readonly UpdateInfo _info;
    private readonly Func<IProgress<string>, Task> _onUpdate;
    private readonly Action _onSnooze;
    private readonly Action _onIgnore;
    private readonly TextBlock _progress;
    private readonly ProgressBar _bar;
    private readonly Button _updateBtn, _snoozeBtn, _ignoreBtn, _closeBtn;

    public UpdateDialog(UpdateInfo info, Func<IProgress<string>, Task> onUpdate,
                        Action onSnooze, Action onIgnore)
    {
        _info = info;
        _onUpdate = onUpdate;
        _onSnooze = onSnooze;
        _onIgnore = onIgnore;

        Title = "发现新版本 — Desktop Todo";
        Width = 440; Height = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;

        var sp = new StackPanel { Margin = new Thickness(16) };

        sp.Children.Add(new TextBlock
        {
            Text = $"新版本 v{info.Version} 可用（当前 v{UpdateService.CurrentVersionText}）",
            FontSize = 15, FontWeight = FontWeights.Bold,
            Margin = new Thickness(0, 0, 0, 8)
        });
        sp.Children.Add(new TextBlock
        {
            Text = "更新不会改动你的 config.json 和已保存的账号密码。",
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 10)
        });

        var link = new Hyperlink(new Run("查看完整发布说明"))
        {
            NavigateUri = SafeUri(info.HtmlUrl)
        };
        link.RequestNavigate += (_, e) =>
        {
            try
            {
                Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true });
            }
            catch { /* 打不开浏览器不影响更新 */ }
        };
        var linkBlock = new TextBlock(link) { Margin = new Thickness(0, 0, 0, 10) };
        sp.Children.Add(linkBlock);

        sp.Children.Add(new TextBlock { Text = "本次更新内容：", Margin = new Thickness(0, 0, 0, 4) });
        var notes = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(info.Notes) ? "（未提供更新说明）" : info.Notes,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Foreground = new SolidColorBrush(Color.FromRgb(0x44, 0x44, 0x44))
        };
        sp.Children.Add(new ScrollViewer
        {
            Content = notes,
            Height = 130,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 10)
        });

        _progress = new TextBlock
        {
            Text = "准备好后点「立即更新」，程序会自动下载并重启。",
            TextWrapping = TextWrapping.Wrap,
            MinHeight = 36,
            Margin = new Thickness(0, 0, 0, 4)
        };
        sp.Children.Add(_progress);
        _bar = new ProgressBar
        {
            Height = 6,
            IsIndeterminate = false,
            Visibility = Visibility.Collapsed,
            Margin = new Thickness(0, 0, 0, 10)
        };
        sp.Children.Add(_bar);

        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        _updateBtn = MakeButton("立即更新", 92, true);
        _snoozeBtn = MakeButton("稍后提醒", 82, false);
        _ignoreBtn = MakeButton("忽略此版本", 88, false);
        _closeBtn = MakeButton("关闭", 62, false);
        _updateBtn.Click += async (_, _) => await RunUpdateAsync();
        _snoozeBtn.Click += (_, _) => { _onSnooze(); Close(); };
        _ignoreBtn.Click += (_, _) => { _onIgnore(); Close(); };
        _closeBtn.Click += (_, _) => Close();
        row.Children.Add(_updateBtn);
        row.Children.Add(_snoozeBtn);
        row.Children.Add(_ignoreBtn);
        row.Children.Add(_closeBtn);
        sp.Children.Add(row);

        Content = sp;
    }

    private async Task RunUpdateAsync()
    {
        SetBusy(true);
        var reporter = new Progress<string>(msg => _progress.Text = msg);
        try
        {
            await _onUpdate(reporter);
            // 成功时主程序已退出，本窗口随 Shutdown 一起关闭。
        }
        catch (Exception ex)
        {
            _progress.Text = "更新未完成：" + ex.Message + "\n旧版本可继续使用，可稍后重试。";
            _progress.Foreground = new SolidColorBrush(Color.FromRgb(0xc0, 0x39, 0x2b));
            SetBusy(false);
        }
    }

    private void SetBusy(bool busy)
    {
        _updateBtn.IsEnabled = !busy;
        _snoozeBtn.IsEnabled = !busy;
        _ignoreBtn.IsEnabled = !busy;
        _closeBtn.IsEnabled = !busy;
        _bar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        _bar.IsIndeterminate = busy;
    }

    private static Button MakeButton(string text, double width, bool primary)
    {
        var b = new Button
        {
            Content = text,
            Width = width,
            Height = 30,
            Margin = new Thickness(4, 0, 0, 0)
        };
        if (primary) b.Background = new SolidColorBrush(Color.FromRgb(0x2d, 0x7d, 0x46));
        return b;
    }

    private static Uri SafeUri(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) &&
        u.Scheme == Uri.UriSchemeHttps &&
        u.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            ? u
            : new Uri("https://github.com/lingting0/desktop-todo-wpf/releases/latest");
}
