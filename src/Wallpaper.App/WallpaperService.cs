using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Wallpaper.Core;
using Wallpaper.DesktopHost;
using Wallpaper.Player;

namespace Wallpaper.App;

/// <summary>把「配置 + 桌面层 + 播放器」粘在一起的应用服务。多显示器时每块屏一个会话。</summary>
public sealed class WallpaperService : IDisposable
{
    private static readonly string[] SupportedExtensions = { ".mp4", ".webm", ".m4v", ".mkv" };

    /// <summary>调试用：把主屏切成 N 块虚拟显示器（用于单屏环境下验证多屏逻辑）。</summary>
    public static int SimulateMonitors { get; set; }

    private sealed class Session : IDisposable
    {
        public required DesktopSurface Surface { get; init; }
        public required MpvPlayer Player { get; init; }

        public void Dispose()
        {
            Player.Dispose();
            Surface.Dispose();
        }
    }

    private readonly ConfigStore _store = new();
    private readonly AppConfig _config;
    private readonly FullscreenWatcher _fullscreen = new();
    private readonly List<Session> _sessions = new();
    private System.Threading.Timer? _rotation;
    private bool _rotating;
    private bool _autoPaused;
    private bool _manualPaused;

    public WallpaperService()
    {
        _config = _store.Load();
        Items = new ObservableCollection<WallpaperItem>(_config.Library);
        _fullscreen.FullscreenChanged += OnFullscreenChanged;
    }

    public ObservableCollection<WallpaperItem> Items { get; }

    public WallpaperItem? Active { get; private set; }

    /// <summary>主屏播放器（状态显示用）。</summary>
    private IVideoPlayer? Primary => _sessions.Count > 0 ? _sessions[0].Player : null;

    private IEnumerable<IVideoPlayer> Players => _sessions.Select(s => (IVideoPlayer)s.Player);

    // ---------- 会话（每块显示器一个） ----------

    private void EnsureSessions()
    {
        if (_sessions.Count > 0) return;

        var displays = BuildDisplays();
        foreach (var display in displays)
        {
            var surface = new DesktopSurface(display.Bounds);
            var player = new MpvPlayer();

            player.Error += message => OnUi(() => PlaybackFailed?.Invoke(message));
            player.Restarted += attempt => OnUi(() => PlaybackRestarted?.Invoke(attempt));

            _sessions.Add(new Session { Surface = surface, Player = player });
        }

        // 只在主屏回填时长，避免多屏重复写
        if (_sessions.Count > 0)
        {
            _sessions[0].Player.DurationChanged += seconds =>
            {
                if (Active is null) return;
                Active.DurationSec = seconds;
                Save();
                OnUi(() => Saved?.Invoke());
            };
        }

        Log.Info($"sessions created: {_sessions.Count} (simulate={SimulateMonitors})");
    }

    private List<Displays.Display> BuildDisplays()
    {
        if (SimulateMonitors > 1)
        {
            var (w, h) = DesktopSurface.PrimaryScreenSize();
            var width = w / SimulateMonitors;
            var fake = new List<Displays.Display>();
            for (var i = 0; i < SimulateMonitors; i++)
            {
                var last = i == SimulateMonitors - 1;
                fake.Add(new Displays.Display(i,
                    new PixelRect(i * width, 0, last ? w - i * width : width, h), i == 0));
            }

            return fake;
        }

        var all = Displays.All();
        if (all.Count == 0)
        {
            var (w, h) = DesktopSurface.PrimaryScreenSize();
            all.Add(new Displays.Display(0, new PixelRect(0, 0, w, h), true));
        }

        if (!_config.SpanAllMonitors)
        {
            var primary = all.FirstOrDefault(d => d.IsPrimary);
            all = new List<Displays.Display> { primary.Index == 0 && primary.Bounds.Width > 0 ? primary : all[0] };
        }

        foreach (var d in all) Log.Info($"display {d.Index}: {d.Bounds.X},{d.Bounds.Y} {d.Bounds.Width}x{d.Bounds.Height} primary={d.IsPrimary}");
        return all;
    }

    /// <summary>是否在所有显示器上播放。</summary>
    public bool SpanAllMonitors
    {
        get => _config.SpanAllMonitors;
        set
        {
            if (_config.SpanAllMonitors == value) return;
            _config.SpanAllMonitors = value;
            Save();

            // 会话数量变了，重建
            if (_sessions.Count > 0)
            {
                var current = Active;
                Stop();
                if (current is not null) SetWallpaper(current);
            }
        }
    }

    // ---------- 素材 / 状态 ----------

    public void SetActive(WallpaperItem? item)
    {
        Active = item;
        foreach (var i in Items) i.IsActive = ReferenceEquals(i, item);
    }

    public int RemoveMissing()
    {
        var missing = Items.Where(i => i.FileMissing).ToList();
        foreach (var item in missing)
        {
            if (Active?.Id == item.Id) Stop();
            Items.Remove(item);
        }

        if (missing.Count > 0) Save();
        return missing.Count;
    }

