using System.IO;
using System.Windows;
using Wallpaper.Core;
using Application = System.Windows.Application;
using Wallpaper.Player;

namespace Wallpaper.App;

public partial class App : Application
{
    private const string MutexName = "Wallpaper-SingleInstance-8F2C";
    private const string ShowEventName = "Wallpaper-ShowWindow-8F2C";

    private static Mutex? _instanceMutex;
    private bool _errorShown;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // 自检模式不受单实例限制，方便本地测试
        var isTestMode = e.Args.Any(a =>
            a.StartsWith("--attach-test", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--play", StringComparison.OrdinalIgnoreCase) ||
            a.Equals("--ipc-test", StringComparison.OrdinalIgnoreCase));

        if (!isTestMode && !EnsureSingleInstance())
        {
            Shutdown();
            return;
        }

        DispatcherUnhandledException += (_, args) =>
        {
            Core.Log.Error("unhandled: " + args.Exception);
            args.Handled = true;

            // 只弹一次，避免异常发生在布局/渲染回调里时反复弹窗导致重入
            if (_errorShown) return;
            _errorShown = true;

            // 不能在布局/渲染回调里直接弹模态框，否则会重入布局（表现为栈溢出崩溃）
            Dispatcher.BeginInvoke(new Action(() =>
            {
                System.Windows.MessageBox.Show(args.Exception.Message, "Wallpaper 出错了");
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        };

        // 调试：把主屏切成 N 块虚拟显示器，用于单屏验证多屏逻辑
        // 必须放在所有自检分支之前，否则 --play 等分支会先返回
        var simIndex = Array.FindIndex(e.Args, a => a.Equals("--simulate-monitors", StringComparison.OrdinalIgnoreCase));
        if (simIndex >= 0 && simIndex + 1 < e.Args.Length && int.TryParse(e.Args[simIndex + 1], out var simCount))
        {
            WallpaperService.SimulateMonitors = Math.Clamp(simCount, 1, 4);
            Core.Log.Info("simulate monitors: " + WallpaperService.SimulateMonitors);
        }

        var testArg = e.Args.FirstOrDefault(a =>
            a.StartsWith("--attach-test", StringComparison.OrdinalIgnoreCase));
        if (testArg is not null)
        {
            var mode = DesktopHost.DesktopSurface.AttachMode.WorkerWChild;
            if (testArg.Contains(':'))
            {
                var name = testArg[(testArg.IndexOf(':') + 1)..];
                Enum.TryParse(name, ignoreCase: true, out mode);
            }

            RunAttachTest(mode);
            return;
        }

        var playIndex = Array.FindIndex(e.Args, a => a.Equals("--play", StringComparison.OrdinalIgnoreCase));
        if (playIndex >= 0 && playIndex + 1 < e.Args.Length)
        {
            RunPlayTest(e.Args[playIndex + 1]);
            return;
        }

        var ipcIndex = Array.FindIndex(e.Args, a => a.Equals("--ipc-test", StringComparison.OrdinalIgnoreCase));
        if (ipcIndex >= 0 && ipcIndex + 1 < e.Args.Length)
        {
            RunIpcTest(e.Args[ipcIndex + 1]);
            return;
        }

        // 命令行开关：--autostart on|off（也方便排查注册表状态）
        var autoIndex = Array.FindIndex(e.Args, a => a.Equals("--autostart", StringComparison.OrdinalIgnoreCase));
        if (autoIndex >= 0 && autoIndex + 1 < e.Args.Length)
        {
            var on = e.Args[autoIndex + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            StartupManager.SetEnabled(on);
            Core.Log.Info("cli autostart -> " + on);
            Shutdown();
            return;
        }

        // 自检：验证 IPC 换片（进程应保持不变）
        var switchIndex = Array.FindIndex(e.Args, a => a.Equals("--switch-test", StringComparison.OrdinalIgnoreCase));
        if (switchIndex >= 0 && switchIndex + 2 < e.Args.Length)
        {
            RunSwitchTest(e.Args[switchIndex + 1], e.Args[switchIndex + 2]);
            return;
        }

        var startMinimized = e.Args.Any(a => a.Equals("--minimized", StringComparison.OrdinalIgnoreCase));
        var window = new MainWindow();
        MainWindow = window;

        if (startMinimized)
        {
            // 开机自启动场景：只常驻托盘，不弹窗打扰
            ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Core.Log.Info("started minimized (autostart)");
        }
        else
        {
            window.Show();
        }

        window.RestoreWallpaper();
    }

    /// <summary>
    /// 保证只有一个实例。重复启动时，把已有实例的窗口叫到前面，然后自己退出。
    /// </summary>
    private bool EnsureSingleInstance()
    {
        try
        {
            _instanceMutex = new Mutex(true, MutexName, out var isFirst);

            if (!isFirst)
            {
                Core.Log.Info("已有实例在运行，通知其显示窗口");
                try
                {
                    if (EventWaitHandle.TryOpenExisting(ShowEventName, out var existing))
                    {
                        existing.Set();
                        existing.Dispose();
                    }
                }
                catch (Exception ex)
                {
                    Core.Log.Warn("通知已有实例失败: " + ex.Message);
                }

                return false;
            }

            // 第一个实例：监听"有人叫我出来"的信号
            var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
            var thread = new Thread(() =>
            {
                while (true)
                {
                    showEvent.WaitOne();
                    Dispatcher.BeginInvoke(() =>
                    {
                        if (MainWindow is Wallpaper.App.MainWindow window)
                        {
                            window.ShowFromExternal();
                        }
                    });
                }
            })
            {
                IsBackground = true,
                Name = "Wallpaper-ShowWindow",
            };
            thread.Start();

            return true;
        }
        catch (Exception ex)
        {
            Core.Log.Warn("single instance check failed: " + ex.Message);
            return true;
        }
    }

    /// <summary>M1.3 自检：切素材时应走 IPC loadfile，mpv 进程不变。</summary>
    private void RunSwitchTest(string fileA, string fileB)
    {
        var service = new WallpaperService();
        service.AddFiles(new[] { fileA, fileB });

        var a = service.Items.FirstOrDefault(i => i.Path == fileA);
        var b = service.Items.FirstOrDefault(i => i.Path == fileB);
        var lines = new List<string>();
        void Report(string m) { lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + m); Core.Log.Info("switch-test: " + m); }

        if (a is null || b is null) { Report("导入失败"); Finish(); return; }

        int MpvPid()
        {
            var p = System.Diagnostics.Process.GetProcessesByName("mpv")
                .FirstOrDefault(x => x.MainWindowHandle != IntPtr.Zero || true);
            return p?.Id ?? -1;
        }

        Report("play A = " + Path.GetFileName(fileA));
        service.SetWallpaper(a);

        var stage = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            stage++;
            if (stage == 1)
            {
                Report("A 播放中 mpv pid = " + MpvPid() + " duration=" + a.DurationText);
                Report("切到 B: " + Path.GetFileName(fileB));
                service.SetWallpaper(b);
            }
            else if (stage == 2)
            {
                Report("B 播放中 mpv pid = " + MpvPid() + " duration=" + b.DurationText + " (应与 A 相同)");
                service.Stop();
                timer.Stop();
                Finish();
            }
        };
        timer.Start();

        void Finish()
        {
            try
            {
                Directory.CreateDirectory(Core.Log.LogDir);
                File.WriteAllText(Path.Combine(Core.Log.LogDir, "switch-test.txt"), string.Join(Environment.NewLine, lines));
            }
            catch { }

            service.Dispose();
            Shutdown();
        }
    }

    /// <summary>M1.1 自检：验证 IPC（暂停/继续/时长读数），结果写入 logs/ipc-test.txt。</summary>
    private void RunIpcTest(string file)
    {
        var service = new WallpaperService();
        service.AddFiles(new[] { file });
        var item = service.Items.FirstOrDefault(i =>
            string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase));

        static int MpvPid()
        {
            var p = System.Diagnostics.Process.GetProcessesByName("mpv").FirstOrDefault();
            return p?.Id ?? -1;
        }

        var lines = new List<string>();
        void Report(string message)
        {
            lines.Add(DateTime.Now.ToString("HH:mm:ss") + "  " + message);
            Core.Log.Info("ipc-test: " + message);
        }

        if (item is null)
        {
            Report("导入失败");
            Finish();
            return;
        }

        var ok = service.SetWallpaper(item);
        Report("SetWallpaper=" + ok);

        var stage = 0;
        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        timer.Tick += (_, _) =>
        {
            stage++;
            switch (stage)
            {
                case 1:
                    Report("durationText=" + item.DurationText + " raw=" + item.DurationSec);
                    service.Pause();
                    Report("after Pause -> IsPaused=" + service.IsPaused);
                    break;

                case 2:
                    Report("still paused=" + service.IsPaused + " (屏幕应冻结)");
                    service.Resume();
                    Report("after Resume -> IsPaused=" + service.IsPaused);
                    break;

                case 3:
                    // 运行中改适配 / 音量：应走 IPC，不重启进程
                    Report("mpv pid before = " + MpvPid());
                    service.Fit = FitMode.Contain;
                    service.Volume = 30;
                    service.Muted = false;
                    Report("fit=Contain volume=30 muted=false 已下发");
                    break;

                case 4:
                    Report("mpv pid after  = " + MpvPid() + " (应与 before 相同)");
                    service.Stop();
                    Report("stopped");
                    timer.Stop();
                    Finish();
                    break;
            }
        };
        timer.Start();

        void Finish()
        {
            try
            {
                Directory.CreateDirectory(Core.Log.LogDir);
                File.WriteAllText(Path.Combine(Core.Log.LogDir, "ipc-test.txt"), string.Join(Environment.NewLine, lines));
            }
            catch (Exception ex)
            {
                Core.Log.Error("write ipc-test failed: " + ex.Message);
            }

            service.Dispose();
            Shutdown();
        }
    }

    /// <summary>M1 自检：走完整链路（导入 → 缩略图 → 挂载 → 播放），20 秒后停止。</summary>
    private void RunPlayTest(string file)
    {
        var service = new WallpaperService();
        service.AddFiles(new[] { file });

        var item = service.Items.FirstOrDefault(i =>
            string.Equals(i.Path, file, StringComparison.OrdinalIgnoreCase));
        if (item is null)
        {
            Core.Log.Error("play-test: 无法导入 " + file);
            Shutdown();
            return;
        }

        var ok = service.SetWallpaper(item);
        Core.Log.Info($"play-test: ok={ok} file={item.Name} thumb={item.Thumb}");

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            service.Dispose();
            Shutdown();
        };
        timer.Start();
    }

