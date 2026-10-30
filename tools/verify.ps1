# Wallpaper 桌面层验证脚本
# 用法： pwsh -File tools\verify.ps1 -Mode WorkerWChild
# 前置条件：桌面必须处于解锁状态（锁屏时截图只会拍到锁屏界面）

param(
    [ValidateSet("WorkerWChild", "ProgmanTop", "ProgmanBottom", "SetParentProgmanBottom", "SetParentWorkerW", "Standalone", "Mpv")]
    [string]$Mode = "WorkerWChild",
    [string]$OutDir = "docs"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root $OutDir
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class V {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string c, string t);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L,T,R,B; }
  public static IntPtr WorkerW() {
    var progman = FindWindow("Progman", null);
    var child = FindWindowEx(progman, IntPtr.Zero, "WorkerW", null);
    while (child != IntPtr.Zero) {
      RECT r; GetWindowRect(child, out r);
      var defView = FindWindowEx(child, IntPtr.Zero, "SHELLDLL_DefView", null);
      if (defView == IntPtr.Zero && IsWindowVisible(child) && (r.R - r.L) >= 1000) return child;
      child = FindWindowEx(progman, child, "WorkerW", null);
    }
    return IntPtr.Zero;
  }
}
'@ -Language CSharp

# DPI 感知必须在任何截图之前设置，否则拿到的是缩放后的半屏图
[V]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Drawing

$W = [V]::GetSystemMetrics(0)
$H = [V]::GetSystemMetrics(1)
Write-Host "物理分辨率: $W x $H"

function Take-Shot([string]$path) {
    $bmp = New-Object System.Drawing.Bitmap($W, $H)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose()
    return $bmp
}

function Get-Stats($bmp) {
    $magenta = 0; $cyan = 0; $n = 0
    for ($y = 0; $y -lt $bmp.Height; $y += 10) {
        for ($x = 0; $x -lt $bmp.Width; $x += 10) {
            $p = $bmp.GetPixel($x, $y); $n++
            if ($p.R -ge 240 -and $p.G -le 15 -and $p.B -ge 240) { $magenta++ }
            elseif ($p.R -le 15 -and $p.G -ge 240 -and $p.B -ge 240) { $cyan++ }
        }
    }
    return @{ Magenta = $magenta; Cyan = $cyan; Sampled = $n }
}

$shell = New-Object -ComObject Shell.Application
$shell.ToggleDesktop()
Start-Sleep -Seconds 2
$base = Take-Shot (Join-Path $outDir "verify-base.png")

$exe = Join-Path $root "src/Wallpaper.App/bin/Debug/net10.0-windows/Wallpaper.exe"
if ($Mode -eq "Mpv") {
    $worker = [V]::WorkerW()
    $mpv = Join-Path $root "lib/mpv/mpv.exe"
    if (-not (Test-Path $mpv)) { throw "缺少 lib/mpv/mpv.exe（先运行 tools/fetch-mpv.ps1）" }
    $proc = Start-Process -FilePath $mpv -PassThru -ArgumentList @(
        "--wid=" + $worker.ToInt64(), "--loop-file=inf", "--no-osc", "--no-input-default-bindings",
        "--mute=yes", "--hwdec=auto-safe", "--really-quiet",
        "av://lavfi:testsrc=duration=10:size=1280x720:rate=30"
    )
} else {
    $proc = Start-Process -FilePath $exe -PassThru -ArgumentList "--attach-test:$Mode"
}
Start-Sleep -Seconds 4

$shot = Take-Shot (Join-Path $outDir "verify-$Mode.png")
$stats = Get-Stats $shot

$diff = 0; $total = 0
for ($y = 0; $y -lt $base.Height; $y += 10) {
    for ($x = 0; $x -lt $base.Width; $x += 10) {
        $a = $base.GetPixel($x, $y); $b = $shot.GetPixel($x, $y); $total++
        if ([Math]::Abs($a.R - $b.R) -gt 12 -or [Math]::Abs($a.G - $b.G) -gt 12 -or [Math]::Abs($a.B - $b.B) -gt 12) { $diff++ }
    }
}

Write-Host ("模式={0}  magenta={1} cyan={2}  与基线差异={3}%" -f $Mode, $stats.Magenta, $stats.Cyan, [math]::Round(100 * $diff / $total, 1))

$shot.Dispose(); $base.Dispose()
Stop-Process -Id $proc.Id -Force -ErrorAction SilentlyContinue
$shell.ToggleDesktop()
