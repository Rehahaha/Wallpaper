namespace Wallpaper.Core;

/// <summary>画面适配模式。</summary>
public enum FitMode
{
    Cover,
    Contain,
    Stretch,
}

public sealed class AppConfig
{
    public int Version { get; set; } = 1;

    /// <summary>当前使用的素材 Id；null 表示未启用壁纸。</summary>
    public string? ActiveId { get; set; }

    public FitMode FitMode { get; set; } = FitMode.Cover;

    public bool Muted { get; set; } = true;

    /// <summary>音量 0-100（未静音时生效）。</summary>
    public int Volume { get; set; } = 70;

    public bool AutoStart { get; set; }

    /// <summary>检测到全屏应用时自动暂停壁纸。</summary>
    public bool PauseOnFullscreen { get; set; } = true;

    /// <summary>省电模式：用 mpv 的 fast profile（画面略降，CPU/内存明显降）。</summary>
    public bool PowerSaving { get; set; } = true;

    /// <summary>在所有显示器上播放（关闭则只覆盖主屏）。</summary>
    public bool SpanAllMonitors { get; set; } = true;

    /// <summary>自动轮播：按间隔切换素材。</summary>
    public bool RotationEnabled { get; set; }

    /// <summary>轮播间隔（秒），默认 15 分钟。</summary>
    public int RotationIntervalSeconds { get; set; } = 900;

    /// <summary>轮播时随机顺序。</summary>
    public bool RotationShuffle { get; set; }

    public List<WallpaperItem> Library { get; set; } = new();
}
