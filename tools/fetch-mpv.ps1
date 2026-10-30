# 获取 mpv 运行时（放到 lib/mpv/mpv.exe）
# 用法：
#   powershell -ExecutionPolicy Bypass -File tools/fetch-mpv.ps1
#   powershell -ExecutionPolicy Bypass -File tools/fetch-mpv.ps1 -From "D:\somewhere\mpv.exe"

param(
    [string]$From = "",
    [string]$Url = ""
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$dest = Join-Path $root "lib\mpv"
$exe = Join-Path $dest "mpv.exe"

New-Item -ItemType Directory -Force -Path $dest | Out-Null

if (Test-Path $exe) {
    Write-Host "已存在：$exe（如需替换请先删除）" -ForegroundColor Yellow
    exit 0
}

if ($From) {
    if (-not (Test-Path $From)) { throw "找不到文件：$From" }
    Copy-Item $From $exe -Force
    Write-Host "已复制到 $exe" -ForegroundColor Green
    exit 0
}

if ($Url) {
    Write-Host "下载中：$Url"
    Invoke-WebRequest -Uri $Url -OutFile $exe -UseBasicParsing
    Write-Host "已下载到 $exe" -ForegroundColor Green
    exit 0
}

Write-Host @"
未指定 -From 或 -Url，请手动获取 mpv：

  1. 官方站：https://mpv.io/installation/
  2. Windows 构建：
     - shinchiro 构建：https://github.com/shinchiro/mpv-winbuild-cmake/releases
     - 解压后取 mpv.exe，放到：$exe
  3. 或者用本机已有的 mpv：
     powershell -ExecutionPolicy Bypass -File tools/fetch-mpv.ps1 -From "C:\path\to\mpv.exe"

说明：mpv 体积约 110 MB，采用 GPLv2+，因此不纳入本仓库。
"@ -ForegroundColor Cyan