    public FitMode Fit
    {
        get => _config.FitMode;
        set
        {
            _config.FitMode = value;
            foreach (var player in Players) player.SetFit(value);
            Save();
        }
    }

    public bool Muted
    {
        get => _config.Muted;
        set
        {
            _config.Muted = value;
            foreach (var player in Players) player.SetMuted(value);
            Save();
        }
    }

    public int Volume
    {
        get => _config.Volume;
        set
        {
            _config.Volume = Math.Clamp(value, 0, 100);
            foreach (var player in Players) player.SetVolume(_config.Volume);
            Save();
        }
    }

    public bool PowerSaving
    {
        get => _config.PowerSaving;
        set
        {
            if (_config.PowerSaving == value) return;
            _config.PowerSaving = value;
            Save();

            if (Active is not null) Play(Active, forceRestart: true);
        }
    }

    public bool RotationEnabled
    {
        get => _config.RotationEnabled;
        set
        {
            _config.RotationEnabled = value;
            Save();
            RestartRotationTimer();
        }
    }

    public int RotationIntervalSeconds
    {
        get => _config.RotationIntervalSeconds;
        set
        {
            _config.RotationIntervalSeconds = Math.Clamp(value, 5, 86400);
            Save();
            RestartRotationTimer();
        }
    }

    public bool RotationShuffle
    {
        get => _config.RotationShuffle;
        set
        {
            _config.RotationShuffle = value;
            Save();
        }
    }

    public bool AutoStart
    {
        get => _config.AutoStart && StartupManager.IsEnabled();
        set
        {
            _config.AutoStart = value;
            StartupManager.SetEnabled(value);
            Save();
        }
    }

    public bool PauseOnFullscreen
    {
        get => _config.PauseOnFullscreen;
        set
        {
            _config.PauseOnFullscreen = value;
            if (!value && _autoPaused)
            {
                foreach (var player in Players) player.Resume();
                _autoPaused = false;
            }
            Save();
        }
    }

    public bool IsPaused => Primary?.IsPaused ?? false;

    public bool IsPlaying => _sessions.Any(s => s.Player.IsPlaying);

    public bool IsAutoPaused => _autoPaused;

    public string? CurrentFile => Primary?.CurrentFile;

    // ---------- 事件 ----------

    public event Action<string>? PlaybackFailed;
    public event Action<int>? PlaybackRestarted;
    public event Action? Saved;
    public event Action<bool>? AutoPauseChanged;

