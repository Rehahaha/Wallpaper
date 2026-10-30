# Wallpaper

[🇨🇳 中文](README.md) | English

![build](https://github.com/Rehahaha/Wallpaper/actions/workflows/build.yml/badge.svg)
![license](https://img.shields.io/badge/license-MIT-blue.svg)
![platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011-lightgrey.svg)

A lightweight Windows app that turns local video files into live desktop wallpapers.
C# + WPF + mpv. No network, no accounts, no background services.

> Built for the simple case: *I downloaded a video, I want it behind my desktop icons.*

## Features

| | |
|---|---|
| Video wallpaper | Rendered beneath desktop icons; icons, taskbar and Start menu keep working |
| Formats | mp4 / webm / m4v / mkv, up to 4K |
| Fit modes | Cover / contain / stretch, switched **live without restarting the player** |
| Library | Thumbnails, auto-probed duration, search, sort, missing-file detection & cleanup |
| Auto rotation | Every 5 / 15 / 30 / 60 min; skipped while fullscreen or paused |
| Fullscreen pause | Auto-pauses for games / fullscreen video, resumes on exit |
| Power saving | mpv `fast` profile — measured 4K60: 522 MB → 349 MB RSS |
| Multi-monitor | One host window + one player per display |
| Tray / hotkeys | Close-to-tray; Ctrl+Alt+W / Space / N |
| Resilience | Player crashes are auto-restarted (1s/2s/3s backoff, max 3) |

## Requirements

- Windows 10 1903+ / Windows 11 (x64)
- .NET SDK 10 (build only — the packaged build is self-contained)
- mpv (runtime dependency, see below)

## Build

```powershell
# 1) get mpv (~110 MB, not vendored in this repo)
powershell -ExecutionPolicy Bypass -File tools/fetch-mpv.ps1

# 2) build
dotnet build Wallpaper.slnx -c Release

# 3) run
dotnet run --project src/Wallpaper.App
```

Package a self-contained, shareable folder (~151 MB zip):

```powershell
powershell -ExecutionPolicy Bypass -File tools/pack.ps1
```

## Usage

1. Click **Add video…** or drag files into the window
2. Select an item → **Set as wallpaper** (or just double-click)
3. Press Win+D to see the desktop

| Hotkey | Action |
|---|---|
| Ctrl+Alt+W | Show / hide the main window |
| Ctrl+Alt+Space | Pause / resume |
| Ctrl+Alt+N | Next wallpaper |

Config and logs live in `%APPDATA%\Wallpaper\`.

## Implementation notes (things that differ from the common tutorials)

### 1. The wallpaper layer is a *child* WorkerW of Progman, not a top-level one

The classic recipe is: send `0x052C` to `Progman`, then enumerate top-level windows for `WorkerW`.
On Windows 11 build 26200 there are indeed a dozen top-level `WorkerW` windows — all sized 131×36
(input method / taskbar related). The real wallpaper layer is a **child `WorkerW` of `Progman`**
(full-screen, no `SHELLDLL_DefView` child).

### 2. You cannot draw it with GDI — it must go through the D3D swap chain

Measured: drawing a checkerboard into the WorkerW with GDI triggers `WM_PAINT` normally (count = 3),
but nothing appears on screen. `Progman` has `WS_EX_NOREDIRECTIONBITMAP`, so it has no DWM redirection
surface and GDI content never gets composited.

| Renderer | Diff vs. baseline |
|---|---|
| GDI self-drawn | 0% (invisible) |
| mpv `--wid=<WorkerW>` | 96.1% |
| mpv `--wid=<own host window>` | 31.8% (correct) |

So the picture must come from mpv's swap chain; WPF only provides the control UI.

### 3. "Fullscreen" cannot be detected by "covers the monitor"

A maximized window rect extends **beyond** the monitor bounds (Windows keeps ~14px of invisible border at 200% DPI).
Checking `rect covers monitor` therefore treats every maximized browser as fullscreen and the wallpaper pauses for no reason.
Compare the four edges with an *approximate equality* tolerance instead.

### 4. Never call `Items.Refresh()` on a virtualized WPF list

If `Items.Refresh()` lands inside a layout pass, `VirtualizingStackPanel.MeasureChild` throws `NullReferenceException`;
showing a modal MessageBox from that handler re-enters layout and ends in a stack overflow (`0xc00000fd`).
Use `INotifyPropertyChanged` and defer the dialog to `ApplicationIdle` instead.

More details in [docs/M0-notes.md](docs/M0-notes.md) and [docs/PRD-MVP.md](docs/PRD-MVP.md).

## Layout

```
src/
  Wallpaper.App/          WPF UI, tray, orchestration
  Wallpaper.DesktopHost/  Win32: WorkerW attach, fullscreen watcher, global hotkeys
  Wallpaper.Player/       IVideoPlayer + mpv process/IPC + thumbnail/duration probing
  Wallpaper.Core/         config, models, logging
lib/mpv/                  mpv runtime (fetch it yourself, git-ignored)
docs/                     requirements and findings
tools/                    packaging, verification, mpv fetcher
```

## Known limitations

- Windows only; macOS / Linux not implemented
- Real multi-monitor has not been tested on a dual-screen machine (the multi-session path is verified on one screen via `--simulate-monitors 2`)
- Conflicts with other live-wallpaper apps (e.g. Lively Wallpaper) — run only one
- The distributed exe is not code-signed, so SmartScreen will warn on first run
- Missing: zoom / pan adjustment, H.265 software-decode fallback hint

## License

Source code: **MIT**.

The app depends on [mpv](https://mpv.io/) (GPLv2+), which is **not** part of this repository.
If you redistribute a package that bundles `mpv.exe`, you must also satisfy GPLv2 — see [THIRD-PARTY.md](THIRD-PARTY.md).
