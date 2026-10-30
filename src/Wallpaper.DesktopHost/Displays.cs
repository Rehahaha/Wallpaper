using System.Runtime.InteropServices;

namespace Wallpaper.DesktopHost;

/// <summary>一块显示器的物理矩形。</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
}

/// <summary>枚举显示器。多屏支持的基础。</summary>
public static class Displays
{
    public readonly record struct Display(int Index, PixelRect Bounds, bool IsPrimary);

    public static List<Display> All()
    {
        var list = new List<Display>();

        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMon, IntPtr hdc, ref Win32.RECT rect, IntPtr data) =>
        {
            var info = new Win32.MONITORINFO
            {
                cbSize = Marshal.SizeOf<Win32.MONITORINFO>(),
            };

            var isPrimary = Win32.GetMonitorInfo(hMon, ref info) && (info.dwFlags & 1) != 0;
            list.Add(new Display(
                list.Count,
                new PixelRect(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top),
                isPrimary));
            return true;
        }, IntPtr.Zero);

        return list;
    }
}