    /// <summary>M0 自检：只挂载桌面层，不显示主窗口，20 秒后自动退出。</summary>
    private void RunAttachTest(DesktopHost.DesktopSurface.AttachMode mode)
    {
        var surface = new DesktopHost.DesktopSurface();
        var attached = surface.Attach(mode);
        var report = "mode=" + mode + Environment.NewLine + "attached=" + attached + Environment.NewLine + surface.Describe();

        try
        {
            Directory.CreateDirectory(Core.Log.LogDir);
            File.WriteAllText(Path.Combine(Core.Log.LogDir, "m0-result.txt"), report);
        }
        catch (Exception ex)
        {
            Core.Log.Error("write m0-result failed: " + ex.Message);
        }

        Core.Log.Info("attach-test -> " + report.Replace(Environment.NewLine, " | "));

        var resample = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        resample.Tick += (_, _) =>
        {
            resample.Stop();
            try
            {
                Directory.CreateDirectory(Core.Log.LogDir);
                File.WriteAllText(Path.Combine(Core.Log.LogDir, "m0-result.txt"),
                    "mode=" + mode + Environment.NewLine + surface.Describe());
            }
            catch (Exception ex)
            {
                Core.Log.Error("rewrite m0-result failed: " + ex.Message);
            }
        };
        resample.Start();

        var timer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            surface.Dispose();
            Shutdown();
        };
        timer.Start();
    }
}
