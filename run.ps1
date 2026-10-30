# 启动 Wallpaper（自动编译 Release 并运行）
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot

$dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
if (-not $dotnet) {
    $local = Join-Path $env:LOCALAPPDATA "Microsoftdotnetdotnet.exe"
    if (Test-Path $local) { $dotnet = $local } else { throw "找不到 dotnet，请先安装 .NET SDK" }
} else {
    $dotnet = $dotnet.Source
}

& $dotnet build .Wallpaper.slnx -c Release --nologo | Out-Host
$exe = Join-Path $PSScriptRoot "srcWallpaper.AppinRelease
et10.0-windowsWallpaper.exe"
if (-not (Test-Path $exe)) { throw "编译产物不存在：$exe" }

Start-Process -FilePath $exe
Write-Host ""
Write-Host "已启动 Wallpaper。提示：" -ForegroundColor Cyan
Write-Host "  - 关闭主窗口 = 收进托盘（壁纸继续播放）"
Write-Host "  - 右键托盘图标可停止壁纸或退出"
Write-Host "  - Win+D 可以看到桌面壁纸"