    private static void OnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.BeginInvoke(action);
    }

    // ---------- 播放 ----------

    public bool SetWallpaper(WallpaperItem item)
    {
        EnsureSessions();

        if (!Play(item))
        {
            PlaybackFailed?.Invoke("播放失败：" + item.Name);
            return false;
        }

        SetActive(item);
        _config.ActiveId = item.Id;
        Save();

        if (_config.RotationEnabled && !_rotating) RestartRotationTimer();
        return true;
    }

    private bool Play(WallpaperItem item, bool forceRestart = false)
    {
        EnsureSessions();

        var options = new PlayerOptions
        {
            Fit = _config.FitMode,
            Muted = _config.Muted,
            Volume = _config.Volume,
            PowerSaving = _config.PowerSaving,
            MaxFps = 0,
        };

        var allOk = true;

        foreach (var session in _sessions)
        {
            if (!session.Surface.IsAttached && !session.Surface.Attach())
            {
                Log.Error("attach failed for a surface");
                allOk = false;
                continue;
            }

            var player = session.Player;

            // 已在播放别的素材时优先 IPC 换片，避免黑屏与内存抖动
            if (!forceRestart && player.IsPlaying && player.CurrentFile is { } current
                && !string.Equals(current, item.Path, StringComparison.OrdinalIgnoreCase)
                && player.Switch(item.Path))
            {
                continue;
            }

            if (!player.Play(item.Path, session.Surface.Handle, options)) allOk = false;
        }

        return allOk;
    }

    public void Stop()
    {
        foreach (var session in _sessions) session.Dispose();
        _sessions.Clear();

        _autoPaused = false;
        _manualPaused = false;
        SetActive(null);
        _config.ActiveId = null;
        Save();
    }

    public void Restore()
    {
        if (string.IsNullOrEmpty(_config.ActiveId)) return;
        var item = Items.FirstOrDefault(i => i.Id == _config.ActiveId);
        if (item is null || !File.Exists(item.Path)) return;
        SetWallpaper(item);
    }

    public void Pause()
    {
        foreach (var player in Players) player.Pause();
    }

    public void Resume()
    {
        foreach (var player in Players) player.Resume();
    }

    public bool TogglePause()
    {
        if (_autoPaused)
        {
            _autoPaused = false;
            foreach (var player in Players) player.Resume();
            _manualPaused = false;
            return false;
        }

        if (IsPaused)
        {
            foreach (var player in Players) player.Resume();
            _manualPaused = false;
        }
        else
        {
            foreach (var player in Players) player.Pause();
            _manualPaused = true;
        }

        return IsPaused;
    }

    // ---------- 轮播 ----------

    private void RestartRotationTimer()
    {
        _rotation?.Dispose();
        _rotation = null;

        if (!_config.RotationEnabled) return;

        var period = TimeSpan.FromSeconds(Math.Clamp(_config.RotationIntervalSeconds, 5, 86400));
        _rotation = new System.Threading.Timer(OnRotationTick, null, period, period);
        Log.Info($"rotation timer started, interval={period.TotalSeconds}s shuffle={_config.RotationShuffle}");
    }

    private void OnRotationTick(object? state)
    {
        if (!_config.RotationEnabled) return;
        if (Active is null || _autoPaused || _manualPaused) return;
        if (!IsPlaying) return;

        OnUi(() =>
        {
            _rotating = true;
            try
            {
                NextWallpaper(auto: true);
            }
            finally
            {
                _rotating = false;
            }
        });
    }

    public bool NextWallpaper(bool auto = false)
    {
        var usable = Items.Where(i => File.Exists(i.Path)).ToList();
        if (usable.Count == 0) return false;
        if (usable.Count == 1)
        {
            if (!auto) PlaybackFailed?.Invoke("素材库里只有一个可用素材");
            return false;
        }

        WallpaperItem next;
        if (_config.RotationShuffle)
        {
            var pool = usable.Where(i => i.Id != Active?.Id).ToList();
            next = pool[Random.Shared.Next(pool.Count)];
        }
        else
        {
            var index = Active is null ? -1 : usable.FindIndex(i => i.Id == Active.Id);
            next = usable[(index + 1) % usable.Count];
        }

        var ok = SetWallpaper(next);
        if (ok) Log.Info("rotated to " + next.Name + (auto ? " (auto)" : " (manual)"));
        return ok;
    }

    // ---------- 全屏暂停 ----------

    private void OnFullscreenChanged(bool isFullscreen)
    {
        if (!_config.PauseOnFullscreen) return;

        if (isFullscreen)
        {
            if (!IsPlaying || IsPaused || _manualPaused) return;
            foreach (var player in Players) player.Pause();
            _autoPaused = true;
            OnUi(() => AutoPauseChanged?.Invoke(true));
        }
        else if (_autoPaused)
        {
            foreach (var player in Players) player.Resume();
            _autoPaused = false;
            OnUi(() => AutoPauseChanged?.Invoke(false));
        }
    }

    // ---------- 素材库 ----------

    public string ThumbDir => Path.Combine(_store.RootDir, "thumbs");

    public int AddFiles(IEnumerable<string> paths)
    {
        var added = new List<WallpaperItem>();

        foreach (var path in paths)
        {
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (!SupportedExtensions.Contains(ext)) continue;
            if (!File.Exists(path)) continue;
            if (Items.Any(i => string.Equals(i.Path, path, StringComparison.OrdinalIgnoreCase))) continue;

            var item = WallpaperItem.FromFile(path);
            Items.Add(item);
            added.Add(item);
        }

        if (added.Count == 0) return 0;

        Save();
        Enrich(added);
        return added.Count;
    }

    private void Enrich(IReadOnlyCollection<WallpaperItem> items)
    {
        if (items.Count == 0) return;

        Task.Run(() =>
        {
            foreach (var item in items)
            {
                try
                {
                    var thumbPath = Path.Combine(ThumbDir, item.Id + ".png");
                    var thumb = File.Exists(thumbPath) ? thumbPath : Thumbnailer.Create(item.Path, thumbPath);
                    var duration = item.DurationSec > 0 ? item.DurationSec : MediaProbe.GetDuration(item.Path) ?? 0;

                    OnUi(() =>
                    {
                        item.Thumb = thumb;
                        item.DurationSec = duration;
                    });
                }
                catch (Exception ex)
                {
                    Log.Warn("enrich failed for " + item.Name + ": " + ex.Message);
                }
            }

            OnUi(Save);
        });
    }

    public void EnrichMissingMetadata()
    {
        var missing = Items.Where(i => i.DurationSec <= 0 && File.Exists(i.Path)).ToList();
        Enrich(missing);
    }

    public void Remove(WallpaperItem item)
    {
        Items.Remove(item);
        if (Active?.Id == item.Id) Stop();
        Save();
    }

    public bool Apply(WallpaperItem item) => SetWallpaper(item);

    public void Save()
    {
        _config.Library = Items.ToList();
        _store.Save(_config);
    }

    public void Dispose()
    {
        _rotation?.Dispose();
        _fullscreen.Dispose();
        foreach (var session in _sessions) session.Dispose();
        _sessions.Clear();
    }
}
