using System.Diagnostics;
using System.Text.Json;
using Wallpaper.Core;

namespace Wallpaper.Player;

/// <summary>
/// 用 mpv 子进程渲染到宿主窗口（--wid），通过 JSON IPC 做实时控制。
/// 实测：壁纸层不允许 GDI 绘制（Progman 带 WS_EX_NOREDIRECTIONBITMAP），
/// 只有 mpv 的 D3D 交换链能真正上屏。
/// </summary>
public sealed class MpvPlayer : IVideoPlayer
{
    private const int MaxRestarts = 3;

    private Process? _process;
    private MpvIpc? _ipc;
    private bool _stopping;

    private string? _lastFile;
    private IntPtr _lastHost;
    private PlayerOptions? _lastOptions;
    private int _restarts;

    public bool IsPlaying => _process is { HasExited: false };

    public bool IsPaused { get; private set; }

    public double? Duration { get; private set; }

    public string? CurrentFile => _lastFile;

    public event Action<double>? DurationChanged;
    public event Action<string>? Error;
    public event Action<int>? Restarted;

    public bool Play(string filePath, IntPtr host, PlayerOptions options)
    {
        Stop();

        _lastFile = filePath;
        _lastHost = host;
        _lastOptions = options;
        _restarts = 0;

        return Start();
    }

    /// <summary>真正拉起 mpv 进程（首次播放与崩溃重启共用）。</summary>
    private bool Start()
    {
        if (_lastFile is null || _lastOptions is null) return false;

        if (!File.Exists(_lastFile))
        {
            Fail("文件不存在：" + _lastFile);
            return false;
        }

        var mpv = _lastOptions.MpvPath ?? MpvLocator.Find();
        if (mpv is null || !File.Exists(mpv))
        {
            Fail("找不到 mpv.exe，请把它放到 lib/mpv/ 目录下");
            return false;
        }

        var pipeName = "wallpaper-mpv-" + Guid.NewGuid().ToString("N")[..10];
        var args = BuildArgs(_lastFile, _lastHost, _lastOptions, pipeName);

        var psi = new ProcessStartInfo(mpv)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            _stopping = false;
            Duration = null;
            IsPaused = false;

            _process = new Process { StartInfo = psi, EnableRaisingEvents = true };
            _process.ErrorDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("[mpv] " + e.Data);
            };
            _process.OutputDataReceived += (_, e) =>
            {
                if (!string.IsNullOrWhiteSpace(e.Data)) Log.Info("[mpv-out] " + e.Data);
            };
            _process.Exited += OnProcessExited;
            _process.Start();
            _process.BeginErrorReadLine();
            _process.BeginOutputReadLine();

            Log.Info($"mpv started pid={_process.Id} file={Path.GetFileName(_lastFile)} host=0x{_lastHost:X} powerSaving={_lastOptions.PowerSaving}");

            _ipc = MpvIpc.Connect(pipeName, 5000);
            if (_ipc is not null)
            {
                _ipc.PropertyChanged += OnPropertyChanged;
                _ipc.Observe("duration", 1);
                _ipc.Observe("pause", 2);
            }

