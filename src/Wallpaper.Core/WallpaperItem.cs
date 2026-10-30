using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Wallpaper.Core;

/// <summary>素材库中的一项。只保存路径引用，不复制视频文件。</summary>
public sealed class WallpaperItem : INotifyPropertyChanged
{
    private double _durationSec;

    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    public string Path { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    private string? _thumb;
    public string? Thumb
    {
        get => _thumb;
        set
        {
            if (_thumb == value) return;
            _thumb = value;
            OnPropertyChanged();
        }
    }
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.Now;

    /// <summary>
    /// 时长（秒）。由 mpv 的 duration 事件回填。
    /// 走 INotifyPropertyChanged 通知 UI，**不要**用 Items.Refresh()（会触发 WPF 虚拟化面板的空引用崩溃）。
    /// </summary>
    public double DurationSec
    {
        get => _durationSec;
        set
        {
            if (Math.Abs(_durationSec - value) < 0.01) return;
            _durationSec = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DurationText));
        }
    }

    [JsonIgnore]
    public bool FileMissing => !File.Exists(Path);

    private bool _isActive;

    /// <summary>是否是当前正在使用的壁纸（列表里显示"使用中"徽标）。</summary>
    [JsonIgnore]
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            OnPropertyChanged();
        }
    }

    /// <summary>列表里显示的时长文本。</summary>
    [JsonIgnore]
    public string DurationText
    {
        get
        {
            if (DurationSec <= 0) return "时长未知";
            var ts = TimeSpan.FromSeconds(DurationSec);
            return ts.Minutes.ToString("00") + ":" + ts.Seconds.ToString("00");
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public static WallpaperItem FromFile(string path) => new()
    {
        Path = path,
        Name = System.IO.Path.GetFileName(path),
    };
}
