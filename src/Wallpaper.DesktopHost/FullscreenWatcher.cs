using Wallpaper.Core;

namespace Wallpaper.DesktopHost;

/// <summary>
/// 检测前台是否有全屏应用（游戏、全屏视频、演示）。有就通知上层暂停壁纸，
/// 避免壁纸在后台继续解码、抢 GPU。
/// </summary>
public sealed class FullscreenWatcher : IDisposable
{
    private readonly System.Threading.Timer _timer;
    private readonly int _enterThreshold;
    private readonly int _exitThreshold;

    private int _hits;
    private int _misses;
    private bool _isFullscreen;

    /// <summary>全屏状态变化（true = 进入全屏）。</summary>
    public event Action<bool>? FullscreenChanged;

    public bool IsFullscreen => _isFullscreen;

    public FullscreenWatcher(int intervalMs = 1000, int enterThreshold = 2, int exitThreshold = 2)
    {
        _enterThreshold = Math.Max(1, enterThreshold);
        _exitThreshold = Math.Max(1, exitThreshold);
        _timer = new System.Threading.Timer(Tick, null, 500, intervalMs);
    }

    private void Tick(object? state)
    {
        bool now;
        try
        {
            now = IsFullscreenForeground();
        }
        catch
        {
            return;
        }

        if (!_isFullscreen)
        {
            _hits = now ? _hits + 1 : 0;
            if (_hits >= _enterThreshold)
            {
                _isFullscreen = true;
                _hits = 0;
                _misses = 0;
                Log.Info("fullscreen detected -> pause wallpaper");
                FullscreenChanged?.Invoke(true);
            }
        }
        else
        {
            _misses = now ? 0 : _misses + 1;
            if (_misses >= _exitThreshold)
            {
                _isFullscreen = false;
                _hits = 0;
                _misses = 0;
                Log.Info("fullscreen ended -> resume wallpaper");
                FullscreenChanged?.Invoke(false);
            }
        }
    }

    /// <summary>判断当前前台窗口是否是覆盖整个显示器的应用窗口。</summary>
    public static bool IsFullscreenForeground()
    {
        var hwnd = Win32.GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return false;

        Win32.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid == (uint)Environment.ProcessId) return false;   // 我们自己

        var className = ClassNameOf(hwnd);
        // 桌面/任务栏不算"应用"
        if (className is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow")
            return false;

        if (!Win32.IsWindowVisible(hwnd) || Win32.IsIconic(hwnd)) return false;

        var monitor = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        if (!Win32.GetMonitorInfo(monitor, ref info)) return false;
        if (!Win32.GetWindowRect(hwnd, out var rect)) return false;

        // 注意：必须用"近似等于"而不是"覆盖"。
        // 最大化窗口的矩形会**超出**显示器边界（Windows 给边框留的透明边，200% 缩放下约 14px），
        // 用"覆盖"判断会把普通最大化的浏览器误判成全屏，导致壁纸无故暂停。
        const int tolerance = 4;
        return Math.Abs(rect.Left - info.rcMonitor.Left) <= tolerance
               && Math.Abs(rect.Top - info.rcMonitor.Top) <= tolerance
               && Math.Abs(rect.Right - info.rcMonitor.Right) <= tolerance
               && Math.Abs(rect.Bottom - info.rcMonitor.Bottom) <= tolerance;
    }

    private static string ClassNameOf(IntPtr hwnd)
    {
        var sb = new System.Text.StringBuilder(256);
        Win32.GetClassName(hwnd, sb, sb.Capacity);
        return sb.ToString();
    }

    public void Dispose() => _timer.Dispose();
}
