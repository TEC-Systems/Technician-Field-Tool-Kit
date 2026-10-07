#requires -version 5.1
param()

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.Drawing

$root = Split-Path -Parent $PSScriptRoot
$dist = Join-Path $root 'dist'
New-Item -ItemType Directory -Path $dist -Force | Out-Null
$iconPng = Join-Path $dist 'app-icon.png'
$iconIco = Join-Path $dist 'app-icon.ico'
$trayPng = Join-Path $dist 'tray-icon.png'
$trayIco = Join-Path $dist 'tray-icon.ico'
$launcher = Join-Path $dist 'TEC-Systems-FieldToolkit.exe'
$payload = Join-Path $dist 'toolkit-payload.zip'
$setup = Join-Path $dist 'TEC-Systems-FieldToolkit-Setup.exe'
$installerZip = Join-Path $dist 'TEC-Systems-FieldToolkit-Install.zip'
$installer7z = Join-Path $dist 'TEC-Systems-FieldToolkit-Install.7z'

# Use the official emblem (left of the wordmark) at each Windows icon size.
$logoPath = Join-Path $root 'assets\TEC Systems Full Logo Cobalt RGB.png'
$logo = [System.Drawing.Bitmap]::FromFile($logoPath)
$iconImages = @()
try {
    $emblem = New-Object System.Drawing.Rectangle(0, 0, 396, $logo.Height)
    foreach ($size in @(16, 32, 48, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $memory = New-Object System.IO.MemoryStream
        try {
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::HighQuality
            $padding = [Math]::Max(1, [int][Math]::Round($size * 0.035))
            $destination = New-Object System.Drawing.Rectangle($padding, $padding, ($size - 2 * $padding), ($size - 2 * $padding))
            $graphics.DrawImage($logo, $destination, $emblem, [System.Drawing.GraphicsUnit]::Pixel)
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            if ($size -eq 256) { $bitmap.Save($iconPng, [System.Drawing.Imaging.ImageFormat]::Png) }
            $iconImages += [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
        }
        finally { $memory.Dispose(); $graphics.Dispose(); $bitmap.Dispose() }
    }
}
finally { $logo.Dispose() }

function Write-IconImages {
    param([object[]]$Images, [string]$Path)
    $stream = New-Object System.IO.FileStream($Path, [System.IO.FileMode]::Create)
    $writer = New-Object System.IO.BinaryWriter($stream)
    try {
        $writer.Write([uint16]0)
        $writer.Write([uint16]1)
        $writer.Write([uint16]$Images.Count)
        $offset = 6 + 16 * $Images.Count
        foreach ($image in $Images) {
            $dimension = if ($image.Size -eq 256) { 0 } else { $image.Size }
            $writer.Write([byte]$dimension)
            $writer.Write([byte]$dimension)
            $writer.Write([byte]0)
            $writer.Write([byte]0)
            $writer.Write([uint16]1)
            $writer.Write([uint16]32)
            $writer.Write([uint32]$image.Bytes.Length)
            $writer.Write([uint32]$offset)
            $offset += $image.Bytes.Length
        }
        foreach ($image in $Images) { $writer.Write([byte[]]$image.Bytes) }
    }
    finally { $writer.Dispose() }
}

Write-IconImages -Images $iconImages -Path $iconIco
$packagedIcon = Join-Path $root 'assets\TEC Systems Field Toolkit.ico'
Copy-Item -LiteralPath $iconIco -Destination $packagedIcon -Force

# The tray uses a darker emblem on a white disc so it remains visible at 16px.
$logo = [System.Drawing.Bitmap]::FromFile($logoPath)
$trayImages = @()
try {
    foreach ($size in @(16, 32, 48, 256)) {
        $bitmap = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $mask = New-Object System.Drawing.Bitmap($size, $size, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
        $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
        $maskGraphics = [System.Drawing.Graphics]::FromImage($mask)
        $memory = New-Object System.IO.MemoryStream
        $white = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::White)
        $border = New-Object System.Drawing.Pen([System.Drawing.Color]::FromArgb(0, 49, 140), [Math]::Max(1, $size * 0.015))
        try {
            $graphics.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
            $graphics.Clear([System.Drawing.Color]::Transparent)
            $graphics.FillEllipse($white, 0, 0, ($size - 1), ($size - 1))
            $graphics.DrawEllipse($border, 0, 0, ($size - 1), ($size - 1))
            $maskGraphics.Clear([System.Drawing.Color]::Transparent)
            $maskGraphics.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
            $padding = [Math]::Max(2, [int][Math]::Round($size * 0.09))
            $destination = New-Object System.Drawing.Rectangle($padding, $padding, ($size - 2 * $padding), ($size - 2 * $padding))
            $emblem = New-Object System.Drawing.Rectangle(0, 0, 396, $logo.Height)
            $maskGraphics.DrawImage($logo, $destination, $emblem, [System.Drawing.GraphicsUnit]::Pixel)
            for ($y = 0; $y -lt $size; $y++) {
                for ($x = 0; $x -lt $size; $x++) {
                    $alpha = [int]$mask.GetPixel($x, $y).A
                    if ($alpha -gt 0) {
                        $red = [int][Math]::Round(255 * (255 - $alpha) / 255)
                        $green = [int][Math]::Round((49 * $alpha + 255 * (255 - $alpha)) / 255)
                        $blue = [int][Math]::Round((140 * $alpha + 255 * (255 - $alpha)) / 255)
                        $bitmap.SetPixel($x, $y, [System.Drawing.Color]::FromArgb(255, $red, $green, $blue))
                    }
                }
            }
            $bitmap.Save($memory, [System.Drawing.Imaging.ImageFormat]::Png)
            if ($size -eq 256) { $bitmap.Save($trayPng, [System.Drawing.Imaging.ImageFormat]::Png) }
            $trayImages += [pscustomobject]@{ Size = $size; Bytes = $memory.ToArray() }
        }
        finally { $border.Dispose(); $white.Dispose(); $memory.Dispose(); $maskGraphics.Dispose(); $graphics.Dispose(); $mask.Dispose(); $bitmap.Dispose() }
    }
}
finally { $logo.Dispose() }
Write-IconImages -Images $trayImages -Path $trayIco
Copy-Item -LiteralPath $trayIco -Destination (Join-Path $root 'assets\TEC Systems Field Toolkit Tray.ico') -Force

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework\v4.0.30319\csc.exe'
if (-not (Test-Path $compiler)) { throw 'The .NET Framework C# compiler was not found.' }

$compileOutput = & $compiler /nologo /target:winexe ('/win32manifest:"{0}"' -f (Join-Path $PSScriptRoot 'Toolkit.manifest')) ("/out:`"{0}`"" -f $launcher) ("/win32icon:`"{0}`"" -f $iconIco) ("/resource:`"{0}`",MacVendors" -f (Join-Path $PSScriptRoot 'mac-vendors.tsv')) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Management.dll /reference:System.ServiceProcess.dll /reference:System.Web.Extensions.dll /reference:System.Core.dll /reference:System.Security.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'NativeToolkit.cs') (Join-Path $PSScriptRoot 'RdpManager.cs') (Join-Path $PSScriptRoot 'SiteWorkspace.cs') 2>&1
$compileExit = $LASTEXITCODE
$compileOutput | Write-Host
if ($compileExit -ne 0) { throw ('Native toolkit compilation failed: ' + ($compileOutput -join "`n")) }
$selfTestError = Join-Path $dist 'self-test-error.txt'
if (Test-Path -LiteralPath $selfTestError) { Remove-Item -LiteralPath $selfTestError -Force }
$selfTest = Start-Process -FilePath $launcher -ArgumentList '/self-test' -PassThru -Wait
if ($selfTest.ExitCode -ne 0) {
    $detail = if (Test-Path -LiteralPath $selfTestError) { Get-Content -LiteralPath $selfTestError -Raw } else { 'No error details were written.' }
    throw "Native toolkit self-test failed: $detail"
}

$payloadFiles = @($launcher, (Join-Path $root 'version.txt'), (Join-Path $root 'assets'))
$archiveError = $null
for ($attempt = 1; $attempt -le 5; $attempt++) {
    try {
        Start-Sleep -Seconds 2
        Compress-Archive -Path $payloadFiles -DestinationPath $payload -Force -ErrorAction Stop
        $archiveError = $null
        break
    }
    catch {
        $archiveError = $_
        if ($attempt -eq 5) { throw "Could not package the native EXE: $archiveError" }
    }
}

$compileOutput = & $compiler /nologo /target:winexe ('/win32manifest:"{0}"' -f (Join-Path $PSScriptRoot 'Toolkit.manifest')) ("/out:`"{0}`"" -f $setup) ("/win32icon:`"{0}`"" -f $iconIco) ("/resource:`"{0}`",ToolkitPayload" -f $payload) /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.IO.Compression.dll /reference:System.IO.Compression.FileSystem.dll (Join-Path $PSScriptRoot 'ToolkitSetup.cs') 2>&1
$compileExit = $LASTEXITCODE
$compileOutput | Write-Host
if ($compileExit -ne 0) { throw ('Installer compilation failed: ' + ($compileOutput -join "`n")) }

$verify = Start-Process -FilePath $setup -ArgumentList '/verify' -PassThru -Wait
if ($verify.ExitCode -ne 0) { throw 'Installer payload verification failed.' }
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[System.IO.File]::WriteAllText((Join-Path $dist 'SHA256SUMS.txt'), ("{0}  TEC-Systems-FieldToolkit-Setup.exe`n" -f $hash), [System.Text.Encoding]::ASCII)
Compress-Archive -Path $setup -DestinationPath $installerZip -Force
$sevenZip = Join-Path $env:ProgramFiles '7-Zip\7z.exe'
if (-not (Test-Path -LiteralPath $sevenZip)) {
    $command = Get-Command 7z.exe -ErrorAction SilentlyContinue
    if ($null -eq $command) { throw '7-Zip is required to package the Teams-friendly installer archive.' }
    $sevenZip = $command.Source
}
if (Test-Path -LiteralPath $installer7z) { Remove-Item -LiteralPath $installer7z -Force }
& $sevenZip a -t7z -mx=9 $installer7z $setup (Join-Path $dist 'SHA256SUMS.txt') | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'Could not create the installer 7z archive.' }
& $sevenZip t $installer7z | Write-Host
if ($LASTEXITCODE -ne 0) { throw 'The installer 7z archive failed integrity verification.' }
Write-Host ('Built: {0}' -f $setup)
Write-Host ('Installer ZIP: {0}' -f $installerZip)
Write-Host ('Installer 7z: {0}' -f $installer7z)
