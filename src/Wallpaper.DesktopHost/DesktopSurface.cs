using System.Runtime.InteropServices;
using Wallpaper.Core;

namespace Wallpaper.DesktopHost;

/// <summary>
/// 把窗口挂到桌面图标层之下（Progman / WorkerW）。
/// M0 阶段窗口内容是一个棋盘格，用来验证挂载是否成功；
/// M1 会把内容换成 mpv 渲染的独立子窗口。
/// </summary>
public sealed class DesktopSurface : IDisposable
{
    private const string ClassName = "WallpaperDesktopSurface";
    private const int TileSize = 160;

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static Win32.WndProc? _procKeepAlive;

    private readonly PixelRect? _bounds;
    private IntPtr _hwnd;
    private IntPtr _host;
    private bool _disposed;

    /// <summary>不指定区域时铺满主显示器。</summary>
    public DesktopSurface() : this(null)
    {
    }

    /// <summary>在指定显示器区域上创建壁纸宿主窗口（多屏时每屏一个）。</summary>
    public DesktopSurface(PixelRect? bounds)
    {
        _bounds = bounds;
    }

    public bool IsAttached => _hwnd != IntPtr.Zero;
    public IntPtr Handle => _hwnd;
    public IntPtr HostHandle => _host;

    /// <summary>挂载策略，用于在不同 Windows 版本上试验。</summary>
    public enum AttachMode
    {
        /// <summary>挂到 Progman 的子 WorkerW（Win11 26200 实测的正确壁纸层）。</summary>
        WorkerWChild,

        /// <summary>挂到 Progman，并压到 z 序最底部（图标在上）。</summary>
        ProgmanBottom,

        /// <summary>挂到 Progman，置于 z 序顶部（会盖住图标，仅用于排查）。</summary>
        ProgmanTop,

        /// <summary>不挂桌面，作为普通顶层窗口显示（用于隔离"画不出来"的排查）。</summary>
        Standalone,

        /// <summary>先建普通窗口，再 SetParent 到 Progman 并压到底部（Lively 的做法）。</summary>
        SetParentProgmanBottom,

        /// <summary>先建普通窗口，再 SetParent 到 WorkerW（子窗口版）。</summary>
        SetParentWorkerW,
    }

    /// <summary>挂载到桌面层，返回是否成功。</summary>
    public bool Attach() => Attach(AttachMode.WorkerWChild);

    public bool Attach(AttachMode mode)
    {
        if (_hwnd != IntPtr.Zero) return true;

        var progman = Win32.FindWindow("Progman", null);
        _host = mode switch
        {
            AttachMode.WorkerWChild => FindWallpaperHost(),
            AttachMode.ProgmanBottom => progman,
            AttachMode.ProgmanTop => progman,
            AttachMode.Standalone => IntPtr.Zero,
            AttachMode.SetParentProgmanBottom => progman,
            AttachMode.SetParentWorkerW => FindWallpaperHost(),
            _ => FindWallpaperHost(),
        };

        if (_host == IntPtr.Zero && mode != AttachMode.Standalone)
        {
            Log.Error("worker/progman not found");
            return false;
        }

        var useSetParent = mode is AttachMode.SetParentProgmanBottom or AttachMode.SetParentWorkerW;

        EnsureClass();

        var (w, h) = Size();
        var style = useSetParent
            ? Win32.WS_POPUP | Win32.WS_VISIBLE
            : mode == AttachMode.Standalone
                ? Win32.WS_POPUP | Win32.WS_VISIBLE
                : Win32.WS_CHILD | Win32.WS_VISIBLE | Win32.WS_CLIPCHILDREN | Win32.WS_CLIPSIBLINGS;

        var parentAtCreate = useSetParent ? IntPtr.Zero : _host;

        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_NOACTIVATE | Win32.WS_EX_TOOLWINDOW,
            ClassName, "Wallpaper",
            style,
            0, 0, w, h, parentAtCreate, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Log.Error("CreateWindowEx failed: " + Marshal.GetLastWin32Error());
            _host = IntPtr.Zero;
            return false;
        }

        if (useSetParent)
        {
            var sp = Win32.SetParent(_hwnd, _host);
            Log.Info($"SetParent -> 0x{_host:X} ok={sp != IntPtr.Zero} err={Marshal.GetLastWin32Error()}");
        }

