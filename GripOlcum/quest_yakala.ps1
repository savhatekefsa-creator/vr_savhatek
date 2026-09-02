# Quest ekranindan kare yakalar, SOL GOZE kirpar ve kucultur.
# Ham kare 4128x2208 (goz basina 2064x2208) ve ~3 MB; kirpilmis/kucultulmus
# hali incelemek icin cok daha kullanisli.
#
# Kullanim:  powershell -File quest_yakala.ps1 -Count 12 -DelaySeconds 5 -OutDir <klasor>
param(
    [int]$Count = 1,
    [int]$DelaySeconds = 5,
    [string]$OutDir = ".",
    [int]$Width = 760
)

$adb = "C:\Program Files\Unity\Hub\Editor\6000.3.18f1\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe"
if (-not (Test-Path $adb)) { Write-Output "adb YOK: $adb"; exit 1 }
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null
Add-Type -AssemblyName System.Drawing

for ($i = 1; $i -le $Count; $i++) {
    $raw = Join-Path $OutDir "_ham.png"
    & $adb shell screencap -p /sdcard/_cs.png
    & $adb pull /sdcard/_cs.png $raw 2>&1 | Out-Null

    if (-not (Test-Path $raw)) { Write-Output "$i : yakalanamadi"; continue }

    $img = [System.Drawing.Image]::FromFile($raw)
    # sol goz = sol yari
    $eyeW = [int]($img.Width / 2)
    $scale = $Width / $eyeW
    $h = [int]($img.Height * $scale)
    $bmp = New-Object System.Drawing.Bitmap($Width, $h)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.DrawImage($img, (New-Object System.Drawing.Rectangle(0, 0, $Width, $h)),
                       (New-Object System.Drawing.Rectangle(0, 0, $eyeW, $img.Height)),
                       [System.Drawing.GraphicsUnit]::Pixel)
    $g.Dispose()
    $name = Join-Path $OutDir ("kare{0:D2}.png" -f $i)
    $bmp.Save($name, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose(); $img.Dispose()
    Remove-Item $raw -Force -ErrorAction SilentlyContinue

    Write-Output ("{0:D2} -> {1} ({2} KB)" -f $i, (Split-Path $name -Leaf), [int]((Get-Item $name).Length / 1KB))
    if ($i -lt $Count) { Start-Sleep -Seconds $DelaySeconds }
}
& $adb shell rm -f /sdcard/_cs.png 2>&1 | Out-Null
Write-Output "BITTI"
