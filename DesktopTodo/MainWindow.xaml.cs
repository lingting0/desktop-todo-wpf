using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using DesktopTodo.Config;
using DesktopTodo.Models;
using DesktopTodo.Services;
using Microsoft.Win32;

namespace DesktopTodo;

public partial class MainWindow : Window
{
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x00010000L;

    // win-x64：使用 Ptr 版本，避免 64 位下截断窗口样式。
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly AppConfig _cfg;
    private CalDAVService _sync;
    private readonly TaskManager _mgr;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private readonly System.Windows.Data.ListCollectionView _taskView;

    private DispatcherTimer? _timer;
    private bool _syncing;
    private bool _exiting;
    private bool _cleaned;
    private bool _pinned = true;
    private bool _showCompleted;
    private bool _dragging;

    // 自动更新：后台静默检查，只在有新版本时点亮一个角标，绝不自动弹窗。
    private UpdateService _updateSvc;
    private readonly CancellationTokenSource _updateCts = new();
    private DispatcherTimer? _updateTimer;
    private UpdateInfo? _update;
    private UpdateDialog? _updateDialog;
    private System.Windows.Forms.ToolStripMenuItem? _trayUpdateItem;
    private bool _updateChecking;

    private static readonly Dictionary<string, (string card, string title)> Themes = new()
    {
        ["暗黑"] = ("#2a2a2a", "#363636"),
        ["亮白"] = ("#ffffff", "#f0f0f0"),
        ["深蓝"] = ("#1a2332", "#243447"),
        ["墨绿"] = ("#1c2e1c", "#263a26"),
        ["暗紫"] = ("#2a1f3d", "#362a4a"),
    };

    public MainWindow()
    {
        InitializeComponent();

        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var style = GetWindowLongPtr(hwnd, GWL_STYLE).ToInt64();
            SetWindowLongPtr(hwnd, GWL_STYLE, new IntPtr(style & ~WS_MAXIMIZEBOX));
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };

        _cfg = ConfigManager.Load();
        _sync = CreateSyncService(_cfg);
        _mgr = new TaskManager(_sync);
        _updateSvc = new UpdateService(_cfg.ProxyUrl);

        _taskView = new System.Windows.Data.ListCollectionView(_mgr.Tasks)
        {
            Filter = o => _showCompleted || o is TaskData t && !t.Completed
        };
        TaskList.ItemsSource = _taskView;

        Width = _cfg.CardWidth;
        Height = _cfg.CardHeight;
        Left = _cfg.CardPositionX;
        Top = _cfg.CardPositionY;

