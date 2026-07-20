using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DesktopTodo.Config;
using DesktopTodo.Models;
using DesktopTodo.Services;

namespace DesktopTodo;

public partial class MainWindow : Window
{
    // Win32 常量 — 禁用最大化按钮（阻止 Aero Snap）
    private const int GWL_STYLE = -16;
    private const long WS_MAXIMIZEBOX = 0x00010000L;
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetWindowLong(IntPtr hWnd, int nIndex);
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SetWindowLong(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    private readonly AppConfig _cfg;
    private readonly CalDAVService _sync;
    private readonly TaskManager _mgr;
    private readonly System.Windows.Forms.NotifyIcon _tray;
    private bool _pinned = true;

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

        // 阻止 Aero Snap 自动最大化
        SourceInitialized += (_, _) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            var style = GetWindowLong(hwnd, GWL_STYLE);
            SetWindowLong(hwnd, GWL_STYLE, (IntPtr)(style & ~WS_MAXIMIZEBOX));
        };
        StateChanged += (_, _) => { if (WindowState == WindowState.Maximized) WindowState = WindowState.Normal; };

        _cfg = ConfigManager.Load();
        _sync = new CalDAVService(_cfg.NextcloudUrl, _cfg.Username, _cfg.AppPassword);
        _mgr = new TaskManager(_sync);
        TaskList.ItemsSource = _mgr.Tasks;

        Width = _cfg.CardWidth;
        Height = _cfg.CardHeight;
        Left = _cfg.CardPositionX;
        Top = _cfg.CardPositionY;

        // 裁剪 Grid 到圆角（WPF Border 不自动裁剪子元素）
        CardBorder.SizeChanged += (_, _) =>
        {
            CardBorder.Clip = new RectangleGeometry(
                new Rect(0, 0, CardBorder.ActualWidth, CardBorder.ActualHeight), 12, 12);
        };

        ApplyTheme(_cfg.Theme);
        _tray = SetupTray();
        _mgr.StatusChanged += (_, msg) => Dispatcher.Invoke(() => StatusLabel.Text = msg);

        Loaded += async (_, _) =>
        {
            StatusLabel.Text = "正在连接 Nextcloud...";
            try
            {
                await _sync.ConnectAsync();
                await _mgr.LoadFromRemoteAsync();
            }
            catch (Exception ex)
            {
                StatusLabel.Text = $"连接失败: {ex.Message}";
            }

            var timer = new System.Timers.Timer(_cfg.SyncIntervalSeconds * 1000);
            timer.Elapsed += async (_, _) => await Dispatcher.Invoke(async () => await _mgr.LoadFromRemoteAsync());
            timer.AutoReset = true;
            timer.Start();
        };

