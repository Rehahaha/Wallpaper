# 打包脚本：生成可分发的目录 + zip
# 用法：pwsh -File tools/pack.ps1

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

# 优先用用户级安装的 SDK（系统目录里可能只有运行时）
$dn = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
if (-not (Test-Path $dn)) {
    $found = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $found) { throw "找不到 dotnet SDK" }
    $dn = $found.Source
}

$out = Join-Path $root "dist\Wallpaper-win-x64"
if (Test-Path $out) { Remove-Item $out -Recurse -Force }

Write-Host "1/4 发布（自包含单文件）..."
$pubArgs = @(
    "publish", "src\Wallpaper.App\Wallpaper.App.csproj",
    "-c", "Release", "-r", "win-x64", "--self-contained", "true",
    "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true",
    "-p:EnableCompressionInSingleFile=true",
    "-p:DebugType=none",
    "-o", $out
)
& $dn @pubArgs | Out-Host

Write-Host "2/4 复制 mpv 运行时..."
$mpvSrc = Join-Path $root "lib\mpv"
if (-not (Test-Path (Join-Path $mpvSrc "mpv.exe"))) { throw "缺少 lib/mpv/mpv.exe" }
$mpvDst = Join-Path $out "lib\mpv"
New-Item -ItemType Directory -Force -Path $mpvDst | Out-Null
Copy-Item (Join-Path $mpvSrc "*") $mpvDst -Recurse -Force

Write-Host "3/4 复制图标资源 + 使用说明..."
$assetSrc = Join-Path $root "assets\wallpaper.ico"
if (Test-Path $assetSrc) {
    $assetDst = Join-Path $out "assets"
    New-Item -ItemType Directory -Force -Path $assetDst | Out-Null
    Copy-Item $assetSrc $assetDst -Force
}

$guide = @"
Wallpaper — 视频桌面壁纸
========================

直接双击 Wallpaper.exe 即可运行，无需安装 .NET 运行时。

首次使用
  1) 点「添加视频…」或把 mp4/webm 拖进窗口
  2) 选中素材 → 「设为壁纸」
  3) 按 Win+D 查看桌面

常用快捷键
  Ctrl+Alt+W      显示 / 隐藏主界面
  Ctrl+Alt+空格   暂停 / 继续壁纸
  Ctrl+Alt+N      切换到下一个素材

托盘
  关闭主窗口只是收进托盘，壁纸继续播放。
  右键托盘图标 → 可停止壁纸或退出。

配置与日志
  %APPDATA%Wallpaperconfig.json
  %APPDATA%Wallpaperlogswallpaper.log

注意
  - 需要 Windows 10 1903+ / Windows 11（64 位）
  - 与本机其他动态壁纸软件（如 Lively Wallpaper）冲突，请只运行一个
  - libmpvmpv.exe 是视频引擎，mvp 版本基于 mpv（GPLv2），请勿单独删除

版本：0.10
"@
Set-Content -Path (Join-Path $out "使用说明.txt") -Value $guide -Encoding UTF8

Write-Host "4/4 打 zip..."
$zip = Join-Path $root "dist\Wallpaper-win-x64.zip"
if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $out "*") -DestinationPath $zip -CompressionLevel Optimal

$size = [math]::Round((Get-Item $zip).Length / 1MB, 1)
Write-Host ""
Write-Host ("完成：{0}  ({1} MB)" -f $zip, $size) -ForegroundColor Green
Get-ChildItem $out -Recurse -File | Select-Object @{n='文件';e={ $_.FullName.Replace($out + '\','') }}, @{n='MB';e={[math]::Round($_.Length/1MB,1)}} | Format-Table -AutoSize | Out-Host