        Win32.MoveWindow(_hwnd, OffsetX, OffsetY, w, h, true);
        Win32.ShowWindow(_hwnd, Win32.SW_SHOW);

        if (mode is AttachMode.ProgmanTop or AttachMode.ProgmanBottom or AttachMode.SetParentProgmanBottom)
        {
            var insertAfter = mode == AttachMode.ProgmanTop ? Win32.HWND_TOP : Win32.HWND_BOTTOM;
            Win32.SetWindowPos(_hwnd, insertAfter, 0, 0, 0, 0,
                Win32.SWP_NOMOVE | Win32.SWP_NOSIZE | Win32.SWP_NOACTIVATE);
        }

        Log.Info($"attached mode={mode} hwnd=0x{_hwnd:X} host=0x{_host:X} size={w}x{h}");
        return true;
    }

    public void Detach()
    {
        if (_hwnd == IntPtr.Zero) return;
        Win32.DestroyWindow(_hwnd);
        _hwnd = IntPtr.Zero;
        _host = IntPtr.Zero;
        Log.Info("detached");
    }

    /// <summary>屏幕分辨率变化后重新铺满。</summary>
    public void ResizeToPrimaryScreen()
    {
        if (_hwnd == IntPtr.Zero) return;
        var (w, h) = Size();
        Win32.MoveWindow(_hwnd, OffsetX, OffsetY, w, h, true);
    }

    /// <summary>调试用：打印窗口的父级链，确认确实挂在 WorkerW 之下。</summary>
    public string Describe()
    {
        if (_hwnd == IntPtr.Zero) return "not attached";

        var sb = new System.Text.StringBuilder();
        var myStyle = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_STYLE).ToInt64();
        var myEx = Win32.GetWindowLongPtr(_hwnd, Win32.GWL_EXSTYLE).ToInt64();

        sb.Append("hwnd=0x").Append(_hwnd.ToString("X"))
          .Append(" visible=").Append(Win32.IsWindowVisible(_hwnd))
          .Append(" paintCount=").Append(PaintCount)
          .Append(" style=0x").Append(myStyle.ToString("X"))
          .Append(" exstyle=0x").Append(myEx.ToString("X"));

        if (_host != IntPtr.Zero)
        {
            var hostEx = Win32.GetWindowLongPtr(_host, Win32.GWL_EXSTYLE).ToInt64();
            sb.Append(Environment.NewLine)
              .Append("  host exstyle=0x").Append(hostEx.ToString("X"))
              .Append(" norendirection=")
              .Append((hostEx & Win32.WS_EX_NOREDIRECTIONBITMAP) != 0);
        }

        var node = _hwnd;
        for (var i = 0; i < 6 && node != IntPtr.Zero; i++)
        {
            var cls = new System.Text.StringBuilder(256);
            Win32.GetClassName(node, cls, cls.Capacity);
            sb.Append(Environment.NewLine)
              .Append("  parent[").Append(i).Append("] = ")
              .Append(cls.ToString()).Append(" (0x").Append(node.ToString("X")).Append(')');
            node = Win32.GetParent(node);
        }

        return sb.ToString();
    }

    public static (int Width, int Height) PrimaryScreenSize() =>
        (Win32.GetSystemMetrics(Win32.SM_CXSCREEN), Win32.GetSystemMetrics(Win32.SM_CYSCREEN));

    private int OffsetX => _bounds?.X ?? 0;
    private int OffsetY => _bounds?.Y ?? 0;

    private (int Width, int Height) Size() =>
        _bounds is { } b ? (b.Width, b.Height) : PrimaryScreenSize();

    /// <summary>
    /// 找到承载桌面壁纸的窗口。实测（Windows 11 26200）：
    /// 向 Progman 发送 0x052C 后，壁纸层是 **Progman 的子窗口 WorkerW**（全屏、无 SHELLDLL_DefView）。
    /// 老版本 Windows 则是顶层窗口 WorkerW（与 Progman 同级）。两种都试，最后退回 Progman。
    /// </summary>
    public static IntPtr FindWallpaperHost()
    {
        var progman = Win32.FindWindow("Progman", null);
        if (progman == IntPtr.Zero)
        {
            Log.Error("Progman not found");
            return IntPtr.Zero;
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                new IntPtr(0xD), IntPtr.Zero, Win32.SMTO_NORMAL, 1000, out _);
            Win32.SendMessageTimeout(progman, Win32.WM_SPAWN_WORKER,
                new IntPtr(0xD), new IntPtr(1), Win32.SMTO_NORMAL, 1000, out _);

            var worker = FindWorkerWChildOf(progman) ?? FindTopLevelWorkerW();
            if (worker is { } w && w != IntPtr.Zero)
            {
                Log.Info($"WorkerW found on attempt {attempt + 1}: 0x{w:X}");
                return w;
            }

            Thread.Sleep(150);
        }

        Log.Warn("WorkerW not found, fallback to Progman (may cover desktop icons)");
        return progman;
    }

    /// <summary>Progman 的子 WorkerW：全屏、且内部没有 SHELLDLL_DefView（那个是图标层）。</summary>
    private static IntPtr? FindWorkerWChildOf(IntPtr progman)
    {
        var child = Win32.FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
        var (sw, sh) = PrimaryScreenSize();

        while (child != IntPtr.Zero)
        {
            var defView = Win32.FindWindowEx(child, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero && Win32.IsWindowVisible(child))
            {
                Win32.GetWindowRect(child, out var rect);
                var coversScreen = rect.Right - rect.Left >= sw && rect.Bottom - rect.Top >= sh;
                if (coversScreen) return child;
            }

            child = Win32.FindWindowEx(progman, child, "WorkerW", null);
        }

        return null;
    }

    /// <summary>老式做法：顶层 WorkerW 中，与含 SHELLDLL_DefView 的窗口同级的那个。</summary>
    private static IntPtr FindTopLevelWorkerW()
    {
        IntPtr result = IntPtr.Zero;
        Win32.EnumWindows((top, _) =>
        {
            var defView = Win32.FindWindowEx(top, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView == IntPtr.Zero) return true;

            var worker = Win32.FindWindowEx(IntPtr.Zero, top, "WorkerW", null);
            if (worker == IntPtr.Zero) return true;

            result = worker;
            return false;
        }, IntPtr.Zero);
        return result;
    }

    private static void EnsureClass()
    {
        lock (ClassGate)
        {
            if (_classRegistered) return;

            _procKeepAlive = StaticWndProc;
            var wc = new Win32.WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<Win32.WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_procKeepAlive),
                hInstance = Win32.GetModuleHandle(null),
                lpszClassName = ClassName,
                style = 0,
            };

            var atom = Win32.RegisterClassEx(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                // 1410 = 类已存在，可以忽略
                if (err != 1410) Log.Warn("RegisterClassEx failed: " + err);
            }

            _classRegistered = true;
        }
    }

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case Win32.WM_ERASEBKGND:
                Paint(hWnd, wParam);
                return new IntPtr(1);

            case Win32.WM_PAINT:
            {
                var ps = Win32.BeginPaint(hWnd, out var paint);
                Paint(hWnd, paint.hdc);
                Win32.EndPaint(hWnd, ref paint);
                return IntPtr.Zero;
            }

            case Win32.WM_DISPLAYCHANGE:
            {
                var (w, h) = PrimaryScreenSize();
                Win32.MoveWindow(hWnd, 0, 0, w, h, true);
                return IntPtr.Zero;
            }
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    /// <summary>WM_PAINT 被调用的次数，用于判断"窗口到底有没有在画"。</summary>
    public static int PaintCount;

    /// <summary>M0 测试画面：棋盘格。换成视频后这里不再需要。</summary>
    private static void Paint(IntPtr hWnd, IntPtr hdc)
    {
        PaintCount++;
        if (!Win32.GetClientRect(hWnd, out var rect)) return;

        var brushA = Win32.CreateSolidBrush(0x00FF00FF); // 品红 (BGR)
        var brushB = Win32.CreateSolidBrush(0x00FFFF00); // 青   (BGR)

        try
        {
            for (var y = rect.Top; y < rect.Bottom; y += TileSize)
            {
                for (var x = rect.Left; x < rect.Right; x += TileSize)
                {
                    var cell = new Win32.RECT
                    {
                        Left = x,
                        Top = y,
                        Right = Math.Min(x + TileSize, rect.Right),
                        Bottom = Math.Min(y + TileSize, rect.Bottom),
                    };

                    var dark = ((x / TileSize) + (y / TileSize)) % 2 == 0;
                    Win32.FillRect(hdc, ref cell, dark ? brushA : brushB);
                }
            }
        }
        finally
        {
            Win32.DeleteObject(brushA);
            Win32.DeleteObject(brushB);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Detach();
    }
}