            return true;
        }
        catch (Exception ex)
        {
            Fail("启动 mpv 失败：" + ex.Message);
            _process = null;
            return false;
        }
    }

    private static List<string> BuildArgs(string file, IntPtr host, PlayerOptions options, string pipeName)
    {
        var args = new List<string>
        {
            "--no-config",
            "--wid=" + host.ToInt64(),
            "--input-ipc-server=" + MpvIpc.BuildPipePath(pipeName),
            "--loop-file=inf",
            "--no-osc",
            "--no-osd-bar",
            "--osd-level=0",
            "--no-input-default-bindings",
            "--no-border",
            "--really-quiet",
            "--hwdec=d3d11va,d3d11va-copy,auto-safe",
            "--mute=" + (options.Muted ? "yes" : "no"),
            "--volume=" + Math.Clamp(options.Volume, 0, 100),
        };

        // 省电模式：fast profile 实测 4K60 上 CPU 降约 37%、内存降约 27%
        if (options.PowerSaving) args.Add("--profile=fast");

        switch (options.Fit)
        {
            case FitMode.Cover:
                args.Add("--panscan=1.0");
                break;
            case FitMode.Contain:
                args.Add("--panscan=0.0");
                break;
            case FitMode.Stretch:
                args.Add("--keepaspect=no");
                break;
        }

        if (options.MaxFps > 0) args.Add("--display-fps-override=" + options.MaxFps);

        args.Add(file);
        return args;
    }

    /// <summary>不重启进程直接换片。</summary>
    public bool Switch(string filePath)
    {
        if (_ipc is null || !IsPlaying || !File.Exists(filePath)) return false;

        _ipc.Command("loadfile", filePath, "replace");
        _ipc.SetProperty("pause", false);
        _lastFile = filePath;
        Duration = null;
        IsPaused = false;
        Log.Info("switch file (no restart): " + Path.GetFileName(filePath));
        return true;
    }

    public void SetFit(FitMode fit)
    {
        if (_ipc is null) return;

        switch (fit)
        {
            case FitMode.Cover:
                _ipc.SetProperty("keepaspect", "yes");
                _ipc.SetProperty("panscan", 1.0);
                break;
            case FitMode.Contain:
                _ipc.SetProperty("keepaspect", "yes");
                _ipc.SetProperty("panscan", 0.0);
                break;
            case FitMode.Stretch:
                _ipc.SetProperty("panscan", 0.0);
                _ipc.SetProperty("keepaspect", "no");
                break;
        }
    }

    private void OnPropertyChanged(string name, JsonElement? data)
    {
        if (name == "duration" && data is { } d && d.ValueKind == JsonValueKind.Number)
        {
            var seconds = d.GetDouble();
            if (double.IsFinite(seconds) && seconds > 0 && Math.Abs((Duration ?? -1) - seconds) > 0.01)
            {
                Duration = seconds;
                DurationChanged?.Invoke(seconds);
            }
        }
        else if (name == "pause" && data is { } p)
        {
            IsPaused = p.ValueKind == JsonValueKind.True;
        }
    }

    private void OnProcessExited(object? sender, EventArgs e)
    {
        var code = -1;
        try { code = _process?.ExitCode ?? -1; } catch { }

        _ipc?.Dispose();
        _ipc = null;

        if (_stopping)
        {
            Log.Info("mpv exited (expected) code=" + code);
            return;
        }

        Log.Warn("mpv exited unexpectedly code=" + code);
        ScheduleRestart();
    }

    /// <summary>崩溃后自动重启（1s / 2s / 3s 退避，最多 3 次）。</summary>
    private void ScheduleRestart()
    {
        if (_restarts >= MaxRestarts)
        {
            Fail($"播放器连续崩溃 {MaxRestarts} 次，已停止重试");
            return;
        }

        _restarts++;
        var attempt = _restarts;

        _ = Task.Run(async () =>
        {
            await Task.Delay(1000 * attempt).ConfigureAwait(false);
            if (_stopping || _lastFile is null) return;

            Log.Warn($"restarting mpv (attempt {attempt}/{MaxRestarts})");
            if (Start()) Restarted?.Invoke(attempt);
        });
    }

    public void Pause()
    {
        if (_ipc is null) return;
        _ipc.SetProperty("pause", true);
        IsPaused = true;
        Log.Info("paused");
    }

    public void Resume()
    {
        if (_ipc is null) return;
        _ipc.SetProperty("pause", false);
        IsPaused = false;
        Log.Info("resumed");
    }

    public void SetVolume(int volume) => _ipc?.SetProperty("volume", Math.Clamp(volume, 0, 100));

    public void SetMuted(bool muted) => _ipc?.SetProperty("mute", muted);

    public void Stop()
    {
        var proc = _process;
        _process = null;

        IsPaused = false;
        Duration = null;

        _ipc?.Dispose();
        _ipc = null;

        _stopping = true;
        _lastFile = null;
        _lastHost = IntPtr.Zero;
        _lastOptions = null;
        _restarts = 0;

        if (proc is null) return;

        try
        {
            if (!proc.HasExited) proc.Kill(entireProcessTree: true);
            proc.WaitForExit(3000);
        }
        catch (Exception ex)
        {
            Log.Warn("stop mpv failed: " + ex.Message);
        }
        finally
        {
            proc.Dispose();
        }
    }

    private void Fail(string message)
    {
        Log.Error(message);
        Error?.Invoke(message);
    }

    public void Dispose() => Stop();
}
