using Wallpaper.Core;

namespace Wallpaper.Player;

public sealed class PlayerOptions
{
    /// <summary>静音播放（MVP 默认静音）。</summary>
    public bool Muted { get; init; } = true;

    /// <summary>音量 0-100（未静音时生效）。</summary>
    public int Volume { get; init; } = 70;

    /// <summary>画面适配模式。</summary>
    public FitMode Fit { get; init; } = FitMode.Cover;

    /// <summary>省电模式（mpv fast profile）。</summary>
    public bool PowerSaving { get; init; } = true;

    /// <summary>mpv 可执行文件路径；为空时自动查找。</summary>
    public string? MpvPath { get; init; }

    /// <summary>输出帧率上限，0 表示不限制。</summary>
    public int MaxFps { get; init; }
}

/// <summary>
/// 视频播放器抽象。MVP 用 mpv 实现；将来换成 Media Foundation / VLC 时只改这一层。
/// </summary>
public interface IVideoPlayer : IDisposable
{
    bool IsPlaying { get; }
    bool IsPaused { get; }

    /// <summary>当前素材时长（秒），未知时为 null。</summary>
    double? Duration { get; }

    /// <summary>当前播放的文件路径。</summary>
    string? CurrentFile { get; }

    event Action<double>? DurationChanged;

    /// <summary>在 host 窗口里播放指定文件。</summary>
    bool Play(string filePath, IntPtr host, PlayerOptions options);

    /// <summary>不重启进程直接换片（IPC loadfile）。失败返回 false，调用方可退回 Play。</summary>
    bool Switch(string filePath);

    /// <summary>运行中调整画面适配（IPC 属性，不重启）。</summary>
    void SetFit(FitMode fit);

    void Pause();
    void Resume();
    void Stop();

    /// <summary>设置音量 0-100。</summary>
    void SetVolume(int volume);

    /// <summary>静音开关。</summary>
    void SetMuted(bool muted);

    /// <summary>播放器异常（进程崩溃、文件无法解码等）。</summary>
    event Action<string>? Error;

    /// <summary>崩溃后自动重启了一次（参数为重试次数）。</summary>
    event Action<int>? Restarted;
}
