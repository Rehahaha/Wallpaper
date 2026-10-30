using System.Diagnostics;
using Microsoft.Win32;
using Wallpaper.Core;

namespace Wallpaper.App;

/// <summary>开机自启：写 HKCU 的 Run 项（不需要管理员权限）。</summary>
public static class StartupManager
{
    private const string RunKeyPath = @"Software\\Microsoft\\Windows\\CurrentVersion\\Run";
    private const string ValueName = "Wallpaper";

    public static string ExecutablePath =>
        Environment.ProcessPath
        ?? Process.GetCurrentProcess().MainModule?.FileName
        ?? string.Empty;

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(ValueName) is string value && value.Length > 0;
        }
        catch (Exception ex)
        {
            Log.Warn("read autostart failed: " + ex.Message);
            return false;
        }
    }

    public static void SetEnabled(bool enabled)
    {
        try
        {
            if (enabled)
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
                var quote = ((char)34).ToString();
                key?.SetValue(ValueName, quote + ExecutablePath + quote + " --minimized");
                Log.Info("autostart enabled -> " + ExecutablePath);
            }
            else
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("autostart disabled");
            }
        }
        catch (Exception ex)
        {
            Log.Error("write autostart failed: " + ex.Message);
        }
    }
}
