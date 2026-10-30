using System.Diagnostics;
using System.Text.Json;

namespace Wallpaper.Player;

/// <summary>用 mpv 的 IPC 探测素材时长（加载但暂停，不渲染、不出声）。</summary>
public static class MediaProbe
{
    public static double? GetDuration(string file, string? mpvPath = null, int timeoutMs = 6000)
    {
        var mpv = mpvPath ?? MpvLocator.Find();
        if (mpv is null || !File.Exists(file)) return null;

        var pipeName = "wallpaper-probe-" + Guid.NewGuid().ToString("N")[..10];

        var psi = new ProcessStartInfo(mpv)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };

        foreach (var a in new[]
                 {
                     "--no-config",
                     "--vo=null",
                     "--ao=null",
                     "--pause=yes",
                     "--really-quiet",
                     "--input-ipc-server=" + MpvIpc.BuildPipePath(pipeName),
                     file,
                 })
        {
            psi.ArgumentList.Add(a);
        }

        Process? proc = null;
        try
        {
            proc = Process.Start(psi);
            if (proc is null) return null;

            using var ipc = MpvIpc.Connect(pipeName, 3000);
            if (ipc is null) return null;

            var deadline = Environment.TickCount64 + timeoutMs;
            while (Environment.TickCount64 < deadline)
            {
                var value = ipc.GetPropertyAsync("duration", 800).GetAwaiter().GetResult();
                if (value is { } v && v.ValueKind == JsonValueKind.Number)
                {
                    var seconds = v.GetDouble();
                    if (double.IsFinite(seconds) && seconds > 0) return seconds;
                }

                Thread.Sleep(150);
            }

            return null;
        }
        catch
        {
            return null;
        }
        finally
        {
            try
            {
                if (proc is { HasExited: false }) proc.Kill(entireProcessTree: true);
                proc?.Dispose();
            }
            catch
            {
                // 忽略清理失败
            }
        }
    }
}
