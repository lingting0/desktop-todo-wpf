using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace DesktopTodo;

internal sealed class DesktopWidgetLayer : IDisposable
{
    private readonly Window window;
    private readonly IntPtr handle;
    private readonly DispatcherTimer timer;
    private bool showingDesktop;
    private const uint PositionFlags = 0x0001 | 0x0002 | 0x0010 | 0x0200;

    internal DesktopWidgetLayer(Window window, IntPtr handle)
    {
        this.window = window;
        this.handle = handle;
        timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(220) };
        timer.Tick += Refresh;
        window.IsVisibleChanged += OnVisibilityChanged;
        Start();
    }

    private void Start()
    {
        if (!window.IsVisible) return;
        Refresh(null, EventArgs.Empty);
        timer.Start();
    }

    private void OnVisibilityChanged(object sender, DependencyPropertyChangedEventArgs args)
    {
        if (window.IsVisible) Start();
        else { timer.Stop(); ReleaseDesktopPriority(); }
    }

    private void Refresh(object? sender, EventArgs args)
    {
        if (!window.IsVisible) return;
        var shell = GetShellWindow();
        if (shell == IntPtr.Zero) return;
        var host = shell;
        if (FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null) == IntPtr.Zero)
        {
            GetWindowThreadProcessId(shell, out var shellProcess);
            host = IntPtr.Zero;
            while ((host = FindWindowEx(IntPtr.Zero, host, "WorkerW", null)) != IntPtr.Zero)
            {
                GetWindowThreadProcessId(host, out var hostProcess);
                if (hostProcess == shellProcess && IsWindowVisible(host) &&
                    FindWindowEx(host, IntPtr.Zero, "SHELLDLL_DefView", null) != IntPtr.Zero) break;
            }
        }
        if (host == IntPtr.Zero || !IsWindowVisible(host)) return;
        var foreground = GetForegroundWindow();
        bool desktopActive = foreground == host || foreground == shell;
        GetWindowThreadProcessId(foreground, out var foregroundProcess);
        if (!desktopActive && foregroundProcess != 0)
        {
            GetWindowThreadProcessId(shell, out var shellProcess);
            if (foregroundProcess == shellProcess)
            {
                var className = new StringBuilder(32);
                GetClassName(foreground, className, className.Capacity);
                desktopActive = className.ToString() == "WorkerW";
            }
        }
        if (IsIconic(handle)) ShowWindow(handle, 4);
        if (desktopActive)
        {
            if (!showingDesktop)
                showingDesktop = SetWindowPos(handle, new IntPtr(-1), 0, 0, 0, 0, PositionFlags);
        }
        else if (foregroundProcess != Environment.ProcessId)
        {
            ReleaseDesktopPriority();
            var aboveDesktop = GetWindow(host, 3);
            if (aboveDesktop != handle)
                SetWindowPos(handle, aboveDesktop, 0, 0, 0, 0, PositionFlags);
        }
    }

    private void ReleaseDesktopPriority()
    {
        if (showingDesktop) SetWindowPos(handle, new IntPtr(-2), 0, 0, 0, 0, PositionFlags);
        showingDesktop = false;
    }

    public void Dispose()
    {
        timer.Stop();
        ReleaseDesktopPriority();
        timer.Tick -= Refresh;
        window.IsVisibleChanged -= OnVisibilityChanged;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetShellWindow();
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder name, int size);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr window);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string className, string? title);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr window);
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr window, int command);
    [DllImport("user32.dll")] private static extern IntPtr GetWindow(IntPtr window, uint command);
}
