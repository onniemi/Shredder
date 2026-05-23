$exe = 'D:\data\win\粉碎一切文件夹\src\Shredder.App\bin\Debug\net8.0-windows\Shredder.exe'
if (-not (Test-Path $exe)) {
    Write-Output "EXE_NOT_FOUND: $exe"
    exit 1
}
$p = Start-Process -FilePath $exe -PassThru
Write-Output "STARTED PID=$($p.Id)"
Start-Sleep -Seconds 4
$alive = Get-Process -Id $p.Id -ErrorAction SilentlyContinue
if (-not $alive) {
    Write-Output "PROCESS_EXITED_EARLY"
    exit 1
}
Write-Output "ALIVE PID=$($alive.Id) MainWindow='$($alive.MainWindowTitle)' Mem=$([int]($alive.WorkingSet64/1MB))MB"

# Force window to foreground using user32
Add-Type @"
using System;
using System.Runtime.InteropServices;
public class W {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}
"@
if ($alive.MainWindowHandle -ne [IntPtr]::Zero) {
    [W]::ShowWindow($alive.MainWindowHandle, 9) | Out-Null   # SW_RESTORE
    [W]::SetForegroundWindow($alive.MainWindowHandle) | Out-Null
    Write-Output "FOREGROUNDED"
} else {
    Write-Output "NO_MAIN_WINDOW_HANDLE"
}
Start-Sleep -Seconds 2

# Take screenshot
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save('C:\Users\root\shredder_smoke2.png')
Write-Output 'SCREENSHOT_SAVED'
