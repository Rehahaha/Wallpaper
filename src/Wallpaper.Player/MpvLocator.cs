using Wallpaper.Core;

namespace Wallpaper.Player;

/// <summary>定位 mpv.exe：优先随包的 lib/mpv，其次 PATH。</summary>
public static class MpvLocator
{
    public static string? Find()
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "lib", "mpv", "mpv.exe"),
            Path.Combine(AppContext.BaseDirectory, "mpv.exe"),
        };

        foreach (var path in candidates)
        {
            if (File.Exists(path)) return path;
        }

        var fromPath = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var dir in fromPath.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(dir.Trim(), "mpv.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // 忽略非法 PATH 项
            }
        }

        Log.Warn("mpv.exe not found");
        return null;
    }
}
