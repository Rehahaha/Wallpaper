# M0 技术验证记录

日期：2026-10-28　环境：Windows 11 build 26200（1280x800 虚拟机 / Intel 核显）

## 1. 目的
验证「把窗口挂到桌面图标层之下」在本机可行，确定 WorkerW 的正确查找方式。

## 2. 实测结果

| 项目 | 结果 |
|---|---|
| `Progman` 存在 | 是（hwnd `0x10100`，1280x800） |
| 发送 `0x052C` 后 | `Progman` 下出现子窗口 `WorkerW`（`0x8065E`，全屏、可见） |
| 顶层 `WorkerW` | 有十几个，但尺寸都是 131x36 或 0x0 —— 是任务栏/输入法相关的，**不是壁纸层** |
| 我们的窗口 | 挂载成功：`WallpaperDesktopSurface` → `WorkerW` → `Progman` |

**结论：在 Windows 11 26200 上，壁纸层是 Progman 的 *子* WorkerW。** 直接套用"枚举顶层窗口找 WorkerW"的老写法会失败（会拿到任务栏的小窗口），表现为"挂上了但看不到"。

## 3. 已实现
`src/Wallpaper.DesktopHost/DesktopSurface.cs`
- `FindWorkerWChildOf(progman)`：按「Progman 子窗口 + WorkerW 类 + 无 SHELLDLL_DefView + 覆盖整屏 + 可见」筛选。
- `FindTopLevelWorkerW()`：老系统兜底。
- 都失败则退回 `Progman`（会盖住图标，仅作最后手段并记日志）。

## 4. 未完成的验证（被环境阻塞）

截图取色检查显示测试画面（品红/青棋盘格）**未出现在屏幕上**，采样 16000 点中品红/青色均为 0。

原因排查：本机已安装并运行 **Lively Wallpaper**（`D:\Lively Wallpaper\Lively.exe`，附带 `mpv.exe`）。它的渲染窗口是 `Progman` 的直接子窗口 `mpv`(`0x2105C0`)，z 序在 `WorkerW` 之上，因此把我们的画面整块盖住了。

```
Progman 子窗口 z 序（上 → 下）：
  SHELLDLL_DefView / SysListView32   ← 桌面图标
  mpv (0x2105C0)                     ← Lively 的渲染窗口
  WorkerW (0x8065E)                  ← 我们的挂载点
```

**补充尝试（均失败，原因是遮挡而非代码问题）**

| 尝试 | 结果 | 说明 |
|---|---|---|
| 桌面截图取色 | 品红/青 = 0 / 16000 采样 | 屏幕上全是 Lively 的画面 |
| `PrintWindow` 抓自己的窗口 | 抓到全白 | 窗口被完全遮挡时系统不下发 `WM_PAINT`，所以画面根本没绘制过 |
| 结构检查（父窗口链） | 通过 | `WallpaperDesktopSurface` → `WorkerW` → `Progman`，尺寸 1280x800，可见 |

**待办**：需要壁纸层空出来才能完成视觉验证 —— 方式任选其一：
1. 退出 Lively 后重跑 `Wallpaper.exe --attach-test`；
2. 若要与 Lively 长期共存，则必须在产品层处理"多壁纸程序争抢桌面层"的问题（检测并提示用户）。

## 4.1 追加发现（13:50 复查）

另外两个坑，都会让"验证"得出错误结论：

**坑一：截图必须 DPI 感知。**
本机实际分辨率 **2560x1600、缩放 200%**。如果截图进程不是 Per-Monitor-V2 感知，`GetSystemMetrics` 返回 1280x800，`CopyFromScreen` 只抓到屏幕左上角 1/4。前面几次"取不到颜色"的结论，一部分就来自这里。验证脚本已强制 `SetProcessDpiAwarenessContext(-4)`。

**坑二：会话锁屏时，任何窗口都拍不到。**
复查时 `LockApp` / `LogonUI` 进程在跑，`WindowFromPoint` 全屏返回 `Windows.UI.Core.CoreWindow`（锁屏界面）。此时无论怎么挂载都只会拍到锁屏壁纸 —— 之前 mpv 那轮结论（差异 0%）**作废**，需要在解锁状态下重测。

**目前仍然成立的两个事实**
1. 挂载链路正确：`WallpaperDesktopSurface` → `WorkerW` → `Progman`。
2. 我们的窗口确实收到了 `WM_PAINT`（`paintCount=3`），也就是说"画了但看不见"，不是"没画"。
3. `Progman` 的扩展样式是 `0x200080`，含 **`WS_EX_NOREDIRECTIONBITMAP`** —— 它没有 DWM 重定向表面，纯 GDI 子窗口很可能不会被合成上屏。这条是待验证的**主假设**：渲染必须走 D3D/DirectComposition（mpv 正是这么做的）。

## 4.2 最终验证结果（14:00，桌面已解锁，2560x1600 DPI 感知截图）

| 场景 | 与基线差异 | 是否可见 |
|---|---|---|
| GDI 棋盘格（挂 WorkerW） | **0%** | ❌ |
| mpv 彩条 `--wid=WorkerW` | **96.1%** | ✅ |
| mpv 彩条 `--wid=<我们的宿主窗口>` | **31.8%**（彩条颜色正确：红/白采样命中） | ✅ |

**M0 结论：挂载链路正确 + 渲染必须交给 mpv（D3D 交换链）。**

- GDI 画不出来的原因：`Progman` exstyle = `0x200080`，含 `WS_EX_NOREDIRECTIONBITMAP`，没有 DWM 重定向表面，纯 GDI 子窗口不参与合成。
- 证据图：`docs/verify-workerw.png`（无棋盘格）、`docs/verify-mpv.png`（SMPTE 彩条）、`docs/verify-surface-mpv.png`（经我们宿主窗口渲染）。

## 5. 复现命令

```powershell
# 桌面层验证（会自己切到桌面、截图、比对、关闭测试进程）
pwsh -File tools/verify.ps1 -Mode WorkerWChild
pwsh -File tools/verify.ps1 -Mode Mpv
```

旧命令：

```powershell
Wallpaper.exe --attach-test     # 挂载 20 秒后自动退出
# 结果：%APPDATA%\Wallpaper\logs\m0-result.txt
```
