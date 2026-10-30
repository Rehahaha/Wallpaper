首个公开版本。源码发布，**不包含 mpv 二进制**（体积与 GPL 授权原因）。

## 功能

- **视频壁纸**：挂在桌面图标层之下，图标 / 任务栏 / 开始菜单照常可用
- **格式**：mp4 / webm / m4v / mkv，最高 4K
- **画面适配**：填充 / 适应 / 拉伸，运行中实时切换，不重启播放器
- **素材库**：缩略图、时长自动探测、搜索、排序、丢失文件清理
- **自动轮播**：5 / 15 / 30 / 60 分钟，全屏或暂停时自动跳过
- **全屏自动暂停**：游戏、全屏视频时暂停，退出后恢复
- **省电模式**：mpv fast profile，实测 4K60 内存 522 → 349 MB
- **多显示器**：每屏独立宿主窗口 + 独立播放器
- **容错**：播放器崩溃自动重启（1s/2s/3s 退避，最多 3 次）
- **托盘 + 全局热键**：Ctrl+Alt+W / 空格 / N

## 使用前必读

运行需要 **mpv**（约 110 MB，GPLv2+），本仓库不含。

     git clone https://github.com/Rehahaha/Wallpaper.git
     cd Wallpaper
     powershell -ExecutionPolicy Bypass -File tools/fetch-mpv.ps1
     dotnet build Wallpaper.slnx -c Release

也可以从 https://mpv.io/installation/ 下载，解压后把 mpv.exe 放到 lib/mpv/ 下。

## 环境

- Windows 10 1903+ / Windows 11（x64）
- .NET SDK 10（仅构建需要）

## 已知限制

- 仅 Windows
- 真实多显示器未在双屏机器验证（单屏用 --simulate-monitors 2 验证了多会话链路）
- 与其他动态壁纸软件（如 Lively）冲突，只能运行一个
- 暂不支持画面缩放 / 位移微调

## 许可

源码 MIT。mpv 为 GPLv2+，分发包含 mpv.exe 的产物需遵守 GPLv2，详见 THIRD-PARTY.md。

---

First public release. **Source only** — mpv is not bundled (size + GPL). See README.en.md.