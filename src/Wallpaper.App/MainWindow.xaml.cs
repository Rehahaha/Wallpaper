using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.IO;
using Wallpaper.Core;
using WinForms = System.Windows.Forms;

namespace Wallpaper.App;

public partial class MainWindow : Window
{
    private readonly WallpaperService _service = new();
    private readonly ICollectionView _view;
    private DesktopHost.HotkeyHost? _hotkeys;
    private WinForms.NotifyIcon? _tray;
    private bool _reallyExit;
    private bool _initializing = true;

    public MainWindow()
    {
        InitializeComponent();

        _view = CollectionViewSource.GetDefaultView(_service.Items);
        _view.Filter = FilterItem;
        ApplySort("Added", descending: true);
        LibraryList.ItemsSource = _view;
        SortBox.SelectedIndex = 0;
        FitBox.SelectedIndex = _service.Fit switch
        {
            FitMode.Contain => 1,
            FitMode.Stretch => 2,
            _ => 0,
        };

        _service.PlaybackFailed += message => SetStatus(message, isError: true);
        _service.PlaybackRestarted += attempt =>
            SetStatus($"播放器异常退出，已自动重启（第 {attempt} 次）。", isError: true);

        _service.AutoPauseChanged += paused =>
            SetStatus(paused ? "检测到全屏应用，壁纸已自动暂停。" : "全屏应用已退出，壁纸恢复播放。");

        ChkFullscreen.IsChecked = _service.PauseOnFullscreen;
        ChkAutoStart.IsChecked = _service.AutoStart;
        ChkPower.IsChecked = _service.PowerSaving;
        ChkMute.IsChecked = _service.Muted;
        VolumeSlider.Value = _service.Volume;
        VolumeSlider.IsEnabled = !_service.Muted;
        ChkAllMonitors.IsChecked = _service.SpanAllMonitors;
        ChkRotate.IsChecked = _service.RotationEnabled;
        SelectRotateInterval(_service.RotationIntervalSeconds);
        RotateInterval.IsEnabled = _service.RotationEnabled;
        RegisterHotkeys();

        ApplyIcons();
        CreateTray();
        Closing += OnClosing;
        Closed += (_, _) =>
        {
            _hotkeys?.Dispose();
            _service.Dispose();
        };

        _initializing = false;

        // 历史素材可能没时长（早期版本导入的），后台补齐
        _service.EnrichMissingMetadata();
    }

    // ---------- 素材 ----------

    private void OnAddFiles(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Filter = "视频文件|*.mp4;*.webm;*.m4v;*.mkv|所有文件|*.*",
        };