        Closed += (_, _) => _tray.Dispose();
    }

    // ── Theme ──

    private void ApplyTheme(string name)
    {
        if (!Themes.TryGetValue(name, out var t)) t = Themes["暗黑"];
        Resources["CardBg"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(t.card));
        Resources["TitleBg"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(t.title));
        bool isLight = name == "亮白";
        var textColor = isLight ? "#333333" : "#e0e0e0";
        var borderColor = isLight ? "#cccccc" : "#444444";
        var inputBg = isLight ? "#f5f5f5" : "#1a1a1a";
        Resources["TextColor"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(textColor));
        Resources["BorderColor"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(borderColor));
        Resources["InputBg"] = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(inputBg));
        _cfg.Theme = name;
    }

    // ── Tray ──

    private System.Windows.Forms.NotifyIcon SetupTray()
    {
        var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ToDoIcon.png");
        System.Drawing.Icon icon;
        if (System.IO.File.Exists(iconPath))
        {
            var bmp = new System.Drawing.Bitmap(iconPath);
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
        tray.ContextMenuStrip.Items.Add("显示/隐藏", null, (_, _) => { if (IsVisible) Hide(); else Show(); });
        tray.ContextMenuStrip.Items.Add("立即同步", null, async (_, _) => await _mgr.LoadFromRemoteAsync());
        tray.ContextMenuStrip.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        tray.ContextMenuStrip.Items.Add("退出", null, (_, _) => System.Windows.Application.Current.Shutdown());
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

    private void SettingsBtn_Click(object s, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(_cfg) { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            ConfigManager.Save(_cfg);
            ApplyTheme(_cfg.Theme);
            Width = _cfg.CardWidth;
            Height = _cfg.CardHeight;
        }
    }

    private void MinBtn_Click(object s, RoutedEventArgs e) => Hide();
    private void CloseBtn_Click(object s, RoutedEventArgs e) => System.Windows.Application.Current.Shutdown();

    // ── Drag + Snap ──

    private void TitleBar_Drag(object s, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) { }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        SnapToScreenEdge();
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

    // ── Tasks ──

    private async void AddBtn_Click(object s, RoutedEventArgs e) => await AddTask();
    private async void InputBox_KeyDown(object s, KeyEventArgs e) { if (e.Key == Key.Enter) await AddTask(); }

    private async Task AddTask()
    {
        var text = InputBox.Text.Trim();
        if (string.IsNullOrEmpty(text)) return;
        await _mgr.AddAsync(text);
        InputBox.Clear();
    }

    private async void TaskCheckChanged(object s, RoutedEventArgs e)
    {
        if (s is CheckBox cb && cb.DataContext is TaskData task)
            await _mgr.ToggleCompleteAsync(task);
    }

    private async void EditBtn_Click(object s, RoutedEventArgs e)
    {
        if ((s as System.Windows.Controls.Button)?.DataContext is not TaskData task) return;
        var input = new InputDialog("编辑任务", task.Summary) { Owner = this };
        if (input.ShowDialog() == true && !string.IsNullOrWhiteSpace(input.Result))
        {
            task.Summary = input.Result;
            await _mgr.UpdateAsync(task);
        }
    }

    private async void DelBtn_Click(object s, RoutedEventArgs e)
    {
        if ((s as System.Windows.Controls.Button)?.DataContext is not TaskData task) return;
        await _mgr.DeleteAsync(task);
    }

    private void Window_PreviewMouseMove(object sender, MouseEventArgs e) { }

    // ── Right-click menus ──

    private void TaskRow_RightClick(object sender, MouseButtonEventArgs e)
    {
        if ((sender as Border)?.DataContext is not TaskData task) return;
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var (val, label) in new[] { (0, "无优先级"), (1, "高优先级"), (5, "中优先级"), (9, "低优先级") })
        {
            var item = new MenuItem { Header = label };
            item.Click += async (_, _) => await _mgr.SetPriorityAsync(task, val);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void TitleLabel_RightClick(object sender, MouseButtonEventArgs e)
    {
        var menu = new System.Windows.Controls.ContextMenu();
        foreach (var name in Themes.Keys)
        {
            var item = new MenuItem { Header = (name == _cfg.Theme ? "● " : "    ") + name };
            item.Click += (_, _) => { ApplyTheme(name); ConfigManager.Save(_cfg); };
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
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
            1 => System.Windows.Media.Color.FromRgb(0xe7, 0x4c, 0x3c),
            5 => System.Windows.Media.Color.FromRgb(0xf3, 0x9c, 0x12),
            9 => System.Windows.Media.Color.FromRgb(0x34, 0x98, 0xdb),
            _ => System.Windows.Media.Colors.Gray,
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
        var ok = new System.Windows.Controls.Button { Content = "确定", Width = 70, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => { Result = _tb.Text; DialogResult = true; };
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 70 };
        cancel.Click += (_, _) => { DialogResult = false; };
        btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
        sp.Children.Add(btnRow);
        Content = sp;
        Loaded += (_, _) => _tb.Focus();
    }
}

public class SettingsDialog : Window
{
    private readonly AppConfig _cfg;
    private readonly TextBox _urlBox, _userBox, _pwdBox;
    private readonly CheckBox _autoStartChk;
    private readonly ComboBox _themeCb;

    public SettingsDialog(AppConfig cfg)
    {
        _cfg = cfg;
        Title = "设置 — Desktop Todo"; Width = 420; Height = 440;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        WindowStyle = WindowStyle.ToolWindow; ResizeMode = ResizeMode.NoResize;

        var sp = new StackPanel { Margin = new Thickness(14) };
        sp.Children.Add(Label("Nextcloud URL:"));
        _urlBox = TextBox(cfg.NextcloudUrl); sp.Children.Add(_urlBox);
        sp.Children.Add(Label("用户名:"));
        _userBox = TextBox(cfg.Username); sp.Children.Add(_userBox);
        sp.Children.Add(Label("应用密码:"));
        _pwdBox = TextBox(cfg.AppPassword); sp.Children.Add(_pwdBox);
        sp.Children.Add(Label("主题:"));
        _themeCb = new ComboBox { ItemsSource = new[] { "暗黑", "亮白", "深蓝", "墨绿", "暗紫" } };
        _themeCb.SelectedItem = cfg.Theme;
        sp.Children.Add(_themeCb);
        _autoStartChk = new CheckBox { Content = "开机自启", IsChecked = cfg.AutoStart };
        sp.Children.Add(_autoStartChk);

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        var ok = new System.Windows.Controls.Button { Content = "保存", Width = 80, Margin = new Thickness(0, 0, 8, 0) };
        ok.Click += (_, _) => Save();
        var cancel = new System.Windows.Controls.Button { Content = "取消", Width = 80 };
        cancel.Click += (_, _) => DialogResult = false;
        btnRow.Children.Add(ok); btnRow.Children.Add(cancel);
        sp.Children.Add(btnRow);
        Content = sp;
    }

    private void Save()
    {
        _cfg.NextcloudUrl = _urlBox.Text.Trim();
        _cfg.Username = _userBox.Text.Trim();
        _cfg.AppPassword = _pwdBox.Text.Trim();
        _cfg.Theme = _themeCb.SelectedItem?.ToString() ?? "暗黑";
        _cfg.AutoStart = _autoStartChk.IsChecked ?? false;
        ConfigManager.Save(_cfg);
        SetAutoStart(_cfg.AutoStart);
        DialogResult = true;
    }

    private static void SetAutoStart(bool enable)
    {
        var startupDir = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var shortcut = System.IO.Path.Combine(startupDir, "DesktopTodo.lnk");
        var exe = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "DesktopTodo.exe");
        if (enable) { if (System.IO.File.Exists(exe)) System.IO.File.Copy(exe, shortcut, true); }
        else { if (System.IO.File.Exists(shortcut)) System.IO.File.Delete(shortcut); }
    }

    private static TextBlock Label(string t) => new() { Text = t, Margin = new Thickness(0, 8, 0, 2) };
    private static TextBox TextBox(string v) => new() { Text = v };
}
