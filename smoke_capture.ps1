Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Start-Sleep -Seconds 1
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bmp = New-Object System.Drawing.Bitmap $screen.Width, $screen.Height
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(0, 0, 0, 0, $bmp.Size)
$bmp.Save('C:\Users\root\shredder_smoke.png')
Write-Output 'saved'
