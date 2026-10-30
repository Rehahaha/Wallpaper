using System.Runtime.InteropServices;
using Wallpaper.Core;

namespace Wallpaper.DesktopHost;

/// <summary>
/// 一个隐藏的消息窗口，专门接收全局热键（WM_HOTKEY）。
/// 独立于主窗口，所以主界面收进托盘时热键依然有效。
/// </summary>
public sealed class HotkeyHost : IDisposable
{
    private const string ClassName = "WallpaperHotkeyHost";

    private static readonly object ClassGate = new();
    private static bool _classRegistered;
    private static Win32.WndProc? _procKeepAlive;

    /// <summary>Ctrl+Alt 组合，供调用方使用（Win32 常量不对外暴露）。</summary>
    public const uint CtrlAlt = Win32.MOD_CONTROL | Win32.MOD_ALT;

    private readonly Dictionary<int, (uint Modifiers, uint VirtualKey)> _registered = new();
    private IntPtr _hwnd;

    /// <summary>热键被按下（参数为注册时使用的 id）。</summary>
    public event Action<int>? Pressed;

    public bool Register(int id, uint modifiers, uint virtualKey, string label)
    {
        if (_hwnd == IntPtr.Zero) CreateWindow();

        if (!Win32.RegisterHotKey(_hwnd, id, modifiers, virtualKey))
        {
            Log.Warn($"register hotkey failed id={id} ({label}) err={Marshal.GetLastWin32Error()}");
            return false;
        }

        _registered[id] = (modifiers, virtualKey);
        Log.Info($"hotkey registered: {label}");
        return true;
    }

    private void CreateWindow()
    {
        EnsureClass();
        _hwnd = Win32.CreateWindowEx(
            Win32.WS_EX_TOOLWINDOW | Win32.WS_EX_NOACTIVATE,
            ClassName, "WallpaperHotkey",
            Win32.WS_POPUP,
            0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, Win32.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) Log.Error("hotkey window creation failed: " + Marshal.GetLastWin32Error());
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
            };

            var atom = Win32.RegisterClassEx(ref wc);
            if (atom == 0)
            {
                var err = Marshal.GetLastWin32Error();
                if (err != 1410) Log.Warn("hotkey RegisterClassEx failed: " + err);
            }

            _classRegistered = true;
        }
    }

    private static readonly Dictionary<IntPtr, HotkeyHost> Instances = new();

    private static IntPtr StaticWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        if (msg == Win32.WM_HOTKEY && Instances.TryGetValue(hWnd, out var host))
        {
            host.Pressed?.Invoke(wParam.ToInt32());
            return IntPtr.Zero;
        }

        return Win32.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    public HotkeyHost()
    {
        CreateWindow();
        if (_hwnd != IntPtr.Zero) Instances[_hwnd] = this;
    }

    public void Dispose()
    {
        foreach (var id in _registered.Keys)
        {
            Win32.UnregisterHotKey(_hwnd, id);
        }

        _registered.Clear();

        if (_hwnd != IntPtr.Zero)
        {
            Instances.Remove(_hwnd);
            Win32.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }
}
