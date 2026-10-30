using System.Diagnostics;
using Wallpaper.Core;

namespace Wallpaper.Player;

/// <summary>用 mpv 抽一帧做缩略图（--of=image2 编码单帧）。</summary>
public static class Thumbnailer
{
    public static string? Create(string videoPath, string outputPath, string? mpvPath = null, double atSeconds = 2.0)
    {
        var mpv = mpvPath ?? MpvLocator.Find();
        if (mpv is null) return null;

        try
        {
            var dir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var psi = new ProcessStartInfo(mpv)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
            };
            psi.ArgumentList.Add("--no-config");
            psi.ArgumentList.Add("--really-quiet");
            psi.ArgumentList.Add("--frames=1");
            psi.ArgumentList.Add("--start=" + atSeconds.ToString("0.###"));
            psi.ArgumentList.Add("--no-audio");
            psi.ArgumentList.Add("--o=" + outputPath);
            psi.ArgumentList.Add("--of=image2");
            psi.ArgumentList.Add("--ovc=png");
            psi.ArgumentList.Add(videoPath);

            using var proc = Process.Start(psi);
            if (proc is null) return null;
            proc.WaitForExit(15000);

            return File.Exists(outputPath) ? outputPath : null;
        }
        catch (Exception ex)
        {
            Log.Warn("thumbnail failed: " + ex.Message);
            return null;
        }
    }
}