        CardBorder.SizeChanged += (_, _) =>
        {
            CardBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, CardBorder.ActualWidth, CardBorder.ActualHeight), 12, 12);
        };

        ApplyTheme(_cfg.Theme);
        _tray = SetupTray();
        _mgr.StatusChanged += (_, msg) => Dispatcher.Invoke(() => StatusLabel.Text = msg);

        if (ConfigManager.LastLoadError != null)
            StatusLabel.Text = ConfigManager.LastLoadError;

        Loaded += async (_, _) =>
        {
            // 更新检查不依赖 Nextcloud 是否可连：服务器宕机 / 断网时也应能收到新版本提醒。
            StartUpdateScheduler();
            await ConnectAndLoadAsync();
            RestartTimer();
        };

        // 只有托盘右键「退出」会走到这里；× / Alt+F4 都只隐藏窗口。
        Closed += (_, _) => Cleanup();
    }

    /// <summary>托盘右键「退出」是唯一真正结束进程的入口。</summary>
    private void ExitApplication()
    {
        _exiting = true;
        Cleanup();
        Application.Current.Shutdown();
    }

    /// <summary>任何关闭窗口的请求都只隐藏窗口，进程与右下角图标保留。</summary>
    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_exiting) { Cleanup(); return; }
        e.Cancel = true;
        Hide();
    }

    private void Cleanup()
    {
        if (_cleaned) return;
        _cleaned = true;
        _timer?.Stop();
        _updateTimer?.Stop();
        _updateCts.Cancel();
        try { _updateDialog?.Close(); } catch { /* 退出时不阻断 */ }
        try { SaveWindowGeometry(); } catch { /* 退出时不因写配置失败而阻断 */ }
        try { _updateSvc?.Dispose(); } catch { /* 释放失败不阻断 */ }
        _updateCts.Dispose();
        _tray?.Dispose();
        _sync?.Dispose();
    }

    private static CalDAVService CreateSyncService(AppConfig cfg)
        => new CalDAVService(cfg.NextcloudUrl, cfg.Username, cfg.AppPassword,
                             cfg.CertificateThumbprint, cfg.CalendarName);

    // ── 连接 / 定时同步 ──

    private async Task<bool> ConnectAndLoadAsync()
    {
        StatusLabel.Text = "正在连接 Nextcloud...";
        try
        {
            await _sync.ConnectAsync();
            await _mgr.LoadFromRemoteAsync();
            return _mgr.IsOnline;
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "连接失败: " + ex.Message;
            return false;
        }
    }

    private void RestartTimer()
    {
        _timer?.Stop();
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(Math.Max(30, _cfg.SyncIntervalSeconds))
        };
        _timer.Tick += async (_, _) => await SyncTickAsync();
        _timer.Start();
    }

    private async Task SyncTickAsync()
    {
        if (_syncing) return; // 防止慢网络下重叠触发
        _syncing = true;
        try
        {
            // 首次连接失败后，定时同步应尝试重新连接。
            await _mgr.SyncAsync();
        }
        finally { _syncing = false; }
    }

    /// <summary>保存设置后立刻重建同步服务与定时器，无需重启应用。</summary>
    private async Task ApplySettingsAsync()
    {
        _timer?.Stop();
        // 等正在进行的同步收尾，避免释放仍在使用中的 HttpClient。
        int guard = 0;
        while (_syncing && guard++ < 100) await Task.Delay(50);

        // 创建新服务实例，先连接+读取成功后再切换（原子会话切换）。
        var candidate = CreateSyncService(_cfg);
        List<TaskData>? initialTasks = null;
        try
        {
            await candidate.ConnectAsync();
            initialTasks = await candidate.FetchAllAsync();
        }
        catch (Exception ex)
        {
            candidate.Dispose();
            StatusLabel.Text = "设置已应用，但连接失败 — " + ex.Message + "（旧任务保留，请检查配置后重试）";
            RestartTimer();
            return;
        }

        // 连接+读取成功，原子切换。
        var old = _sync;
        _sync = candidate;
        _mgr.ReplaceSync(_sync);
        old.Dispose();

        // 把刚读到的任务直接灌入，不用再 FetchAll 一次。
        _mgr.Tasks.Clear();
        foreach (var t in initialTasks)
            _mgr.Tasks.Add(t);
        StatusLabel.Text = $"已同步 {_mgr.Tasks.Count} 个任务 (日历: {_sync.CalendarName})";

        // 代理设置变了也要重建更新服务
        _updateSvc = new UpdateService(_cfg.ProxyUrl);

        ApplyTheme(_cfg.Theme);
        Width = _cfg.CardWidth;
        Height = _cfg.CardHeight;

        RestartTimer();
        StatusLabel.Text = "设置已生效（无需重启）— 日历：" + _sync.CalendarName;
    }

    // ── 自动更新 ──

    /// <summary>启动 8 秒后首次检查；之后每 6 小时复查（内部再按 24 小时节流）。</summary>
    private void StartUpdateScheduler()
    {
        // 演练模式带 --pretend-version：立即手动检查并打开更新窗口，便于真实验收。
        if (UpdateService.OverrideVersion != null)
            _ = CheckForUpdateAsync(true);
        else
            _ = DelayedFirstCheckAsync();
        _updateTimer = new DispatcherTimer { Interval = TimeSpan.FromHours(6) };
        _updateTimer.Tick += async (_, _) => await MaybeCheckForUpdateAsync();
        _updateTimer.Start();
    }

    private async Task DelayedFirstCheckAsync()
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), _updateCts.Token);
            await MaybeCheckForUpdateAsync();
        }
        catch (OperationCanceledException) { }
        catch (Exception) { /* 自动检查的任何异常都静默 */ }
    }

    /// <summary>后台节流：关掉检查 / 稍后提醒期内 / 距上次不足 24 小时，都直接跳过。</summary>
    private async Task MaybeCheckForUpdateAsync()
    {
        if (_updateChecking || !_cfg.UpdateCheckEnabled) return;
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (_cfg.UpdateSnoozeUntilUnix > now) return;
        if (_cfg.UpdateLastCheckUnix > 0 && now - _cfg.UpdateLastCheckUnix < 24 * 3600) return;
        await CheckForUpdateAsync(false);
    }

    /// <summary>真正发起检查。manual=true 时给明确反馈；自动检查失败完全静默。</summary>
    private async Task CheckForUpdateAsync(bool manual)
    {
        if (_updateChecking) return;
        _updateChecking = true;
        try
        {
            // 只在检查成功后才写节流时间；失败时用 5 分钟短退避。
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var info = await _updateSvc.CheckAsync(cts.Token);

            // 检查成功：写入 24 小时节流。
            _cfg.UpdateLastCheckUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            ConfigManager.SaveUpdateState(_cfg);

            if (info == null)
            {
                if (manual) StatusLabel.Text = "已是最新版本 v" + UpdateService.CurrentVersionText;
                return;
            }
            if (!manual && info.Version == _cfg.UpdateIgnoredVersion) return;
            PublishUpdate(info, manual);
        }
        catch (Exception ex)
        {
            // 失败时用 5 分钟短退避，方便网络恢复后重试。
            _cfg.UpdateLastCheckUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 24 * 3600 + 300;
            ConfigManager.SaveUpdateState(_cfg);
            if (manual)
                StatusLabel.Text = "检查更新失败：" + ex.Message + "（不影响使用，5 分钟后自动重试）";
        }
        finally { _updateChecking = false; }
    }

    /// <summary>只点亮角标与托盘菜单，不弹窗、不弹气泡、不抢焦点。</summary>
    private void PublishUpdate(UpdateInfo info, bool manual)
    {
        _update = info;
        UpdateBtn.Visibility = Visibility.Visible;
        UpdateBtn.ToolTip = $"发现新版本 v{info.Version} — 点击一键更新";
        if (_trayUpdateItem != null)
        {
            _trayUpdateItem.Text = "更新到 v" + info.Version;
            _trayUpdateItem.Visible = true;
        }
        _tray.Text = "Desktop Todo — 新版本 v" + info.Version;

        if (info.Version != _cfg.UpdateNotifiedVersion)
        {
            _cfg.UpdateNotifiedVersion = info.Version;
            ConfigManager.SaveUpdateState(_cfg);
            StatusLabel.Text = $"发现新版本 v{info.Version} — 点标题栏 ⬆ 可一键更新（不会打断使用）";
        }
        if (manual) ShowUpdateDialog();
    }

    private void HideUpdateHint()
    {
        _update = null;
        UpdateBtn.Visibility = Visibility.Collapsed;
        if (_trayUpdateItem != null) _trayUpdateItem.Visible = false;
        _tray.Text = "Desktop Todo";
    }

    /// <summary>用户主动点击查看，会话内只显示一次，重复点击只是激活已有窗口。</summary>
    private void ShowUpdateDialog()
    {
        var info = _update;
        if (info == null) return;
        if (_updateDialog != null && _updateDialog.IsVisible)
        {
            _updateDialog.Activate();
            return;
        }

        _updateDialog = new UpdateDialog(
            info,
            PerformUpdateAsync,
            () =>
            {
                _cfg.UpdateSnoozeUntilUnix =
                    DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 3L * 24 * 3600;
                ConfigManager.SaveUpdateState(_cfg);
                HideUpdateHint();
                StatusLabel.Text = "更新提醒已延后 3 天";
            },
            () =>
            {
                _cfg.UpdateIgnoredVersion = info.Version;
                ConfigManager.SaveUpdateState(_cfg);
                HideUpdateHint();
                StatusLabel.Text = "已忽略 v" + info.Version + "；之后发布的新版本仍会提醒";
            })
        { Owner = this };
        _updateDialog.Show();
    }

    /// <summary>一键更新：下载 → 校验 → 启动更新器 → 真正退出 → 由更新器替换并重启。</summary>
    private async Task PerformUpdateAsync(IProgress<string> progress)
    {
        var info = _update ?? throw new InvalidOperationException("更新信息已失效，请重新检查更新");
        var staged = await _updateSvc.PrepareAsync(info, progress, _updateCts.Token);
        progress.Report("更新包已就绪，正在重启完成更新...");
        if (!UpdateService.StartUpdater(staged, Environment.ProcessId))
            throw new InvalidOperationException("更新程序启动失败，请到 Releases 手动下载覆盖");
        ExitApplication();
    }

    private void UpdateBtn_Click(object s, RoutedEventArgs e) => ShowUpdateDialog();

    // ── Theme ──

    private void ApplyTheme(string name)
    {
        if (!Themes.TryGetValue(name, out var t)) t = Themes["暗黑"];
        Resources["CardBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t.card));
        Resources["TitleBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(t.title));
        bool isLight = name == "亮白";
        var textColor = isLight ? "#333333" : "#e0e0e0";
        var borderColor = isLight ? "#cccccc" : "#444444";
        var inputBg = isLight ? "#f5f5f5" : "#1a1a1a";
        Resources["TextColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(textColor));
        Resources["BorderColor"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(borderColor));
        Resources["InputBg"] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(inputBg));
        _cfg.Theme = name;
    }

    // ── Tray ──

    private System.Windows.Forms.NotifyIcon SetupTray()
    {
        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ToDoIcon.png");
        System.Drawing.Icon icon;
        if (File.Exists(iconPath))
        {
            using var bmp = new System.Drawing.Bitmap(iconPath);
            icon = System.Drawing.Icon.FromHandle(bmp.GetHicon());
        }
        else
        {
            icon = System.Drawing.SystemIcons.Application;
        }

        var tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = icon,
            Text = "Desktop Todo",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        tray.ContextMenuStrip.Items.Add("显示/隐藏", null, (_, _) => { if (IsVisible) Hide(); else { Show(); Activate(); } });
        tray.ContextMenuStrip.Items.Add("立即同步", null, async (_, _) => await _mgr.SyncAsync());
        tray.ContextMenuStrip.Items.Add("检查更新", null, async (_, _) => await CheckForUpdateAsync(true));
        _trayUpdateItem = new System.Windows.Forms.ToolStripMenuItem("更新到新版本")
        {
            Visible = false,
            Font = new System.Drawing.Font(tray.ContextMenuStrip.Font, System.Drawing.FontStyle.Bold)
        };
        _trayUpdateItem.Click += (_, _) => ShowUpdateDialog();
        tray.ContextMenuStrip.Items.Add(_trayUpdateItem);
        tray.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("反馈与建议", null, (_, _) => ShowFeedback());
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => ExitApplication());
        tray.DoubleClick += (_, _) => { Show(); Activate(); };
        return tray;
    }

    // ── Title bar ──

    private void PinBtn_Click(object s, RoutedEventArgs e)
    {
        _pinned = !_pinned;
        Topmost = _pinned;
        PinBtn.Content = _pinned ? "◆" : "◇";
        PinBtn.ToolTip = _pinned ? "已置顶 — 点击取消" : "未置顶 — 点击置顶";
    }

    private void FilterBtn_Click(object s, RoutedEventArgs e)
    {
        _showCompleted = !_showCompleted;
        FilterBtn.Content = _showCompleted ? "☑" : "☐";
        FilterBtn.ToolTip = _showCompleted ? "隐藏已完成" : "显示已完成";
        _taskView.Refresh();
    }

    private async void SettingsBtn_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var dlg = new SettingsDialog(_cfg) { Owner = this };
            if (dlg.ShowDialog() == true)
            {
                try { ConfigManager.Save(_cfg); } // 只在这里写一次盘
                catch (Exception ex) { ShowError("保存配置", ex); }
                await ApplySettingsAsync(); // 即使写盘失败，设置也应立即生效
            }
        }
        catch (Exception ex) { ShowError("保存设置", ex); }
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => Hide();
    private void CloseBtn_Click(object s, RoutedEventArgs e) => Hide();

    /// <summary>反馈与建议：弹窗显示邮箱，点按钮复制到剪贴板。</summary>
    private void ShowFeedback()
    {
        const string email = "llingting0@qq.com";
        var dlg = new Window
        {
            Title = "反馈与建议",
            Width = 360,
            Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            Owner = this
        };
        var sp = new StackPanel { Margin = new Thickness(16) };
        sp.Children.Add(new TextBlock
        {
            Text = "遇到 bug 或有建议，欢迎邮件联系：",
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 8)
        });
        var emailRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 12) };
        var emailText = new TextBlock
        {
            Text = email,
            FontSize = 15,
            FontWeight = FontWeights.Bold,
            VerticalAlignment = VerticalAlignment.Center
        };
        emailRow.Children.Add(emailText);
        var copyBtn = new Button
        {
            Content = "复制",
            Width = 60,
            Height = 26,
            Margin = new Thickness(8, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center
        };
        var copiedLabel = new TextBlock
        {
            Text = "已复制",
            FontSize = 11,
            Foreground = new SolidColorBrush(Color.FromRgb(0x1e, 0x8e, 0x3e)),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
            Visibility = Visibility.Collapsed
        };
        copyBtn.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(email);
                copiedLabel.Visibility = Visibility.Visible;
            }
            catch { /* 剪贴板被占用不阻断 */ }
        };
        emailRow.Children.Add(copyBtn);
        emailRow.Children.Add(copiedLabel);
        sp.Children.Add(emailRow);
        var closeBtn = new Button
        {
            Content = "关闭",
            Width = 70,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        closeBtn.Click += (_, _) => dlg.Close();
        sp.Children.Add(closeBtn);
        dlg.Content = sp;
        dlg.ShowDialog();
    }

    // ── Drag + Snap ──

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _dragging = true;
            DragMove();
        }
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        // 只在标题栏拖动结束后吸附，点击按钮/复选框不再移动窗口。
        if (_dragging)
        {
            _dragging = false;
            SnapToScreenEdge();
        }
    }

    private void SnapToScreenEdge()
    {
        var screen = System.Windows.Forms.Screen.FromHandle(
            new System.Windows.Interop.WindowInteropHelper(this).Handle);
        var sr = screen.WorkingArea;
        var snap = _cfg.SnapDistance;

        int newX = (int)Left, newY = (int)Top;
        if (Math.Abs((int)Left - sr.Left) <= snap) newX = sr.Left;
        else if (Math.Abs((int)(Left + Width) - sr.Right) <= snap) newX = sr.Right - (int)Width;
        if (Math.Abs((int)Top - sr.Top) <= snap) newY = sr.Top;
        else if (Math.Abs((int)(Top + Height) - sr.Bottom) <= snap) newY = sr.Bottom - (int)Height;
        if (newX != (int)Left || newY != (int)Top) { Left = newX; Top = newY; }
    }

    private void SaveWindowGeometry()
    {
        _cfg.CardPositionX = (int)Left;
        _cfg.CardPositionY = (int)Top;
        _cfg.CardWidth = (int)Width;
        _cfg.CardHeight = (int)Height;
        try { ConfigManager.Save(_cfg); }
        catch { /* 退出时不因写配置失败而阻断 */ }
    }

    // ── Tasks ──

    private async void AddBtn_Click(object s, RoutedEventArgs e) => await AddTask();
    private async void InputBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) await AddTask(); }

    private async Task AddTask()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        try
        {
            await _mgr.AddAsync(text);
            InputBox.Clear();
        }
        catch (Exception ex) { ShowError("新增任务", ex); }
    }

    private async void TaskCheckChanged(object s, RoutedEventArgs e)
    {
        try
        {
            if (s is CheckBox cb && cb.DataContext is TaskData task)
            {
                await _mgr.ToggleCompleteAsync(task);
                _taskView.Refresh();
            }
        }
        catch (Exception ex) { ShowError("完成任务", ex); }
    }

    private async void EditBtn_Click(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.DataContext is not TaskData task) return;
        var input = new InputDialog("编辑任务", task.Summary) { Owner = this };
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Result))
        {
            var old = task.Summary;
            task.Summary = input.Result;
            try { await _mgr.UpdateAsync(task); }
            catch (Exception ex) { task.Summary = old; ShowError("编辑任务", ex); }
        }
    }

    private async void DelBtn_Click(object s, RoutedEventArgs e)
    {
        if ((s as Button)?.DataContext is not TaskData task) return;
        try { await _mgr.DeleteAsync(task); }
        catch (Exception ex) { ShowError("删除任务", ex); }
    }

    private void TaskRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.DataContext is not TaskData task) return;
        var menu = new ContextMenu();
        foreach (var (val, label) in new[] { (0, "无优先级"), (1, "高优先级"), (5, "中优先级"), (9, "低优先级") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) =>
            {
                try { await _mgr.SetPriorityAsync(task, val); }
                catch (Exception ex) { ShowError("设置优先级", ex); }
            };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void TitleLabel_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new ContextMenu();
        foreach (var name in Themes.Keys)
        {
            var item = new MenuItem { Header = (name == _cfg.Theme ? "● " : "    ") + name };
            item.Click += (_, _) => { ApplyTheme(name); ConfigManager.Save(_cfg); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void ShowError(string action, Exception ex)
        => StatusLabel.Text = action + "失败：" + ex.Message + "（下次同步后恢复）";
}

// ═══════════════════════════════════════════════════════════
// Converters
// ═══════════════════════════════════════════════════════════

public class PrioToColorConverter : System.Windows.Data.IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
    {
        int p = value is int i ? i : 0;
        var color = p switch
        {
            1 => Color.FromRgb(0xe7, 0x4c, 0x3c),
            5 => Color.FromRgb(0xf3, 0x9c, 0x12),
            9 => Color.FromRgb(0x34, 0x98, 0xdb),
            _ => Colors.Gray,
        };
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object value, Type targetType, object parameter, System.Globalization.CultureInfo culture)
        => throw new NotImplementedException();
}

// ═══════════════════════════════════════════════════════════
// Dialogs
// ═══════════════════════════════════════════════════════════

public class InputDialog : Window
{
    public string Result { get; private set; } = "";
    private readonly TextBox _tb;

    public InputDialog(string title, string initial)
    {
        Title = title; Width = 350; Height = 140;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.NoResize;
        var sp = new StackPanel { Margin = new Thickness(12) };
        sp.Children.Add(new TextBlock { Text = "任务内容:", Margin = new Thickness(0, 0, 0, 6) });
        _tb = new TextBox { Text = initial };
        _tb.SelectAll();
        sp.Children.Add(_tb);
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var ok = new Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { Result = _tb.Text; DialogResult = true; };
        var cancel = new Button { Content = "取消", Width = 70 };
        cancel.Click += (_, _) => { DialogResult = false; };
        btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
        sp.Children.Add(btnRow);
        Content = sp;
        Loaded += (_, _) => _tb.Focus();
    }
}

public class SettingsDialog : Window
{
    private readonly AppConfig _target;
    private readonly AppConfig _draft;
    private readonly TextBox _urlBox, _userBox, _calBox, _thumbBox, _intervalBox, _proxyBox;
    private readonly PasswordBox _pwdBox;
    private readonly CheckBox _autoStartChk;
    private readonly ComboBox _themeCb;
    private readonly TextBlock _result;
    private readonly Button _testBtn;

    public SettingsDialog(AppConfig cfg)
    {
        _target = cfg;
        _draft = cfg.Clone();

        Title = "设置 — Desktop Todo"; Width = 440; Height = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.NoResize;

        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(MakeLabel("Nextcloud URL:"));
        _urlBox = MakeTextBox(_draft.NextcloudUrl); sp.Children.Add(_urlBox);
        sp.Children.Add(MakeLabel("用户名:"));
        _userBox = MakeTextBox(_draft.Username); sp.Children.Add(_userBox);
        sp.Children.Add(MakeLabel("应用密码（保存在 Windows 凭据管理器，不写入 config.json）:"));
        _pwdBox = new PasswordBox { Password = _draft.AppPassword }; sp.Children.Add(_pwdBox);
        sp.Children.Add(MakeLabel("日历名称:"));
        _calBox = MakeTextBox(_draft.CalendarName); sp.Children.Add(_calBox);
        sp.Children.Add(MakeLabel("同步间隔（秒，最小 30）:"));
        _intervalBox = MakeTextBox(_draft.SyncIntervalSeconds.ToString()); sp.Children.Add(_intervalBox);
        sp.Children.Add(MakeLabel("自签证书 SHA-256 指纹（可选，留空 = 严格校验证书）:"));
        _thumbBox = MakeTextBox(_draft.CertificateThumbprint); sp.Children.Add(_thumbBox);
        sp.Children.Add(MakeLabel("主题:"));
        _themeCb = new ComboBox { ItemsSource = new[] { "暗黑", "亮白", "深蓝", "墨绿", "暗紫" } };
        _themeCb.SelectedItem = _draft.Theme;
        sp.Children.Add(_themeCb);
        _autoStartChk = new CheckBox { Content = "开机自启", IsChecked = _draft.AutoStart };
        sp.Children.Add(_autoStartChk);
        sp.Children.Add(MakeLabel("代理地址（用于更新检查，留空 = 走系统代理）:"));
        _proxyBox = MakeTextBox(_draft.ProxyUrl);
        _proxyBox.ToolTip = "例：http://127.0.0.1:7897";
        sp.Children.Add(_proxyBox);

        var testRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 12, 0, 4) };
        _testBtn = new Button { Content = "测试连接", Width = 100, Height = 28 };
        _testBtn.Click += TestBtn_Click;
        testRow.Children.Add(_testBtn);
        sp.Children.Add(testRow);

        _result = new TextBlock
        {
            Text = "先点「测试连接」验证，确认无误后再保存；保存后立即生效，无需重启。",
            TextWrapping = TextWrapping.Wrap,
            Foreground = new SolidColorBrush(Color.FromRgb(0x66, 0x66, 0x66)),
            Margin = new Thickness(0, 2, 0, 0),
            MinHeight = 40,
        };
        sp.Children.Add(_result);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new Button { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Save();
        var cancel = new Button { Content = "取消", Width = 80 };
        cancel.Click += (_, _) => { DialogResult = false; };
        btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
        sp.Children.Add(btnRow);
        Content = new ScrollViewer
        {
            Content = sp,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private AppConfig Collect()
    {
        _draft.NextcloudUrl = _urlBox.Text.Trim();
        _draft.Username = _userBox.Text.Trim();
        _draft.AppPassword = _pwdBox.Password;
        _draft.CalendarName = _calBox.Text.Trim();
        _draft.CertificateThumbprint = _thumbBox.Text.Trim();
        _draft.SyncIntervalSeconds = int.TryParse(_intervalBox.Text.Trim(), out var n) ? n : 300;
        _draft.Theme = _themeCb.SelectedItem?.ToString() ?? "暗黑";
        _draft.AutoStart = _autoStartChk.IsChecked ?? false;
        _draft.ProxyUrl = _proxyBox.Text.Trim();
        return _draft;
    }

    private async void TestBtn_Click(object sender, RoutedEventArgs e)
    {
        var draft = Collect();
        var warning = ConfigManager.ValidateForConnection(draft);
        if (warning != null && !warning.StartsWith("警告", StringComparison.Ordinal))
        {
            _result.Text = "无法测试：" + warning;
            return;
        }

        _testBtn.IsEnabled = false;
        _result.Text = "正在测试连接...";
        try
        {
            using var svc = new CalDAVService(draft.NextcloudUrl, draft.Username, draft.AppPassword,
                                              draft.CertificateThumbprint, draft.CalendarName);
            var (ok, message) = await svc.TestConnectionAsync();
            _result.Text = (warning != null ? warning + "；" : "") + message;
            _result.Foreground = new SolidColorBrush(ok
                ? Color.FromRgb(0x1e, 0x8e, 0x3e)
                : Color.FromRgb(0xc0, 0x39, 0x2b));
        }
        catch (Exception ex)
        {
            _result.Text = "测试异常：" + ex.Message;
        }
        finally
        {
            _testBtn.IsEnabled = true;
        }
    }

    private void Save()
    {
        var draft = Collect();
        var error = ConfigManager.ValidateForConnection(draft);
        if (error != null)
        {
            if (error.StartsWith("警告", StringComparison.Ordinal))
            {
                // HTTP 警告：弹窗确认，用户同意后持久化 allow_insecure_http。
                var result = MessageBox.Show(
                    error + "\n\n确定要使用 HTTP 吗？密码将以明文传输。",
                    "安全警告", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result != MessageBoxResult.Yes) return;
                draft.AllowInsecureHttp = true;
            }
            else
            {
                _result.Text = "无法保存：" + error;
                return;
            }
        }
        ConfigManager.Normalize(draft);
        CopyTo(draft, _target);
        bool autoStartOk = SetAutoStart(_target.AutoStart);
        if (_target.AutoStart && !autoStartOk)
            _result.Text = "保存成功，但开机自启设置失败（权限不足）。其他设置已生效。";
        else
            DialogResult = true;
    }

    private static void CopyTo(AppConfig from, AppConfig to)
    {
        to.NextcloudUrl = from.NextcloudUrl;
        to.Username = from.Username;
        to.AppPassword = from.AppPassword;
        to.CalendarName = from.CalendarName;
        to.CertificateThumbprint = from.CertificateThumbprint;
        to.SyncIntervalSeconds = from.SyncIntervalSeconds;
        to.Theme = from.Theme;
        to.AutoStart = from.AutoStart;
        to.ProxyUrl = from.ProxyUrl;
        to.AllowInsecureHttp = from.AllowInsecureHttp;
    }

    /// <summary>开机自启写入注册表 Run 项。返回实际是否成功。</summary>
    private static bool SetAutoStart(bool enable)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", true);
            if (key == null) return !enable; // 无法打开注册表，关闭自启视为无操作成功
            if (enable)
            {
                var exe = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopTodo.exe");
                if (File.Exists(exe)) { key.SetValue("DesktopTodo", "\"" + exe + "\""); return true; }
                return false;
            }
            else
            {
                key.DeleteValue("DesktopTodo", false);
                return true;
            }
        }
        catch { return false; }
    }

    private static TextBlock MakeLabel(string t) => new() { Text = t, Margin = new Thickness(0, 8, 0, 2) };
    private static TextBox MakeTextBox(string v) => new() { Text = v };
}
