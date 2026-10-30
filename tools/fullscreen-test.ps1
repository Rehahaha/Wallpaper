# 全屏测试窗口：创建一个覆盖整个显示器的无边框窗口，用于验证"全屏自动暂停"
param([int]$Seconds = 20)

Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class Dpi {
  [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
}
'@ -Language CSharp

[Dpi]::SetProcessDpiAwarenessContext([IntPtr](-4)) | Out-Null
Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.FormBorderStyle = "None"
$form.BackColor = [System.Drawing.Color]::FromArgb(20, 20, 24)
$form.StartPosition = "Manual"
$form.Bounds = New-Object System.Drawing.Rectangle(0, 0, [Dpi]::GetSystemMetrics(0), [Dpi]::GetSystemMetrics(1))
$form.TopMost = $true
$label = New-Object System.Windows.Forms.Label
$label.Text = "FULLSCREEN TEST WINDOW"
$label.ForeColor = [System.Drawing.Color]::Orange
$label.Font = New-Object System.Drawing.Font("Segoe UI", 28)
$label.AutoSize = $true
$label.Location = New-Object System.Drawing.Point(80, 80)
$form.Controls.Add($label)

$form.Show()
$form.Activate()
$form.Focus() | Out-Null

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = $Seconds * 1000
$timer.Add_Tick({ $timer.Stop(); $form.Close() })
$timer.Start()

[System.Windows.Forms.Application]::Run($form)