        if (dialog.ShowDialog(this) == true)
        {
            Import(dialog.FileNames);
        }
    }

    private void Import(IEnumerable<string> files)
    {
        var added = _service.AddFiles(files);
        SetStatus(added > 0 ? $"已添加 {added} 个素材。" : "没有可添加的视频（重复或格式不支持）。");
    }

    private void OnDragOver(object sender, System.Windows.DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop)
            ? System.Windows.DragDropEffects.Copy
            : System.Windows.DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, System.Windows.DragEventArgs e)
    {
        if (e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] files) Import(files);
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (LibraryList.SelectedItem is WallpaperItem item)
            SetStatus("已选中：" + item.Name);
    }

    private void OnRemove(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not WallpaperItem item)
        {
            SetStatus("先选中一个素材。");
            return;
        }

        var answer = System.Windows.MessageBox.Show(this, $"从素材库移除「{item.Name}」？（不会删除本地文件）",
            "移除素材", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (answer != MessageBoxResult.Yes) return;

        _service.Remove(item);
        SetStatus("已移除。");
    }

    // ---------- 壁纸 ----------

    private void OnSetWallpaper(object sender, RoutedEventArgs e)
    {
        if (LibraryList.SelectedItem is not WallpaperItem item)
        {
            SetStatus("先在左侧选中一个素材。");
            return;
        }

        if (item.FileMissing)
        {
            SetStatus("文件已丢失：" + item.Path, isError: true);
            return;
        }

        if (_service.SetWallpaper(item))
            SetStatus("壁纸已设置：" + item.Name + "（按 Win+D 看看桌面）");
    }

    /// <summary>由 App 在窗口创建后调用（最小化启动时窗口不显示，也要恢复壁纸）。</summary>
    public void RestoreWallpaper() => _service.Restore();

    private void OnFullscreenToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;
        _service.PauseOnFullscreen = ChkFullscreen.IsChecked == true;
    }

    // ---------- 列表：搜索 / 排序 / 清理 ----------

    private bool FilterItem(object obj)
    {
        if (obj is not WallpaperItem item) return false;

        var keyword = SearchBox?.Text?.Trim();
        if (string.IsNullOrEmpty(keyword)) return true;

        return item.Name.Contains(keyword, StringComparison.OrdinalIgnoreCase);
    }

    private void ApplySort(string kind, bool descending)
    {
        _view.SortDescriptions.Clear();
        var direction = descending ? ListSortDirection.Descending : ListSortDirection.Ascending;

        switch (kind)
        {
            case "Name":
                _view.SortDescriptions.Add(new SortDescription(nameof(WallpaperItem.Name), direction));
                break;
            case "Duration":
                _view.SortDescriptions.Add(new SortDescription(nameof(WallpaperItem.DurationSec), direction));
                break;
            default:
                _view.SortDescriptions.Add(new SortDescription(nameof(WallpaperItem.AddedAt), direction));
                break;
        }
    }

    private void OnSearchChanged(object sender, TextChangedEventArgs e)
    {
        _view?.Refresh();
    }

    private void OnSortChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || SortBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        ApplySort(tag, descending: tag != "Name");
    }

    private void OnCleanupMissing(object sender, RoutedEventArgs e)
    {
        var missing = _service.Items.Count(i => i.FileMissing);
        if (missing == 0)
        {
            SetStatus("没有丢失的素材。");
            return;
        }

        var answer = System.Windows.MessageBox.Show(this,
            $"有 {missing} 个素材的文件已被移动或删除，从列表中清理掉？（不会删除任何文件）",
            "清理丢失素材", MessageBoxButton.YesNo, MessageBoxImage.Question);

        if (answer != MessageBoxResult.Yes) return;

        var removed = _service.RemoveMissing();
        _view.Refresh();
        SetStatus($"已清理 {removed} 个丢失素材。");
    }

    private void OnItemDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (LibraryList.SelectedItem is not WallpaperItem item) return;
        if (item.FileMissing)
        {
            SetStatus("文件已丢失：" + item.Path, isError: true);
            return;
        }

        if (_service.SetWallpaper(item)) SetStatus("壁纸已设置：" + item.Name);
    }

    // ---------- 轮播 ----------

    private void SelectRotateInterval(int seconds)
    {
        foreach (ComboBoxItem item in RotateInterval.Items)
        {
            if (item.Tag is string tag && int.TryParse(tag, out var value) && value == seconds)
            {
                RotateInterval.SelectedItem = item;
                return;
            }
        }

        RotateInterval.SelectedIndex = 1;   // 默认 15 分钟
    }

    private void OnAllMonitorsToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;

        var on = ChkAllMonitors.IsChecked == true;
        _service.SpanAllMonitors = on;
        SetStatus(on ? "已在所有显示器上播放。多屏时会各起一个播放器。" : "已改为只覆盖主屏。");
    }

    private void OnRotateToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;

        var on = ChkRotate.IsChecked == true;
        _service.RotationEnabled = on;
        RotateInterval.IsEnabled = on;
        SetStatus(on ? "已开启自动轮播。" : "已关闭自动轮播。");
    }

    private void OnRotateIntervalChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing || _service is null) return;
        if (RotateInterval.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!int.TryParse(tag, out var seconds)) return;

        _service.RotationIntervalSeconds = seconds;
        SetStatus("轮播间隔：" + item.Content);
    }

    private void OnNextWallpaper(object sender, RoutedEventArgs e)
    {
        if (_service.NextWallpaper()) SetStatus("已切换到下一个素材。");
        else SetStatus("没有可切换的素材（至少需要两个存在的文件）。", isError: true);
    }

    // ---------- 全局热键 ----------

    private void RegisterHotkeys()
    {
        _hotkeys = new DesktopHost.HotkeyHost();
        _hotkeys.Pressed += OnHotkeyPressed;

        const uint Mods = DesktopHost.HotkeyHost.CtrlAlt;
        _hotkeys.Register(1, Mods, 0x57, "Ctrl+Alt+W 显示/隐藏主界面");
        _hotkeys.Register(2, Mods, 0x20, "Ctrl+Alt+空格 暂停/继续");
        _hotkeys.Register(3, Mods, 0x4E, "Ctrl+Alt+N 下一个壁纸");
    }

    private void OnHotkeyPressed(int id)
    {
        switch (id)
        {
            case 1:
                if (IsVisible && WindowState != WindowState.Minimized) Hide();
                else ShowFromTray();
                break;

            case 2:
                var paused = _service.TogglePause();
                SetStatus(paused ? "已暂停（热键）。" : "已继续（热键）。");
                break;

            case 3:
                if (_service.NextWallpaper()) SetStatus("已切换到下一个素材（热键）。");
                break;
        }
    }

    private void OnVolumeChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_initializing || _service is null) return;
        _service.Volume = (int)e.NewValue;
    }

    private void OnMuteToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;
        var muted = ChkMute.IsChecked == true;
        _service.Muted = muted;
        VolumeSlider.IsEnabled = !muted;
        SetStatus(muted ? "已静音。" : "已取消静音。");
    }

    private void OnPowerToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;
        var on = ChkPower.IsChecked == true;
        _service.PowerSaving = on;
        SetStatus(on ? "已开启省电模式，画面细节略降、CPU 明显下降。" : "已切回高画质（CPU 会上升）。");
    }

    private void OnAutoStartToggle(object sender, RoutedEventArgs e)
    {
        if (_initializing || _service is null) return;

        var enabled = ChkAutoStart.IsChecked == true;
        _service.AutoStart = enabled;
        SetStatus(enabled
            ? "已开启开机自启（下次登录时最小化到托盘）。"
            : "已取消开机自启。");
    }

    private void OnTogglePause(object sender, RoutedEventArgs e)
    {
        var paused = _service.TogglePause();
        SetStatus(paused ? "已暂停。" : "已继续。");
    }

    private void OnStopWallpaper(object sender, RoutedEventArgs e)
    {
        _service.Stop();
        SetStatus("壁纸已停止。");
    }

    private void OnFitChanged(object sender, SelectionChangedEventArgs e)
    {
        if (FitBox.SelectedItem is not ComboBoxItem item || item.Tag is not string tag) return;
        if (!Enum.TryParse<FitMode>(tag, out var mode)) return;

        _service.Fit = mode;
        SetStatus("适配模式：" + item.Content);
    }

    // ---------- 图标 ----------

    private static string IconPath => Path.Combine(AppContext.BaseDirectory, "assets", "wallpaper.ico");

    private void ApplyIcons()
    {
        try
        {
            if (File.Exists(IconPath))
            {
                Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri(IconPath, UriKind.Absolute));
            }
        }
        catch (Exception ex)
        {
            Log.Warn("window icon load failed: " + ex.Message);
        }
    }

    private static System.Drawing.Icon LoadTrayIcon()
    {
        try
        {
            if (File.Exists(IconPath)) return new System.Drawing.Icon(IconPath);
        }
        catch (Exception ex)
        {
            Log.Warn("tray icon load failed: " + ex.Message);
        }

        return System.Drawing.SystemIcons.Application;
    }

    // ---------- 托盘 ----------

    private void CreateTray()
    {
        var menu = new WinForms.ContextMenuStrip();
        menu.Items.Add("显示主界面", null, (_, _) => ShowFromTray());
        menu.Items.Add("暂停 / 继续", null, (_, _) =>
        {
            var paused = _service.TogglePause();
            SetStatus(paused ? "已暂停。" : "已继续。");
        });
        menu.Items.Add("停止壁纸", null, (_, _) =>
        {
            _service.Stop();
            SetStatus("壁纸已停止。");
        });
        menu.Items.Add(new WinForms.ToolStripSeparator());
        menu.Items.Add("退出", null, (_, _) => ExitApp());

        _tray = new WinForms.NotifyIcon
        {
            Icon = LoadTrayIcon(),
            Visible = true,
            Text = "Wallpaper",
            ContextMenuStrip = menu,
        };
        _tray.DoubleClick += (_, _) => ShowFromTray();
    }

    /// <summary>被另一份启动请求唤起时调用（托盘双击也走这里）。</summary>
    public void ShowFromExternal() => ShowFromTray();

    private void ShowFromTray()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;

        // 已经被别的窗口盖住时，Activate 不一定能拿到前台，用 Topmost 抖一下
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (_reallyExit) return;

        e.Cancel = true;
        Hide();
        _tray?.ShowBalloonTip(2000, "Wallpaper 仍在运行", "壁纸继续播放，右键托盘图标可退出。", WinForms.ToolTipIcon.Info);
    }

    private void ExitApp()
    {
        _reallyExit = true;
        _tray?.Dispose();
        _tray = null;
        _service.Dispose();
        System.Windows.Application.Current.Shutdown();
    }

    private void SetStatus(string message, bool isError = false)
    {
        StatusText.Text = message;
        StatusText.Foreground = isError
            ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0x8A, 0x8A))
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8A, 0x8A, 0x96));
    }
}
